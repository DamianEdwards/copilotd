namespace Copilotd.Infrastructure;

public sealed class LogFileManager
{
    public const string DaemonFolderPrefix = "daemon_";
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    private readonly string _rootLogDirectory;
    private readonly string? _currentDaemonInstanceId;

    public LogFileManager(string? currentDaemonInstanceId = null)
    {
        _rootLogDirectory = CopilotdPaths.GetLogsDirectory();
        _currentDaemonInstanceId = string.IsNullOrWhiteSpace(currentDaemonInstanceId)
            ? null
            : currentDaemonInstanceId;

        Directory.CreateDirectory(_rootLogDirectory);
    }

    public string RootLogDirectory => _rootLogDirectory;

    public string? CurrentDaemonInstanceId => _currentDaemonInstanceId;

    public bool IsDaemonInvocation => _currentDaemonInstanceId is not null;

    public string GetLogsRootDirectoryForDisplay() => NormalizePath(_rootLogDirectory);

    public string GetCurrentProcessLogDirectory()
    {
        var path = _currentDaemonInstanceId is { } daemonInstanceId
            ? GetDaemonLogDirectory(daemonInstanceId)
            : _rootLogDirectory;

        Directory.CreateDirectory(path);
        return path;
    }

    public string GetCurrentProcessLogFilePath()
    {
        var logDirectory = GetCurrentProcessLogDirectory();
        return GetLogFilePath(logDirectory);
    }

    public string GetDaemonLogDirectory(string daemonInstanceId)
        => Path.Combine(_rootLogDirectory, $"{DaemonFolderPrefix}{daemonInstanceId}");

    public string GetDaemonLogDirectoryForDisplay(string daemonInstanceId)
        => NormalizePath(GetDaemonLogDirectory(daemonInstanceId));

    public string? GetCurrentDaemonLogDirectoryForDisplay()
        => _currentDaemonInstanceId is { } daemonInstanceId
            ? GetDaemonLogDirectoryForDisplay(daemonInstanceId)
            : null;

    public LogClearResult ClearLogs(int? days, string? activeDaemonInstanceId)
    {
        var deleted = 0;
        var warnings = new List<string>();
        var cutoff = days is { } age ? DateTimeOffset.UtcNow.AddDays(-age) : (DateTimeOffset?)null;
        var activeDaemonDirectory = string.IsNullOrWhiteSpace(activeDaemonInstanceId)
            ? null
            : GetDaemonLogDirectory(activeDaemonInstanceId);
        var currentProcessLogFile = GetCurrentProcessLogFilePath();

        foreach (var logFile in EnumerateLogFiles())
        {
            try
            {
                if (PathsEqual(logFile, currentProcessLogFile))
                    continue;

                if (activeDaemonDirectory is not null && IsPathUnderDirectory(logFile, activeDaemonDirectory))
                    continue;

                var lastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(logFile), TimeSpan.Zero);
                if (cutoff is { } cutoffTime && lastWriteTime >= cutoffTime)
                    continue;

                File.Delete(logFile);
                deleted++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to clear log file '{NormalizePath(logFile)}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Failed to clear log file '{NormalizePath(logFile)}': {ex.Message}");
            }
        }

        CleanupEmptyDaemonDirectories(activeDaemonDirectory, warnings);
        return new LogClearResult(deleted, warnings);
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        if (!Directory.Exists(_rootLogDirectory))
            yield break;

        foreach (var logFile in Directory.EnumerateFiles(_rootLogDirectory, "*.log", SearchOption.AllDirectories))
            yield return logFile;
    }

    private IEnumerable<string> EnumerateDaemonDirectories()
    {
        if (!Directory.Exists(_rootLogDirectory))
            yield break;

        foreach (var directory in Directory.EnumerateDirectories(_rootLogDirectory, $"{DaemonFolderPrefix}*", SearchOption.TopDirectoryOnly))
            yield return directory;
    }

    private void CleanupEmptyDaemonDirectories(string? activeDaemonDirectory, List<string> warnings)
    {
        foreach (var directory in EnumerateDaemonDirectories())
        {
            try
            {
                if (activeDaemonDirectory is not null && PathsEqual(directory, activeDaemonDirectory))
                    continue;

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    continue;

                Directory.Delete(directory);
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to clear log directory '{NormalizePath(directory)}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Failed to clear log directory '{NormalizePath(directory)}': {ex.Message}");
            }
        }
    }

    private static string GetLogFilePath(string logDirectory)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var basePath = Path.Combine(logDirectory, $"copilotd-{date}.log");

        if (File.Exists(basePath))
        {
            var info = new FileInfo(basePath);
            if (info.Length >= MaxFileSize)
            {
                for (var i = 1; ; i++)
                {
                    var rolledPath = Path.Combine(logDirectory, $"copilotd-{date}-{i}.log");
                    if (!File.Exists(rolledPath) || new FileInfo(rolledPath).Length < MaxFileSize)
                        return rolledPath;
                }
            }
        }

        return basePath;
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        var fullPath = NormalizeFullPath(path);
        var fullDirectory = NormalizeFullPath(directory);
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, PathComparison)
            || string.Equals(fullPath, fullDirectory, PathComparison);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizeFullPath(left), NormalizeFullPath(right), PathComparison);

    private static string NormalizeFullPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizePath(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed record LogClearResult(int DeletedCount, IReadOnlyList<string> Warnings);
