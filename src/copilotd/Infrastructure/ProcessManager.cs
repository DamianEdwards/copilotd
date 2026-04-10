using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Models;
using Microsoft.Extensions.Logging;
using static Copilotd.Infrastructure.NativeInterop;

namespace Copilotd.Infrastructure;

/// <summary>
/// Manages launching copilot as independent/detached processes and verifying liveness.
/// Tracks PID + start time to detect PID reuse across daemon restarts.
/// </summary>
public sealed partial class ProcessManager
{
    private static readonly TimeSpan SignalDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(10);

    private readonly StateStore _stateStore;
    private readonly RepoPathResolver _repoResolver;
    private readonly ILogger<ProcessManager> _logger;

    public ProcessManager(StateStore stateStore, RepoPathResolver repoResolver, ILogger<ProcessManager> logger)
    {
        _stateStore = stateStore;
        _repoResolver = repoResolver;
        _logger = logger;
    }

    /// <summary>
    /// Launches a copilot process detached from this daemon so it survives daemon crashes.
    /// Returns the populated session on success, or null on failure.
    /// </summary>
    public DispatchSession? LaunchCopilot(DispatchSession session, CopilotdConfig config, GitHubIssue issue, DaemonState state)
    {
        // Use worktree path if available, otherwise resolve the main repo path
        var repoPath = session.WorktreePath ?? _repoResolver.ResolveRepoPath(issue.Repo, config, state);
        if (repoPath is null || !Directory.Exists(repoPath))
        {
            _logger.LogWarning("Working directory not found for {Repo}", issue.Repo);
            return null;
        }

        var customPrompt = _stateStore.LoadCustomPrompt(config);
        var prompt = BuildPrompt(customPrompt, issue, session, config);
        var args = BuildArguments(session, prompt, config.Rules.GetValueOrDefault(session.RuleName), repoPath, config.DefaultModel);

        _logger.LogInformation("Launching copilot for {IssueKey} with session {SessionId}", session.IssueKey, session.CopilotSessionId);
        _logger.LogDebug("copilot {Args}", args);

        try
        {
            Process? process;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use CreateProcessW directly to set CREATE_NEW_CONSOLE and
                // CREATE_NEW_PROCESS_GROUP, ensuring the copilot process gets its own
                // console and process group. This is required for graceful Ctrl+Break/C
                // termination to work without affecting the daemon's console.
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                var cmdLine = $"copilot {args}";
                var flags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;

                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, repoPath, ref si, out var pi))
                {
                    _logger.LogError("CreateProcessW failed for {IssueKey} (error: {Error})",
                        session.IssueKey, Marshal.GetLastWin32Error());
                    return null;
                }

                session.ProcessId = pi.dwProcessId;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);

                try
                {
                    using var proc = Process.GetProcessById(pi.dwProcessId);
                    session.ProcessStartTime = GetProcessStartTime(proc);
                }
                catch
                {
                    session.ProcessStartTime = DateTimeOffset.UtcNow;
                }

                process = null; // Already tracked via PID
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = args,
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                };

                process = Process.Start(psi);
                if (process is null)
                {
                    _logger.LogError("Failed to start copilot process for {IssueKey}", session.IssueKey);
                    return null;
                }

                session.ProcessId = process.Id;
                session.ProcessStartTime = GetProcessStartTime(process);
                process.Dispose();
            }

            session.Status = SessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.LastVerifiedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Copilot launched for {IssueKey}: PID={Pid}", session.IssueKey, session.ProcessId);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception launching copilot for {IssueKey}", session.IssueKey);
            return null;
        }
    }

    /// <summary>
    /// Checks if a tracked process is still alive and matches the recorded start time.
    /// </summary>
    public ProcessLivenessResult CheckProcess(DispatchSession session)
    {
        if (session.ProcessId is not { } pid)
            return ProcessLivenessResult.Dead;

        try
        {
            var process = Process.GetProcessById(pid);

            // Verify start time to detect PID reuse
            if (session.ProcessStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogDebug("PID {Pid} start time mismatch: expected {Expected}, got {Actual}",
                        pid, expectedStart, actualStart);
                    process.Dispose();
                    return ProcessLivenessResult.PidReused;
                }
            }

            var alive = !process.HasExited;
            process.Dispose();
            return alive ? ProcessLivenessResult.Alive : ProcessLivenessResult.Dead;
        }
        catch (ArgumentException)
        {
            // Process not found
            return ProcessLivenessResult.Dead;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking process {Pid}", pid);
            return ProcessLivenessResult.Dead;
        }
    }

    /// <summary>
    /// Gracefully terminates the process associated with a dispatch session.
    /// On Windows, spawns a helper copilotd instance (shutdown-instance command) that attaches
    /// to the target's console and sends interrupt signals — the daemon cannot do this directly
    /// as FreeConsole disrupts ConPTY sessions.
    /// On Unix, sends SIGINT directly, falling back to SIGKILL.
    /// Verifies PID + start time to avoid terminating an unrelated process after PID reuse.
    /// Returns true if the process was successfully terminated or was already dead.
    /// </summary>
    public bool TerminateProcess(DispatchSession session)
    {
        if (session.ProcessId is not { } pid)
        {
            _logger.LogDebug("No PID tracked for {Key}, nothing to terminate", session.IssueKey);
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _logger.LogDebug("Process {Pid} for {Key} not found, already exited", pid, session.IssueKey);
            return true;
        }

        try
        {
            // Verify start time to avoid terminating a different process that reused the PID
            if (session.ProcessStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogWarning("PID {Pid} for {Key} was reused by another process, skipping termination",
                        pid, session.IssueKey);
                    return true;
                }
            }

            if (process.HasExited)
            {
                _logger.LogDebug("Process {Pid} for {Key} already exited", pid, session.IssueKey);
                return true;
            }

            _logger.LogInformation("Gracefully terminating copilot process {Pid} for {Key}", pid, session.IssueKey);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TerminateViaShutdownInstance(process, pid);
            }
            else
            {
                return TerminateViaSignals(process, pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate process {Pid} for {Key}", pid, session.IssueKey);
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Windows: spawns 'copilotd shutdown-instance --pid PID' which handles the full
    /// graceful shutdown lifecycle (Ctrl+Break → Ctrl+C → Kill) from a separate process
    /// that can safely attach to the target's console.
    /// </summary>
    private bool TerminateViaShutdownInstance(Process process, int pid)
    {
        var copilotdPath = Environment.ProcessPath;
        if (copilotdPath is null)
        {
            _logger.LogWarning("Cannot determine copilotd executable path, falling back to kill");
            process.Kill(entireProcessTree: true);
            return true;
        }

        _logger.LogDebug("Spawning shutdown-instance helper for PID {Pid}", pid);

        var psi = new ProcessStartInfo
        {
            FileName = copilotdPath,
            Arguments = $"shutdown-instance --pid {pid}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var helper = Process.Start(psi);
            if (helper is null)
            {
                _logger.LogWarning("Failed to start shutdown-instance helper, falling back to kill");
                process.Kill(entireProcessTree: true);
                return true;
            }

            // The shutdown-instance command handles signals + kill fallback internally,
            // so we just need to wait for it to complete
            if (helper.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                if (helper.ExitCode == 0)
                {
                    _logger.LogInformation("Process {Pid} terminated via shutdown-instance", pid);
                    return true;
                }

                _logger.LogWarning("shutdown-instance exited with code {Code} for PID {Pid}", helper.ExitCode, pid);
            }
            else
            {
                _logger.LogWarning("shutdown-instance timed out for PID {Pid}", pid);
                try { helper.Kill(); } catch { }
            }

            // Final fallback if shutdown-instance didn't fully clean up
            if (!process.HasExited)
            {
                _logger.LogWarning("Forcing kill of PID {Pid} after shutdown-instance", pid);
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shutdown-instance failed for PID {Pid}, falling back to kill", pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
    }

    /// <summary>
    /// Unix: sends SIGINT directly (twice with delay), falling back to SIGKILL.
    /// No helper process needed — SIGINT works across process boundaries on Unix.
    /// </summary>
    private bool TerminateViaSignals(Process process, int pid)
    {
        try
        {
            _logger.LogDebug("Sending SIGINT to PID {Pid}", pid);
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(SignalDelay))
            {
                _logger.LogInformation("Process {Pid} exited after first SIGINT", pid);
                return true;
            }

            _logger.LogDebug("Sending second SIGINT to PID {Pid}", pid);
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(GracefulTimeout))
            {
                _logger.LogInformation("Process {Pid} exited after second SIGINT", pid);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error sending SIGINT to PID {Pid}", pid);
        }

        // Fall back to SIGKILL
        _logger.LogWarning("Graceful shutdown timed out for PID {Pid}, sending SIGKILL", pid);
        try
        {
            sys_kill(pid, SIGKILL);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
        }

        return true;
    }

    private static string BuildPrompt(string globalCustomPrompt, GitHubIssue issue, DispatchSession session, CopilotdConfig config)
    {
        // Use a PR-specific prompt when re-dispatching for review feedback
        var prompt = session.PullRequestNumber is not null
            ? BuildPrReviewPrompt(issue, session)
            : CopilotdConfig.DefaultPrompt;

        var rule = config.Rules.GetValueOrDefault(session.RuleName);

        // Resolve the effective custom prompt based on rule settings
        var effectiveCustomPrompt = ResolveCustomPrompt(globalCustomPrompt, rule);

        if (!string.IsNullOrWhiteSpace(effectiveCustomPrompt))
        {
            prompt += "\n\nThe user has supplied the following additional context:\n\n" + effectiveCustomPrompt;
        }

        if (!string.IsNullOrWhiteSpace(rule?.ExtraPrompt))
        {
            prompt += "\n\n" + rule.ExtraPrompt;
        }

        // Append security context when re-dispatching in response to comments
        if (session.RedispatchCount > 0)
        {
            prompt += "\n\n" + CopilotdConfig.SecurityPrompt;
        }

        // Replace tokens in the entire prompt (default + custom + extra)
        prompt = prompt
            .Replace("$(issue.repo)", issue.Repo)
            .Replace("$(issue.id)", issue.Number.ToString())
            .Replace("$(issue.type)", issue.Type ?? "issue")
            .Replace("$(issue.milestone)", issue.Milestone ?? "none")
            .Replace("$(pr.id)", session.PullRequestNumber?.ToString() ?? "");

        return prompt;
    }

    /// <summary>
    /// Builds a prompt for re-dispatching a session to address PR review feedback.
    /// </summary>
    private static string BuildPrReviewPrompt(GitHubIssue issue, DispatchSession session)
    {
        return $$"""
            You are addressing review feedback on pull request #$(pr.id) in the $(issue.repo) repository.
            This PR was created for issue #$(issue.id). Read the PR review comments carefully and address all feedback.

            Important:
            - You are on the same branch that was used to create the PR. Your changes will be pushed to the existing PR.
            - Address each review comment by making the requested changes.
            - If a review comment includes a suggested change (```suggestion block), apply it directly to the relevant file.
            - After addressing all review feedback, push your changes to update the PR.
            - Then run `copilotd session pr $(pr.id) $(issue.repo)#$(issue.id)` to continue monitoring for further review feedback.
            - If the changes are complete and no more reviews are expected, run `copilotd session complete $(issue.repo)#$(issue.id)` instead.

            Interacting with the PR:
            - To post a general comment on the PR: `gh pr comment $(pr.id) --repo $(issue.repo) --body "Your comment"`
            - To reply to a specific review thread, use `gh api graphql` with the addPullRequestReviewThreadReply mutation:
              ```
              gh api graphql -f query='mutation { addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: "THREAD_ID", body: "Your reply" }) { comment { id } } }'
              ```
              You can find thread IDs by querying: `gh api graphql -f query='{ repository(owner: "OWNER", name: "REPO") { pullRequest(number: $(pr.id)) { reviewThreads(last: 20) { nodes { id isResolved comments(last: 5) { nodes { body author { login } } } } } } } }'`
            - Do NOT use `copilotd session comment` to post to the issue when in PR review mode. All communication should happen on the PR itself.
            """;
    }

    /// <summary>
    /// Resolves the effective custom prompt by combining the global custom prompt
    /// with the rule's custom prompt based on the rule's <see cref="PromptMode"/>.
    /// </summary>
    private static string ResolveCustomPrompt(string globalCustomPrompt, DispatchRule? rule)
    {
        var ruleCustomPrompt = rule?.CustomPrompt;

        if (string.IsNullOrWhiteSpace(ruleCustomPrompt))
        {
            return globalCustomPrompt;
        }

        return rule!.CustomPromptMode switch
        {
            PromptMode.Override => ruleCustomPrompt,
            // Append (default): combine global + rule custom prompts
            _ => string.IsNullOrWhiteSpace(globalCustomPrompt)
                ? ruleCustomPrompt
                : globalCustomPrompt + "\n\n" + ruleCustomPrompt,
        };
    }

    private static string BuildArguments(DispatchSession session, string prompt, DispatchRule? rule, string repoPath, string? defaultModel)
    {
        var args = new List<string>
        {
            "--remote",
            $"--resume={session.CopilotSessionId}",
            "-i", $"\"{EscapeArg(prompt)}\"",
        };

        // Model: rule-specific overrides global default
        var model = rule?.Model ?? defaultModel;
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add($"\"{EscapeArg(model)}\"");
        }

        var yolo = rule?.Yolo == true;

        if (yolo)
        {
            args.Add("--yolo");
        }
        else
        {
            // Yolo implies both, so only add individually when not using yolo
            if (rule?.AllowAllTools != false)
                args.Add("--allow-all-tools");

            if (rule?.AllowAllUrls == true)
                args.Add("--allow-all-urls");
        }

        // Always add the repo directory as an allowed path
        args.Add("--add-dir");
        args.Add($"\"{EscapeArg(repoPath)}\"");

        return string.Join(' ', args);
    }

    private static string EscapeArg(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static DateTimeOffset? GetProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    // ---- Worktree lifecycle ----

    /// <summary>
    /// Creates a git worktree for the session on a new branch from the latest default branch.
    /// Layout: &lt;repo_home&gt;/org/repo_sessions/issue-N/
    /// If the session already has a worktree (e.g., re-dispatching for PR review), refreshes it instead.
    /// </summary>
    public bool PrepareWorktree(DispatchSession session, CopilotdConfig config, DaemonState state)
    {
        // If the session already has a worktree (PR review re-dispatch), refresh it
        if (!string.IsNullOrEmpty(session.WorktreePath) && Directory.Exists(session.WorktreePath))
        {
            return RefreshWorktree(session);
        }
        var mainRepoPath = _repoResolver.ResolveRepoPath(session.Repo, config, state);
        if (mainRepoPath is null || !Directory.Exists(mainRepoPath))
        {
            _logger.LogWarning("Main repo directory not found for {Repo}", session.Repo);
            return false;
        }

        // Session dir: <repo_home>/org/repo_sessions/issue-N/
        var sessionsDir = mainRepoPath + "_sessions";
        var worktreePath = Path.Combine(sessionsDir, $"issue-{session.IssueNumber}");

        // If the worktree directory already exists from a prior run, remove it
        if (Directory.Exists(worktreePath))
        {
            _logger.LogDebug("Removing existing worktree at {Path}", worktreePath);
            RunGit(mainRepoPath, $"worktree remove \"{worktreePath}\" --force");
        }

        // Prune stale worktree tracking entries (handles crash scenarios where
        // the directory was deleted but git still tracks the worktree internally)
        RunGit(mainRepoPath, "worktree prune");

        // Clean up stale branch from a previous failed attempt (tracked in state).
        // Done AFTER worktree remove + prune so the branch is no longer checked
        // out in any worktree (git refuses to delete checked-out branches).
        if (!string.IsNullOrEmpty(session.BranchName))
        {
            _logger.LogDebug("Cleaning up stale branch {Branch} from previous attempt", session.BranchName);
            if (RunGit(mainRepoPath, $"branch -D {session.BranchName}"))
            {
                session.BranchName = null;
            }
        }

        Directory.CreateDirectory(sessionsDir);

        // Fetch latest from origin
        _logger.LogDebug("Fetching latest from origin for {Repo}", session.Repo);
        if (!RunGit(mainRepoPath, "fetch origin"))
        {
            _logger.LogWarning("Failed to fetch origin for {Repo}", session.Repo);
            return false;
        }

        // Determine default branch (origin/HEAD → origin/main or origin/master)
        var defaultBranch = GetDefaultBranch(mainRepoPath);
        if (defaultBranch is null)
        {
            _logger.LogWarning("Could not determine default branch for {Repo}", session.Repo);
            return false;
        }

        // Generate a unique branch name with random suffix to avoid conflicts
        // with user branches and stale branches from non-atomic git worktree add
        var suffix = Guid.NewGuid().ToString("N")[..4];
        var branchName = $"copilotd/issue-{session.IssueNumber}-{suffix}";

        // Track branch name BEFORE the git command so it's persisted even if
        // worktree add fails partway (git creates the branch before the worktree)
        session.BranchName = branchName;

        // Create worktree on a new branch from origin's default branch
        _logger.LogInformation("Creating worktree for {Key} at {Path} from {Branch} (branch: {BranchName})",
            session.IssueKey, worktreePath, defaultBranch, branchName);

        if (!RunGit(mainRepoPath, $"worktree add \"{worktreePath}\" -b {branchName} {defaultBranch}"))
        {
            _logger.LogWarning("Failed to create worktree for {Key}", session.IssueKey);
            return false;
        }

        session.WorktreePath = worktreePath;
        _logger.LogInformation("Worktree ready for {Key} at {Path}", session.IssueKey, worktreePath);
        return true;
    }

    /// <summary>
    /// Refreshes an existing worktree by pulling the latest changes from origin.
    /// Used when re-dispatching a session for PR review feedback.
    /// </summary>
    private bool RefreshWorktree(DispatchSession session)
    {
        var worktreePath = session.WorktreePath!;
        _logger.LogInformation("Refreshing existing worktree for {Key} at {Path}", session.IssueKey, worktreePath);

        // Fetch latest from origin
        if (!RunGit(worktreePath, "fetch origin"))
        {
            _logger.LogWarning("Failed to fetch origin in worktree for {Key}", session.IssueKey);
            return false;
        }

        // Pull latest changes (the branch may have been updated by the PR)
        if (!RunGit(worktreePath, "pull --ff-only"))
        {
            _logger.LogWarning("Failed to pull latest changes in worktree for {Key}, continuing anyway", session.IssueKey);
            // Non-fatal: the worktree may have local changes that prevent ff-only
        }

        _logger.LogInformation("Worktree refreshed for {Key} at {Path}", session.IssueKey, worktreePath);
        return true;
    }

    /// <summary>
    /// Removes the git worktree and branch associated with a session.
    /// Safe to call even when WorktreePath is null (e.g., after a failed PrepareWorktree).
    /// </summary>
    public void CleanupWorktree(DispatchSession session, CopilotdConfig config, DaemonState state)
    {
        var mainRepoPath = _repoResolver.ResolveRepoPath(session.Repo, config, state);

        // Remove the worktree directory if it exists
        if (!string.IsNullOrEmpty(session.WorktreePath))
        {
            _logger.LogDebug("Cleaning up worktree for {Key} at {Path}", session.IssueKey, session.WorktreePath);

            if (Directory.Exists(session.WorktreePath))
            {
                if (mainRepoPath is not null)
                    RunGit(mainRepoPath, $"worktree remove \"{session.WorktreePath}\" --force");
                else
                    _logger.LogWarning("Cannot run 'git worktree remove' for {Key}: main repo path not found", session.IssueKey);
            }

            session.WorktreePath = null;
        }

        // Delete the branch — use the stored name, falling back to the legacy
        // naming scheme for sessions created before BranchName tracking was added.
        // Only clear BranchName on success so a future cleanup can retry.
        if (mainRepoPath is not null)
        {
            var branchName = session.BranchName ?? $"copilotd/issue-{session.IssueNumber}";
            if (RunGit(mainRepoPath, $"branch -D {branchName}"))
            {
                session.BranchName = null;
            }
        }
        else
        {
            _logger.LogWarning("Cannot clean up branch for {Key}: main repo path not found", session.IssueKey);
        }
    }

    private string? GetDefaultBranch(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref origin/HEAD",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(TimeSpan.FromSeconds(10));

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && output != "origin/HEAD")
                return output;

            // Fallback: try origin/main then origin/master
            return RunGit(repoPath, "rev-parse --verify origin/main") ? "origin/main"
                : RunGit(repoPath, "rev-parse --verify origin/master") ? "origin/master"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool RunGit(string workingDir, string arguments)
    {
        try
        {
            _logger.LogDebug("Running: git {Arguments} (in {Dir})", arguments, workingDir);

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogWarning("Failed to start git process for: git {Arguments}", arguments);
                return false;
            }

            // Read stderr asynchronously to avoid deadlock when stdout/stderr
            // buffers fill in different orders
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("git {Arguments} failed (exit {ExitCode}): {StdErr}",
                    arguments, process.ExitCode, stderr.Trim());
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogDebug("git {Arguments} stderr: {StdErr}", arguments, stderr.Trim());

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception running: git {Arguments}", arguments);
            return false;
        }
    }

    private bool RunGitCheck(string workingDir, string arguments)
    {
        return RunGit(workingDir, arguments);
    }
}

public enum ProcessLivenessResult
{
    Alive,
    Dead,
    PidReused,
}
