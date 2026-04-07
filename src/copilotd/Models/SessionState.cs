namespace Copilotd.Models;

/// <summary>
/// Top-level runtime state persisted to ~/.copilotd/state.json.
/// Self-healing: a missing or corrupt file is treated as empty state.
/// </summary>
public sealed class DaemonState
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// All tracked dispatch sessions keyed by issue key ("org/repo#number").
    /// </summary>
    public Dictionary<string, DispatchSession> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Timestamp of the last successful poll cycle.</summary>
    public DateTimeOffset? LastPollTime { get; set; }
}

/// <summary>
/// Lifecycle status of a dispatched copilot session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Matched by rules, queued for dispatch.</summary>
    Pending,

    /// <summary>Process launch in progress.</summary>
    Dispatching,

    /// <summary>Process is alive and tracked.</summary>
    Running,

    /// <summary>Process exited cleanly or issue no longer matches.</summary>
    Completed,

    /// <summary>Process exited abnormally.</summary>
    Failed,

    /// <summary>State says running but process is gone (candidate for re-dispatch).</summary>
    Orphaned,

    /// <summary>User has taken over this session interactively via 'copilotd join'.</summary>
    Joined,

    /// <summary>
    /// Session posted a comment on the issue and is waiting for a response.
    /// No process is running; the reconciler monitors for new comments.
    /// </summary>
    WaitingForFeedback
}

/// <summary>
/// Tracks a single dispatched copilot session and its lifecycle.
/// </summary>
public sealed class DispatchSession
{
    /// <summary>Issue key: "org/repo#number".</summary>
    public string IssueKey { get; set; } = "";

    /// <summary>Repository in org/repo format.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Issue number.</summary>
    public int IssueNumber { get; set; }

    /// <summary>Name of the rule that triggered this dispatch.</summary>
    public string RuleName { get; set; } = "";

    /// <summary>Generated UUID used with copilot --resume=&lt;id&gt;.</summary>
    public string CopilotSessionId { get; set; } = "";

    /// <summary>OS process ID of the launched copilot process.</summary>
    public int? ProcessId { get; set; }

    /// <summary>Process start time, used alongside PID to detect PID reuse.</summary>
    public DateTimeOffset? ProcessStartTime { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public SessionStatus Status { get; set; } = SessionStatus.Pending;

    /// <summary>When this session was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the status was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the process was last confirmed alive.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>Number of times this session has been re-dispatched after failure/orphan.</summary>
    public int RetryCount { get; set; }

    /// <summary>When the last failure or orphan event occurred, for backoff calculation.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>
    /// True when the session was explicitly marked complete by the copilot session itself
    /// (via 'copilotd session complete'). Prevents automatic re-dispatch even if the issue
    /// still matches rules.
    /// </summary>
    public bool CompletedBySession { get; set; }

    /// <summary>
    /// When the session entered <see cref="SessionStatus.WaitingForFeedback"/> state.
    /// Used to detect new comments on the issue since the session started waiting.
    /// </summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>
    /// Path to the git worktree directory for this session. Null if using the main repo checkout.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Name of the git branch created for this session's worktree (e.g., "copilotd/issue-42-a3f7").
    /// Tracked so cleanup can delete the correct branch even if worktree creation failed partway.
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>Maximum retries before giving up (0 = unlimited).</summary>
    public const int MaxRetries = 3;

    /// <summary>Whether this session is in a terminal state.</summary>
    public bool IsTerminal => Status is SessionStatus.Completed or SessionStatus.Failed;

    /// <summary>Whether this session can be re-dispatched.</summary>
    public bool CanRetry => Status is SessionStatus.Orphaned or SessionStatus.Failed
                            && RetryCount < MaxRetries;
}
