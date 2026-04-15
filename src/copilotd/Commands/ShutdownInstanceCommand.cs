using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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

    public static Command Create()
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
            var pid = parseResult.GetValue(pidOption);
            var expectedStartText = parseResult.GetValue(expectedStartOption);
            var delaySeconds = parseResult.GetValue(delaySecondsOption);
            if (delaySeconds < 0)
                return 1;

            if (!TryParseExpectedStart(expectedStartText, out var expectedStart))
                return 1;

            return ShutdownProcess(pid, expectedStart, TimeSpan.FromSeconds(delaySeconds));
        });

        return command;
    }

    private static int ShutdownProcess(int pid, DateTimeOffset? expectedStart, TimeSpan shutdownDelay)
    {
        var effectiveDelay = shutdownDelay < TimeSpan.Zero ? TimeSpan.Zero : shutdownDelay;
        if (effectiveDelay > TimeSpan.Zero)
            Thread.Sleep(effectiveDelay);

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // Already dead
            return 0;
        }

        try
        {
            if (expectedStart is { } expectedStartTime)
            {
                var actualStart = GetProcessStartTime(process);
                if (actualStart is not null && Math.Abs((actualStart.Value - expectedStartTime).TotalSeconds) > 5)
                    return 0;
            }

            if (process.HasExited)
                return 0;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ShutdownWindows(process, pid);
            else
                return ShutdownUnix(process, pid);
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Windows: attach to the target's console and send Ctrl+Break, then Ctrl+C.
    /// This process was launched without the daemon's console, so FreeConsole/AttachConsole
    /// won't disrupt the daemon.
    /// </summary>
    private static int ShutdownWindows(Process process, int pid)
    {
        // Detach from any inherited console
        FreeConsole();

        // Protect ourselves from the signals we're about to send
        SetConsoleCtrlHandler(null, true);

        // Attach to the target's console
        if (!AttachConsole((uint)pid))
            return FallbackKill(process, pid);

        try
        {
            // First try: Ctrl+Break targeted at the process group (PID)
            GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)pid);

            if (process.WaitForExit(SignalDelay))
                return 0;

            // Second try: Ctrl+C broadcast to all on the console
            GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);

            if (process.WaitForExit(GracefulTimeout))
                return 0;
        }
        catch
        {
            // Fall through to kill
        }

        return FallbackKill(process, pid);
    }

    /// <summary>
    /// Unix: send SIGINT, wait, then SIGKILL if needed.
    /// Note: on Unix the daemon calls this logic directly (no helper process needed).
    /// </summary>
    private static int ShutdownUnix(Process process, int pid)
    {
        try
        {
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(SignalDelay))
                return 0;

            // Second SIGINT
            sys_kill(pid, SIGINT);

            if (process.WaitForExit(GracefulTimeout))
                return 0;
        }
        catch
        {
            // Fall through to kill
        }

        return FallbackKill(process, pid);
    }

    private static int FallbackKill(Process process, int pid)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return 0;
        }
        catch
        {
            return 1;
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
    private const uint CTRL_BREAK_EVENT = 1;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    private const int SIGINT = 2;

    #endregion
}
