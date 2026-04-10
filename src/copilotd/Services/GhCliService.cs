using System.Diagnostics;
using System.Text.Json;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// Adapter for the GitHub CLI (gh). Handles dependency checks, auth, repo listing, and issue queries.
/// </summary>
public sealed class GhCliService
{
    private readonly ILogger<GhCliService> _logger;
    private bool _supportsTypeField = true;

    // Cache collaborator permission checks to avoid excessive API calls (TTL: 15 minutes)
    private readonly Dictionary<string, (bool HasAccess, DateTimeOffset CheckedAt)> _collaboratorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CollaboratorCacheTtl = TimeSpan.FromMinutes(15);

    public GhCliService(ILogger<GhCliService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the gh CLI is available on PATH.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            var (exitCode, _) = RunGh("--version");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether gh is authenticated and returns the current username.
    /// </summary>
    public (bool IsLoggedIn, string? Username) CheckAuth()
    {
        try
        {
            var (exitCode, output) = RunGh("auth status");
            if (exitCode != 0)
                return (false, null);

            // Extract username from "Logged in to github.com account <username>"
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var idx = line.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var rest = line[(idx + 8)..].Trim();
                    var spaceIdx = rest.IndexOf(' ');
                    var username = spaceIdx > 0 ? rest[..spaceIdx] : rest;
                    // Strip trailing parenthetical/punctuation
                    username = username.TrimEnd(')', '(', ' ');
                    if (!string.IsNullOrEmpty(username))
                        return (true, username);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking gh auth");
            return (false, null);
        }
    }

    /// <summary>
    /// Lists repositories the user has access to.
    /// </summary>
    public List<string> ListRepos(int limit = 200)
    {
        var (exitCode, output) = RunGh($"repo list --limit {limit} --json nameWithOwner --jq \".[].nameWithOwner\"");
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to list repos: {Output}", output);
            return [];
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Queries open issues for a repo matching the given rule conditions.
    /// </summary>
    public List<GitHubIssue> QueryIssues(string repo, DispatchRule rule)
    {
        var jsonFields = _supportsTypeField
            ? "number,title,author,assignees,labels,milestone,type"
            : "number,title,author,assignees,labels,milestone";

        var args = $"issue list --repo {repo} --state open --json {jsonFields} --limit 100";

        if (rule.User is not null)
            args += $" --assignee {rule.User}";

        foreach (var label in rule.Labels)
            args += $" --label \"{label}\"";

        if (rule.Milestone is not null)
            args += $" --milestone \"{rule.Milestone}\"";

        var (exitCode, output) = RunGh(args);

        // Fall back to querying without 'type' if gh doesn't support it
        if (exitCode != 0 && _supportsTypeField && output.Contains("Unknown JSON field", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("gh CLI does not support 'type' JSON field, retrying without it");
            _supportsTypeField = false;
            args = $"issue list --repo {repo} --state open --json number,title,author,assignees,labels,milestone --limit 100";

            if (rule.User is not null)
                args += $" --assignee {rule.User}";

            foreach (var label in rule.Labels)
                args += $" --label \"{label}\"";

            if (rule.Milestone is not null)
                args += $" --milestone \"{rule.Milestone}\"";

            (exitCode, output) = RunGh(args);
        }

        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query issues for {Repo}: {Output}", repo, output);
            return [];
        }

        try
        {
            return ParseIssuesJson(output, repo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse issue JSON for {Repo}", repo);
            return [];
        }
    }

    private static List<GitHubIssue> ParseIssuesJson(string json, string repo)
    {
        using var doc = JsonDocument.Parse(json);
        var issues = new List<GitHubIssue>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var issue = new GitHubIssue
            {
                Number = element.GetProperty("number").GetInt32(),
                Title = element.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Repo = repo,
                Labels = [],
            };

            if (element.TryGetProperty("assignees", out var assignees) && assignees.GetArrayLength() > 0)
            {
                var first = assignees[0];
                issue.Assignee = first.TryGetProperty("login", out var login) ? login.GetString() : null;
            }

            if (element.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.Object)
            {
                issue.Author = authorEl.TryGetProperty("login", out var authorLogin) ? authorLogin.GetString() : null;
            }

            if (element.TryGetProperty("labels", out var labels))
            {
                foreach (var label in labels.EnumerateArray())
                {
                    var name = label.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null)
                        issue.Labels.Add(name);
                }
            }

            if (element.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object)
            {
                issue.Milestone = ms.TryGetProperty("title", out var mt) ? mt.GetString() : null;
            }

            if (element.TryGetProperty("type", out var typeEl))
            {
                if (typeEl.ValueKind == JsonValueKind.Object)
                    issue.Type = typeEl.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                else if (typeEl.ValueKind == JsonValueKind.String)
                    issue.Type = typeEl.GetString();
            }

            issues.Add(issue);
        }

        return issues;
    }

    /// <summary>
    /// Marker appended to comments posted by copilotd, used to distinguish
    /// bot-posted comments from human replies when checking for new feedback.
    /// Invisible on GitHub (HTML comment).
    /// </summary>
    internal const string CommentMarker = "<!-- posted by copilotd -->";

    /// <summary>
    /// Posts a comment on a GitHub issue. Appends a hidden marker so copilotd
    /// can distinguish its own comments from human replies.
    /// </summary>
    public bool PostIssueComment(string repo, int issueNumber, string message)
    {
        var body = message + "\n\n" + CommentMarker;

        // Use --body-file - to pipe via stdin, avoiding all shell escaping issues
        var args = $"issue comment {issueNumber} --repo {repo} --body-file -";

        _logger.LogDebug("Running: gh {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.Write(body);
        process.StandardInput.Close();

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(GhTimeout))
        {
            _logger.LogWarning("gh command timed out posting comment on {Repo}#{Issue}", repo, issueNumber);
            process.Kill();
            return false;
        }

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.Result;
            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            _logger.LogWarning("Failed to post comment on {Repo}#{Issue}: {Output}", repo, issueNumber, output);
            return false;
        }

        _logger.LogInformation("Posted comment on {Repo}#{Issue}", repo, issueNumber);
        return true;
    }

    /// <summary>
    /// Information about a new comment detected on an issue or PR.
    /// </summary>
    public sealed record NewCommentInfo(string Author, DateTimeOffset CreatedAt);

    /// <summary>
    /// Checks whether there are new comments on an issue since the given timestamp,
    /// excluding comments posted by copilotd itself (identified by <see cref="CommentMarker"/>).
    /// </summary>
    public bool HasNewCommentsSince(string repo, int issueNumber, DateTimeOffset since)
        => GetNewCommentSince(repo, issueNumber, since) is not null;

    /// <summary>
    /// Returns info about the first new non-bot comment on an issue since the given timestamp,
    /// or null if no new comments exist. Excludes comments posted by copilotd.
    /// </summary>
    public NewCommentInfo? GetNewCommentSince(string repo, int issueNumber, DateTimeOffset since)
    {
        var args = $"issue view {issueNumber} --repo {repo} --json comments";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query comments on {Repo}#{Issue}: {Output}", repo, issueNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("comments", out var comments))
                return null;

            foreach (var comment in comments.EnumerateArray())
            {
                if (!comment.TryGetProperty("createdAt", out var createdAtEl))
                    continue;

                var createdAtStr = createdAtEl.GetString();
                if (createdAtStr is null || !DateTimeOffset.TryParse(createdAtStr, out var createdAt))
                    continue;

                if (createdAt <= since)
                    continue;

                // Skip comments posted by copilotd (identified by hidden marker)
                if (comment.TryGetProperty("body", out var bodyEl))
                {
                    var body = bodyEl.GetString();
                    if (body is not null && body.Contains(CommentMarker, StringComparison.Ordinal))
                        continue;
                }

                var author = comment.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l)
                    ? l.GetString() ?? "unknown"
                    : "unknown";

                _logger.LogDebug("Found new comment on {Repo}#{Issue} from {Author}", repo, issueNumber, author);
                return new NewCommentInfo(author, createdAt);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse comments JSON for {Repo}#{Issue}", repo, issueNumber);
            return null;
        }
    }

    /// <summary>
    /// Checks whether a user has write (or higher) access to a repository.
    /// Results are cached for 15 minutes to avoid excessive API calls.
    /// </summary>
    public bool HasWriteAccess(string repo, string username)
    {
        var cacheKey = $"{repo}/{username}";

        // Check cache first
        if (_collaboratorCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CheckedAt < CollaboratorCacheTtl)
        {
            _logger.LogDebug("Collaborator cache hit for {User} on {Repo}: {HasAccess}", username, repo, cached.HasAccess);
            return cached.HasAccess;
        }

        var args = $"api repos/{repo}/collaborators/{username}/permission --jq .permission";
        var (exitCode, output) = RunGh(args);

        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to check permissions for {User} on {Repo}: {Output}", username, repo, output);
            // On API failure, deny by default (fail-closed)
            _collaboratorCache[cacheKey] = (false, DateTimeOffset.UtcNow);
            return false;
        }

        var permission = output.Trim().ToLowerInvariant();
        var hasAccess = permission is "admin" or "maintain" or "write";

        _collaboratorCache[cacheKey] = (hasAccess, DateTimeOffset.UtcNow);
        _logger.LogDebug("Permission check for {User} on {Repo}: {Permission} (hasAccess={HasAccess})",
            username, repo, permission, hasAccess);

        return hasAccess;
    }

    /// <summary>
    /// Checks whether there are new review comments on a pull request since the given timestamp,
    /// excluding comments posted by copilotd itself (identified by <see cref="CommentMarker"/>).
    /// Checks both PR review comments (from formal reviews) and regular PR comments.
    /// </summary>
    public bool HasNewPrReviewCommentsSince(string repo, int prNumber, DateTimeOffset since)
        => GetNewPrReviewCommentSince(repo, prNumber, since) is not null;

    /// <summary>
    /// Returns info about the first new non-bot review comment or PR comment since the given timestamp,
    /// or null if no new comments exist. Checks both regular PR comments and formal review submissions.
    /// Note: Does not detect individual review-thread replies (only top-level comments and formal
    /// review submissions). Detecting thread replies would require GraphQL queries against reviewThreads.
    /// </summary>
    public NewCommentInfo? GetNewPrReviewCommentSince(string repo, int prNumber, DateTimeOffset since)
    {
        var args = $"pr view {prNumber} --repo {repo} --json comments,reviews";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query PR comments on {Repo}#{Pr}: {Output}", repo, prNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);

            // Check regular PR comments
            if (doc.RootElement.TryGetProperty("comments", out var comments))
            {
                foreach (var comment in comments.EnumerateArray())
                {
                    var info = ExtractNewNonBotComment(comment, since, "createdAt");
                    if (info is not null)
                    {
                        _logger.LogDebug("Found new PR comment on {Repo}!{Pr} from {Author}", repo, prNumber, info.Author);
                        return info;
                    }
                }
            }

            // Check formal review submissions (requested changes, comments)
            if (doc.RootElement.TryGetProperty("reviews", out var reviews))
            {
                foreach (var review in reviews.EnumerateArray())
                {
                    if (!review.TryGetProperty("submittedAt", out var submittedAtEl))
                        continue;

                    var submittedAtStr = submittedAtEl.GetString();
                    if (submittedAtStr is null || !DateTimeOffset.TryParse(submittedAtStr, out var submittedAt))
                        continue;

                    if (submittedAt <= since)
                        continue;

                    // Skip empty/approved-only reviews with no body
                    if (review.TryGetProperty("body", out var bodyEl))
                    {
                        var body = bodyEl.GetString();
                        if (body is not null && body.Contains(CommentMarker, StringComparison.Ordinal))
                            continue;
                    }

                    // Check review state — we care about CHANGES_REQUESTED and COMMENTED
                    if (review.TryGetProperty("state", out var stateEl))
                    {
                        var reviewState = stateEl.GetString();
                        if (reviewState is "CHANGES_REQUESTED" or "COMMENTED")
                        {
                            var author = review.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l)
                                ? l.GetString() ?? "unknown"
                                : "unknown";
                            _logger.LogDebug("Found new review ({State}) on {Repo}!{Pr} from {Author}", reviewState, repo, prNumber, author);
                            return new NewCommentInfo(author, submittedAt);
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse PR comments JSON for {Repo}!{Pr}", repo, prNumber);
            return null;
        }
    }

    /// <summary>
    /// Gets the current state of a pull request (e.g., OPEN, CLOSED, MERGED).
    /// Returns null if the PR state cannot be determined.
    /// </summary>
    public string? GetPullRequestState(string repo, int prNumber)
    {
        var args = $"pr view {prNumber} --repo {repo} --json state";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query PR state for {Repo}!{Pr}: {Output}", repo, prNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("state", out var stateEl))
                return stateEl.GetString();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse PR state JSON for {Repo}!{Pr}", repo, prNumber);
            return null;
        }
    }

    /// <summary>
    /// Extracts comment info from a JSON element if it's a new non-bot comment.
    /// Returns null if the comment is old, from copilotd, or has no timestamp.
    /// </summary>
    private NewCommentInfo? ExtractNewNonBotComment(JsonElement comment, DateTimeOffset since, string timestampField = "createdAt")
    {
        if (!comment.TryGetProperty(timestampField, out var createdAtEl))
            return null;

        var createdAtStr = createdAtEl.GetString();
        if (createdAtStr is null || !DateTimeOffset.TryParse(createdAtStr, out var createdAt))
            return null;

        if (createdAt <= since)
            return null;

        // Skip comments posted by copilotd
        if (comment.TryGetProperty("body", out var bodyEl))
        {
            var body = bodyEl.GetString();
            if (body is not null && body.Contains(CommentMarker, StringComparison.Ordinal))
                return null;
        }

        var author = comment.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l)
            ? l.GetString() ?? "unknown"
            : "unknown";

        return new NewCommentInfo(author, createdAt);
    }

    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(30);

    private (int ExitCode, string Output) RunGh(string arguments)
    {
        _logger.LogDebug("Running: gh {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;

        // Read stderr asynchronously to avoid deadlock when pipe buffers fill
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(GhTimeout))
        {
            _logger.LogWarning("gh command timed out after {Timeout}s: gh {Args}", GhTimeout.TotalSeconds, arguments);
            process.Kill();
            return (-1, "gh command timed out");
        }

        var stderr = stderrTask.Result;
        var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
        return (process.ExitCode, output);
    }
}
