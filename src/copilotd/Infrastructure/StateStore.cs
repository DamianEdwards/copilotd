using System.Text.Json;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Handles atomic read/write of config.json and state.json under ~/.copilotd.
/// Writes use temp-then-rename for crash safety. Corrupt/missing files are
/// treated as empty (self-healing).
/// </summary>
public sealed class StateStore
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _statePath;
    private readonly string _lockPath;
    private readonly string _pidPath;
    private readonly ILogger<StateStore> _logger;

    public string ConfigDir => _configDir;

    private string PromptPath => Path.Combine(_configDir, "prompt.md");

    public StateStore(ILogger<StateStore> logger)
    {
        _logger = logger;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configDir = Path.Combine(home, ".copilotd");
        _configPath = Path.Combine(_configDir, "config.json");
        _statePath = Path.Combine(_configDir, "state.json");
        _lockPath = Path.Combine(_configDir, ".lock");
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

    // --- Prompt ---

    /// <summary>
    /// Loads the user's custom prompt addition from ~/.copilotd/prompt.md if it exists
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
        => NormalizeForComparison(content) == NormalizeForComparison(CopilotdConfig.DefaultPrompt);

    /// <summary>
    /// If the content starts with the default prompt (e.g. a user appended to the old
    /// auto-generated prompt.md), strips the default prefix and returns only the custom part.
    /// </summary>
    private static string StripDefaultPromptPrefix(string content)
    {
        var normalizedContent = NormalizeForComparison(content);
        var normalizedDefault = NormalizeForComparison(CopilotdConfig.DefaultPrompt);

        if (normalizedContent.StartsWith(normalizedDefault, StringComparison.Ordinal))
        {
            var remainder = content.Trim()[CopilotdConfig.DefaultPrompt.Trim().Length..].Trim();
            return remainder;
        }

        return content;
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

    // --- Daemon PID tracking ---

    /// <summary>
    /// Writes the current process ID and start time to ~/.copilotd/.pid.
    /// Used by the stop command to locate and verify the daemon process.
    /// </summary>
    private void WriteDaemonPid()
    {
        try
        {
            var startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var content = $"{Environment.ProcessId}\n{startTime:O}";
            File.WriteAllText(_pidPath, content);
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
    public (int Pid, DateTimeOffset StartTime)? ReadDaemonPid()
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
                return (pid, startTime);
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

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
