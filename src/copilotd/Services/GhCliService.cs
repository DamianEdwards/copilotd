using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

public enum GitHubRepoAccessKind
{
    Owned,
    WriteAccess,
}

public sealed class AccessibleGitHubRepo
{
    public string NameWithOwner { get; init; } = "";
    public GitHubRepoAccessKind AccessKind { get; init; }
}

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
    /// Returns the gh CLI version string, or null if unavailable.
    /// </summary>
    public string? GetVersion()
    {
        try
        {
            var (exitCode, output) = RunGh("--version");
            if (exitCode != 0) return null;

            // Output is like "gh version 2.50.0 (2024-05-29)"
            var trimmed = output.Trim();
            var idx = trimmed.IndexOf('\n');
            return idx > 0 ? trimmed[..idx].Trim() : trimmed;
        }
        catch
        {
            return null;
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
    /// Lists repositories the user owns.
    /// </summary>
    public List<AccessibleGitHubRepo> ListOwnedRepos(int limit = 200)
        => ListOwnedReposFallback(limit);

    /// <summary>
    /// Lists repositories the user can watch during init:
    /// repos they own plus repos where they have write-or-better access.
    /// This can be expensive for users who belong to large organizations because
    /// GitHub's API does not support server-side filtering by write permission.
    /// </summary>
    public List<AccessibleGitHubRepo> ListAccessibleRepos(string? currentUsername = null)
    {
        var repos = new Dictionary<string, AccessibleGitHubRepo>(StringComparer.OrdinalIgnoreCase);
        currentUsername ??= CheckAuth().Username;

        var (exitCode, output) = RunGh(
            "api --paginate --slurp \"user/repos?per_page=100&affiliation=owner,collaborator,organization_member\"");
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to list accessible repos via REST API: {Output}", output);
            return ListOwnedReposFallback();
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            foreach (var page in doc.RootElement.EnumerateArray())
            {
                foreach (var repo in page.EnumerateArray())
                {
                    var fullName = repo.TryGetProperty("full_name", out var fullNameEl)
                        ? fullNameEl.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(fullName))
                        continue;

                    var ownerLogin = repo.TryGetProperty("owner", out var ownerEl)
                        && ownerEl.TryGetProperty("login", out var ownerLoginEl)
                        ? ownerLoginEl.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(ownerLogin))
                        continue;

                    var isOwnedByViewer = !string.IsNullOrWhiteSpace(currentUsername)
                        && string.Equals(ownerLogin, currentUsername, StringComparison.OrdinalIgnoreCase);

                    if (!isOwnedByViewer && !HasWriteOrBetter(repo))
                        continue;

                    repos[fullName] = new AccessibleGitHubRepo
                    {
                        NameWithOwner = fullName,
                        AccessKind = isOwnedByViewer ? GitHubRepoAccessKind.Owned : GitHubRepoAccessKind.WriteAccess,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse accessible repo REST response");
            return ListOwnedReposFallback();
        }

        return repos.Values
            .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Searches for repositories the current user can access and returns only owned
    /// or write-access matches. Intended for narrowing the expensive write-access
    /// not-cloned picker during init.
    /// </summary>
    public List<AccessibleGitHubRepo> SearchAccessibleRepos(string searchTerm, string? currentUsername = null, int limit = 50)
    {
        currentUsername ??= CheckAuth().Username;
        var query = BuildRepositorySearchQuery(searchTerm);
        var request = new JsonObject
        {
            ["query"] = """
                query($query: String!, $limit: Int!) {
                    search(type: REPOSITORY, query: $query, first: $limit) {
                        nodes {
                            ... on Repository {
                                nameWithOwner
                                viewerPermission
                                owner {
                                    login
                                }
                            }
                        }
                    }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["query"] = query,
                ["limit"] = limit,
            },
        };

        var (exitCode, output) = RunGhWithStdin("api graphql --input -", request.ToJsonString());
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to search accessible repos for '{SearchTerm}': {Output}", searchTerm, output);
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                _logger.LogWarning("GraphQL errors searching repos for '{SearchTerm}': {Errors}", searchTerm, errors.ToString());
                return [];
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("search", out var search)
                || !search.TryGetProperty("nodes", out var nodes))
            {
                return [];
            }

            var repos = new Dictionary<string, AccessibleGitHubRepo>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.EnumerateArray())
            {
                var nameWithOwner = node.TryGetProperty("nameWithOwner", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                var ownerLogin = node.TryGetProperty("owner", out var ownerEl)
                    && ownerEl.TryGetProperty("login", out var ownerLoginEl)
                    ? ownerLoginEl.GetString()
                    : null;
                var viewerPermission = node.TryGetProperty("viewerPermission", out var permissionEl)
                    ? permissionEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(nameWithOwner) || string.IsNullOrWhiteSpace(ownerLogin))
                    continue;

                var isOwnedByViewer = !string.IsNullOrWhiteSpace(currentUsername)
                    && string.Equals(ownerLogin, currentUsername, StringComparison.OrdinalIgnoreCase);

                if (!isOwnedByViewer && !HasWriteOrBetter(viewerPermission))
                    continue;

                repos[nameWithOwner] = new AccessibleGitHubRepo
                {
                    NameWithOwner = nameWithOwner,
                    AccessKind = isOwnedByViewer ? GitHubRepoAccessKind.Owned : GitHubRepoAccessKind.WriteAccess,
                };
            }

            return repos.Values
                .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse accessible repo search response for '{SearchTerm}'", searchTerm);
            return [];
        }
    }

    private List<AccessibleGitHubRepo> ListOwnedReposFallback(int limit = 200)
    {
        var (exitCode, output) = RunGh($"repo list --limit {limit} --json nameWithOwner --jq \".[].nameWithOwner\"");
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to list fallback owned repos: {Output}", output);
            return [];
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(nameWithOwner => new AccessibleGitHubRepo
            {
                NameWithOwner = nameWithOwner,
                AccessKind = GitHubRepoAccessKind.Owned,
            })
            .ToList();
    }

    /// <summary>
    /// Queries open issues for a repo matching the given rule conditions.
    /// </summary>
    public List<GitHubIssue> QueryIssues(string repo, IssueDispatchRule issueRule)
    {
        var jsonFields = _supportsTypeField
            ? "number,title,author,assignees,labels,milestone,type"
            : "number,title,author,assignees,labels,milestone";

        var args = $"issue list --repo {repo} --state open --json {jsonFields} --limit 100";

        if (issueRule.Assignee is not null)
            args += $" --assignee {issueRule.Assignee}";

        foreach (var label in issueRule.Labels)
            args += $" --label \"{label}\"";

        if (issueRule.Milestone is not null)
            args += $" --milestone \"{issueRule.Milestone}\"";

        var (exitCode, output) = RunGh(args);

        // Fall back to querying without 'type' if gh doesn't support it
        if (exitCode != 0 && _supportsTypeField && output.Contains("Unknown JSON field", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("gh CLI does not support 'type' JSON field, retrying without it");
            _supportsTypeField = false;
            args = $"issue list --repo {repo} --state open --json number,title,author,assignees,labels,milestone --limit 100";

            if (issueRule.Assignee is not null)
                args += $" --assignee {issueRule.Assignee}";

            foreach (var label in issueRule.Labels)
                args += $" --label \"{label}\"";

            if (issueRule.Milestone is not null)
                args += $" --milestone \"{issueRule.Milestone}\"";

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
    /// Queries open pull requests for a repo matching the given rule conditions.
    /// </summary>
    public List<GitHubPullRequest> QueryPullRequests(string repo, PullRequestDispatchRule pullRequestRule)
    {
        const string jsonFields = "number,title,author,assignees,labels,baseRefName,headRefName,headRepository,headRepositoryOwner,headRefOid,isDraft,reviewDecision,state";
        var args = $"pr list --repo {repo} --state open --json {jsonFields} --limit 100";

        if (pullRequestRule.Assignee is not null)
            args += $" --assignee {pullRequestRule.Assignee}";

        foreach (var label in pullRequestRule.Labels)
            args += $" --label \"{label}\"";

        if (pullRequestRule.BaseBranch is not null)
            args += $" --base \"{pullRequestRule.BaseBranch}\"";

        if (pullRequestRule.AuthorMode == AuthorMode.Allowed && pullRequestRule.Authors.Count == 1)
            args += $" --author \"{pullRequestRule.Authors[0]}\"";

        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query pull requests for {Repo}: {Output}", repo, output);
            return [];
        }

        try
        {
            return ParsePullRequestsJson(output, repo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse pull request JSON for {Repo}", repo);
            return [];
        }
    }

    private static List<GitHubPullRequest> ParsePullRequestsJson(string json, string repo)
    {
        using var doc = JsonDocument.Parse(json);
        var pullRequests = new List<GitHubPullRequest>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var pullRequest = new GitHubPullRequest
            {
                Number = element.GetProperty("number").GetInt32(),
                Title = element.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                Repo = repo,
                BaseBranch = element.TryGetProperty("baseRefName", out var baseRefName) ? baseRefName.GetString() : null,
                HeadBranch = element.TryGetProperty("headRefName", out var headRefName) ? headRefName.GetString() : null,
                HeadSha = element.TryGetProperty("headRefOid", out var headRefOid) ? headRefOid.GetString() : null,
                IsDraft = element.TryGetProperty("isDraft", out var isDraft) && isDraft.ValueKind == JsonValueKind.True,
                ReviewDecision = element.TryGetProperty("reviewDecision", out var reviewDecision) ? reviewDecision.GetString() : null,
                State = element.TryGetProperty("state", out var state) ? state.GetString() ?? "OPEN" : "OPEN",
                Labels = [],
                Assignees = [],
            };

            if (element.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.Object)
            {
                pullRequest.Author = authorEl.TryGetProperty("login", out var authorLogin) ? authorLogin.GetString() : null;
            }

            if (element.TryGetProperty("assignees", out var assignees))
            {
                foreach (var assignee in assignees.EnumerateArray())
                {
                    var login = assignee.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
                    if (login is not null)
                        pullRequest.Assignees.Add(login);
                }
            }

            if (element.TryGetProperty("labels", out var labels))
            {
                foreach (var label in labels.EnumerateArray())
                {
                    var name = label.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (name is not null)
                        pullRequest.Labels.Add(name);
                }
            }

            pullRequest.HeadRepo = GetHeadRepositorySlug(element);
            pullRequests.Add(pullRequest);
        }

        return pullRequests;
    }

    private static string? GetHeadRepositorySlug(JsonElement pullRequest)
    {
        if (pullRequest.TryGetProperty("headRepository", out var headRepo)
            && headRepo.ValueKind == JsonValueKind.Object)
        {
            if (headRepo.TryGetProperty("nameWithOwner", out var nameWithOwner))
                return nameWithOwner.GetString();

            var name = headRepo.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var owner = headRepo.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object
                ? ownerEl.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null
                : null;
            if (owner is not null && name is not null)
                return $"{owner}/{name}";
        }

        if (pullRequest.TryGetProperty("headRepositoryOwner", out var headRepoOwner)
            && headRepoOwner.ValueKind == JsonValueKind.Object
            && headRepoOwner.TryGetProperty("login", out var ownerLogin)
            && pullRequest.TryGetProperty("headRepository", out var repoEl)
            && repoEl.ValueKind == JsonValueKind.Object
            && repoEl.TryGetProperty("name", out var repoName))
        {
            var owner = ownerLogin.GetString();
            var name = repoName.GetString();
            if (owner is not null && name is not null)
                return $"{owner}/{name}";
        }

        return null;
    }

    /// <summary>
    /// Prefix for hidden HTML metadata appended to comments posted by copilotd, used to distinguish
    /// bot-posted comments from human replies when checking for new feedback.
    /// Invisible on GitHub (HTML comment).
    /// </summary>
    internal const string CommentMarkerPrefix = "<!-- posted by copilotd";

    /// <summary>
    /// Legacy hidden marker used before machine identifiers were added to comment metadata.
    /// Preserved so older copilotd comments remain attributable and ignored during feedback checks.
    /// </summary>
    internal const string LegacyCommentMarker = "<!-- posted by copilotd -->";

    /// <summary>
    /// Posts a comment on a GitHub issue. Appends a hidden marker so copilotd
    /// can distinguish its own comments from human replies.
    /// </summary>
    public bool PostIssueComment(string repo, int issueNumber, string message, string machineIdentifier)
    {
        var body = message + "\n\n" + BuildCommentMarker(machineIdentifier);

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
    public sealed record NewCommentInfo(string Author, DateTimeOffset CreatedAt, long? IssueCommentId = null);

    /// <summary>
    /// Checks whether there are new comments on an issue since the given timestamp,
    /// excluding comments posted by copilotd itself (identified by the hidden copilotd marker).
    /// </summary>
    public bool HasNewCommentsSince(string repo, int issueNumber, DateTimeOffset since)
        => GetNewCommentSince(repo, issueNumber, since) is not null;

    /// <summary>
    /// Returns info about the first new non-bot comment on an issue since the given timestamp,
    /// or null if no new comments exist. Excludes comments posted by copilotd.
    /// </summary>
    public NewCommentInfo? GetNewCommentSince(string repo, int issueNumber, DateTimeOffset since)
    {
        var args = $"api repos/{repo}/issues/{issueNumber}/comments?per_page=100";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query comments on {Repo}#{Issue}: {Output}", repo, issueNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var comment in doc.RootElement.EnumerateArray())
            {
                if (!comment.TryGetProperty("created_at", out var createdAtEl))
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
                    if (IsCopilotdComment(body))
                        continue;
                }

                var author = comment.TryGetProperty("user", out var a) && a.TryGetProperty("login", out var l)
                    ? l.GetString() ?? "unknown"
                    : "unknown";
                var commentId = comment.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : (long?)null;

                _logger.LogDebug("Found new comment on {Repo}#{Issue} from {Author}", repo, issueNumber, author);
                return new NewCommentInfo(author, createdAt, commentId);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse comments JSON for {Repo}#{Issue}", repo, issueNumber);
            return null;
        }
    }

    private NewCommentInfo? GetNewPrReviewThreadCommentSince(string repo, int prNumber, DateTimeOffset since)
    {
        var repoParts = repo.Split('/', 2);
        if (repoParts.Length != 2)
            return null;

        var request = new JsonObject
        {
            ["query"] = """
                query($owner: String!, $name: String!, $number: Int!) {
                  repository(owner: $owner, name: $name) {
                    pullRequest(number: $number) {
                      reviewThreads(first: 100) {
                        nodes {
                          comments(first: 100) {
                            nodes {
                              databaseId
                              body
                              createdAt
                              author {
                                login
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["owner"] = repoParts[0],
                ["name"] = repoParts[1],
                ["number"] = prNumber,
            },
        };

        var (exitCode, output) = RunGhWithStdin("api graphql --input -", request.ToJsonString());
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query PR review thread comments on {Repo}!{Pr}: {Output}", repo, prNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                _logger.LogWarning("GraphQL errors querying PR review thread comments on {Repo}!{Pr}: {Errors}", repo, prNumber, errors.ToString());
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("repository", out var repository)
                || !repository.TryGetProperty("pullRequest", out var pullRequest)
                || !pullRequest.TryGetProperty("reviewThreads", out var reviewThreads)
                || !reviewThreads.TryGetProperty("nodes", out var threadNodes))
            {
                return null;
            }

            foreach (var thread in threadNodes.EnumerateArray())
            {
                if (!thread.TryGetProperty("comments", out var comments)
                    || !comments.TryGetProperty("nodes", out var commentNodes))
                {
                    continue;
                }

                foreach (var comment in commentNodes.EnumerateArray())
                {
                    var info = ExtractNewNonBotComment(comment, since, "createdAt");
                    if (info is null)
                        continue;

                    _logger.LogDebug("Found new PR review thread comment on {Repo}!{Pr} from {Author}", repo, prNumber, info.Author);
                    return info;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse PR review thread comments JSON for {Repo}!{Pr}", repo, prNumber);
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
    /// Returns the current assignee logins for an issue by querying the GitHub API.
    /// Returns an empty list if there are no assignees, or null on API failure (so callers
    /// can distinguish "no assignees" from "couldn't check").
    /// </summary>
    public List<string>? GetIssueAssignees(string repo, int issueNumber)
    {
        var args = $"issue view {issueNumber} --repo {repo} --json assignees";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query assignees on {Repo}#{Issue}: {Output}", repo, issueNumber, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("assignees", out var assignees))
                return [];

            var result = new List<string>();
            foreach (var assignee in assignees.EnumerateArray())
            {
                if (assignee.TryGetProperty("login", out var login))
                {
                    var loginStr = login.GetString();
                    if (loginStr is not null)
                        result.Add(loginStr);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse assignees JSON for {Repo}#{Issue}", repo, issueNumber);
            return null;
        }
    }

    /// <summary>
    /// Checks whether there are new review comments on a pull request since the given timestamp,
    /// excluding comments posted by copilotd itself (identified by the hidden copilotd marker).
    /// Checks both PR review comments (from formal reviews) and regular PR comments.
    /// </summary>
    public bool HasNewPrReviewCommentsSince(string repo, int prNumber, DateTimeOffset since)
        => GetNewPrReviewCommentSince(repo, prNumber, since) is not null;

    /// <summary>
    /// Returns info about the first new non-bot review comment or PR comment since the given timestamp,
    /// or null if no new comments exist. Checks regular PR comments, formal review submissions,
    /// and individual review-thread replies.
    /// </summary>
    public NewCommentInfo? GetNewPrReviewCommentSince(string repo, int prNumber, DateTimeOffset since)
    {
        var args = $"pr view {prNumber} --repo {repo} --json comments,reviews";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to query PR comments on {Repo}#{Pr}: {Output}", repo, prNumber, output);
            var threadComment = GetNewPrReviewThreadCommentSince(repo, prNumber, since);
            if (threadComment is not null)
                return threadComment;

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
                        if (IsCopilotdComment(body))
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
            if (IsCopilotdComment(body))
                return null;
        }

        var author = comment.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l)
            ? l.GetString() ?? "unknown"
            : "unknown";

        return new NewCommentInfo(author, createdAt);
    }

    private static string BuildCommentMarker(string machineIdentifier)
        => $"<!-- posted by copilotd; machine-id: {machineIdentifier} -->";

    private static bool IsCopilotdComment(string? body)
        => body is not null
           && (body.Contains(CommentMarkerPrefix, StringComparison.Ordinal)
               || body.Contains(LegacyCommentMarker, StringComparison.Ordinal));

    // Reaction content constants for GitHub issue reactions
    internal const string ReactionEyes = "eyes";
    internal const string ReactionRocket = "rocket";
    internal const string ReactionThumbsUp = "+1";
    internal const string ReactionThumbsDown = "-1";

    /// <summary>
    /// Adds a reaction emoji to a GitHub issue body. Returns the reaction ID for later removal,
    /// or null on failure. Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public long? AddIssueReaction(string repo, int issueNumber, string content)
        => AddReaction($"repos/{repo}/issues/{issueNumber}/reactions", content, $"{repo}#{issueNumber}");

    /// <summary>
    /// Adds a reaction emoji to a GitHub issue comment. Returns the reaction ID for later removal,
    /// or null on failure. Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public long? AddIssueCommentReaction(string repo, long issueCommentId, string content)
        => AddReaction($"repos/{repo}/issues/comments/{issueCommentId}/reactions", content, $"{repo} issue comment {issueCommentId}");

    /// <summary>
    /// Removes a reaction from a GitHub issue body by reaction ID. Returns true on success.
    /// Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public bool RemoveIssueReaction(string repo, int issueNumber, long reactionId)
        => RemoveReaction($"repos/{repo}/issues/{issueNumber}/reactions/{reactionId}", reactionId, $"{repo}#{issueNumber}");

    /// <summary>
    /// Removes a reaction from a GitHub issue comment by reaction ID. Returns true on success.
    /// Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public bool RemoveIssueCommentReaction(string repo, long issueCommentId, long reactionId)
        => RemoveReaction($"repos/{repo}/issues/comments/{issueCommentId}/reactions/{reactionId}", reactionId, $"{repo} issue comment {issueCommentId}");

    private long? AddReaction(string endpoint, string content, string targetDescription)
    {
        var args = $"api {endpoint} -f content={content}";
        var (exitCode, output) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to add {Reaction} reaction on {Target}: {Output}", content, targetDescription, output);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                return idEl.GetInt64();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse reaction response for {Target}", targetDescription);
            return null;
        }
    }

    private bool RemoveReaction(string endpoint, long reactionId, string targetDescription)
    {
        var args = $"api {endpoint} -X DELETE";
        var (exitCode, _) = RunGh(args);
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to remove reaction {Id} from {Target}", reactionId, targetDescription);
            return false;
        }

        return true;
    }

    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(30);

    private static bool HasWriteOrBetter(JsonElement repo)
    {
        if (!repo.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object)
            return false;

        return GetBooleanProperty(permissions, "admin")
            || GetBooleanProperty(permissions, "maintain")
            || GetBooleanProperty(permissions, "push");
    }

    private static bool GetBooleanProperty(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();

    private static bool HasWriteOrBetter(string? viewerPermission)
        => viewerPermission is "ADMIN" or "MAINTAIN" or "WRITE";

    private static string BuildRepositorySearchQuery(string searchTerm)
    {
        var trimmed = searchTerm.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "";

        if (trimmed.Contains(':', StringComparison.Ordinal))
            return trimmed;

        if (trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.Contains(' ', StringComparison.Ordinal))
            return $"repo:{trimmed}";

        return $"{trimmed} in:name";
    }

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

    private (int ExitCode, string Output) RunGhWithStdin(string arguments, string stdinContent)
    {
        _logger.LogDebug("Running: gh {Args} (with stdin)", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            _logger.LogWarning("Failed to start gh process for: gh {Args}", arguments);
            return (-1, "failed to start process");
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdinContent);
        process.StandardInput.Close();

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

    /// <summary>
    /// Creates a linked branch for a GitHub issue via the Development sidebar
    /// using the <c>createLinkedBranch</c> GraphQL mutation. This mutation
    /// creates the remote branch as part of linking it, so call it before
    /// pushing the local branch and use the same branch name locally.
    /// Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    public bool CreateLinkedBranchForIssue(string repo, int issueNumber, string branchName, string commitSha)
    {
        var parts = repo.Split('/');
        if (parts.Length != 2)
        {
            _logger.LogWarning("Cannot link branch: invalid repo format {Repo}", repo);
            return false;
        }
        var owner = parts[0];
        var repoName = parts[1];

        // Step 1: Get the GitHub node IDs for the issue and repository
        var idsRequest = new JsonObject
        {
            ["query"] = """
                query($owner: String!, $repo: String!, $number: Int!) {
                    repository(owner: $owner, name: $repo) {
                        id
                        issue(number: $number) { id }
                    }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["owner"] = owner,
                ["repo"] = repoName,
                ["number"] = issueNumber,
            },
        };

        var (idsExitCode, idsOutput) = RunGhWithStdin("api graphql --input -", idsRequest.ToJsonString());
        if (idsExitCode != 0)
        {
            _logger.LogWarning("Failed to get GitHub node IDs for {Repo}#{Issue}: {Output}", repo, issueNumber, idsOutput);
            return false;
        }

        string issueId, repoId;
        try
        {
            using var doc = JsonDocument.Parse(idsOutput);
            var data = doc.RootElement.GetProperty("data");
            var repository = data.GetProperty("repository");
            repoId = repository.GetProperty("id").GetString()!;
            issueId = repository.GetProperty("issue").GetProperty("id").GetString()!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse GitHub node IDs for {Repo}#{Issue}", repo, issueNumber);
            return false;
        }

        // Step 2: Create the linked branch via GraphQL mutation
        var mutationRequest = new JsonObject
        {
            ["query"] = """
                mutation($issueId: ID!, $oid: GitObjectID!, $name: String!, $repositoryId: ID!) {
                    createLinkedBranch(input: {issueId: $issueId, oid: $oid, name: $name, repositoryId: $repositoryId}) {
                        linkedBranch { id }
                    }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["issueId"] = issueId,
                ["oid"] = commitSha,
                ["name"] = branchName,
                ["repositoryId"] = repoId,
            },
        };

        var (mutExitCode, mutOutput) = RunGhWithStdin("api graphql --input -", mutationRequest.ToJsonString());
        if (mutExitCode != 0)
        {
            _logger.LogWarning("Failed to create linked branch {Branch} for {Repo}#{Issue}: {Output}",
                branchName, repo, issueNumber, mutOutput);
            return false;
        }

        // Check for GraphQL-level errors in the response
        try
        {
            using var doc = JsonDocument.Parse(mutOutput);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                _logger.LogWarning("GraphQL errors creating linked branch {Branch} for {Repo}#{Issue}: {Errors}",
                    branchName, repo, issueNumber, errors.ToString());
                return false;
            }
        }
        catch
        {
            // Best-effort error check — if parsing fails, assume success
        }

        _logger.LogInformation("Created linked branch {Branch} for {Repo}#{Issue}", branchName, repo, issueNumber);
        return true;
    }
}
