using System.Text;
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
    private readonly ILogger<GitHubRemoteSessionUrlResolver> _logger;

    public const string ControlSessionRepo = "DamianEdwards/copilotd";

    public GitHubRemoteSessionUrlResolver(ILogger<GitHubRemoteSessionUrlResolver> logger)
    {
        _logger = logger;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logDir = Path.Combine(home, ".copilot", "logs");
    }

    public string? TryResolve(DispatchSession session, string? currentUser)
        => TryResolve(session.CopilotSessionId, session.ProcessId, currentUser);

    public string? TryResolve(ControlSessionInfo session, string? currentUser)
        => TryResolve(session.CopilotSessionId, session.ProcessId, currentUser);

    public string? TryResolve(string? sessionId, int? processId, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(_logDir))
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
        }

        return null;
    }

    private IEnumerable<string> EnumerateCandidateLogFiles(int? processId)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (processId is { } pid)
        {
            foreach (var path in Directory.EnumerateFiles(_logDir, $"process-*-{pid}.log", SearchOption.TopDirectoryOnly))
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

    private string? TryResolveFromLog(string logPath, string sessionId, string? currentUser)
    {
        var matchedSession = false;

        foreach (var line in File.ReadLines(logPath, Encoding.UTF8))
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
