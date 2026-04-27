namespace Copilotd.Models;

/// <summary>
/// Represents a GitHub pull request as returned by the gh CLI/API.
/// </summary>
public sealed class GitHubPullRequest
{
    /// <summary>Pull request number.</summary>
    public int Number { get; set; }

    /// <summary>Pull request title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Repository in org/repo format.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Pull request author login.</summary>
    public string? Author { get; set; }

    /// <summary>Assigned user logins.</summary>
    public List<string> Assignees { get; set; } = [];

    /// <summary>All label names.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Base branch targeted by the PR.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>Source branch name.</summary>
    public string? HeadBranch { get; set; }

    /// <summary>Source repository in org/repo format.</summary>
    public string? HeadRepo { get; set; }

    /// <summary>Current PR head commit SHA.</summary>
    public string? HeadSha { get; set; }

    /// <summary>Whether the PR is a draft.</summary>
    public bool IsDraft { get; set; }

    /// <summary>Current review decision, if available.</summary>
    public string? ReviewDecision { get; set; }

    /// <summary>PR state (OPEN, CLOSED, MERGED).</summary>
    public string State { get; set; } = "OPEN";

    /// <summary>Whether the PR head is in the same repo as the base repo.</summary>
    public bool IsSameRepository => string.Equals(Repo, HeadRepo, StringComparison.OrdinalIgnoreCase);

    /// <summary>Composite key for deduplication: "org/repo#number".</summary>
    public string Key => $"{Repo}#{Number}";
}
