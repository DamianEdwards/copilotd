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
                var state = stateStore.LoadState();
                var config = stateStore.LoadConfig();

                var issueKey = parseResult.GetValue(issueArg)!;

                if (!state.Sessions.TryGetValue(issueKey, out var session))
                {
                    ConsoleOutput.Error($"No session found for '{issueKey}'.");
                    ConsoleOutput.Info("Use 'copilotd session list --all' to see all tracked sessions.");
                    return 1;
                }

                if (string.IsNullOrEmpty(session.CopilotSessionId))
                {
                    ConsoleOutput.Error($"Session for '{issueKey}' has no copilot session ID.");
                    return 1;
                }

                // Use worktree path if available, otherwise resolve the main repo
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
                var workingDir = session.WorktreePath ?? repoResolver.ResolveRepoPath(session.Repo, config, state);
                if (workingDir is null || !Directory.Exists(workingDir))
                {
                    ConsoleOutput.Error($"Working directory not found for {session.Repo}. Ensure the repository is cloned under the configured RepoHome directory.");
                    return 1;
                }

                // If the session is currently running, terminate the orchestrated process first
                if (session.Status is SessionStatus.Running or SessionStatus.Dispatching)
                {
                    ConsoleOutput.Info($"Stopping orchestrated session for {issueKey} (PID {session.ProcessId})...");
                    processManager.TerminateProcess(session);
                }
                else if (session.Status is SessionStatus.Joined)
                {
                    ConsoleOutput.Info($"Session was previously joined but not cleaned up. Resuming...");
                }

                // Mark as Joined so the daemon doesn't interfere
                session.Status = SessionStatus.Joined;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);

                ConsoleOutput.Success($"Joining session {session.CopilotSessionId} for {issueKey}");
                if (session.WorktreePath is not null)
                    ConsoleOutput.Info($"Working directory: {session.WorktreePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)}");
                ConsoleOutput.Info("Press Ctrl+C to exit the interactive session.");
                Console.WriteLine();

                // Launch copilot interactively — inherit terminal stdin/stdout/stderr
                var psi = new ProcessStartInfo
                {
                    FileName = "copilot",
                    Arguments = $"--resume={session.CopilotSessionId}",
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
                    state = stateStore.LoadState();
                    if (state.Sessions.TryGetValue(issueKey, out var tracked))
                    {
                        tracked.ProcessId = interactiveProcess.Id;
                        try { tracked.ProcessStartTime = new DateTimeOffset(interactiveProcess.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                        catch { tracked.ProcessStartTime = DateTimeOffset.UtcNow; }
                        stateStore.SaveState(state);
                    }

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
                var state = stateStore.LoadState();

                var issueKey = parseResult.GetValue(issueArg)!;
                var message = parseResult.GetValue(messageOption)!;

                if (!state.Sessions.TryGetValue(issueKey, out var session))
                {
                    ConsoleOutput.Error($"No session found for '{issueKey}'.");
                    return 1;
                }

                if (session.IsTerminal)
                {
                    ConsoleOutput.Error($"Session for '{issueKey}' is already {session.Status.ToString().ToLowerInvariant()}.");
                    return 1;
                }

                // Post the comment to the issue
                if (!ghCli.PostIssueComment(session.Repo, session.IssueNumber, message))
                {
                    ConsoleOutput.Error($"Failed to post comment on {issueKey}.");
                    return 1;
                }

                // Transition to WaitingForFeedback. The calling copilot process is
                // expected to exit on its own after this command returns. We clear the
                // PID so the reconciler doesn't try to verify a dead process.
                session.Status = SessionStatus.WaitingForFeedback;
                session.WaitingSince = DateTimeOffset.UtcNow;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);

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
                var state = stateStore.LoadState();

                var issueKey = parseResult.GetValue(issueArg)!;

                if (!state.Sessions.TryGetValue(issueKey, out var session))
                {
                    ConsoleOutput.Error($"No session found for '{issueKey}'.");
                    return 1;
                }

                if (session.IsTerminal)
                {
                    ConsoleOutput.Info($"Session for '{issueKey}' is already {session.Status.ToString().ToLowerInvariant()}.");
                    return 0;
                }

                session.Status = SessionStatus.Completed;
                session.CompletedBySession = true;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);

                ConsoleOutput.Success($"Session for {issueKey} marked as completed.");
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
                var state = stateStore.LoadState();
                var config = stateStore.LoadConfig();

                var issueKey = parseResult.GetValue(issueArg)!;

                if (!state.Sessions.TryGetValue(issueKey, out var session))
                {
                    ConsoleOutput.Error($"No session found for '{issueKey}'.");
                    return 1;
                }

                if (session.Status == SessionStatus.Pending)
                {
                    ConsoleOutput.Info($"Session for '{issueKey}' is already pending.");
                    return 0;
                }

                // Clean up old worktree before resetting
                processManager.CleanupWorktree(session, config, state);

                session.Status = SessionStatus.Pending;
                session.CompletedBySession = false;
                session.CopilotSessionId = Guid.NewGuid().ToString();
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.RetryCount = 0;
                session.LastFailureAt = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);

                ConsoleOutput.Success($"Session for {issueKey} reset to pending (new session {session.CopilotSessionId}).");
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
        var state = stateStore.LoadState();

        // Recover stale Joined sessions — no PID or process dead
        var stateChanged = false;
        foreach (var (key, s) in state.Sessions)
        {
            if (s.Status != SessionStatus.Joined)
                continue;

            var isStale = s.ProcessId is null;
            if (!isStale)
            {
                var liveness = processManager.CheckProcess(s);
                isStale = liveness is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused;
            }

            if (isStale)
            {
                s.Status = SessionStatus.Pending;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.UpdatedAt = DateTimeOffset.UtcNow;
                stateChanged = true;
                ConsoleOutput.Warning($"Recovered stale joined session {key} → Pending");
            }
        }
        if (stateChanged)
        {
            stateStore.SaveState(state);
            AnsiConsole.WriteLine();
        }

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
            var state = stateStore.LoadState();
            if (state.Sessions.TryGetValue(issueKey, out var session) &&
                session.Status == SessionStatus.Joined)
            {
                session.Status = SessionStatus.Pending;
                session.ProcessId = null;
                session.ProcessStartTime = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                stateStore.SaveState(state);
                ConsoleOutput.Info("Session re-queued for orchestrated dispatch.");
            }
        }
        catch
        {
            // Best effort — if state save fails, the daemon's safety net
            // will detect the stale Joined session and reset it
        }
    }
}
