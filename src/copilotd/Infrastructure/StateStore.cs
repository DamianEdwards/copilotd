using System.Text.Json;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Handles atomic read/write of config.json and state.json under copilotd's home
/// directory (defaults to ~/.copilotd, overrideable with COPILOTD_HOME).
/// Writes use temp-then-rename for crash safety. Corrupt/missing files are
/// treated as empty (self-healing).
/// </summary>
public sealed class StateStore
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _statePath;
    private readonly string _updateStatePath;
    private readonly string _machineIdentityDir;
    private readonly string _machineIdentifierPath;
    private readonly string _machineIdentifierLockPath;
    private readonly string _lockPath;
    private readonly string _stateLockPath;
    private readonly string _updateLockPath;
    private readonly string _pidPath;
    private readonly LogFileManager _logFileManager;
    private readonly ILogger<StateStore> _logger;

    public string ConfigDir => _configDir;

    private string PromptPath => Path.Combine(_configDir, "prompt.md");

    public StateStore(LogFileManager logFileManager, ILogger<StateStore> logger)
    {
        _logFileManager = logFileManager;
        _logger = logger;
        _configDir = CopilotdPaths.GetCopilotdHomeDirectory();
        _configPath = Path.Combine(_configDir, "config.json");
        _statePath = Path.Combine(_configDir, "state.json");
        _updateStatePath = Path.Combine(_configDir, "update-state.json");
        _machineIdentityDir = CopilotdPaths.GetMachineIdentityDirectory();
        _machineIdentifierPath = CopilotdPaths.GetMachineIdentifierPath();
        _machineIdentifierLockPath = Path.Combine(_machineIdentityDir, ".machine-id.lock");
        _lockPath = Path.Combine(_configDir, ".lock");
        _stateLockPath = Path.Combine(_configDir, ".state-lock");
        _updateLockPath = Path.Combine(_configDir, ".update-lock");
        _pidPath = Path.Combine(_configDir, ".pid");
        Directory.CreateDirectory(_configDir);
    }

    public bool ConfigExists() => File.Exists(_configPath);

    // --- Config ---

    public CopilotdConfig LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogDebug("No config file found, returning defaults");
            return new CopilotdConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize(json, CopilotdJsonContext.Default.CopilotdConfig)
                   ?? new CopilotdConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config file is corrupt or unreadable, returning defaults");
            return new CopilotdConfig();
        }
    }

    public void SaveConfig(CopilotdConfig config)
    {
        var json = JsonSerializer.Serialize(config, CopilotdJsonContext.Default.CopilotdConfig);
        AtomicWrite(_configPath, json);
        _logger.LogDebug("Config saved to {Path}", _configPath);
    }

    // --- State ---

    public DaemonState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            _logger.LogDebug("No state file found, returning empty state");
            return new DaemonState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize(json, CopilotdJsonContext.Default.DaemonState)
                   ?? new DaemonState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State file is corrupt or unreadable, treating as empty state");
            return new DaemonState();
        }
    }

    public void SaveState(DaemonState state)
    {
        var json = JsonSerializer.Serialize(state, CopilotdJsonContext.Default.DaemonState);
        AtomicWrite(_statePath, json);
        _logger.LogDebug("State saved to {Path}", _statePath);
    }

    public string? GetMachineIdentifier()
    {
        return ReadMachineIdentifierFromFile();
    }

    public string EnsureMachineIdentifier(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_machineIdentityDir);

        using var lockStream = AcquireExclusiveLock(_machineIdentifierLockPath, ct);

        var persistedIdentifier = ReadMachineIdentifierFromFile();
        if (persistedIdentifier is not null)
            return persistedIdentifier;

        var machineIdentifier = Guid.NewGuid().ToString("D");

        AtomicWrite(_machineIdentifierPath, machineIdentifier);
        _logger.LogDebug("Machine identifier saved to {Path}", _machineIdentifierPath);
        return machineIdentifier;
    }

    /// <summary>
    /// Serializes state mutations across daemon and CLI commands so load-modify-save
    /// sequences cannot overwrite each other.
    /// </summary>
    public T WithStateLock<T>(Func<T> action, CancellationToken ct = default)
    {
        using var lockStream = AcquireExclusiveLock(_stateLockPath, ct);
        return action();
    }

    public void WithStateLock(Action action, CancellationToken ct = default)
        => WithStateLock(() =>
        {
            action();
            return 0;
        }, ct);

    // --- Prompt ---

    /// <summary>
    /// Loads the user's custom prompt addition from the copilotd home directory's prompt.md if it exists
    /// and contains non-default content, falling back to the config's inline Prompt property.
    /// Returns empty string if no custom prompt is configured.
    /// </summary>
    public string LoadCustomPrompt(CopilotdConfig config)
    {
        if (File.Exists(PromptPath))
        {
            try
            {
                var content = File.ReadAllText(PromptPath).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    // Strip the old default prompt prefix if present (backward compat
                    // for users who appended to the auto-generated prompt.md)
                    content = StripDefaultPromptPrefix(content);
                    if (!string.IsNullOrEmpty(content))
                    {
                        _logger.LogDebug("Loaded custom prompt from {Path}", PromptPath);
                        return content;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read prompt.md, falling back to config prompt");
            }
        }

        var configPrompt = config.Prompt.Trim();
        if (!string.IsNullOrEmpty(configPrompt) && !IsDefaultPrompt(configPrompt))
        {
            return configPrompt;
        }

        return "";
    }

    /// <summary>
    /// Returns true if the content matches the built-in default prompt (backward compat).
    /// Older versions wrote the default prompt to prompt.md and config.Prompt; these
    /// should be treated as "no custom prompt" rather than appended content.
    /// </summary>
    private static bool IsDefaultPrompt(string content)
        => GetDefaultPromptVariants()
            .Select(NormalizeForComparison)
            .Any(defaultPrompt => NormalizeForComparison(content) == defaultPrompt);

    /// <summary>
    /// If the content starts with the default prompt (e.g. a user appended to the old
    /// auto-generated prompt.md), strips the default prefix and returns only the custom part.
    /// </summary>
    private static string StripDefaultPromptPrefix(string content)
    {
        var normalizedContent = NormalizeForComparison(content);
        foreach (var defaultPrompt in GetDefaultPromptVariants())
        {
            var normalizedDefault = NormalizeForComparison(defaultPrompt);
            if (normalizedContent.StartsWith(normalizedDefault, StringComparison.Ordinal))
            {
                var remainder = content.Trim()[defaultPrompt.Trim().Length..].Trim();
                return remainder;
            }
        }

        return content;
    }

    private static IEnumerable<string> GetDefaultPromptVariants()
    {
        yield return CopilotdConfig.DefaultPrompt;
        yield return CopilotdConfig.DefaultPrompt.Replace("$(copilotd.command)", "copilotd", StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string s)
        => s.Trim().ReplaceLineEndings("\n");

    // --- Single-instance guard ---

    private FileStream? _lockStream;

    /// <summary>
    /// Attempts to acquire an exclusive lock. Returns false if another instance holds it.
    /// Writes daemon PID and start time to a .pid file for the stop command.
    /// </summary>
    public bool TryAcquireLock()
    {
        try
        {
            _lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WriteDaemonPid();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void ReleaseLock()
    {
        // Clear PID file BEFORE releasing lock to prevent a new daemon from
        // writing its PID and then having it deleted by the old daemon
        ClearDaemonPid();
        _lockStream?.Dispose();
        _lockStream = null;
        try { File.Delete(_lockPath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Checks whether the daemon lock file is currently held by another process
    /// without acquiring it. Returns true if a daemon instance is running.
    /// </summary>
    public bool IsLockHeld()
    {
        if (!File.Exists(_lockPath))
            return false;

        try
        {
            using var fs = new FileStream(_lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            // We were able to open it exclusively, so no daemon holds the lock
            return false;
        }
        catch (IOException)
        {
            // Another process holds the lock
            return true;
        }
    }

    /// <summary>
    /// Attempts to acquire the daemon lock file without writing daemon PID metadata.
    /// Used by the staged installer to prevent a new daemon from starting while the
    /// binary replacement is in progress.
    /// </summary>
    public FileStream? TryAcquireInstallWindowLock()
    {
        try
        {
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // --- Daemon PID tracking ---

    /// <summary>
    /// Writes the current process ID and start time to the copilotd home directory's .pid file.
    /// Used by the stop command to locate and verify the daemon process.
    /// </summary>
    private void WriteDaemonPid()
    {
        try
        {
            var startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var lines = new List<string>
            {
                Environment.ProcessId.ToString(),
                startTime.ToString("O")
            };

            if (_logFileManager.CurrentDaemonInstanceId is { } daemonInstanceId)
                lines.Add(daemonInstanceId);

            AtomicWrite(_pidPath, string.Join(Environment.NewLine, lines));
            _logger.LogDebug("Wrote daemon PID {Pid} to {Path}", Environment.ProcessId, _pidPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write daemon PID file");
        }
    }

    /// <summary>
    /// Reads the daemon PID and start time from the .pid file.
    /// Returns null if the file is missing, corrupt, or unreadable.
    /// </summary>
    public (int Pid, DateTimeOffset StartTime, string? LogInstanceId)? ReadDaemonPid()
    {
        if (!File.Exists(_pidPath))
            return null;

        try
        {
            var lines = File.ReadAllLines(_pidPath);
            if (lines.Length >= 2
                && int.TryParse(lines[0].Trim(), out var pid)
                && DateTimeOffset.TryParse(lines[1].Trim(), out var startTime))
            {
                var logInstanceId = lines.Length >= 3 ? lines[2].Trim() : null;
                if (string.IsNullOrWhiteSpace(logInstanceId))
                    logInstanceId = null;

                return (pid, startTime, logInstanceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read daemon PID file");
        }

        return null;
    }

    private void ClearDaemonPid()
    {
        try { File.Delete(_pidPath); } catch { /* best effort */ }
    }

    // --- Helpers ---

    private string? ReadMachineIdentifierFromFile()
    {
        if (!File.Exists(_machineIdentifierPath))
            return null;

        try
        {
            var persistedIdentifier = File.ReadAllText(_machineIdentifierPath).Trim();
            return Guid.TryParse(persistedIdentifier, out var parsedIdentifier)
                ? parsedIdentifier.ToString("D")
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read machine identifier from {Path}", _machineIdentifierPath);
            return null;
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private FileStream AcquireExclusiveLock(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Timed out waiting to acquire state lock '{path}'.", ex);

                if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                    ct.ThrowIfCancellationRequested();
            }
        }
    }

    // --- Update state ---

    public UpdateState LoadUpdateState()
    {
        if (!File.Exists(_updateStatePath))
        {
            _logger.LogDebug("No update state file found, returning defaults");
            return new UpdateState();
        }

        try
        {
            var json = File.ReadAllText(_updateStatePath);
            return JsonSerializer.Deserialize(json, CopilotdJsonContext.Default.UpdateState)
                   ?? new UpdateState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update state file is corrupt or unreadable, returning defaults");
            return new UpdateState();
        }
    }

    public void SaveUpdateState(UpdateState state)
    {
        var json = JsonSerializer.Serialize(state, CopilotdJsonContext.Default.UpdateState);
        AtomicWrite(_updateStatePath, json);
        _logger.LogDebug("Update state saved to {Path}", _updateStatePath);
    }

    public void ClearUpdateState()
    {
        try { File.Delete(_updateStatePath); } catch { /* best effort */ }
        _logger.LogDebug("Update state cleared");
    }

    // --- Update lock (separate from daemon lock) ---

    private FileStream? _updateLockStream;

    /// <summary>Maximum age of an update lock before it's considered stale.</summary>
    private static readonly TimeSpan UpdateLockStaleThreshold = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Acquires an exclusive lock for update operations. Prevents concurrent
    /// updates from the daemon and manual <c>copilotd update</c> invocations.
    /// If a stale lock is detected (holder process dead or lock too old), it is
    /// automatically recovered to prevent terminal locked states.
    /// </summary>
    public bool TryAcquireUpdateLock()
    {
        try
        {
            _updateLockStream = new FileStream(_updateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WriteUpdateLockInfo();
            return true;
        }
        catch (IOException)
        {
            // Lock is held — check if it's stale
            if (IsUpdateLockStale())
            {
                _logger.LogWarning("Detected stale update lock, recovering");
                try
                {
                    File.Delete(_updateLockPath);
                    _updateLockStream = new FileStream(_updateLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    WriteUpdateLockInfo();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to recover stale update lock");
                }
            }

            return false;
        }
    }

    public void ReleaseUpdateLock()
    {
        _updateLockStream?.Dispose();
        _updateLockStream = null;
        try { File.Delete(_updateLockPath); } catch { /* best effort */ }
    }

    public bool IsUpdateLockHeld()
    {
        if (!File.Exists(_updateLockPath))
            return false;

        try
        {
            using var fs = new FileStream(_updateLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return !IsUpdateLockStale();
        }
    }

    private void WriteUpdateLockInfo()
    {
        try
        {
            if (_updateLockStream is null) return;
            _updateLockStream.SetLength(0);
            using var writer = new StreamWriter(_updateLockStream, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId);
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
            writer.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write update lock info");
        }
    }

    /// <summary>
    /// Checks whether the existing update lock file is stale by verifying:
    /// 1. The lock holder process is no longer running, or
    /// 2. The lock is older than the stale threshold.
    /// </summary>
    private bool IsUpdateLockStale()
    {
        try
        {
            if (!File.Exists(_updateLockPath))
                return false;

            // Check file age as a simple heuristic
            var lockAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_updateLockPath);
            if (lockAge > UpdateLockStaleThreshold)
                return true;

            // Try to read PID from the lock file and check if process is alive
            var lines = File.ReadAllLines(_updateLockPath);
            if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out var pid))
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    // Process exists — lock is valid
                    proc.Dispose();
                    return false;
                }
                catch (ArgumentException)
                {
                    // Process not found — lock is stale
                    return true;
                }
            }

            // Can't determine — assume stale if file is old enough (>1 minute)
            return lockAge > TimeSpan.FromMinutes(1);
        }
        catch
        {
            return false;
        }
    }
}
