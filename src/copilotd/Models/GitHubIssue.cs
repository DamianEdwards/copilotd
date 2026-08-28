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

    /// <summary>Issue body.</summary>
    public string Body { get; set; } = "";

    /// <summary>Repository in org/repo format.</summary>
    public string Repo { get; set; } = "";

    /// <summary>All assigned user logins.</summary>
    public List<string> Assignees { get; set; } = [];

    /// <summary>Compatibility accessor for the first assigned user.</summary>
    public string? Assignee
    {
        get => Assignees.FirstOrDefault();
        set
        {
            Assignees.Clear();
            if (value is not null)
                Assignees.Add(value);
        }
    }

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

/// <summary>
/// Result of binding an issue dispatch to the label or assignment event that approved it.
/// </summary>
public sealed class IssueDispatchApproval
{
    public IssueDispatchApprovalStatus Status { get; init; }
    public string? TriggerId { get; init; }
    public string? TriggerDescription { get; init; }
    public string? TriggerActor { get; init; }
    public DateTimeOffset? TriggeredAt { get; init; }
    public string? RetriggerInstruction { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset? LastBodyEditAt { get; init; }
    public DateTimeOffset? LastTitleEditAt { get; init; }
    public string? Error { get; init; }
}

public enum IssueDispatchApprovalStatus
{
    Approved,
    Stale,
    Unavailable,
}
