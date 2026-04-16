using System.Text.Json;
using Copilotd.Models;

namespace Copilotd.Infrastructure;

public sealed class SessionLogManager
{
    public const string DaemonFolderName = "daemon";
    private const string MetadataFileName = "session.json";

    private readonly string _rootLogDirectory;

    public SessionLogManager()
    {
        _rootLogDirectory = CopilotdPaths.GetLogsDirectory();
        Directory.CreateDirectory(_rootLogDirectory);
    }

    public string RootLogDirectory => _rootLogDirectory;

    public string GetDaemonLogDirectory()
    {
        var path = Path.Combine(_rootLogDirectory, DaemonFolderName);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetSessionLogDirectory(string sessionId)
        => Path.Combine(_rootLogDirectory, sessionId);

    public void SyncState(DaemonState state)
    {
        Directory.CreateDirectory(_rootLogDirectory);

        foreach (var session in state.Sessions.Values)
        {
            var ensureDirectory = !session.IsTerminal;
            UpsertDispatchSession(session, ensureDirectory);
        }

        if (state.ControlSession is { } controlSession)
        {
            var ensureDirectory = controlSession.Status is ControlSessionStatus.Running or ControlSessionStatus.Starting;
            UpsertControlSession(controlSession, ensureDirectory);
        }
    }

    public void ClearSessionLogs(string sessionId)
    {
        var sessionLogDirectory = GetSessionLogDirectory(sessionId);
        if (Directory.Exists(sessionLogDirectory))
            Directory.Delete(sessionLogDirectory, recursive: true);
    }

    public SessionLogPurgeResult PurgeCompletedSessionLogsOlderThan(int days, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-days);
        var deleted = 0;
        var warnings = new List<string>();

        foreach (var sessionDirectory in EnumerateSessionDirectories())
        {
            try
            {
                var metadata = TryReadMetadata(sessionDirectory, warnings);
                if (metadata is null
                    || !string.Equals(metadata.SessionType, "dispatch", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(metadata.Status, nameof(SessionStatus.Completed), StringComparison.OrdinalIgnoreCase)
                    || metadata.UpdatedAt >= cutoff)
                {
                    continue;
                }

                Directory.Delete(sessionDirectory, recursive: true);
                deleted++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to purge log folder '{NormalizePath(sessionDirectory)}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Failed to purge log folder '{NormalizePath(sessionDirectory)}': {ex.Message}");
            }
        }

        return new SessionLogPurgeResult(deleted, warnings);
    }

    public SessionLogPurgeResult PurgeSessionLogsOlderThan(int days, DateTimeOffset now, IReadOnlySet<string> activeSessionIds)
    {
        var cutoff = now.AddDays(-days);
        var deleted = 0;
        var warnings = new List<string>();

        foreach (var sessionDirectory in EnumerateSessionDirectories())
        {
            try
            {
                var sessionId = Path.GetFileName(sessionDirectory);
                if (activeSessionIds.Contains(sessionId))
                    continue;

                var metadata = TryReadMetadata(sessionDirectory, warnings);
                var updatedAt = metadata?.UpdatedAt
                    ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(sessionDirectory), TimeSpan.Zero);

                if (updatedAt >= cutoff)
                    continue;

                Directory.Delete(sessionDirectory, recursive: true);
                deleted++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to purge log folder '{NormalizePath(sessionDirectory)}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Failed to purge log folder '{NormalizePath(sessionDirectory)}': {ex.Message}");
            }
        }

        return new SessionLogPurgeResult(deleted, warnings);
    }

    public string FormatPath(string path) => NormalizePath(path);

    public string GetDaemonLogDirectoryForDisplay()
        => NormalizePath(GetDaemonLogDirectory());

    public string GetSessionLogDirectoryForDisplay(string sessionId)
        => NormalizePath(GetSessionLogDirectory(sessionId));

    private void UpsertDispatchSession(DispatchSession session, bool ensureDirectory)
    {
        var metadata = new SessionLogMetadata
        {
            SessionId = session.CopilotSessionId,
            SessionType = "dispatch",
            IssueKey = session.IssueKey,
            Status = session.Status.ToString(),
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
        };

        UpsertMetadata(session.CopilotSessionId, metadata, ensureDirectory);
    }

    private void UpsertControlSession(ControlSessionInfo session, bool ensureDirectory)
    {
        var metadata = new SessionLogMetadata
        {
            SessionId = session.CopilotSessionId,
            SessionType = "control",
            Status = session.Status.ToString(),
            CreatedAt = session.StartedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = session.UpdatedAt ?? session.StartedAt ?? DateTimeOffset.UtcNow,
        };

        UpsertMetadata(session.CopilotSessionId, metadata, ensureDirectory);
    }

    private void UpsertMetadata(string sessionId, SessionLogMetadata metadata, bool ensureDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var sessionLogDirectory = GetSessionLogDirectory(sessionId);
        if (!ensureDirectory && !Directory.Exists(sessionLogDirectory))
            return;

        Directory.CreateDirectory(sessionLogDirectory);
        var json = JsonSerializer.Serialize(metadata, CopilotdJsonContext.Default.SessionLogMetadata);
        File.WriteAllText(Path.Combine(sessionLogDirectory, MetadataFileName), json);
    }

    private SessionLogMetadata? TryReadMetadata(string sessionDirectory, List<string>? warnings = null)
    {
        var metadataPath = Path.Combine(sessionDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize(json, CopilotdJsonContext.Default.SessionLogMetadata);
        }
        catch (JsonException ex)
        {
            warnings?.Add($"Failed to read log metadata '{NormalizePath(metadataPath)}': {ex.Message}");
            return null;
        }
    }

    private IEnumerable<string> EnumerateSessionDirectories()
    {
        if (!Directory.Exists(_rootLogDirectory))
            yield break;

        foreach (var directory in Directory.EnumerateDirectories(_rootLogDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            if (!string.Equals(folderName, DaemonFolderName, StringComparison.OrdinalIgnoreCase))
                yield return directory;
        }
    }

    private static string NormalizePath(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}

public sealed record SessionLogPurgeResult(int DeletedCount, IReadOnlyList<string> Warnings);
