using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Copilotd.Infrastructure;
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
    private static readonly TimeSpan DaemonSignalDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CopilotSignalDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CopilotGracefulTimeout = TimeSpan.FromSeconds(15) - CopilotSignalDelay;
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
        var signalProfileOption = new Option<string>("--signal-profile")
        {
            Description = "Signal profile to use: daemon or copilot",
            DefaultValueFactory = _ => ShutdownSignalProfile.Daemon.ToString().ToLowerInvariant()
        };
        command.Options.Add(pidOption);
        command.Options.Add(expectedStartOption);
        command.Options.Add(delaySecondsOption);
        command.Options.Add(signalProfileOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ShutdownInstanceCommand).FullName!);
            var pid = parseResult.GetValue(pidOption);
            var expectedStartText = parseResult.GetValue(expectedStartOption);
            var delaySeconds = parseResult.GetValue(delaySecondsOption);
            var signalProfileText = parseResult.GetValue(signalProfileOption);
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

            if (!TryParseSignalProfile(signalProfileText, out var signalProfile))
            {
                logger.LogWarning("shutdown-instance received invalid signal profile '{SignalProfile}' for PID {Pid}", signalProfileText, pid);
                return (int)ShutdownInstanceExitCode.InvalidArguments;
            }

            try
            {
                return (int)ShutdownProcess(pid, expectedStart, TimeSpan.FromSeconds(delaySeconds), signalProfile, logger);
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

    private static ShutdownInstanceExitCode ShutdownProcess(
        int pid,
        DateTimeOffset? expectedStart,
        TimeSpan shutdownDelay,
        ShutdownSignalProfile signalProfile,
        ILogger logger)
    {
        var effectiveDelay = shutdownDelay < TimeSpan.Zero ? TimeSpan.Zero : shutdownDelay;
        logger.LogInformation("shutdown-instance starting for PID {Pid} with delay {Delay}, expected start {ExpectedStart}, and signal profile {SignalProfile}",
            pid, effectiveDelay, expectedStart, signalProfile);

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
                outcome = ShutdownWindows(process, pid, signalProfile, logger);
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
    /// Windows: attach to the target's console and send signals according to the configured profile.
    /// This process was launched without the daemon's console, so FreeConsole/AttachConsole
    /// won't disrupt the daemon.
    /// </summary>
    private static ShutdownInstanceExitCode ShutdownWindows(Process process, int pid, ShutdownSignalProfile signalProfile, ILogger logger)
    {
        var signalTargetPid = ResolveSignalTargetPid(pid, signalProfile, logger);

        // Detach from any inherited console
        FreeConsole();

        // Protect ourselves from the signals we're about to send.
        SetConsoleCtrlHandler(null, true);

        // Attach to the target's console
        if (!AttachConsole((uint)signalTargetPid))
        {
            logger.LogWarning("shutdown-instance failed to attach to console for PID {Pid} (Win32 error {Error}), falling back to kill",
                signalTargetPid, Marshal.GetLastWin32Error());
            return FallbackKill(process, pid, logger, "attach-console-failed");
        }

        try
        {
            if (signalProfile == ShutdownSignalProfile.Copilot)
            {
                if (TrySendCtrlCInput(process, pid, signalTargetPid, logger, CopilotSignalDelay, ShutdownInstanceExitCode.ExitedAfterFirstInterrupt) is { } firstOutcome)
                    return firstOutcome;

                if (TrySendCtrlCInput(process, pid, signalTargetPid, logger, CopilotGracefulTimeout, ShutdownInstanceExitCode.ExitedAfterSecondInterrupt) is { } secondOutcome)
                    return secondOutcome;
            }
            else
            {
                if (TrySendConsoleSignal(process, pid, signalTargetPid, logger, CTRL_BREAK_EVENT, unchecked((uint)signalTargetPid), "Ctrl+Break", DaemonSignalDelay, ShutdownInstanceExitCode.ExitedAfterFirstInterrupt) is { } firstOutcome)
                    return firstOutcome;

                if (TrySendConsoleSignal(process, pid, signalTargetPid, logger, CTRL_C_EVENT, 0, "Ctrl+C", GracefulTimeout, ShutdownInstanceExitCode.ExitedAfterSecondInterrupt) is { } secondOutcome)
                    return secondOutcome;
            }
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

            if (process.WaitForExit(DaemonSignalDelay))
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
        int rootPid,
        int signalTargetPid,
        ILogger logger,
        uint signal,
        uint processGroupId,
        string description,
        TimeSpan waitTime,
        ShutdownInstanceExitCode successOutcome)
    {
        logger.LogInformation("shutdown-instance sending {SignalDescription} to PID {Pid}", description, signalTargetPid);

        if (!GenerateConsoleCtrlEvent(signal, processGroupId))
        {
            logger.LogWarning("shutdown-instance failed to send {SignalDescription} to PID {Pid} (Win32 error {Error})",
                description, signalTargetPid, Marshal.GetLastWin32Error());
            return null;
        }

        if (WaitForProcessTreeExit(process, rootPid, waitTime))
        {
            logger.LogInformation("PID {Pid} process tree exited after {SignalDescription}", rootPid, description);
            return successOutcome;
        }

        logger.LogInformation("PID {Pid} process tree was still running {WaitTime} after {SignalDescription}", rootPid, waitTime, description);
        return null;
    }

    private static ShutdownInstanceExitCode? TrySendCtrlCInput(
        Process process,
        int rootPid,
        int signalTargetPid,
        ILogger logger,
        TimeSpan waitTime,
        ShutdownInstanceExitCode successOutcome)
    {
        logger.LogInformation("shutdown-instance sending Ctrl+C to PID {Pid}", signalTargetPid);

        if (!WriteConsoleCtrlCInput(signalTargetPid, logger))
            return null;

        if (WaitForCopilotShutdown(process, rootPid, waitTime))
        {
            logger.LogInformation("PID {Pid} exited after Ctrl+C", rootPid);
            return successOutcome;
        }

        logger.LogInformation("PID {Pid} was still running {WaitTime} after Ctrl+C", rootPid, waitTime);
        return null;
    }

    private static bool WriteConsoleCtrlCInput(int signalTargetPid, ILogger logger)
    {
        using var consoleInputHandle = CreateFileW(
            "CONIN$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (consoleInputHandle.IsInvalid)
        {
            logger.LogWarning("shutdown-instance failed to open console input for PID {Pid} (Win32 error {Error})",
                signalTargetPid, Marshal.GetLastWin32Error());
            return false;
        }

        INPUT_RECORD[] inputRecords =
        [
            CreateKeyInputRecord(true, VK_CONTROL, '\0', LEFT_CTRL_PRESSED),
            CreateKeyInputRecord(true, VK_C, '\u0003', LEFT_CTRL_PRESSED),
            CreateKeyInputRecord(false, VK_C, '\u0003', LEFT_CTRL_PRESSED),
            CreateKeyInputRecord(false, VK_CONTROL, '\0', 0),
        ];

        if (!WriteConsoleInputW(consoleInputHandle, inputRecords, (uint)inputRecords.Length, out var eventsWritten)
            || eventsWritten != inputRecords.Length)
        {
            logger.LogWarning("shutdown-instance failed to write Ctrl+C console input for PID {Pid} (Win32 error {Error})",
                signalTargetPid, Marshal.GetLastWin32Error());
            return false;
        }

        return true;
    }

    private static INPUT_RECORD CreateKeyInputRecord(bool keyDown, ushort virtualKeyCode, char character, uint controlKeyState) =>
        new()
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown,
                wRepeatCount = 1,
                wVirtualKeyCode = virtualKeyCode,
                wVirtualScanCode = 0,
                UnicodeChar = character,
                dwControlKeyState = controlKeyState,
            }
        };

    private static int ResolveSignalTargetPid(int pid, ShutdownSignalProfile signalProfile, ILogger logger)
    {
        if (signalProfile != ShutdownSignalProfile.Copilot || !OperatingSystem.IsWindows())
            return pid;

        var childCopilotPid = NativeInterop.FindDeepestWindowsDescendantProcessId(pid, "copilot.exe");
        if (childCopilotPid is not { } childPid || childPid == pid)
            return pid;

        logger.LogInformation("shutdown-instance retargeting copilot signals from PID {RootPid} to child copilot PID {ChildPid}", pid, childPid);
        return childPid;
    }

    private static ShutdownInstanceExitCode FallbackKill(Process process, int pid, ILogger logger, string reason)
    {
        logger.LogWarning("shutdown-instance falling back to kill for PID {Pid} ({Reason})", pid, reason);

        try
        {
            var killedAnyProcess = false;

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                logger.LogWarning("shutdown-instance killed PID {Pid}", pid);
                killedAnyProcess = true;
            }
            else
            {
                logger.LogInformation("shutdown-instance found PID {Pid} already exited during fallback", pid);
            }

            killedAnyProcess |= KillDescendants(pid, logger);
            return killedAnyProcess ? ShutdownInstanceExitCode.FallbackKill : ShutdownInstanceExitCode.ExitedDuringFallback;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "shutdown-instance failed to kill PID {Pid}", pid);
            return ShutdownInstanceExitCode.Failed;
        }
    }

    private static bool WaitForProcessTreeExit(Process process, int rootPid, TimeSpan waitTime)
    {
        var deadline = DateTime.UtcNow + waitTime;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited && !HasRelevantDescendants(rootPid))
                return true;

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return process.HasExited && !HasRelevantDescendants(rootPid);
    }

    private static bool WaitForCopilotShutdown(Process process, int rootPid, TimeSpan waitTime)
    {
        var deadline = DateTime.UtcNow + waitTime;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited && !HasRelevantDescendants(rootPid))
                return true;

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return process.HasExited && !HasRelevantDescendants(rootPid);
    }

    private static bool HasRelevantDescendants(int rootPid)
    {
        var processes = NativeInterop.EnumerateWindowsProcesses();
        if (processes.Count == 0)
            return false;

        var descendants = EnumerateDescendantProcessIds(rootPid, processes);
        return descendants.Any(descendant =>
            !string.Equals(descendant.ExecutableName, "conhost.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool KillDescendants(int rootPid, ILogger logger)
    {
        var descendantIds = EnumerateDescendantProcessIds(rootPid);
        var killedAny = false;

        foreach (var descendantPid in descendantIds.AsEnumerable().Reverse())
        {
            try
            {
                using var descendant = Process.GetProcessById(descendantPid);
                if (descendant.HasExited)
                    continue;

                descendant.Kill(entireProcessTree: true);
                logger.LogWarning("shutdown-instance killed descendant PID {Pid} from root PID {RootPid}", descendantPid, rootPid);
                killedAny = true;
            }
            catch (ArgumentException)
            {
            }
        }

        return killedAny;
    }

    private static List<int> EnumerateDescendantProcessIds(int rootPid)
    {
        var processes = NativeInterop.EnumerateWindowsProcesses();
        if (processes.Count == 0)
            return [];

        return EnumerateDescendantProcessIds(rootPid, processes)
            .Select(process => process.ProcessId)
            .ToList();
    }

    private static List<NativeInterop.WindowsProcessEntry> EnumerateDescendantProcessIds(
        int rootPid,
        IReadOnlyList<NativeInterop.WindowsProcessEntry> processes)
    {
        if (processes.Count == 0)
            return [];

        var childrenByParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var descendants = new List<NativeInterop.WindowsProcessEntry>();
        var pending = new Stack<int>();
        pending.Push(rootPid);

        while (pending.Count > 0)
        {
            var currentPid = pending.Pop();
            if (!childrenByParent.TryGetValue(currentPid, out var children))
                continue;

            foreach (var childPid in children)
            {
                descendants.Add(childPid);
                pending.Push(childPid.ProcessId);
            }
        }

        return descendants;
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

    private static bool TryParseSignalProfile(string? text, out ShutdownSignalProfile signalProfile)
    {
        if (Enum.TryParse(text, ignoreCase: true, out signalProfile))
            return true;

        signalProfile = default;
        return false;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(
        SafeFileHandle hConsoleInput,
        [MarshalAs(UnmanagedType.LPArray), In] INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    private delegate bool ConsoleCtrlHandler(uint dwCtrlType);

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;
    private const uint LEFT_CTRL_PRESSED = 0x0008;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    private const int SIGINT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

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

    private enum ShutdownSignalProfile
    {
        Daemon,
        Copilot
    }
}
