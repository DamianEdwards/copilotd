using System.Text.Json.Serialization;

namespace Copilotd.Models;

/// <summary>
/// Top-level runtime state persisted under copilotd's home directory
/// (defaults to ~/.copilotd/state.json, overrideable with COPILOTD_HOME).
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

    /// <summary>
     /// Cached resolved local paths for repos, keyed by repo slug ("org/repo").
    /// Populated by <see cref="Infrastructure.RepoPathResolver"/> and validated on each use.
    /// </summary>
    public Dictionary<string, string> ResolvedRepoPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The control remote session that allows remote management of copilotd.
    /// Null when no control session has been started. Tracked separately from
    /// dispatch sessions to avoid interference with reconciliation and pruning.
    /// </summary>
    public ControlSessionInfo? ControlSession { get; set; }
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

    /// <summary>Legacy interactive takeover state retained for compatibility with older persisted state.</summary>
    Joined,

    /// <summary>
    /// Session posted a comment on the issue and is waiting for a response.
    /// No process is running; the reconciler monitors for new comments.
    /// </summary>
    WaitingForFeedback,

    /// <summary>
    /// Session created a pull request and is waiting for review feedback.
    /// No process is running; the reconciler monitors for new PR review comments.
    /// </summary>
    WaitingForReview
}

/// <summary>
/// The GitHub subject kind that caused a session to be dispatched.
/// </summary>
[JsonConverter(typeof(TolerantDispatchSubjectKindConverter))]
public enum DispatchSubjectKind
{
    /// <summary>The root dispatch subject is an issue.</summary>
    Issue,

    /// <summary>The root dispatch subject is a pull request.</summary>
    PullRequest,
}

/// <summary>
/// The GitHub object currently hosting a session's lifecycle reaction.
/// </summary>
[JsonConverter(typeof(TolerantReactionTargetTypeConverter))]
public enum ReactionTargetType
{
    /// <summary>The issue body is the current lifecycle anchor.</summary>
    IssueBody,

    /// <summary>A specific issue comment is the current lifecycle anchor.</summary>
    IssueComment
}

/// <summary>
/// Identifies the GitHub object that should host a session's lifecycle reaction.
/// </summary>
public readonly record struct ReactionAnchor(ReactionTargetType TargetType, long? IssueCommentId)
{
    public static ReactionAnchor IssueBody => new(ReactionTargetType.IssueBody, null);

    public static ReactionAnchor ForIssueComment(long issueCommentId)
        => new(ReactionTargetType.IssueComment, issueCommentId);
}

/// <summary>
/// Tracks a single dispatched copilot session and its lifecycle.
/// </summary>
public sealed class DispatchSession
{
    /// <summary>The kind of GitHub subject that caused this session to be dispatched.</summary>
    public DispatchSubjectKind SubjectKind { get; set; } = DispatchSubjectKind.Issue;

    /// <summary>
    /// Subject key in "org/repo#number" format. Stored under the legacy issueKey
    /// JSON name for compatibility with existing state files.
    /// </summary>
    [JsonPropertyName("issueKey")]
    public string IssueKey { get; set; } = "";

    /// <summary>Subject key in "org/repo#number" format.</summary>
    [JsonIgnore]
    public string SubjectKey
    {
        get => IssueKey;
        set => IssueKey = value;
    }

    /// <summary>Repository in org/repo format.</summary>
    public string Repo { get; set; } = "";

    /// <summary>
    /// Subject number. Stored under the legacy issueNumber JSON name for compatibility
    /// with existing state files.
    /// </summary>
    [JsonPropertyName("issueNumber")]
    public int IssueNumber { get; set; }

    /// <summary>Root issue or PR number.</summary>
    [JsonIgnore]
    public int SubjectNumber
    {
        get => IssueNumber;
        set => IssueNumber = value;
    }

    /// <summary>Name of the rule that triggered this dispatch.</summary>
    public string RuleName { get; set; } = "";

    /// <summary>
    /// The GitHub login of the root subject author at the time of dispatch.
    /// Used by <see cref="CommentTrustLevel.IssueAuthor"/> and
    /// <see cref="CommentTrustLevel.IssueAuthorAndCollaborators"/> trust checks.
    /// </summary>
    [JsonPropertyName("issueAuthor")]
    public string? IssueAuthor { get; set; }

    /// <summary>GitHub login of the root subject author at dispatch time.</summary>
    [JsonIgnore]
    public string? SubjectAuthor
    {
        get => IssueAuthor;
        set => IssueAuthor = value;
    }

    /// <summary>Title of the root subject at dispatch time, when available.</summary>
    public string? SubjectTitle { get; set; }

    /// <summary>
    /// Stable local identifier for this tracked session. Existing persisted sessions also use this
    /// as their copilot <c>--resume</c> value. New named sessions keep this identifier for
    /// copilotd's own state tracking while resuming via <see cref="CopilotSessionName"/>.
    /// </summary>
    public string CopilotSessionId { get; set; } = "";

    /// <summary>
    /// Optional name used to start a new Copilot session via <c>copilot --name</c> and later
    /// resume it via <c>copilot --resume=&lt;name&gt;</c>.
    /// </summary>
    public string? CopilotSessionName { get; set; }

    /// <summary>
    /// True once copilotd has successfully launched this session at least once. Used to decide
    /// whether a named session should start with <c>--name</c> or resume with <c>--resume</c>.
    /// </summary>
    public bool HasStarted { get; set; }

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
    /// Optional diagnostic detail explaining why the session most recently failed.
    /// Shown by <c>copilotd session info</c> when available.
    /// </summary>
    public string? FailureDetail { get; set; }

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
    /// The pull request number associated with this issue-root session, if one was created.
    /// Set via 'copilotd session pr' and used to monitor for review comments.
    /// </summary>
    [JsonPropertyName("pullRequestNumber")]
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// Pull request number monitored by an issue-root session for review feedback.
    /// </summary>
    [JsonIgnore]
    public int? ReviewPullRequestNumber
    {
        get => PullRequestNumber;
        set => PullRequestNumber = value;
    }

    /// <summary>PR base branch for PR-root sessions.</summary>
    public string? PullRequestBaseBranch { get; set; }

    /// <summary>PR head branch for PR-root sessions.</summary>
    public string? PullRequestHeadBranch { get; set; }

    /// <summary>PR head repository for PR-root sessions.</summary>
    public string? PullRequestHeadRepo { get; set; }

    /// <summary>PR head SHA that was last dispatched for a PR-root session.</summary>
    public string? PullRequestHeadSha { get; set; }

    /// <summary>Branch strategy used by PR-root sessions.</summary>
    public PullRequestBranchStrategy? PullRequestBranchStrategy { get; set; }

    /// <summary>
    /// Number of times this session has been re-dispatched via comment/review feedback loops.
    /// Used to enforce <see cref="CopilotdConfig.MaxRedispatches"/> and prevent unbounded loops.
    /// </summary>
    public int RedispatchCount { get; set; }

    /// <summary>
    /// True when the most recent feedback-triggered re-dispatch was caused by an issue comment
    /// rather than a PR review comment. Used to choose the correct resume prompt when the
    /// session is associated with a pull request.
    /// </summary>
    public bool LastRedispatchWasIssueComment { get; set; }

    /// <summary>
    /// Path to the git worktree directory for this session. Null if using the main repo checkout.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Name of the git branch created for this session's worktree (e.g., "copilotd/issue-42-a3f7").
    /// Tracked so cleanup can delete the correct branch even if worktree creation failed partway.
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>
    /// The GitHub reaction ID currently posted by copilotd for lifecycle tracking.
    /// Kept under its legacy name for persisted-state compatibility.
    /// </summary>
    [JsonPropertyName("issueReactionId")]
    public long? IssueReactionId { get; set; }

    /// <summary>
    /// The GitHub object currently hosting the lifecycle reaction.
    /// Defaults to <see cref="Models.ReactionTargetType.IssueBody"/> for compatibility
    /// with state written before comment-targeted reactions were introduced.
    /// </summary>
    public ReactionTargetType ReactionTargetType { get; set; } = ReactionTargetType.IssueBody;

    /// <summary>
    /// The numeric issue comment ID when <see cref="ReactionTargetType"/> is
    /// <see cref="Models.ReactionTargetType.IssueComment"/>.
    /// </summary>
    public long? ReactionTargetCommentId { get; set; }

    /// <summary>Maximum retries before giving up (0 = unlimited).</summary>
    public const int MaxRetries = 3;

    /// <summary>Whether this session is in a terminal state.</summary>
    public bool IsTerminal => Status is SessionStatus.Completed or SessionStatus.Failed;

    /// <summary>Whether this session can be re-dispatched.</summary>
    public bool CanRetry => Status is SessionStatus.Orphaned or SessionStatus.Failed
                            && RetryCount < MaxRetries;

    /// <summary>Returns the current lifecycle reaction anchor for this session.</summary>
    public ReactionAnchor GetReactionAnchor()
        => ReactionTargetType == Models.ReactionTargetType.IssueComment && ReactionTargetCommentId.HasValue
            ? ReactionAnchor.ForIssueComment(ReactionTargetCommentId.Value)
            : ReactionAnchor.IssueBody;

    /// <summary>Updates the current lifecycle reaction anchor.</summary>
    public void SetReactionAnchor(ReactionAnchor anchor)
    {
        ReactionTargetType = anchor.TargetType;
        ReactionTargetCommentId = anchor.TargetType == Models.ReactionTargetType.IssueComment
            ? anchor.IssueCommentId
            : null;
    }
}

/// <summary>
/// Lifecycle status of the control remote session.
/// </summary>
[JsonConverter(typeof(TolerantControlSessionStatusConverter))]
public enum ControlSessionStatus
{
    /// <summary>Control session is not running.</summary>
    Stopped,

    /// <summary>Control session process is being launched.</summary>
    Starting,

    /// <summary>Control session process is alive.</summary>
    Running,

    /// <summary>Control session process exited abnormally.</summary>
    Failed,
}

/// <summary>
/// Tracks the daemon's control remote session — a special <c>copilot --remote</c> session
/// that allows remote management of copilotd via the GitHub remote sessions UI.
/// Tracked separately from dispatch sessions to avoid interference with reconciliation.
/// </summary>
public sealed class ControlSessionInfo
{
    /// <summary>
    /// Copilot session ID when known. For named control sessions, copilotd may initially
    /// resolve the live remote session via <see cref="CopilotSessionName"/> instead.
    /// </summary>
    public string CopilotSessionId { get; set; } = "";

    /// <summary>
    /// Human-readable control session name shown in the remote sessions UI.
    /// Includes the copilotd machine identifier, but never the real machine name.
    /// </summary>
    public string? CopilotSessionName { get; set; }

    /// <summary>OS process ID of the copilot process.</summary>
    public int? ProcessId { get; set; }

    /// <summary>Process start time, used alongside PID to detect PID reuse.</summary>
    public DateTimeOffset? ProcessStartTime { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public ControlSessionStatus Status { get; set; } = ControlSessionStatus.Stopped;

    /// <summary>When the control session was last started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the control session status was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
