namespace Copilotd.Models;

/// <summary>
/// Represents a GitHub issue as returned by the gh CLI.
/// </summary>
public sealed class GitHubIssue
{
    /// <summary>Issue number.</summary>
    public int Number { get; set; }

    /// <summary>Issue title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Repository in org/repo format.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Assigned user login (first assignee).</summary>
    public string? Assignee { get; set; }

    /// <summary>Issue author login.</summary>
    public string? Author { get; set; }

    /// <summary>All label names.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Milestone title, if any.</summary>
    public string? Milestone { get; set; }

    /// <summary>Issue type/category if available.</summary>
    public string? Type { get; set; }

    /// <summary>Issue state (OPEN, CLOSED).</summary>
    public string State { get; set; } = "OPEN";

    /// <summary>Composite key for deduplication: "org/repo#number".</summary>
    public string Key => $"{Repo}#{Number}";
}
