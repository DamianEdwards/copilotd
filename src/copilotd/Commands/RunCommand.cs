using System.CommandLine;
using System.Diagnostics;
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
                var preflightResult = PreflightChecks.Run(ghCli, copilotCli, stateStore);
                if (preflightResult != 0)
                    return preflightResult;

                // Clean up old binary from previous update
                var updateService = services.GetRequiredService<UpdateService>();
                updateService.CleanupOldBinary();

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

                            // Self-update: check for staged update or fire background check
                            var updateState = stateStore.LoadUpdateState();
                            if (updateState.Status == UpdateStatus.Staged
                                && !string.IsNullOrEmpty(updateState.StagedPath)
                                && File.Exists(updateState.StagedPath))
                            {
                                // Staged update ready — spawn install process and exit daemon
                                ConsoleOutput.Info($"Staged update {updateState.StagedVersion} detected, initiating install...");
                                logger.LogInformation("Spawning update installer for staged version {Version}", updateState.StagedVersion);
                                if (SpawnUpdateInstaller(logger))
                                {
                                    cts.Cancel();
                                    break;
                                }
                                else
                                {
                                    ConsoleOutput.Warning("Failed to spawn update installer, will retry next cycle.");
                                }
                            }

                            // Fire non-blocking update check/stage (runs in background, result picked up next cycle)
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await updateService.CheckAndStageAsync(
                                        allowPreRelease: false,
                                        skipProvenance: false,
                                        cts.Token);
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex)
                                {
                                    logger.LogDebug(ex, "Background update check failed");
                                }
                            }, cts.Token);
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

    /// <summary>
    /// Spawns a detached <c>copilotd update --install-staged</c> process
    /// that will wait for this daemon to exit, then perform the binary replacement.
    /// Windows: CreateProcessW with CREATE_NEW_CONSOLE for proper console isolation.
    /// Unix: setsid for session detachment, with fallback to direct Process.Start.
    /// Returns true if the process was successfully spawned.
    /// </summary>
    private static bool SpawnUpdateInstaller(ILogger logger)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            logger.LogError("Cannot determine executable path for update installer");
            return false;
        }

        var commandLine = $"\"{exePath}\" update --install-staged";
        logger.LogDebug("Spawning update installer: {CommandLine}", commandLine);

        if (OperatingSystem.IsWindows())
            return SpawnUpdateInstallerWindows(exePath, logger);

        return SpawnUpdateInstallerUnix(exePath, logger);
    }

    private static bool SpawnUpdateInstallerWindows(string exePath, ILogger logger)
    {
        var commandLine = $"\"{exePath}\" update --install-staged";

        var si = new NativeInterop.STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.STARTUPINFO>() };
        si.dwFlags = NativeInterop.STARTF_USESHOWWINDOW;
        si.wShowWindow = NativeInterop.SW_HIDE;

        var success = NativeInterop.CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            NativeInterop.CREATE_NEW_CONSOLE | NativeInterop.CREATE_NEW_PROCESS_GROUP,
            IntPtr.Zero,
            null,
            ref si,
            out var pi);

        if (success)
        {
            NativeInterop.CloseHandle(pi.hProcess);
            NativeInterop.CloseHandle(pi.hThread);
            logger.LogInformation("Update installer spawned (PID {Pid})", pi.dwProcessId);
            return true;
        }

        logger.LogError("Failed to spawn update installer via CreateProcessW");
        return false;
    }

    /// <summary>
    /// Spawns the update installer as a detached process on Unix using setsid,
    /// mirroring the pattern used by <see cref="StartCommand"/>.
    /// </summary>
    private static bool SpawnUpdateInstallerUnix(string exePath, ILogger logger)
    {
        // Try setsid first for full session detachment
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            Arguments = $"\"{exePath}\" update --install-staged",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            var process = Process.Start(psi);
            if (process is not null)
            {
                process.StandardInput.Close();
                logger.LogInformation("Update installer spawned via setsid (PID {Pid})", process.Id);
                return true;
            }
        }
        catch
        {
            // setsid not available, fall back to direct launch
            logger.LogDebug("setsid not available, falling back to direct launch");
        }

        // Fallback: direct process launch
        var fallbackPsi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "update --install-staged",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            var process = Process.Start(fallbackPsi);
            if (process is not null)
            {
                process.StandardInput.Close();
                logger.LogInformation("Update installer spawned (PID {Pid})", process.Id);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to spawn update installer");
        }

        return false;
    }
}
