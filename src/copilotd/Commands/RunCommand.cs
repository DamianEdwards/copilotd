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
        var disableSelfUpdatesOption = new Option<bool>("--disable-self-updates")
        {
            Description = $"Disable automatic background self-updates for this daemon run (also supported via {RuntimeContext.DisableSelfUpdatesEnvVar})."
        };

        command.Options.Add(intervalOption);
        command.Options.Add(logLevelOption);
        command.Options.Add(disableSelfUpdatesOption);

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
                var runtimeContext = services.GetRequiredService<RuntimeContext>();

                var interval = parseResult.GetValue(intervalOption);
                var disableSelfUpdates = runtimeContext.IsAutomaticSelfUpdateDisabled(parseResult.GetValue(disableSelfUpdatesOption));

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
                    if (disableSelfUpdates && runtimeContext.GetAutomaticSelfUpdateDisableReason(parseResult.GetValue(disableSelfUpdatesOption)) is { } reason)
                        ConsoleOutput.Info($"Automatic self-updates {reason}.");

                    // Startup reconciliation pass
                    var config = stateStore.LoadConfig();
                    ConsoleOutput.Info("Running startup reconciliation...");
                    stateStore.WithStateLock(() =>
                    {
                        var state = stateStore.LoadState();
                        reconciliation.Reconcile(config, state);
                    }, ct);
                    ConsoleOutput.Success("Startup reconciliation complete.");

                    // Launch control session if enabled
                    if (config.EnableControlSession)
                    {
                        var existingControlPid = default(int?);
                        var launchedControlPid = default(int?);
                        var launchFailed = false;

                        stateStore.WithStateLock(() =>
                        {
                            var state = stateStore.LoadState();

                            var existingAlive = state.ControlSession is not null
                                && state.ControlSession.Status == ControlSessionStatus.Running
                                && processManager.CheckControlSession(state.ControlSession) == ProcessLivenessResult.Alive;

                            if (existingAlive)
                            {
                                existingControlPid = state.ControlSession!.ProcessId;
                                return;
                            }

                            if (state.ControlSession?.ProcessId is not null)
                                processManager.TerminateControlSession(state.ControlSession);

                            var controlSession = processManager.LaunchControlSession(config);
                            if (controlSession is not null)
                            {
                                state.ControlSession = controlSession;
                                launchedControlPid = controlSession.ProcessId;
                            }
                            else
                            {
                                state.ControlSession = new ControlSessionInfo { Status = ControlSessionStatus.Failed };
                                launchFailed = true;
                            }

                            stateStore.SaveState(state);
                        }, ct);

                        if (existingControlPid is not null)
                        {
                            ConsoleOutput.Success($"Control session already running (PID {existingControlPid}).");
                        }
                        else if (launchedControlPid is not null)
                        {
                            ConsoleOutput.Success($"Control session launched (PID {launchedControlPid}).");
                        }
                        else if (launchFailed)
                        {
                            ConsoleOutput.Warning("Failed to launch control session. Will retry next cycle.");
                        }
                    }

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
                            stateStore.WithStateLock(() =>
                            {
                                var state = stateStore.LoadState();

                                reconciliation.Reconcile(config, state);

                                if (config.EnableControlSession)
                                {
                                    var controlAlive = state.ControlSession is not null
                                        && state.ControlSession.Status == ControlSessionStatus.Running
                                        && processManager.CheckControlSession(state.ControlSession) == ProcessLivenessResult.Alive;

                                    if (!controlAlive)
                                    {
                                        if (state.ControlSession?.ProcessId is not null)
                                            processManager.TerminateControlSession(state.ControlSession);

                                        logger.LogInformation("Relaunching control session...");
                                        var controlSession = processManager.LaunchControlSession(config);
                                        if (controlSession is not null)
                                        {
                                            state.ControlSession = controlSession;
                                            logger.LogInformation("Control session relaunched (PID {Pid})", controlSession.ProcessId);
                                        }
                                        else
                                        {
                                            state.ControlSession = new ControlSessionInfo { Status = ControlSessionStatus.Failed };
                                            logger.LogWarning("Failed to relaunch control session");
                                        }

                                        stateStore.SaveState(state);
                                    }
                                }
                                else if (state.ControlSession is not null
                                         && state.ControlSession.Status == ControlSessionStatus.Running)
                                {
                                    logger.LogInformation("Control session disabled, terminating...");
                                    processManager.TerminateControlSession(state.ControlSession);
                                    state.ControlSession.Status = ControlSessionStatus.Stopped;
                                    state.ControlSession.ProcessId = null;
                                    state.ControlSession.ProcessStartTime = null;
                                    stateStore.SaveState(state);
                                }
                            }, cts.Token);

                            if (!disableSelfUpdates)
                            {
                                // Self-update: schedule or maintain a deferred installer for any staged update,
                                // then fire a background check/stage task.
                                var updateState = stateStore.LoadUpdateState();
                                if (UpdateService.HasUsableStagedUpdate(updateState))
                                {
                                    if (EnsureDeferredInstallWatcher(updateService, runtimeContext, logger, updateState))
                                    {
                                        if (updateState.Status == UpdateStatus.Staged)
                                            ConsoleOutput.Info($"Staged update {updateState.StagedVersion} detected. It will install after this daemon exits.");
                                    }
                                    else if (updateState.Status == UpdateStatus.Staged)
                                    {
                                        ConsoleOutput.Warning("Failed to schedule deferred update installer, will retry next cycle.");
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
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error during poll cycle");
                            ConsoleOutput.Error($"Poll cycle error: {ex.Message}");
                            // Continue running — only catastrophic errors should exit
                        }
                    }

                    // Gracefully terminate running sessions before exit
                    stateStore.WithStateLock(() =>
                    {
                        var state = stateStore.LoadState();

                        if (state.ControlSession is not null
                            && state.ControlSession.Status == ControlSessionStatus.Running)
                        {
                            ConsoleOutput.Info("Shutting down control session...");
                            processManager.TerminateControlSession(state.ControlSession);
                            state.ControlSession.Status = ControlSessionStatus.Stopped;
                            state.ControlSession.ProcessId = null;
                            state.ControlSession.ProcessStartTime = null;
                        }

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
                                session.ProcessId = null;
                                session.ProcessStartTime = null;
                                session.UpdatedAt = DateTimeOffset.UtcNow;
                            }
                        }

                        stateStore.SaveState(state);
                    }, ct);

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
    /// Ensures a detached <c>copilotd update --install-staged</c> helper is waiting for the
    /// tracked daemon instance to exit naturally before installing the staged binary.
    /// If the helper is already running, this is a no-op.
    /// </summary>
    private static bool EnsureDeferredInstallWatcher(
        UpdateService updateService,
        RuntimeContext runtimeContext,
        ILogger logger,
        UpdateState updateState)
    {
        if (updateState.Status == UpdateStatus.WaitingForExit
            && IsTrackedProcessAlive(updateState.WatcherPid, updateState.WatcherStartTime))
        {
            return true;
        }

        var waitTarget = updateState.Status == UpdateStatus.WaitingForExit
                         && updateState.WaitForPid is { } waitPid
                         && updateState.WaitForStartTime is { } waitStartTime
            ? new TrackedProcess(waitPid, waitStartTime)
            : new TrackedProcess(Environment.ProcessId, GetCurrentProcessStartTime());

        var installer = SpawnUpdateInstaller(runtimeContext, logger, waitTarget.ProcessId, waitTarget.ProcessStartTime);
        if (installer is null)
            return false;

        if (!updateService.TryScheduleDeferredInstall(
                waitTarget.ProcessId,
                waitTarget.ProcessStartTime,
                installer.Value.ProcessId,
                installer.Value.ProcessStartTime))
        {
            TryTerminateTrackedProcess(installer.Value, logger);
            logger.LogWarning(
                "Deferred installer PID {WatcherPid} was terminated because the update state could not be recorded for daemon PID {WaitPid}",
                installer.Value.ProcessId,
                waitTarget.ProcessId);
            return false;
        }

        logger.LogInformation(
            "Deferred installer PID {WatcherPid} is waiting for daemon PID {WaitPid} to exit naturally",
            installer.Value.ProcessId,
            waitTarget.ProcessId);
        return true;
    }

    /// <summary>
    /// Spawns a detached <c>copilotd update --install-staged</c> process
    /// that will wait for the specified daemon PID/start-time pair to exit, then perform the binary replacement.
    /// Windows: CreateProcessW with CREATE_NEW_CONSOLE for proper console isolation.
    /// Unix: setsid for session detachment, with fallback to direct Process.Start.
    /// Returns the spawned installer's PID and start time on success.
    /// </summary>
    private static TrackedProcess? SpawnUpdateInstaller(RuntimeContext runtimeContext, ILogger logger, int waitForPid, DateTimeOffset waitForStartTime)
    {
        var invocation = runtimeContext.GetSelfInvocation(
            $"update --install-staged --wait-for-pid {waitForPid} --wait-for-start-time {waitForStartTime:O} --passive-wait");
        if (invocation is null)
        {
            logger.LogError("Cannot determine executable path for update installer");
            return null;
        }

        var commandLine = invocation.GetCommandLine();
        logger.LogDebug("Spawning update installer: {CommandLine}", commandLine);

        if (OperatingSystem.IsWindows())
            return SpawnUpdateInstallerWindows(invocation, logger);

        return SpawnUpdateInstallerUnix(invocation, logger);
    }

    private static TrackedProcess? SpawnUpdateInstallerWindows(CommandInvocation invocation, ILogger logger)
    {
        var commandLine = invocation.GetCommandLine();

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
            var startTime = default(DateTimeOffset?);
            try
            {
                using var process = Process.GetProcessById(pi.dwProcessId);
                startTime = GetProcessStartTime(process);
            }
            catch
            {
                startTime = DateTimeOffset.UtcNow;
            }

            NativeInterop.CloseHandle(pi.hProcess);
            NativeInterop.CloseHandle(pi.hThread);
            logger.LogInformation("Update installer spawned (PID {Pid})", pi.dwProcessId);
            return new TrackedProcess(pi.dwProcessId, startTime ?? DateTimeOffset.UtcNow);
        }

        logger.LogError("Failed to spawn update installer via CreateProcessW");
        return null;
    }

    /// <summary>
    /// Spawns the update installer as a detached process on Unix using setsid,
    /// mirroring the pattern used by <see cref="StartCommand"/>.
    /// </summary>
    private static TrackedProcess? SpawnUpdateInstallerUnix(CommandInvocation invocation, ILogger logger)
    {
        // Try setsid first for full session detachment
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            Arguments = invocation.GetCommandLine(),
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
                var startTime = GetProcessStartTime(process) ?? DateTimeOffset.UtcNow;
                process.StandardInput.Close();
                logger.LogInformation("Update installer spawned via setsid (PID {Pid})", process.Id);
                return new TrackedProcess(process.Id, startTime);
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
            FileName = invocation.FileName,
            Arguments = invocation.Arguments,
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
                var startTime = GetProcessStartTime(process) ?? DateTimeOffset.UtcNow;
                process.StandardInput.Close();
                logger.LogInformation("Update installer spawned (PID {Pid})", process.Id);
                return new TrackedProcess(process.Id, startTime);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to spawn update installer");
        }

        return null;
    }

    private static bool IsTrackedProcessAlive(int? pid, DateTimeOffset? expectedStartTime)
    {
        if (pid is not { } trackedPid || expectedStartTime is not { } expectedStart)
            return false;

        try
        {
            using var process = Process.GetProcessById(trackedPid);
            var actualStart = GetProcessStartTime(process);
            if (actualStart is not null && Math.Abs((actualStart.Value - expectedStart).TotalSeconds) > 5)
                return false;

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void TryTerminateTrackedProcess(TrackedProcess trackedProcess, ILogger logger)
    {
        try
        {
            using var process = Process.GetProcessById(trackedProcess.ProcessId);
            var actualStart = GetProcessStartTime(process);
            if (actualStart is not null && Math.Abs((actualStart.Value - trackedProcess.ProcessStartTime).TotalSeconds) > 5)
                return;

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to terminate deferred installer PID {Pid}", trackedProcess.ProcessId);
        }
    }

    private static DateTimeOffset GetCurrentProcessStartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return GetProcessStartTime(process) ?? DateTimeOffset.UtcNow;
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
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

    private readonly record struct TrackedProcess(int ProcessId, DateTimeOffset ProcessStartTime);
}
