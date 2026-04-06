using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd.Commands;

public static class RunCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("run", "Start the copilotd daemon");
        var intervalOption = new Option<int>("--interval") { Description = "Polling interval in seconds", DefaultValueFactory = _ => 60 };
        var logLevelOption = new Option<string?>("--log-level") { Description = "Set console logging level (default: info). Use 'debug' for more detail or 'error' for less." };

        command.Options.Add(intervalOption);
        command.Options.Add(logLevelOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var ghCli = services.GetRequiredService<GhCliService>();
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var stateStore = services.GetRequiredService<StateStore>();
                var reconciliation = services.GetRequiredService<ReconciliationEngine>();
                var processManager = services.GetRequiredService<ProcessManager>();

                var interval = parseResult.GetValue(intervalOption);

                // Pre-flight checks
                if (!ghCli.IsAvailable())
                {
                    ConsoleOutput.Error("gh CLI is not available. Install from: https://cli.github.com/");
                    return 1;
                }

                if (!copilotCli.IsAvailable())
                {
                    ConsoleOutput.Error("copilot CLI is not available. Install from: https://docs.github.com/copilot/how-tos/copilot-cli");
                    return 1;
                }

                var authResult = ghCli.CheckAuth();
                if (!authResult.IsLoggedIn)
                {
                    ConsoleOutput.Error("gh CLI is not authenticated. Run 'gh auth login' first.");
                    return 1;
                }

                if (!stateStore.ConfigExists())
                {
                    ConsoleOutput.Error("copilotd is not configured. Run 'copilotd init' first.");
                    return 1;
                }

                // Single-instance guard
                if (!stateStore.TryAcquireLock())
                {
                    ConsoleOutput.Error("Another instance of copilotd is already running.");
                    return 1;
                }

                try
                {
                    ConsoleOutput.Success($"copilotd daemon started (polling every {interval}s). Press Ctrl+C to stop.");

                    // Startup reconciliation pass
                    var config = stateStore.LoadConfig();
                    var state = stateStore.LoadState();
                    ConsoleOutput.Info("Running startup reconciliation...");
                    reconciliation.Reconcile(config, state);
                    ConsoleOutput.Success("Startup reconciliation complete.");

                    // Main poll loop
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                        ConsoleOutput.Warning("Shutdown requested, finishing current cycle...");
                    };

                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        try
                        {
                            // Reload config each cycle for live editing support
                            config = stateStore.LoadConfig();
                            state = stateStore.LoadState();

                            reconciliation.Reconcile(config, state);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error during poll cycle");
                            ConsoleOutput.Error($"Poll cycle error: {ex.Message}");
                            // Continue running — only catastrophic errors should exit
                        }
                    }

                    // Gracefully terminate running sessions before exit
                    state = stateStore.LoadState();
                    var runningSessions = state.Sessions.Values
                        .Where(s => s.Status == SessionStatus.Running)
                        .ToList();
                    if (runningSessions.Count > 0)
                    {
                        ConsoleOutput.Info($"Shutting down {runningSessions.Count} active copilot session(s)...");
                        foreach (var session in runningSessions)
                        {
                            processManager.TerminateProcess(session);
                            session.Status = SessionStatus.Completed;
                            session.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                        stateStore.SaveState(state);
                    }

                    ConsoleOutput.Info("copilotd daemon stopped.");
                    return 0;
                }
                finally
                {
                    stateStore.ReleaseLock();
                }
            }, logger);
        });

        return command;
    }
}
