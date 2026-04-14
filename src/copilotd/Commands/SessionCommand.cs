using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class SessionCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("session", "Manage dispatched copilot sessions");

        command.Subcommands.Add(CreateListCommand(services));
        command.Subcommands.Add(CreateJoinCommand(services));
        command.Subcommands.Add(CreateCommentCommand(services));
        command.Subcommands.Add(CreateCompleteCommand(services));
        command.Subcommands.Add(CreatePrCommand(services));
        command.Subcommands.Add(CreateResetCommand(services));

        // Default to list behavior when no subcommand is specified
        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter sessions by status (pending, dispatching, running, joined, completed, failed, orphaned)"
        };
        command.Options.Add(filterOption);

        var allOption = new Option<bool>("--all")
        {
            Description = "Include ended (completed/failed) sessions"
        };
        command.Options.Add(allOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                new HelpAction().Invoke(parseResult);
                Console.WriteLine();

                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var filterValue = parseResult.GetValue(filterOption);
                var showAll = parseResult.GetValue(allOption);

                return RenderSessionList(stateStore, processManager, filterValue, showAll);
            }, logger);
        });

        return command;
    }

    // ---- list subcommand ----

    private static Command CreateListCommand(IServiceProvider services)
    {
        var command = new Command("list", "List dispatched copilot sessions");

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter sessions by status (pending, dispatching, running, joined, completed, failed, orphaned)"
        };
        command.Options.Add(filterOption);

        var allOption = new Option<bool>("--all")
        {
            Description = "Include ended (completed/failed) sessions"
        };
        command.Options.Add(allOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var filterValue = parseResult.GetValue(filterOption);
                var showAll = parseResult.GetValue(allOption);

                return RenderSessionList(stateStore, processManager, filterValue, showAll);
            }, logger);
        });

        return command;
    }

    // ---- join subcommand ----

    private static Command CreateJoinCommand(IServiceProvider services)
    {
        var command = new Command("join", "Take over a copilot session interactively");

        var issueArg = new Argument<string>("issue") { Description = "Issue key to join (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();
                var config = stateStore.LoadConfig();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();

                var issueKey = parseResult.GetValue(issueArg)!;
                string? sessionId = null;
                string? worktreePath = null;
                string? workingDir = null;
                var priorProcess = default(TrackedProcessRef);
                var priorStatus = SessionStatus.Pending;
                string? errorMessage = null;

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();

                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"No session found for '{issueKey}'.";
                        return;
                    }

                    if (string.IsNullOrEmpty(session.CopilotSessionId))
                    {
                        errorMessage = $"Session for '{issueKey}' has no copilot session ID.";
                        return;
                    }

                    workingDir = session.WorktreePath ?? repoResolver.ResolveRepoPath(session.Repo, config, state);
                    if (workingDir is null || !Directory.Exists(workingDir))
                    {
                        errorMessage = $"Working directory not found for {session.Repo}. Ensure the repository is cloned under the configured RepoHome directory.";
                        return;
                    }

                    sessionId = session.CopilotSessionId;
                    worktreePath = session.WorktreePath;
                    priorStatus = session.Status;

                    if (session.Status is SessionStatus.Running or SessionStatus.Dispatching or SessionStatus.Joined)
                        priorProcess = CaptureTrackedProcess(session);

                    session.Status = SessionStatus.Joined;
                    ClearTrackedProcess(session);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    if (errorMessage.StartsWith("No session found", StringComparison.Ordinal))
                        ConsoleOutput.Info("Use 'copilotd session list --all' to see all tracked sessions.");
                    return 1;
                }

                if (priorProcess.HasProcess)
                {
                    var message = priorStatus is SessionStatus.Joined
                        ? $"Stopping previously joined interactive session for {issueKey} (PID {priorProcess.ProcessId})..."
                        : $"Stopping orchestrated session for {issueKey} (PID {priorProcess.ProcessId})...";
                    ConsoleOutput.Info(message);
                    processManager.TerminateProcess(priorProcess.Label, priorProcess.ProcessId, priorProcess.ProcessStartTime);
                }

                ConsoleOutput.Success($"Joining session {sessionId} for {issueKey}");
                if (worktreePath is not null)
                    ConsoleOutput.Info($"Working directory: {worktreePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)}");
                ConsoleOutput.Info("Press Ctrl+C to exit the interactive session.");
                Console.WriteLine();

                // Launch copilot interactively — inherit terminal stdin/stdout/stderr
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = $"--resume={sessionId}",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };

                Process? interactiveProcess = null;
                try
                {
                    interactiveProcess = Process.Start(psi);
                    if (interactiveProcess is null)
                    {
                        ConsoleOutput.Error("Failed to launch copilot.");
                        RequeueSession(stateStore, issueKey);
                        return 1;
                    }

                    // Track the interactive process PID so the daemon can detect
                    // if it exits (e.g., terminal killed without cleanup)
                    stateStore.WithStateLock(() =>
                    {
                        var state = stateStore.LoadState();
                        if (state.Sessions.TryGetValue(issueKey, out var tracked))
                        {
                            tracked.ProcessId = interactiveProcess.Id;
                            try { tracked.ProcessStartTime = new DateTimeOffset(interactiveProcess.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                            catch { tracked.ProcessStartTime = DateTimeOffset.UtcNow; }
                            stateStore.SaveState(state);
                        }
                    }, ct);

                    await interactiveProcess.WaitForExitAsync(ct);
                    var exitCode = interactiveProcess.ExitCode;

                    Console.WriteLine();
                    ConsoleOutput.Info($"Interactive session exited (code {exitCode}).");
                }
                finally
                {
                    if (interactiveProcess is not null)
                    {
                        if (!interactiveProcess.HasExited)
                        {
                            try { interactiveProcess.Kill(entireProcessTree: true); } catch { }
                        }
                        interactiveProcess.Dispose();
                    }

                    // Always re-queue, even on cancellation or crash
                    RequeueSession(stateStore, issueKey);
                }

                return 0;
            }, logger);
        });

        return command;
    }

    // ---- comment subcommand ----

    private static Command CreateCommentCommand(IServiceProvider services)
    {
        var command = new Command("comment", "Post a comment on the issue and wait for feedback (can be called from within a copilot session)");

        var issueArg = new Argument<string>("issue") { Description = "Issue key (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        var messageOption = new Option<string>("--message")
        {
            Description = "The comment message to post on the issue",
            Required = true,
        };
        command.Options.Add(messageOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var ghCli = services.GetRequiredService<GhCliService>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var issueKey = parseResult.GetValue(issueArg)!;
                var message = parseResult.GetValue(messageOption)!;
                var trackedProcess = default(TrackedProcessRef);
                string? repo = null;
                var issueNumber = 0;
                string? expectedSessionId = null;
                string? errorMessage = null;

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();
                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"No session found for '{issueKey}'.";
                        return;
                    }

                    if (session.IsTerminal)
                    {
                        errorMessage = $"Session for '{issueKey}' is already {session.Status.ToString().ToLowerInvariant()}.";
                        return;
                    }
                    repo = session.Repo;
                    issueNumber = session.IssueNumber;
                    expectedSessionId = session.CopilotSessionId;
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    return 1;
                }

                if (!ghCli.PostIssueComment(repo!, issueNumber, message))
                {
                    ConsoleOutput.Error($"Failed to post comment on {issueKey}.");
                    return 1;
                }

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();
                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"Comment posted on {issueKey}, but the session is no longer tracked.";
                        return;
                    }

                    if (session.CopilotSessionId != expectedSessionId)
                    {
                        errorMessage = $"Comment posted on {issueKey}, but the session was reset before the wait state could be recorded.";
                        return;
                    }

                    if (session.IsTerminal)
                    {
                        errorMessage = $"Comment posted on {issueKey}, but the session is already {session.Status.ToString().ToLowerInvariant()}.";
                        return;
                    }

                    trackedProcess = CaptureTrackedProcess(session);
                    session.Status = SessionStatus.WaitingForFeedback;
                    session.WaitingSince = DateTimeOffset.UtcNow;
                    ClearTrackedProcess(session);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    return 1;
                }

                processManager.TerminateProcess(trackedProcess.Label, trackedProcess.ProcessId, trackedProcess.ProcessStartTime);

                ConsoleOutput.Success($"Comment posted on {issueKey}. Session is now waiting for feedback.");
                return 0;
            }, logger);
        });

        return command;
    }

    // ---- complete subcommand ----

    private static Command CreateCompleteCommand(IServiceProvider services)
    {
        var command = new Command("complete", "Mark a session as completed (can be called from within a copilot session)");

        var issueArg = new Argument<string>("issue") { Description = "Issue key to mark complete (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var issueKey = parseResult.GetValue(issueArg)!;
                var trackedProcess = default(TrackedProcessRef);
                string? terminalStatus = null;
                string? errorMessage = null;

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();
                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"No session found for '{issueKey}'.";
                        return;
                    }

                    if (session.IsTerminal)
                    {
                        terminalStatus = session.Status.ToString().ToLowerInvariant();
                        return;
                    }

                    trackedProcess = CaptureTrackedProcess(session);
                    session.Status = SessionStatus.Completed;
                    session.CompletedBySession = true;
                    ClearTrackedProcess(session);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    return 1;
                }

                if (terminalStatus is not null)
                {
                    ConsoleOutput.Info($"Session for '{issueKey}' is already {terminalStatus}.");
                    return 0;
                }

                processManager.TerminateProcess(trackedProcess.Label, trackedProcess.ProcessId, trackedProcess.ProcessStartTime);

                ConsoleOutput.Success($"Session for {issueKey} marked as completed.");
                return 0;
            }, logger);
        });

        return command;
    }

    // ---- pr subcommand ----

    private static Command CreatePrCommand(IServiceProvider services)
    {
        var command = new Command("pr", "Associate a pull request with a session and wait for review feedback (can be called from within a copilot session)");

        var prNumberArg = new Argument<int>("pr-number") { Description = "Pull request number" };
        command.Arguments.Add(prNumberArg);

        var issueArg = new Argument<string>("issue") { Description = "Issue key (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var prNumber = parseResult.GetValue(prNumberArg);
                var issueKey = parseResult.GetValue(issueArg)!;

                if (prNumber <= 0)
                {
                    ConsoleOutput.Error("Pull request number must be a positive integer.");
                    return 1;
                }

                var trackedProcess = default(TrackedProcessRef);
                string? errorMessage = null;

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();
                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"No session found for '{issueKey}'.";
                        return;
                    }

                    if (session.IsTerminal)
                    {
                        errorMessage = $"Session for '{issueKey}' is already {session.Status.ToString().ToLowerInvariant()}.";
                        return;
                    }

                    trackedProcess = CaptureTrackedProcess(session);
                    session.PullRequestNumber = prNumber;
                    session.Status = SessionStatus.WaitingForReview;
                    session.WaitingSince = DateTimeOffset.UtcNow;
                    ClearTrackedProcess(session);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    return 1;
                }

                processManager.TerminateProcess(trackedProcess.Label, trackedProcess.ProcessId, trackedProcess.ProcessStartTime);

                ConsoleOutput.Success($"PR #{prNumber} associated with session for {issueKey}. Session is now waiting for review feedback.");
                return 0;
            }, logger);
        });

        return command;
    }

    // ---- reset subcommand ----

    private static Command CreateResetCommand(IServiceProvider services)
    {
        var command = new Command("reset", "Reset a completed session so it can be re-dispatched");

        var issueArg = new Argument<string>("issue") { Description = "Issue key to reset (e.g., owner/repo#123)" };
        command.Arguments.Add(issueArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();
                var config = stateStore.LoadConfig();

                var issueKey = parseResult.GetValue(issueArg)!;
                var pending = false;
                string? errorMessage = null;
                string? newSessionId = null;

                stateStore.WithStateLock(() =>
                {
                    var state = stateStore.LoadState();

                    if (!state.Sessions.TryGetValue(issueKey, out var session))
                    {
                        errorMessage = $"No session found for '{issueKey}'.";
                        return;
                    }

                    if (session.Status == SessionStatus.Pending)
                    {
                        pending = true;
                        return;
                    }

                    processManager.TerminateProcess(session.IssueKey, session.ProcessId, session.ProcessStartTime);
                    processManager.CleanupWorktree(session, config, state);

                    session.Status = SessionStatus.Pending;
                    session.CompletedBySession = false;
                    session.PullRequestNumber = null;
                    session.CopilotSessionId = Guid.NewGuid().ToString();
                    ClearTrackedProcess(session);
                    session.RetryCount = 0;
                    session.RedispatchCount = 0;
                    session.LastFailureAt = null;
                    session.WaitingSince = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    newSessionId = session.CopilotSessionId;
                    stateStore.SaveState(state);
                }, ct);

                if (errorMessage is not null)
                {
                    ConsoleOutput.Error(errorMessage);
                    return 1;
                }

                if (pending)
                {
                    ConsoleOutput.Info($"Session for '{issueKey}' is already pending.");
                    return 0;
                }

                ConsoleOutput.Success($"Session for {issueKey} reset to pending (new session {newSessionId}).");
                return 0;
            }, logger);
        });

        return command;
    }

    // ---- shared rendering ----

    /// <summary>
    /// Renders the session list table. Used by both 'copilotd session list' and 'copilotd status'.
    /// Returns the exit code.
    /// </summary>
    public static int RenderSessionList(StateStore stateStore, ProcessManager processManager,
        string? filterValue, bool showAll)
    {
        var stateChanged = false;
        var state = stateStore.WithStateLock(() =>
        {
            var currentState = stateStore.LoadState();

            // Recover stale Joined sessions — no PID or process dead
            foreach (var (key, s) in currentState.Sessions)
            {
                if (s.Status != SessionStatus.Joined)
                    continue;

                var isStale = s.ProcessId is null;
                if (!isStale)
                {
                    var liveness = processManager.CheckProcess(s);
                    isStale = liveness is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused;
                }

                if (!isStale)
                    continue;

                s.Status = SessionStatus.Pending;
                ClearTrackedProcess(s);
                s.UpdatedAt = DateTimeOffset.UtcNow;
                stateChanged = true;
                ConsoleOutput.Warning($"Recovered stale joined session {key} → Pending");
            }

            if (stateChanged)
                stateStore.SaveState(currentState);

            return currentState;
        });

        if (stateChanged)
            AnsiConsole.WriteLine();

        // Filter sessions
        SessionStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(filterValue))
        {
            if (!Enum.TryParse<SessionStatus>(filterValue, ignoreCase: true, out var parsed))
            {
                var validValues = string.Join(", ", Enum.GetNames<SessionStatus>().Select(n => n.ToLowerInvariant()));
                ConsoleOutput.Error($"Unknown status: {filterValue}");
                ConsoleOutput.Info($"Valid values: {validValues}");
                return 1;
            }
            statusFilter = parsed;
        }

        var sessions = state.Sessions.Values.AsEnumerable();

        if (statusFilter.HasValue)
        {
            sessions = sessions.Where(s => s.Status == statusFilter.Value);
        }
        else if (!showAll)
        {
            sessions = sessions.Where(s => !s.IsTerminal);
        }

        var list = sessions.OrderBy(s => s.CreatedAt).ToList();

        if (list.Count == 0)
        {
            var qualifier = statusFilter.HasValue
                ? $" with status '{statusFilter.Value.ToString().ToLowerInvariant()}'"
                : showAll ? "" : " (use --all to include ended sessions)";
            ConsoleOutput.Info($"No sessions found{qualifier}.");
            return 0;
        }

        RenderSessionTable(list);

        ConsoleOutput.Info($"{list.Count} session(s)");
        AnsiConsole.WriteLine();
        ConsoleOutput.Info("Use 'copilotd session join <issue>' to take over a session interactively.");

        return 0;
    }

    /// <summary>
    /// Renders the session table. Used by RenderSessionList and StatusCommand.
    /// </summary>
    public static void RenderSessionTable(List<DispatchSession> sessions)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Issue");
        table.AddColumn("Rule");
        table.AddColumn("Status");
        table.AddColumn("PID");
        table.AddColumn("Session ID");
        table.AddColumn("Worktree");
        table.AddColumn("Created");
        table.AddColumn("Updated");

        foreach (var s in sessions)
        {
            var statusMarkup = s.Status switch
            {
                SessionStatus.Running => $"[green]{s.Status}[/]",
                SessionStatus.Joined => $"[blue]{s.Status}[/]",
                SessionStatus.WaitingForFeedback => $"[cyan]{s.Status}[/]",
                SessionStatus.WaitingForReview => $"[magenta]{s.Status}[/]",
                SessionStatus.Pending or SessionStatus.Dispatching => $"[yellow]{s.Status}[/]",
                SessionStatus.Failed or SessionStatus.Orphaned => $"[red]{s.Status}[/]",
                SessionStatus.Completed => $"[grey]{s.Status}[/]",
                _ => s.Status.ToString()
            };

            var pid = s.ProcessId?.ToString() ?? "-";
            var sessionId = string.IsNullOrEmpty(s.CopilotSessionId)
                ? "-"
                : s.CopilotSessionId;
            var worktree = string.IsNullOrEmpty(s.WorktreePath)
                ? "-"
                : s.WorktreePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            table.AddRow(
                Markup.Escape(s.IssueKey),
                Markup.Escape(s.RuleName),
                statusMarkup,
                Markup.Escape(pid),
                Markup.Escape(sessionId),
                Markup.Escape(worktree),
                FormatTime(s.CreatedAt),
                FormatTime(s.UpdatedAt)
            );
        }

        AnsiConsole.Write(table);
    }

    public static string FormatTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h {age.Minutes}m ago";
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private static void RequeueSession(StateStore stateStore, string issueKey)
    {
        try
        {
            stateStore.WithStateLock(() =>
            {
                var state = stateStore.LoadState();
                if (state.Sessions.TryGetValue(issueKey, out var session) &&
                    session.Status == SessionStatus.Joined)
                {
                    session.Status = SessionStatus.Pending;
                    ClearTrackedProcess(session);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    stateStore.SaveState(state);
                    ConsoleOutput.Info("Session re-queued for orchestrated dispatch.");
                }
            });
        }
        catch
        {
            // Best effort — if state save fails, the daemon's safety net
            // will detect the stale Joined session and reset it
        }
    }

    private static TrackedProcessRef CaptureTrackedProcess(DispatchSession session)
        => new(session.IssueKey, session.ProcessId, session.ProcessStartTime);

    private static void ClearTrackedProcess(DispatchSession session)
    {
        session.ProcessId = null;
        session.ProcessStartTime = null;
    }

    private readonly record struct TrackedProcessRef(string Label, int? ProcessId, DateTimeOffset? ProcessStartTime)
    {
        public bool HasProcess => ProcessId is not null;
    }
}
