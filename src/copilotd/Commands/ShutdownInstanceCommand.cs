using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd.Commands;

/// <summary>
/// Hidden command used internally to gracefully shut down a copilot process from a separate
/// process instance. On Windows this is necessary because the daemon cannot call FreeConsole
/// (it disrupts ConPTY sessions), so a helper process is spawned that attaches to the target's
/// console and sends interrupt signals.
/// </summary>
public static class ShutdownInstanceCommand
{
    private static readonly TimeSpan SignalDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(10);

    public static Command Create(IServiceProvider services)
    {
        var command = new Command("shutdown-instance", "Internal: gracefully shut down a copilot process")
        {
            Hidden = true
        };

        var pidOption = new Option<int>("--pid") { Description = "Process ID to shut down" };
        var expectedStartOption = new Option<string?>("--expected-start") { Description = "Expected UTC process start time in round-trip format" };
        var delaySecondsOption = new Option<int>("--delay-seconds") { Description = "Seconds to wait before beginning shutdown" };
        command.Options.Add(pidOption);
        command.Options.Add(expectedStartOption);
        command.Options.Add(delaySecondsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ShutdownInstanceCommand).FullName!);
            var pid = parseResult.GetValue(pidOption);
            var expectedStartText = parseResult.GetValue(expectedStartOption);
            var delaySeconds = parseResult.GetValue(delaySecondsOption);
            if (delaySeconds < 0)
            {
                logger.LogWarning("shutdown-instance received invalid negative delay {DelaySeconds} for PID {Pid}", delaySeconds, pid);
                return (int)ShutdownInstanceExitCode.InvalidArguments;
            }

            if (!TryParseExpectedStart(expectedStartText, out var expectedStart))
            {
                logger.LogWarning("shutdown-instance received invalid expected start '{ExpectedStart}' for PID {Pid}", expectedStartText, pid);
                return (int)ShutdownInstanceExitCode.InvalidArguments;
            }

            try
            {
                return (int)ShutdownProcess(pid, expectedStart, TimeSpan.FromSeconds(delaySeconds), logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "shutdown-instance failed unexpectedly for PID {Pid}", pid);
                return (int)ShutdownInstanceExitCode.Failed;
            }
        });

        return command;
    }

    public static bool IsSuccessExitCode(int exitCode)
        => exitCode == (int)ShutdownInstanceExitCode.AlreadyExited
            || exitCode == (int)ShutdownInstanceExitCode.ExitedAfterFirstInterrupt
            || exitCode == (int)ShutdownInstanceExitCode.ExitedAfterSecondInterrupt
            || exitCode == (int)ShutdownInstanceExitCode.FallbackKill
            || exitCode == (int)ShutdownInstanceExitCode.ExitedDuringFallback
            || exitCode == (int)ShutdownInstanceExitCode.StartTimeMismatch;

    public static bool UsedFallbackKillExitCode(int exitCode)
        => exitCode == (int)ShutdownInstanceExitCode.FallbackKill;

    public static string DescribeExitCode(int exitCode)
        => exitCode switch
        {
            (int)ShutdownInstanceExitCode.AlreadyExited => "already exited",
            (int)ShutdownInstanceExitCode.ExitedAfterFirstInterrupt => "exited after first interrupt",
            (int)ShutdownInstanceExitCode.ExitedAfterSecondInterrupt => "exited after second interrupt",
            (int)ShutdownInstanceExitCode.FallbackKill => "fallback kill",
            (int)ShutdownInstanceExitCode.ExitedDuringFallback => "exited during fallback",
            (int)ShutdownInstanceExitCode.StartTimeMismatch => "start time mismatch",
            (int)ShutdownInstanceExitCode.InvalidArguments => "invalid arguments",
            (int)ShutdownInstanceExitCode.Failed => "failed",
            _ => $"unknown exit code {exitCode}"
        };

    private static ShutdownInstanceExitCode ShutdownProcess(int pid, DateTimeOffset? expectedStart, TimeSpan shutdownDelay, ILogger logger)
    {
        var effectiveDelay = shutdownDelay < TimeSpan.Zero ? TimeSpan.Zero : shutdownDelay;
        logger.LogInformation("shutdown-instance starting for PID {Pid} with delay {Delay} and expected start {ExpectedStart}",
            pid, effectiveDelay, expectedStart);

        if (effectiveDelay > TimeSpan.Zero)
        {
            logger.LogInformation("shutdown-instance waiting {Delay} before signaling PID {Pid}", effectiveDelay, pid);
            Thread.Sleep(effectiveDelay);
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            logger.LogInformation("shutdown-instance found PID {Pid} already exited before signaling", pid);
            return ShutdownInstanceExitCode.AlreadyExited;
        }

        try
        {
            if (expectedStart is { } expectedStartTime)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStartTime).TotalSeconds) > 5)
                {
                    logger.LogWarning("shutdown-instance skipped PID {Pid} due to start time mismatch. Expected {ExpectedStart}, actual {ActualStart}",
                        pid, expectedStartTime, actualStart);
                    return ShutdownInstanceExitCode.StartTimeMismatch;
                }
            }

            if (process.HasExited)
            {
                logger.LogInformation("shutdown-instance found PID {Pid} already exited after validation", pid);
                return ShutdownInstanceExitCode.AlreadyExited;
            }

            ShutdownInstanceExitCode outcome;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                outcome = ShutdownWindows(process, pid, logger);
            else
                outcome = ShutdownUnix(process, pid, logger);

            logger.LogInformation("shutdown-instance completed for PID {Pid} with outcome {Outcome}", pid, outcome);
            return outcome;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Windows: attach to the target's console and send Ctrl+C twice.
    /// This process was launched without the daemon's console, so FreeConsole/AttachConsole
    /// won't disrupt the daemon.
    /// </summary>
    private static ShutdownInstanceExitCode ShutdownWindows(Process process, int pid, ILogger logger)
    {
        // Detach from any inherited console
        FreeConsole();

        // Protect ourselves from the signals we're about to send
        SetConsoleCtrlHandler(null, true);

        // Attach to the target's console
        if (!AttachConsole((uint)pid))
        {
            logger.LogWarning("shutdown-instance failed to attach to console for PID {Pid} (Win32 error {Error}), falling back to kill",
                pid, Marshal.GetLastWin32Error());
            return FallbackKill(process, pid, logger, "attach-console-failed");
        }

        try
        {
            if (TrySendConsoleSignal(process, pid, logger, CTRL_C_EVENT, 0, "first Ctrl+C", SignalDelay, ShutdownInstanceExitCode.ExitedAfterFirstInterrupt) is { } firstOutcome)
                return firstOutcome;

            if (TrySendConsoleSignal(process, pid, logger, CTRL_C_EVENT, 0, "second Ctrl+C", GracefulTimeout, ShutdownInstanceExitCode.ExitedAfterSecondInterrupt) is { } secondOutcome)
                return secondOutcome;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shutdown-instance signal sequence failed for PID {Pid}, falling back to kill", pid);
        }
        finally
        {
            FreeConsole();
        }

        return FallbackKill(process, pid, logger, "signals-exhausted");
    }

    /// <summary>
    /// Unix: send SIGINT, wait, then SIGKILL if needed.
    /// Note: on Unix the daemon calls this logic directly (no helper process needed).
    /// </summary>
    private static ShutdownInstanceExitCode ShutdownUnix(Process process, int pid, ILogger logger)
    {
        try
        {
            logger.LogInformation("shutdown-instance sending first SIGINT to PID {Pid}", pid);
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(SignalDelay))
            {
                logger.LogInformation("PID {Pid} exited after first SIGINT", pid);
                return ShutdownInstanceExitCode.ExitedAfterFirstInterrupt;
            }

            // Second SIGINT
            logger.LogInformation("shutdown-instance sending second SIGINT to PID {Pid}", pid);
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(GracefulTimeout))
            {
                logger.LogInformation("PID {Pid} exited after second SIGINT", pid);
                return ShutdownInstanceExitCode.ExitedAfterSecondInterrupt;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shutdown-instance SIGINT sequence failed for PID {Pid}, falling back to kill", pid);
        }

        return FallbackKill(process, pid, logger, "signals-exhausted");
    }

    private static ShutdownInstanceExitCode? TrySendConsoleSignal(
        Process process,
        int pid,
        ILogger logger,
        uint signal,
        uint processGroupId,
        string description,
        TimeSpan waitTime,
        ShutdownInstanceExitCode successOutcome)
    {
        logger.LogInformation("shutdown-instance sending {SignalDescription} to PID {Pid}", description, pid);

        if (!GenerateConsoleCtrlEvent(signal, processGroupId))
        {
            logger.LogWarning("shutdown-instance failed to send {SignalDescription} to PID {Pid} (Win32 error {Error})",
                description, pid, Marshal.GetLastWin32Error());
            return null;
        }

        if (process.WaitForExit(waitTime))
        {
            logger.LogInformation("PID {Pid} exited after {SignalDescription}", pid, description);
            return successOutcome;
        }

        logger.LogInformation("PID {Pid} was still running {WaitTime} after {SignalDescription}", pid, waitTime, description);
        return null;
    }

    private static ShutdownInstanceExitCode FallbackKill(Process process, int pid, ILogger logger, string reason)
    {
        logger.LogWarning("shutdown-instance falling back to kill for PID {Pid} ({Reason})", pid, reason);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                logger.LogWarning("shutdown-instance killed PID {Pid}", pid);
            }
            else
            {
                logger.LogInformation("shutdown-instance found PID {Pid} already exited during fallback", pid);
                return ShutdownInstanceExitCode.ExitedDuringFallback;
            }

            return ShutdownInstanceExitCode.FallbackKill;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "shutdown-instance failed to kill PID {Pid}", pid);
            return ShutdownInstanceExitCode.Failed;
        }
    }

    private static bool TryParseExpectedStart(string? text, out DateTimeOffset? expectedStart)
    {
        expectedStart = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;

        expectedStart = parsed;
        return true;
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

    #region Platform Interop

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    private delegate bool ConsoleCtrlHandler(uint dwCtrlType);

    private const uint CTRL_C_EVENT = 0;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    private const int SIGINT = 2;

    #endregion

    private enum ShutdownInstanceExitCode
    {
        AlreadyExited = 0,
        ExitedAfterFirstInterrupt = 1,
        ExitedAfterSecondInterrupt = 2,
        FallbackKill = 3,
        StartTimeMismatch = 4,
        ExitedDuringFallback = 5,
        InvalidArguments = 64,
        Failed = 65
    }
}
