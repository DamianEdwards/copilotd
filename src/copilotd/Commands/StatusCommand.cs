using System.CommandLine;
using System.CommandLine.Parsing;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class StatusCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("status", "Show daemon status and tracked copilot sessions");

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
                var remoteSessionUrls = services.GetRequiredService<GitHubRemoteSessionUrlResolver>();

                // Daemon status header
                var daemonRunning = stateStore.IsLockHeld();
                var state = stateStore.LoadState();
                var config = stateStore.LoadConfig();

                AnsiConsole.MarkupLine(daemonRunning
                    ? "[green]● Daemon is running[/]"
                    : "[grey]○ Daemon is not running[/]");

                if (state.LastPollTime.HasValue)
                {
                    ConsoleOutput.Info($"  Last poll: {SessionCommand.FormatTime(state.LastPollTime.Value)}");
                }

                var watchedRepos = config.Rules.Values
                    .SelectMany(r => r.Repos)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (watchedRepos.Count > 0)
                {
                    ConsoleOutput.Info($"  Watching:  {string.Join(", ", watchedRepos)}");
                }

                ConsoleOutput.Info($"  Rules:     {config.Rules.Count}");

                // Control session status
                if (state.ControlSession is not null)
                {
                    var controlLiveness = state.ControlSession.Status == ControlSessionStatus.Running
                        ? processManager.CheckControlSession(state.ControlSession)
                        : ProcessLivenessResult.Dead;

                    var controlStatus = state.ControlSession.Status switch
                    {
                        ControlSessionStatus.Running => "[green]● Running[/]",
                        ControlSessionStatus.Starting => "[yellow]◐ Starting[/]",
                        ControlSessionStatus.Failed => "[red]✕ Failed[/]",
                        _ => "[grey]○ Stopped[/]",
                    };
                    var controlPid = state.ControlSession.ProcessId is { } pid ? $" (PID {pid})" : "";
                    AnsiConsole.MarkupLine($"  Control:   {controlStatus}{Markup.Escape(controlPid)}");

                    if (controlLiveness == ProcessLivenessResult.Alive)
                    {
                        var controlUrl = remoteSessionUrls.TryResolve(state.ControlSession, config.CurrentUser)
                            ?? "unavailable";
                        ConsoleOutput.Info("  Remote:");
                        ConsoleOutput.Info($"    {controlUrl}");
                    }
                }
                else if (config.EnableControlSession)
                {
                    AnsiConsole.MarkupLine("  Control:   [grey]○ Not started[/]");
                }

                Console.WriteLine();

                // Delegate session list rendering to the shared helper
                var filterValue = parseResult.GetValue(filterOption);
                var showAll = parseResult.GetValue(allOption);

                return SessionCommand.RenderSessionList(stateStore, processManager, remoteSessionUrls, config, filterValue, showAll);
            }, logger);
        });

        return command;
    }
}
