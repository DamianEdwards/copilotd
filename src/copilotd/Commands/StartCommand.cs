using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Copilotd.Infrastructure;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Copilotd.Infrastructure.NativeInterop;

namespace Copilotd.Commands;

public static class StartCommand
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);

    public static Command Create(IServiceProvider services)
    {
        var command = new Command("start", "Start the copilotd daemon in the background");
        var intervalOption = new Option<int>("--interval") { Description = "Polling interval in seconds", DefaultValueFactory = _ => 60 };
        var logLevelOption = new Option<string?>("--log-level") { Description = "Set logging level (default: info). Use 'debug' for more detail or 'error' for less." };

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

                var interval = parseResult.GetValue(intervalOption);
                var logLevel = parseResult.GetValue(logLevelOption);

                // Pre-flight checks
                var preflightResult = PreflightChecks.Run(ghCli, copilotCli, stateStore);
                if (preflightResult != 0)
                    return preflightResult;

                // Check if daemon is already running
                if (stateStore.IsLockHeld())
                {
                    ConsoleOutput.Error("copilotd daemon is already running.");
                    return 1;
                }

                // Build arguments for the run command
                var args = $"run --interval {interval}";
                if (logLevel is not null)
                {
                    args += $" --log-level {logLevel}";
                }

                var copilotdPath = Environment.ProcessPath;
                if (copilotdPath is null)
                {
                    ConsoleOutput.Error("Cannot determine copilotd executable path.");
                    return 1;
                }

                // Launch daemon as a detached background process
                int childPid;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    childPid = LaunchDetachedWindows(copilotdPath, args);
                }
                else
                {
                    childPid = LaunchDetachedUnix(copilotdPath, args);
                }

                if (childPid <= 0)
                {
                    ConsoleOutput.Error("Failed to start copilotd daemon process.");
                    return 1;
                }

                // Poll until the daemon acquires the lock or the child dies
                var deadline = DateTime.UtcNow + StartupTimeout;
                var started = false;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(StartupPollInterval, ct);

                    // Check if child process died early (preflight failure, etc.)
                    try
                    {
                        using var child = Process.GetProcessById(childPid);
                        if (child.HasExited)
                        {
                            ConsoleOutput.Error($"Daemon process exited unexpectedly (exit code: {child.ExitCode}).");
                            return 1;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process already gone
                        ConsoleOutput.Error("Daemon process exited unexpectedly.");
                        return 1;
                    }

                    // Check if the daemon has acquired the lock and written its PID
                    if (stateStore.IsLockHeld())
                    {
                        var pidInfo = stateStore.ReadDaemonPid();
                        if (pidInfo is { } info && info.Pid == childPid)
                        {
                            started = true;
                            break;
                        }
                    }
                }

                if (!started)
                {
                    ConsoleOutput.Error("Daemon did not start within the expected time.");
                    // Try to clean up the child process
                    try
                    {
                        using var child = Process.GetProcessById(childPid);
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                    }
                    catch { /* best effort */ }
                    return 1;
                }

                ConsoleOutput.Success($"copilotd daemon started in background (PID: {childPid}). Use 'copilotd stop' to shut down.");
                return 0;
            }, logger);
        });

        return command;
    }

    /// <summary>
    /// Windows: use CreateProcessW with CREATE_NEW_CONSOLE to give the daemon its own
    /// console (hidden). This is required for shutdown-instance to attach and send signals.
    /// </summary>
    private static int LaunchDetachedWindows(string exePath, string args)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;

        var cmdLine = $"\"{exePath}\" {args}";
        var flags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;

        if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
            flags, IntPtr.Zero, null, ref si, out var pi))
        {
            return -1;
        }

        var pid = pi.dwProcessId;
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return pid;
    }

    /// <summary>
    /// Unix: use setsid to create a new session, detaching from the terminal.
    /// Stdin/stdout/stderr are redirected to /dev/null.
    /// </summary>
    private static int LaunchDetachedUnix(string exePath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            Arguments = $"\"{exePath}\" {args}",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return -1;

            // Close stdin so daemon doesn't wait for input.
            // Don't close stdout/stderr - let them be inherited (they go to the
            // hidden console or are silently discarded by setsid).
            process.StandardInput.Close();

            // setsid is the child we started; the actual daemon is its child.
            // But on most systems setsid execs directly, so PID is the same.
            // Wait briefly for setsid to exec, then check if copilotd is running.
            // Since setsid replaces itself via exec, process.Id IS the daemon PID.
            return process.Id;
        }
        catch
        {
            // setsid not available, fall back to direct launch
            var fallbackPsi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                var process = Process.Start(fallbackPsi);
                if (process is null)
                    return -1;

                process.StandardInput.Close();
                var pid = process.Id;
                // Don't dispose - we want the process to keep running
                return pid;
            }
            catch
            {
                return -1;
            }
        }
    }
}
