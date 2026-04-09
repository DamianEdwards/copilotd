using System.Text.Json.Serialization;

namespace Copilotd.Models;

/// <summary>
/// Top-level user-managed configuration persisted to ~/.copilotd/config.json.
/// </summary>
public sealed class CopilotdConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Root directory where repos are cloned. Supports both flat (&lt;repo_name&gt;) and
    /// nested (&lt;org&gt;/&lt;repo_name&gt;) layouts. The resolver will scan to find clones.
    /// </summary>
    public string? RepoHome { get; set; }

    /// <summary>
    /// Returns the normalized local filesystem path for a given repo slug (e.g., "org/repo").
    /// Ensures consistent path separators for the current platform.
    /// </summary>
    [Obsolete("Use RepoPathResolver.ResolveRepoPath() for flexible repo layout support. " +
              "This method assumes <RepoHome>/<org>/<repo> which doesn't work for all users.")]
    public string GetRepoPath(string repo)
        => Path.GetFullPath(Path.Combine(RepoHome ?? ".", repo));

    /// <summary>
    /// Custom prompt text appended to the default copilotd prompt.
    /// When non-empty, this is added after the built-in prompt with a trailer.
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// The GitHub username of the authenticated user (populated during init).
    /// </summary>
    public string? CurrentUser { get; set; }

    /// <summary>
    /// Maximum number of concurrent copilot instances. Sessions beyond this limit
    /// are queued as Pending until a slot opens. Default is 3.
    /// </summary>
    public int MaxInstances { get; set; } = 3;

    /// <summary>
    /// Default model to use for all copilot sessions. When set, <c>--model</c> is
    /// passed to the copilot CLI unless a rule specifies its own model override.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Named dispatch rules. Key is the rule name.
    /// </summary>
    public Dictionary<string, DispatchRule> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const string DefaultRuleName = "Default";

    public const string DefaultPrompt =
        """
        You are working on issue #$(issue.id) in the $(issue.repo) repository.
        Read the issue details carefully and decide on the best course of action.
        Follow the project's coding conventions and ensure all tests pass.

        If you have enough information to implement the requested changes:
        - You are already on a new branch created for this issue. Commit your changes here.
        - When your work is complete, push the branch and create a pull request that references the issue (e.g., "Closes #$(issue.id)").
        - After the pull request is created, run `copilotd session complete $(issue.repo)#$(issue.id)` to mark this session as done.

        If you need more information or clarification before proceeding:
        - Run `copilotd session comment $(issue.repo)#$(issue.id) --message "Your question or findings here"` to post a comment on the issue.
        - This will pause your session until a response is posted on the issue, at which point your session will automatically resume.
        """;
}

/// <summary>
/// A named dispatch rule with match conditions and launch options.
/// </summary>
public sealed class DispatchRule
{
    /// <summary>Assigned user to match (null = any).</summary>
    public string? User { get; set; }

    /// <summary>Labels that must all be present on the issue.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Milestone the issue must belong to (null = any).</summary>
    public string? Milestone { get; set; }

    /// <summary>Issue type filter, e.g. "bug", "feature" (null = any).</summary>
    public string? Type { get; set; }

    /// <summary>Repositories this rule applies to (org/repo format).</summary>
    public List<string> Repos { get; set; } = [];

    // --- Launch options ---

    /// <summary>Whether to pass --yolo to copilot, which implies --allow-all-tools and --allow-all-urls.</summary>
    public bool Yolo { get; set; }

    /// <summary>Whether to pass --allow-all-tools to copilot. Defaults to true. Implied by Yolo.</summary>
    public bool AllowAllTools { get; set; } = true;

    /// <summary>Whether to pass --allow-all-urls to copilot. Defaults to false. Implied by Yolo.</summary>
    public bool AllowAllUrls { get; set; }

    /// <summary>
    /// Model to use for sessions triggered by this rule. Overrides the global
    /// <see cref="CopilotdConfig.DefaultModel"/> when set.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Extra prompt text appended when this rule triggers.</summary>
    public string? ExtraPrompt { get; set; }

    /// <summary>
    /// Per-rule custom prompt text. When set, this is used as the custom prompt
    /// for sessions matching this rule. See <see cref="CustomPromptMode"/> for
    /// how it interacts with the global custom prompt.
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>
    /// Controls how <see cref="CustomPrompt"/> interacts with the global custom prompt.
    /// <see cref="PromptMode.Append"/>: rule prompt is appended after the global custom prompt (default).
    /// <see cref="PromptMode.Override"/>: rule prompt replaces the global custom prompt entirely.
    /// </summary>
    public PromptMode CustomPromptMode { get; set; } = PromptMode.Append;

    /// <summary>
    /// Returns true if the given issue matches all conditions on this rule.
    /// All conditions are logical AND.
    /// </summary>
    public bool Matches(GitHubIssue issue)
    {
        if (User is not null && !string.Equals(User, issue.Assignee, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Labels.Count > 0 && !Labels.All(l => issue.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (Milestone is not null && !string.Equals(Milestone, issue.Milestone, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Type is not null && !string.Equals(Type, issue.Type, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

/// <summary>
/// Controls how a rule's custom prompt interacts with the global custom prompt.
/// </summary>
[JsonConverter(typeof(TolerantPromptModeConverter))]
public enum PromptMode
{
    /// <summary>Rule prompt is appended after the global custom prompt.</summary>
    Append,

    /// <summary>Rule prompt replaces the global custom prompt entirely.</summary>
    Override,
}
