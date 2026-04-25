using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Commands;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
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
    private static readonly TimeSpan WindowsCopilotChildDiscoveryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WindowsCopilotChildDiscoveryPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly NuGetVersion MinimumNamedSessionVersion = new(1, 0, 35);

    private readonly StateStore _stateStore;
    private readonly RepoPathResolver _repoResolver;
    private readonly RuntimeContext _runtimeContext;
    private readonly CopilotCliService _copilotCli;
    private readonly ILogger<ProcessManager> _logger;

    public ProcessManager(
        StateStore stateStore,
        RepoPathResolver repoResolver,
        RuntimeContext runtimeContext,
        CopilotCliService copilotCli,
        ILogger<ProcessManager> logger)
    {
        _stateStore = stateStore;
        _repoResolver = repoResolver;
        _runtimeContext = runtimeContext;
        _copilotCli = copilotCli;
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
        var copilotdCommand = _runtimeContext.GetCopilotdCallbackCommand();
        var prompt = BuildPrompt(customPrompt, issue, session, config, copilotdCommand);
        var hasExistingResumeContext = HasExistingResumeContext(session);
        var sessionName = session.CopilotSessionName;
        if (!hasExistingResumeContext && string.IsNullOrWhiteSpace(sessionName))
        {
            sessionName = TryBuildSessionName(issue, session, config, copilotdCommand);
            session.CopilotSessionName = sessionName;
        }

        var args = BuildArguments(
            session,
            prompt,
            sessionName,
            config.Rules.GetValueOrDefault(session.RuleName),
            repoPath,
            config.DefaultModel,
            _runtimeContext.GetExtraAllowedDirectories());

        _logger.LogInformation(
            "Launching copilot for {IssueKey} with session {SessionId}",
            session.IssueKey,
            session.CopilotSessionName ?? session.CopilotSessionId);
        _logger.LogDebug("copilot {Args}", args);

        try
        {
            Process? process;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use CreateProcessW directly to set CREATE_NEW_CONSOLE and
                // CREATE_NEW_PROCESS_GROUP, ensuring the copilot process gets its own
                // console and process group. This is required for graceful console-control
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
                AssignTrackedWindowsCopilotProcess(session.IssueKey, pi.dwProcessId, tracked =>
                {
                    session.ProcessId = tracked.ProcessId;
                    session.ProcessStartTime = tracked.ProcessStartTime;
                });

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
            session.FailureDetail = null;
            session.HasStarted = true;
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
        => TerminateProcess(session.IssueKey, session.ProcessId, session.ProcessStartTime);

    /// <summary>
    /// Gracefully terminates a tracked copilot process using the saved PID and start time.
    /// The label is used only for logging.
    /// </summary>
    public bool TerminateProcess(string processLabel, int? processId, DateTimeOffset? processStartTime)
    {
        if (processId is not { } pid)
        {
            _logger.LogDebug("No PID tracked for {ProcessLabel}, nothing to terminate", processLabel);
            return true;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _logger.LogDebug("Process {Pid} for {ProcessLabel} not found, already exited", pid, processLabel);
            return true;
        }

        try
        {
            // Verify start time to avoid terminating a different process that reused the PID
            if (processStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogWarning("PID {Pid} for {ProcessLabel} was reused by another process, skipping termination",
                        pid, processLabel);
                    return true;
                }
            }

            if (process.HasExited)
            {
                _logger.LogDebug("Process {Pid} for {ProcessLabel} already exited", pid, processLabel);
                return true;
            }

            _logger.LogInformation("Gracefully terminating copilot process {Pid} for {ProcessLabel}", pid, processLabel);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TerminateViaShutdownInstance(process, pid, processStartTime);
            }
            else
            {
                return TerminateViaSignals(process, pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate process {Pid} for {ProcessLabel}", pid, processLabel);
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Schedules a background shutdown helper for a tracked copilot process. This keeps the
    /// caller responsive while the helper optionally waits before sending shutdown signals.
    /// Falls back to synchronous termination if the helper cannot be started.
    /// </summary>
    public bool ScheduleTerminateProcess(string processLabel, int? processId, DateTimeOffset? processStartTime, TimeSpan shutdownDelay)
    {
        if (processId is not { } pid)
        {
            _logger.LogDebug("No PID tracked for {ProcessLabel}, nothing to schedule", processLabel);
            return true;
        }

        var invocation = GetShutdownInstanceInvocation(pid, processStartTime, shutdownDelay);
        if (invocation is null)
        {
            _logger.LogWarning("Cannot determine copilotd executable path, falling back to synchronous termination");
            return TerminateProcess(processLabel, processId, processStartTime);
        }

        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var helper = Process.Start(psi);
            if (helper is null)
            {
                _logger.LogWarning("Failed to start shutdown-instance helper for PID {Pid}, falling back to synchronous termination", pid);
                return TerminateProcess(processLabel, processId, processStartTime);
            }

            _logger.LogInformation("Scheduled shutdown-instance for {ProcessLabel} (PID {Pid}) with delay {Delay}", processLabel, pid, shutdownDelay);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to schedule shutdown-instance for {ProcessLabel} (PID {Pid}), falling back to synchronous termination", processLabel, pid);
            return TerminateProcess(processLabel, processId, processStartTime);
        }
    }

    /// <summary>
    /// Windows: spawns 'copilotd shutdown-instance --pid PID --signal-profile copilot'
    /// which handles the full graceful shutdown lifecycle (Ctrl+C → 1s wait → Ctrl+C → Kill) from a separate process
    /// that can safely attach to the target's console.
    /// </summary>
    private bool TerminateViaShutdownInstance(Process process, int pid, DateTimeOffset? processStartTime)
    {
        var invocation = GetShutdownInstanceInvocation(pid, processStartTime, TimeSpan.Zero);
        if (invocation is null)
        {
            _logger.LogWarning("Cannot determine copilotd executable path, falling back to kill");
            process.Kill(entireProcessTree: true);
            return true;
        }

        _logger.LogDebug("Spawning shutdown-instance helper for PID {Pid}", pid);

        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
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
                if (ShutdownInstanceCommand.IsSuccessExitCode(helper.ExitCode))
                {
                    var outcome = ShutdownInstanceCommand.DescribeExitCode(helper.ExitCode);
                    if (ShutdownInstanceCommand.UsedFallbackKillExitCode(helper.ExitCode))
                        _logger.LogWarning("Process {Pid} terminated after shutdown-instance {Outcome}", pid, outcome);
                    else
                        _logger.LogInformation("Process {Pid} terminated via shutdown-instance ({Outcome})", pid, outcome);
                    return true;
                }

                _logger.LogWarning("shutdown-instance exited with code {Code} ({Outcome}) for PID {Pid}",
                    helper.ExitCode, ShutdownInstanceCommand.DescribeExitCode(helper.ExitCode), pid);
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

    private CommandInvocation? GetShutdownInstanceInvocation(int pid, DateTimeOffset? processStartTime, TimeSpan shutdownDelay)
    {
        var effectiveDelay = shutdownDelay < TimeSpan.Zero ? TimeSpan.Zero : shutdownDelay;

        var arguments = $"shutdown-instance --pid {pid} --signal-profile copilot";
        if (processStartTime is { } expectedStart)
            arguments += $" --expected-start {expectedStart:O}";

        if (effectiveDelay > TimeSpan.Zero)
            arguments += $" --delay-seconds {(int)Math.Ceiling(effectiveDelay.TotalSeconds)}";

        return _runtimeContext.GetSelfInvocation(arguments);
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

    /// <summary>
    /// Checks if the control session process is still alive.
    /// </summary>
    public ProcessLivenessResult CheckControlSession(ControlSessionInfo session)
    {
        if (session.ProcessId is not { } pid)
            return ProcessLivenessResult.Dead;

        try
        {
            var process = Process.GetProcessById(pid);

            if (session.ProcessStartTime is { } expectedStart)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                {
                    _logger.LogDebug("Control session PID {Pid} start time mismatch", pid);
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
            return ProcessLivenessResult.Dead;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking control session process {Pid}", pid);
            return ProcessLivenessResult.Dead;
        }
    }

    /// <summary>
    /// Launches the control remote session — a special <c>copilot --remote</c> session that
    /// allows remote management of copilotd via the GitHub remote sessions UI.
    /// Returns a populated <see cref="ControlSessionInfo"/> on success, or null on failure.
    /// </summary>
    public ControlSessionInfo? LaunchControlSession(CopilotdConfig config)
    {
        // Clone the copilotd repo to <RepoHome>/DamianEdwards/copilotd if needed, then
        // launch from a dedicated subdirectory so repo-root wrapper scripts don't shadow
        // the installed copilotd command inside the remote session harness.
        var workingDir = EnsureControlSessionWorkingDirectory(config);
        if (workingDir is null)
        {
            _logger.LogError("Cannot launch control session: failed to set up control session working directory");
            return null;
        }

        var session = new ControlSessionInfo
        {
            CopilotSessionId = Guid.NewGuid().ToString("D"),
            Status = ControlSessionStatus.Starting,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var prompt = CopilotdConfig.ControlSessionPrompt
            .Replace("$(copilotd.command)", _runtimeContext.GetCopilotdCallbackCommand(), StringComparison.Ordinal);
        var args = BuildControlSessionArguments(session.CopilotSessionId, prompt, config.DefaultModel, _runtimeContext);
        _logger.LogInformation("Launching control session {SessionId}", session.CopilotSessionId);
        _logger.LogDebug("copilot {Args}", args);

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                var cmdLine = $"copilot {args}";
                var flags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;

                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, workingDir, ref si, out var pi))
                {
                    _logger.LogError("CreateProcessW failed for control session (error: {Error})",
                        Marshal.GetLastWin32Error());
                    return null;
                }

                session.ProcessId = pi.dwProcessId;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                AssignTrackedWindowsCopilotProcess("control session", pi.dwProcessId, tracked =>
                {
                    session.ProcessId = tracked.ProcessId;
                    session.ProcessStartTime = tracked.ProcessStartTime;
                });
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                };

                var process = Process.Start(psi);
                if (process is null)
                {
                    _logger.LogError("Failed to start copilot process for control session");
                    return null;
                }

                session.ProcessId = process.Id;
                session.ProcessStartTime = GetProcessStartTime(process);
                process.Dispose();
            }

            session.Status = ControlSessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Control session launched: PID={Pid}", session.ProcessId);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception launching control session");
            return null;
        }
    }

    /// <summary>
    /// Gracefully terminates the control session process.
    /// </summary>
    public bool TerminateControlSession(ControlSessionInfo session)
        => TerminateProcess("control session", session.ProcessId, session.ProcessStartTime);

    private static string BuildControlSessionArguments(string sessionId, string prompt, string? defaultModel, RuntimeContext runtimeContext)
    {
        var args = new List<string>
        {
            "--remote",
            $"--resume={sessionId}",
            "-i", $"\"{EscapeArg(prompt)}\"",
        };

        // Only allow the commands needed to manage the daemon remotely — no general shell access.
        foreach (var command in runtimeContext.GetControlSessionAllowedShellCommands().Distinct(StringComparer.OrdinalIgnoreCase))
            args.Add($"--allow-tool=shell({command}:*)");

        if (!string.IsNullOrWhiteSpace(defaultModel))
        {
            args.Add("--model");
            args.Add($"\"{EscapeArg(defaultModel)}\"");
        }

        foreach (var extraDir in runtimeContext.GetExtraAllowedDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--add-dir");
            args.Add($"\"{EscapeArg(extraDir)}\"");
        }

        return string.Join(' ', args);
    }

    /// <summary>
    /// The copilotd repo to clone for the control session's working directory.
    /// </summary>
    private const string ControlSessionRepo = GitHubRemoteSessionUrlResolver.ControlSessionRepo;

    /// <summary>
    /// Dedicated subdirectory under the control-session repo clone used as the cwd for the
    /// remote session, avoiding repo-root wrapper scripts like copilotd.ps1.
    /// </summary>
    private const string ControlSessionWorkingDirectoryName = ".control-session";

    /// <summary>
    /// Ensures the control session repo exists locally and returns a dedicated working
    /// directory within that repo for launching the remote session.
    /// </summary>
    private string? EnsureControlSessionWorkingDirectory(CopilotdConfig config)
    {
        var repoPath = EnsureControlSessionRepo(config);
        if (repoPath is null)
            return null;

        var workingDir = Path.Combine(repoPath, ControlSessionWorkingDirectoryName);

        try
        {
            Directory.CreateDirectory(workingDir);
            return workingDir;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating control session working directory at {Path}", workingDir);
            return null;
        }
    }

    /// <summary>
    /// Ensures the copilotd repo is cloned to <c>&lt;RepoHome&gt;/DamianEdwards/copilotd</c>.
    /// Clones it if not already present.
    /// </summary>
    private string? EnsureControlSessionRepo(CopilotdConfig config)
    {
        if (string.IsNullOrEmpty(config.RepoHome))
        {
            _logger.LogError("RepoHome is not configured; cannot set up control session repo");
            return null;
        }

        var repoPath = Path.Combine(config.RepoHome, "DamianEdwards", "copilotd");

        if (Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            _logger.LogDebug("Control session repo already exists at {Path}", repoPath);
            return repoPath;
        }

        _logger.LogInformation("Cloning {Repo} for control session...", ControlSessionRepo);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(repoPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"repo clone {ControlSessionRepo} \"{repoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("Failed to start gh repo clone for control session");
                return null;
            }

            process.WaitForExit(TimeSpan.FromSeconds(60));

            if (!process.HasExited)
            {
                _logger.LogWarning("gh repo clone timed out for control session");
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                _logger.LogError("gh repo clone failed (exit {Code}): {Stderr}", process.ExitCode, stderr);
                return null;
            }

            _logger.LogInformation("Cloned {Repo} to {Path}", ControlSessionRepo, repoPath);
            return repoPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception cloning {Repo} for control session", ControlSessionRepo);
            return null;
        }
    }

    private static string BuildPrompt(string globalCustomPrompt, GitHubIssue issue, DispatchSession session, CopilotdConfig config, string copilotdCommand)
    {
        var prompt = session.RedispatchCount > 0
            ? session.PullRequestNumber is not null && !session.LastRedispatchWasIssueComment
                ? BuildPrReviewPrompt(issue, session)
                : CopilotdConfig.IssueFeedbackPrompt
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
        return ExpandTemplate(prompt, issue, session, copilotdCommand, config.CurrentUser);
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
            - Then run `$(copilotd.command) session pr $(pr.id) $(issue.repo)#$(issue.id)` to continue monitoring for further review feedback.
            - If the changes are complete and no more reviews are expected, run `$(copilotd.command) session complete $(issue.repo)#$(issue.id)` instead.

            Interacting with the PR:
            - To post a general comment on the PR: `gh pr comment $(pr.id) --repo $(issue.repo) --body "Your comment"`
            - To reply to a specific review thread, use `gh api graphql` with the addPullRequestReviewThreadReply mutation:
              ```
              gh api graphql -f query='mutation { addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: "THREAD_ID", body: "Your reply" }) { comment { id } } }'
              ```
              You can find thread IDs by querying: `gh api graphql -f query='{ repository(owner: "OWNER", name: "REPO") { pullRequest(number: $(pr.id)) { reviewThreads(last: 20) { nodes { id isResolved comments(last: 5) { nodes { body author { login } } } } } } } }'`
            - Do NOT use `$(copilotd.command) session comment` to post to the issue when in PR review mode. All communication should happen on the PR itself.
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

    private string? TryBuildSessionName(GitHubIssue issue, DispatchSession session, CopilotdConfig config, string copilotdCommand)
    {
        if (string.IsNullOrWhiteSpace(config.SessionNameFormat))
            return null;

        if (!_copilotCli.TryGetSemanticVersion(out var installedVersion, out var versionDisplay))
        {
            if (string.IsNullOrWhiteSpace(versionDisplay))
            {
                _logger.LogWarning(
                    "Could not determine the installed copilot CLI version; skipping session name for {IssueKey}",
                    session.IssueKey);
            }
            else
            {
                _logger.LogWarning(
                    "Could not parse copilot CLI version '{VersionDisplay}'; skipping session name for {IssueKey}",
                    versionDisplay,
                    session.IssueKey);
            }

            return null;
        }

        if (installedVersion < MinimumNamedSessionVersion)
        {
            _logger.LogWarning(
                "copilot CLI {VersionDisplay} is older than {MinimumVersion}; skipping session name for {IssueKey}",
                versionDisplay,
                MinimumNamedSessionVersion,
                session.IssueKey);
            return null;
        }

        try
        {
            var sessionName = ExpandTemplate(config.SessionNameFormat, issue, session, copilotdCommand, config.CurrentUser).Trim();
            if (string.IsNullOrWhiteSpace(sessionName))
            {
                _logger.LogWarning(
                    "session_name_format resolved to an empty session name for {IssueKey}; skipping --name",
                    session.IssueKey);
                return null;
            }

            return sessionName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build session name for {IssueKey}; continuing without --name", session.IssueKey);
            return null;
        }
    }

    private static string ExpandTemplate(string template, GitHubIssue issue, DispatchSession session, string copilotdCommand, string? ghUser)
    {
        var (org, repo) = SplitRepoSlug(issue.Repo);

        return template
            .Replace("$(copilotd.command)", copilotdCommand, StringComparison.Ordinal)
            .Replace("$(issue.repo)", issue.Repo, StringComparison.Ordinal)
            .Replace("$(issue.id)", issue.Number.ToString(), StringComparison.Ordinal)
            .Replace("$(issue.type)", issue.Type ?? "issue", StringComparison.Ordinal)
            .Replace("$(issue.milestone)", issue.Milestone ?? "none", StringComparison.Ordinal)
            .Replace("$(pr.id)", session.PullRequestNumber?.ToString() ?? "", StringComparison.Ordinal)
            .Replace("$(org)", org, StringComparison.Ordinal)
            .Replace("$(repo)", repo, StringComparison.Ordinal)
            .Replace("$(issue_id)", issue.Number.ToString(), StringComparison.Ordinal)
            .Replace("$(session_id)", session.CopilotSessionId, StringComparison.Ordinal)
            .Replace("$(machine_name)", Environment.MachineName, StringComparison.Ordinal)
            .Replace("$(gh_user)", ghUser ?? "", StringComparison.Ordinal);
    }

    private static (string Org, string Repo) SplitRepoSlug(string repoSlug)
    {
        if (string.IsNullOrWhiteSpace(repoSlug))
            return ("", "");

        var separatorIndex = repoSlug.IndexOf('/');
        if (separatorIndex < 0)
            return ("", repoSlug);

        return (repoSlug[..separatorIndex], repoSlug[(separatorIndex + 1)..]);
    }

    private static string BuildArguments(DispatchSession session, string prompt, string? sessionName, DispatchRule? rule, string repoPath, string? defaultModel, IEnumerable<string> extraAllowedDirectories)
    {
        var args = new List<string>
        {
            "--remote",
        };

        string? resumeTarget = null;
        if (HasExistingResumeContext(session))
        {
            resumeTarget = !string.IsNullOrWhiteSpace(session.CopilotSessionName)
                ? session.CopilotSessionName
                : session.CopilotSessionId;
        }
        else if (string.IsNullOrWhiteSpace(sessionName))
        {
            resumeTarget = session.CopilotSessionId;
        }

        if (!string.IsNullOrWhiteSpace(resumeTarget))
        {
            args.Add($"--resume=\"{EscapeArg(resumeTarget)}\"");
        }
        else if (!string.IsNullOrWhiteSpace(sessionName))
        {
            args.Add("--name");
            args.Add($"\"{EscapeArg(sessionName)}\"");
        }

        args.Add("-i");
        args.Add($"\"{EscapeArg(prompt)}\"");

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

        var normalizedRepoPath = Path.GetFullPath(repoPath);
        foreach (var extraDir in extraAllowedDirectories
                     .Where(dir => !string.Equals(Path.GetFullPath(dir), normalizedRepoPath, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--add-dir");
            args.Add($"\"{EscapeArg(extraDir)}\"");
        }

        return string.Join(' ', args);
    }

    private static bool HasExistingResumeContext(DispatchSession session)
        => session.HasStarted
            || session.ProcessId is not null
            || session.ProcessStartTime is not null
            || session.LastVerifiedAt is not null
            || session.WaitingSince is not null
            || session.PullRequestNumber is not null
            || session.RedispatchCount > 0
            || session.CompletedBySession;

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

    private void AssignTrackedWindowsCopilotProcess(
        string processLabel,
        int rootPid,
        Action<(int ProcessId, DateTimeOffset ProcessStartTime)> assign)
    {
        var tracked = TryResolveTrackedWindowsCopilotProcess(rootPid);
        if (tracked is { } trackedProcess)
        {
            assign(trackedProcess);

            if (trackedProcess.ProcessId != rootPid)
            {
                _logger.LogDebug(
                    "Tracking Windows child copilot PID {TrackedPid} instead of bootstrap PID {RootPid} for {ProcessLabel}",
                    trackedProcess.ProcessId,
                    rootPid,
                    processLabel);
            }

            return;
        }

        try
        {
            using var proc = Process.GetProcessById(rootPid);
            assign((rootPid, GetProcessStartTime(proc) ?? DateTimeOffset.UtcNow));
        }
        catch
        {
            assign((rootPid, DateTimeOffset.UtcNow));
        }
    }

    private static (int ProcessId, DateTimeOffset ProcessStartTime)? TryResolveTrackedWindowsCopilotProcess(int rootPid)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var deadline = DateTime.UtcNow + WindowsCopilotChildDiscoveryTimeout;

        while (true)
        {
            var trackedPid = FindDeepestWindowsCopilotDescendant(rootPid) ?? rootPid;

            try
            {
                using var process = Process.GetProcessById(trackedPid);
                var startTime = GetProcessStartTime(process) ?? DateTimeOffset.UtcNow;
                if (trackedPid != rootPid || DateTime.UtcNow >= deadline)
                    return (trackedPid, startTime);
            }
            catch
            {
                if (DateTime.UtcNow >= deadline)
                    return null;
            }

            if (DateTime.UtcNow >= deadline)
                return null;

            Thread.Sleep(WindowsCopilotChildDiscoveryPollInterval);
        }
    }

    private static int? FindDeepestWindowsCopilotDescendant(int rootPid)
        => NativeInterop.FindDeepestWindowsDescendantProcessId(rootPid, "copilot.exe");

    // ---- Worktree lifecycle ----

    /// <summary>
    /// Creates a git worktree for the session on a new branch from the latest default branch.
    /// Layout: &lt;repo_home&gt;/org/repo_sessions/issue-N/
    /// If the session already has a worktree (e.g., re-dispatching for PR review), refreshes it instead.
    /// Returns <see cref="WorktreeResult.CreatedNew"/> for new worktrees,
    /// <see cref="WorktreeResult.Refreshed"/> for existing ones, or
    /// <see cref="WorktreeResult.Failed"/> on error.
    /// </summary>
    public WorktreeResult PrepareWorktree(DispatchSession session, CopilotdConfig config, DaemonState state)
    {
        // If the session already has a worktree (PR review re-dispatch), refresh it
        if (!string.IsNullOrEmpty(session.WorktreePath) && Directory.Exists(session.WorktreePath))
        {
            return RefreshWorktree(session) ? WorktreeResult.Refreshed : WorktreeResult.Failed;
        }
        var mainRepoPath = _repoResolver.ResolveRepoPath(session.Repo, config, state);
        if (mainRepoPath is null || !Directory.Exists(mainRepoPath))
        {
            _logger.LogWarning("Main repo directory not found for {Repo}", session.Repo);
            return WorktreeResult.Failed;
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
            return WorktreeResult.Failed;
        }

        // Determine default branch (origin/HEAD → origin/main or origin/master)
        var defaultBranch = GetDefaultBranch(mainRepoPath);
        if (defaultBranch is null)
        {
            _logger.LogWarning("Could not determine default branch for {Repo}", session.Repo);
            return WorktreeResult.Failed;
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
            return WorktreeResult.Failed;
        }

        session.WorktreePath = worktreePath;
        _logger.LogInformation("Worktree ready for {Key} at {Path}", session.IssueKey, worktreePath);
        return WorktreeResult.CreatedNew;
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

    private (bool Success, string Output) RunGitWithOutput(string workingDir, string arguments)
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
                return (false, "");
            }

            // Read both streams asynchronously to avoid deadlock when pipe buffers fill
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("git {Arguments} timed out after 30 seconds", arguments);
                process.Kill();
                return (false, "git command timed out");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("git {Arguments} failed (exit {ExitCode}): {StdErr}",
                    arguments, process.ExitCode, stderr.Trim());
                return (false, stderr);
            }

            return (true, stdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception running: git {Arguments}", arguments);
            return (false, "");
        }
    }

    private bool RunGitCheck(string workingDir, string arguments)
    {
        return RunGit(workingDir, arguments);
    }

    /// <summary>
    /// Pushes the session branch to the remote and sets up upstream tracking.
    /// Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public bool PushBranch(DispatchSession session, CopilotdConfig config, DaemonState state)
    {
        if (string.IsNullOrEmpty(session.BranchName))
        {
            _logger.LogWarning("Cannot push branch for {Key}: no branch name set", session.IssueKey);
            return false;
        }

        // Push from the worktree directory (shares the same git repo as the main checkout)
        var workDir = session.WorktreePath;
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            // Fall back to the main repo path
            workDir = _repoResolver.ResolveRepoPath(session.Repo, config, state);
            if (workDir is null || !Directory.Exists(workDir))
            {
                _logger.LogWarning("Cannot push branch for {Key}: no working directory found", session.IssueKey);
                return false;
            }
        }

        _logger.LogInformation("Pushing branch {Branch} to origin for {Key}", session.BranchName, session.IssueKey);
        return RunGit(workDir, $"push -u origin {session.BranchName}");
    }

    /// <summary>
    /// Gets the HEAD commit SHA from the session's worktree directory.
    /// Returns null if the worktree is unavailable or the command fails.
    /// </summary>
    public string? GetHeadSha(DispatchSession session)
    {
        if (string.IsNullOrEmpty(session.WorktreePath) || !Directory.Exists(session.WorktreePath))
            return null;

        var (success, output) = RunGitWithOutput(session.WorktreePath, "rev-parse HEAD");
        return success ? output.Trim() : null;
    }
}

public enum WorktreeResult
{
    Failed,
    CreatedNew,
    Refreshed,
}

public enum ProcessLivenessResult
{
    Alive,
    Dead,
    PidReused,
}
