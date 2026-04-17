using System.Runtime.InteropServices;

namespace Copilotd.Infrastructure;

/// <summary>
/// Shared platform-specific interop declarations used by process management and
/// daemon lifecycle commands.
/// </summary>
internal static class NativeInterop
{
    // --- Windows process creation ---

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    public const int STARTF_USESHOWWINDOW = 0x00000001;
    public const short SW_HIDE = 0;

    // --- Windows process snapshot APIs ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    public readonly record struct WindowsProcessEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    public static IReadOnlyList<WindowsProcessEntry> EnumerateWindowsProcesses()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
            return [];

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (!Process32FirstW(snapshot, ref entry))
                return [];

            var processes = new List<WindowsProcessEntry>();
            do
            {
                processes.Add(new WindowsProcessEntry(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile));

                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32NextW(snapshot, ref entry));

            return processes;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static int? FindDeepestWindowsDescendantProcessId(int rootPid, string executableName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var processes = EnumerateWindowsProcesses();
        if (processes.Count == 0)
            return null;

        var childrenByParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var bestDepth = -1;
        int? bestPid = null;

        void Walk(int parentPid, int depth)
        {
            if (!childrenByParent.TryGetValue(parentPid, out var children))
                return;

            foreach (var child in children)
            {
                var childDepth = depth + 1;
                if (string.Equals(child.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase)
                    && childDepth > bestDepth)
                {
                    bestDepth = childDepth;
                    bestPid = child.ProcessId;
                }

                Walk(child.ProcessId, childDepth);
            }
        }

        Walk(rootPid, 0);
        return bestPid;
    }

    // --- Unix signal APIs ---

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static extern int sys_kill(int pid, int sig);

    public const int SIGINT = 2;
    public const int SIGKILL = 9;
}
