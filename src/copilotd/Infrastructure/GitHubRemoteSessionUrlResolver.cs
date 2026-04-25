using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Resolves the actual github.com task URL for a Copilot remote session by reading
/// Copilot CLI log files. The browser task URL is not the same as the CLI resume ID.
/// </summary>
public sealed class GitHubRemoteSessionUrlResolver
{
    private const int MaxCandidateLogFiles = 200;
    private static readonly Regex RemoteSessionUrlPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\S+\s\[(?:INFO|DEBUG)\]\s(?:RemoteSessionExporter: active with session [^:]+:\s+|Remote session active \(steerable\):\s+|Remote session export active \(not steerable\):\s+)(https://github\.com/\S+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _logDir;
    private readonly string _sessionStateDir;
    private readonly ILogger<GitHubRemoteSessionUrlResolver> _logger;

    public const string ControlSessionRepo = "DamianEdwards/copilotd";

    public GitHubRemoteSessionUrlResolver(ILogger<GitHubRemoteSessionUrlResolver> logger)
    {
        _logger = logger;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logDir = Path.Combine(home, ".copilot", "logs");
        _sessionStateDir = Path.Combine(home, ".copilot", "session-state");
    }

    public string? TryResolve(DispatchSession session, string? currentUser)
    {
        if (session.ProcessId is { } pid && session.Status is SessionStatus.Dispatching or SessionStatus.Running)
        {
            var currentProcessUrl = TryResolveFromCurrentProcess(session.CopilotSessionId, pid, currentUser)
                ?? TryResolveBySessionName(session.CopilotSessionName, pid, currentUser);
            if (currentProcessUrl is not null)
                return currentProcessUrl;
        }

        return TryResolve(session.CopilotSessionId, session.ProcessId, currentUser)
            ?? TryResolveBySessionName(session.CopilotSessionName, session.ProcessId, currentUser);
    }

    public string? TryResolve(ControlSessionInfo session, string? currentUser)
    {
        if (session.ProcessId is { } pid && session.Status is ControlSessionStatus.Starting or ControlSessionStatus.Running)
        {
            var currentProcessUrl = TryResolveFromCurrentProcess(session.CopilotSessionId, pid, currentUser)
                ?? TryResolveBySessionName(session.CopilotSessionName, pid, currentUser);
            if (currentProcessUrl is not null)
                return currentProcessUrl;
        }

        return TryResolve(session.CopilotSessionId, session.ProcessId, currentUser)
            ?? TryResolveBySessionName(session.CopilotSessionName, session.ProcessId, currentUser);
    }

    public string? TryResolveTaskId(DispatchSession session, string? currentUser)
        => TryExtractTaskId(TryResolve(session, currentUser), out var taskId) ? taskId : null;

    public string? TryResolveTaskId(ControlSessionInfo session, string? currentUser)
        => TryExtractTaskId(TryResolve(session, currentUser), out var taskId) ? taskId : null;

    public string? TryResolve(string? sessionId, int? processId, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var sessionStateUrl = TryResolveFromSessionState(sessionId, currentUser);
        if (sessionStateUrl is not null)
            return sessionStateUrl;

        if (!Directory.Exists(_logDir))
            return null;

        foreach (var logPath in EnumerateCandidateLogFiles(processId))
        {
            try
            {
                var resolved = TryResolveFromLog(logPath, sessionId, currentUser);
                if (resolved is not null)
                    return resolved;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed reading Copilot log {Path}", logPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied reading Copilot log {Path}", logPath);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed parsing Copilot session state from {Path}", logPath);
            }
        }

        return null;
    }

    private string? TryResolveBySessionName(string? sessionName, int? processId, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            return null;

        var sessionId = TryResolveSessionIdFromSessionName(sessionName, processId);
        return sessionId is null ? null : TryResolve(sessionId, processId, currentUser);
    }

    private string? TryResolveFromCurrentProcess(string? sessionId, int processId, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(_logDir))
            return null;

        foreach (var logPath in EnumerateProcessLogFiles(processId))
        {
            try
            {
                var resolved = TryResolveFromLog(logPath, sessionId, currentUser);
                if (resolved is not null)
                    return resolved;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed reading Copilot log {Path}", logPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied reading Copilot log {Path}", logPath);
            }
        }

        return null;
    }

    public string? TryResolveTaskId(string? sessionId, int? processId, string? currentUser)
    {
        var url = TryResolve(sessionId, processId, currentUser);
        return TryExtractTaskId(url, out var taskId) ? taskId : null;
    }

    private string? TryResolveFromSessionState(string sessionId, string? currentUser)
    {
        var eventsPath = Path.Combine(_sessionStateDir, sessionId, "events.jsonl");
        if (!File.Exists(eventsPath))
            return null;

        string? resolvedUrl = null;
        foreach (var line in ReadLinesShared(eventsPath))
        {
            if (TryExtractRemoteTaskUrlFromEvent(line, out var url))
                resolvedUrl = AppendAuthorQuery(url!, currentUser);
        }

        return resolvedUrl;
    }

    private string? TryResolveSessionIdFromSessionName(string sessionName, int? processId)
    {
        var sessionId = TryResolveSessionIdFromSessionStateName(sessionName);
        if (sessionId is not null)
            return sessionId;

        if (processId is not { } pid)
            return null;

        foreach (var logPath in EnumerateProcessLogFiles(pid))
        {
            try
            {
                var resolved = TryResolveSessionIdFromLog(logPath);
                if (resolved is not null)
                    return resolved;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed reading Copilot log {Path}", logPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied reading Copilot log {Path}", logPath);
            }
        }

        return null;
    }

    private string? TryResolveSessionIdFromSessionStateName(string sessionName)
    {
        if (!Directory.Exists(_sessionStateDir))
            return null;

        foreach (var sessionDirectory in new DirectoryInfo(_sessionStateDir)
                     .EnumerateDirectories()
                     .OrderByDescending(directory => directory.LastWriteTimeUtc))
        {
            var workspacePath = Path.Combine(sessionDirectory.FullName, "workspace.yaml");
            if (!File.Exists(workspacePath))
                continue;

            foreach (var line in ReadLinesShared(workspacePath))
            {
                if (TryExtractWorkspaceName(line, out var workspaceName)
                    && string.Equals(workspaceName, sessionName, StringComparison.OrdinalIgnoreCase))
                {
                    return sessionDirectory.Name;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateCandidateLogFiles(int? processId)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (processId is { } pid)
        {
            foreach (var path in EnumerateProcessLogFiles(pid))
            {
                if (yielded.Add(path))
                    yield return path;
            }
        }

        foreach (var path in new DirectoryInfo(_logDir)
                     .EnumerateFiles("process-*.log", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(MaxCandidateLogFiles)
                     .Select(file => file.FullName))
        {
            if (yielded.Add(path))
                yield return path;
        }
    }

    private IEnumerable<string> EnumerateProcessLogFiles(int processId)
        => new DirectoryInfo(_logDir)
            .EnumerateFiles($"process-*-{processId}.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName);

    private string? TryResolveFromLog(string logPath, string sessionId, string? currentUser)
    {
        var matchedSession = false;

        foreach (var line in ReadLinesShared(logPath))
        {
            if (!matchedSession && LineReferencesSession(line, sessionId))
            {
                matchedSession = true;
                continue;
            }

            if (matchedSession && TryExtractRemoteTaskUrl(line, out var url))
                return AppendAuthorQuery(url!, currentUser);
        }

        return null;
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string? TryResolveSessionIdFromLog(string logPath)
    {
        foreach (var line in ReadLinesShared(logPath))
        {
            if (TryExtractSessionId(line, out var sessionId))
                return sessionId;
        }

        return null;
    }

    private static bool LineReferencesSession(string line, string sessionId)
    {
        if (line.Equals($@"  ""session_id"": ""{sessionId}"",", StringComparison.Ordinal)
            || line.Equals($@"""session_id"": ""{sessionId}"",", StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(line,
                $@"^\d{{4}}-\d{{2}}-\d{{2}}T\S+\s\[DEBUG\]\sFailed to get task {Regex.Escape(sessionId)}: 404$",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(line,
                $@"^\d{{4}}-\d{{2}}-\d{{2}}T\S+\s\[INFO\]\sCreating new session with provided ID: {Regex.Escape(sessionId)}$",
                RegexOptions.CultureInvariant)
             || Regex.IsMatch(line,
                 $@"^\d{{4}}-\d{{2}}-\d{{2}}T\S+\s\[INFO\]\sWorkspace initialized: {Regex.Escape(sessionId)} \(checkpoints: \d+\)$",
                 RegexOptions.CultureInvariant);
    }

    private static bool TryExtractSessionId(string line, out string? sessionId)
    {
        var workspaceMatch = Regex.Match(
            line,
            @"^\d{4}-\d{2}-\d{2}T\S+\s\[INFO\]\sWorkspace initialized:\s+([0-9a-fA-F-]{36})\s+\(checkpoints:\s+\d+\)$",
            RegexOptions.CultureInvariant);
        if (workspaceMatch.Success)
        {
            sessionId = workspaceMatch.Groups[1].Value;
            return true;
        }

        var jsonMatch = Regex.Match(
            line,
            @"^\s*""session_id"":\s+""([0-9a-fA-F-]{36})"",\s*$",
            RegexOptions.CultureInvariant);
        if (jsonMatch.Success)
        {
            sessionId = jsonMatch.Groups[1].Value;
            return true;
        }

        sessionId = null;
        return false;
    }

    private static bool TryExtractWorkspaceName(string line, out string? workspaceName)
    {
        const string NamePrefix = "name:";

        if (line.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            workspaceName = line[NamePrefix.Length..].Trim();
            return !string.IsNullOrEmpty(workspaceName);
        }

        workspaceName = null;
        return false;
    }

    private static bool TryExtractRemoteTaskUrl(string line, out string? url)
    {
        var match = RemoteSessionUrlPattern.Match(line);
        if (!match.Success)
        {
            url = null;
            return false;
        }

        url = match.Groups[1].Value;
        return url.Contains("/tasks/", StringComparison.Ordinal);
    }

    private static bool TryExtractRemoteTaskUrlFromEvent(string line, out string? url)
    {
        url = null;

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement)
            || !string.Equals(typeElement.GetString(), "session.info", StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("data", out var dataElement)
            || !dataElement.TryGetProperty("infoType", out var infoTypeElement)
            || !string.Equals(infoTypeElement.GetString(), "remote", StringComparison.Ordinal)
            || !dataElement.TryGetProperty("url", out var urlElement))
        {
            return false;
        }

        var candidate = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.Contains("/tasks/", StringComparison.Ordinal))
            return false;

        url = candidate;
        return true;
    }

    private static bool TryExtractTaskId(string? url, out string? taskId)
    {
        taskId = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!string.Equals(segments[i], "tasks", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Uri.UnescapeDataString(segments[i + 1]);
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            taskId = candidate;
            return true;
        }

        return false;
    }

    private static string AppendAuthorQuery(string url, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(currentUser))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = uri.Query.TrimStart('?');
        if (query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith("author=", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.IsNullOrEmpty(query)
                ? $"author={Uri.EscapeDataString(currentUser)}"
                : $"{query}&author={Uri.EscapeDataString(currentUser)}"
        };

        return builder.Uri.ToString();
    }
}
