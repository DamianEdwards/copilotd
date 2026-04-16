using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Copilotd.Infrastructure.NativeInterop;

namespace Copilotd.Commands;

public static class StopCommand
{
    private static readonly TimeSpan SignalDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(15);

    public static Command Create(IServiceProvider services)
    {
        var command = new Command("stop", "Stop the copilotd daemon");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var runtimeContext = services.GetRequiredService<RuntimeContext>();

                // Check if daemon is running
                if (!stateStore.IsLockHeld())
                {
                    ConsoleOutput.Warning("copilotd daemon is not running.");
                    return 0;
                }

                // Read daemon PID
                var pidInfo = stateStore.ReadDaemonPid();
                if (pidInfo is null)
                {
                    ConsoleOutput.Error("Daemon appears to be running but PID file is missing. Cannot send shutdown signal.");
                    ConsoleOutput.Info($"If the daemon was started with '{runtimeContext.GetCopilotdCallbackCommand()} run', press Ctrl+C in that terminal instead.");
                    return 1;
                }

                var (pid, expectedStartTime) = pidInfo.Value;

                // Verify the process is alive and matches
                Process process;
                try
                {
                    process = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    ConsoleOutput.Warning("Daemon process not found (may have already exited). Cleaning up.");
                    return 0;
                }

                try
                {
                    // Verify start time to avoid signalling a wrong process after PID reuse
                    try
                    {
                        var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                        if (Math.Abs((actualStart - expectedStartTime).TotalSeconds) > 5)
                        {
                            ConsoleOutput.Error($"PID {pid} belongs to a different process (start time mismatch). The daemon may have already exited.");
                            return 1;
                        }
                    }
                    catch
                    {
                        // Can't read start time — proceed anyway, lock file confirms daemon is running
                    }

                    if (process.HasExited)
                    {
                        ConsoleOutput.Warning("Daemon process has already exited.");
                        return 0;
                    }

                    ConsoleOutput.Info($"Stopping copilotd daemon (PID: {pid})...");

                    bool terminated;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        terminated = StopDaemonWindows(process, pid, runtimeContext);
                    }
                    else
                    {
                        terminated = StopDaemonUnix(process, pid);
                    }

                    if (!terminated)
                    {
                        ConsoleOutput.Error("Failed to stop the daemon process.");
                        return 1;
                    }

                    // Wait for the lock to be released (daemon cleans up on exit)
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (DateTime.UtcNow < deadline && stateStore.IsLockHeld())
                    {
                        await Task.Delay(250, ct);
                    }

                    ConsoleOutput.Success("copilotd daemon stopped.");
                    return 0;
                }
                finally
                {
                    process.Dispose();
                }
            }, logger);
        });

        return command;
    }

    /// <summary>
    /// Windows: use the shutdown-instance helper to attach to the daemon's console
    /// and send Ctrl+C twice, triggering Console.CancelKeyPress in the daemon.
    /// </summary>
    private static bool StopDaemonWindows(Process process, int pid, RuntimeContext runtimeContext)
    {
        var invocation = runtimeContext.GetSelfInvocation($"shutdown-instance --pid {pid}");
        if (invocation is null)
        {
            ConsoleOutput.Warning("Cannot determine copilotd path, forcing termination.");
            return FallbackKill(process);
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
                ConsoleOutput.Warning("Failed to start shutdown helper, forcing termination.");
                return FallbackKill(process);
            }

            if (helper.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                if (ShutdownInstanceCommand.IsSuccessExitCode(helper.ExitCode))
                {
                    if (ShutdownInstanceCommand.UsedFallbackKillExitCode(helper.ExitCode))
                        ConsoleOutput.Warning("Daemon required forced termination after graceful shutdown timed out.");
                    return true;
                }
            }
            else
            {
                try { helper.Kill(); } catch { }
            }

            // Fallback if shutdown-instance didn't fully clean up
            if (!process.HasExited)
                return FallbackKill(process);

            return true;
        }
        catch
        {
            return FallbackKill(process);
        }
    }

    /// <summary>
    /// Unix: send SIGINT directly to the daemon, which triggers Console.CancelKeyPress
    /// for graceful shutdown.
    /// </summary>
    private static bool StopDaemonUnix(Process process, int pid)
    {
        try
        {
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(SignalDelay))
                return true;

            // Second SIGINT
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(GracefulTimeout))
                return true;
        }
        catch
        {
            // Fall through to kill
        }

        return FallbackKill(process);
    }

    private static bool FallbackKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
