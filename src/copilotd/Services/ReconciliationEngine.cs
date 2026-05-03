using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// The reconciliation engine. Compares desired dispatches (from rules + issues/PRs)
/// against observed state (persisted sessions + live processes) and takes
/// corrective action to converge.
///
/// Truth sources (in order of precedence for conflicts):
/// 1. Live process status (ground truth for "is it actually running?")
/// 2. Current GitHub issue/PR matches (ground truth for "should it be running?")
/// 3. Persisted state (bookkeeping, used as starting point)
/// </summary>
public sealed class ReconciliationEngine
{
    private readonly ProcessManager _processManager;
    private readonly RepoPathResolver _repoResolver;
    private readonly CopilotTrustService _copilotTrustService;
    private readonly GhCliService _ghCli;
    private readonly StateStore _stateStore;
    private readonly ILogger<ReconciliationEngine> _logger;

    public ReconciliationEngine(
        ProcessManager processManager,
        RepoPathResolver repoResolver,
        CopilotTrustService copilotTrustService,
        GhCliService ghCli,
        StateStore stateStore,
        ILogger<ReconciliationEngine> logger)
    {
        _processManager = processManager;
        _repoResolver = repoResolver;
        _copilotTrustService = copilotTrustService;
        _ghCli = ghCli;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full reconciliation cycle. Used both at startup and on each poll.
    /// </summary>
    public void Reconcile(CopilotdConfig config, DaemonState state)
    {
        _logger.LogInformation("Starting reconciliation cycle");

        var hadMachineIdentifier = _stateStore.GetMachineIdentifier() is not null;
        var machineIdentifier = _stateStore.EnsureMachineIdentifier();
        if (!hadMachineIdentifier)
            _logger.LogInformation("Assigned machine identifier {MachineIdentifier}", machineIdentifier);

        // Step 0: Prune terminal sessions older than 7 days
        PruneTerminalSessions(state);

        // Step 1: Verify all tracked non-terminal sessions against live processes
        VerifyTrackedSessions(state);

        // Step 2: Gather all matching issues and pull requests from configured issue/PR rules
        var (desiredIssueDispatches, queriedIssueRepos) = ComputeDesiredIssueDispatches(config);
        var (desiredPullRequestDispatches, queriedPullRequestRepos) = ComputeDesiredPullRequestDispatches(config);

        // Step 3: Reconcile desired vs observed
        ReconcileDesiredVsObserved(config, state, desiredIssueDispatches, queriedIssueRepos, desiredPullRequestDispatches, queriedPullRequestRepos);

        // Step 4: Dispatch pending sessions (respects MaxInstances)
        DispatchPendingSessions(config, state);

        // Step 5: Persist corrected state
        state.LastPollTime = DateTimeOffset.UtcNow;
        _stateStore.SaveState(state);

        _logger.LogInformation("Reconciliation complete: {Active} active, {Pending} pending, {Waiting} waiting, {WaitingForReview} waiting for review, {Terminal} terminal sessions",
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Running),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Pending),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.WaitingForFeedback),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.WaitingForReview),
            state.Sessions.Values.Count(s => s.IsTerminal));
    }

    /// <summary>
    /// Step 0: Remove terminal sessions older than 7 days to prevent unbounded growth.
    /// </summary>
    private void PruneTerminalSessions(DaemonState state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var toRemove = state.Sessions
            .Where(kv => kv.Value.IsTerminal && kv.Value.UpdatedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            var session = state.Sessions[key];
            // Clean up any lingering worktree or branch
            if (!string.IsNullOrEmpty(session.WorktreePath) || !string.IsNullOrEmpty(session.BranchName))
            {
                var config = _stateStore.LoadConfig();
                _processManager.CleanupWorktree(session, config, state);
            }
            state.Sessions.Remove(key);
            _logger.LogDebug("Pruned terminal session {Key}", key);
        }

        if (toRemove.Count > 0)
            _logger.LogInformation("Pruned {Count} terminal session(s) older than 7 days", toRemove.Count);
    }

    /// <summary>
    /// Step 1: For each non-terminal tracked session, verify the process is still alive.
    /// </summary>
    private void VerifyTrackedSessions(DaemonState state)
    {
        foreach (var (key, session) in state.Sessions)
        {
            if (session.IsTerminal)
                continue;

            if (session.Status is SessionStatus.Pending)
                continue;

            // WaitingForFeedback and WaitingForReview sessions have no running process — skip liveness check
            if (session.Status is SessionStatus.WaitingForFeedback or SessionStatus.WaitingForReview)
                continue;

            // Legacy Joined sessions come from older copilotd versions that launched
            // a local interactive takeover process. If that process is gone, reset to Pending.
            if (session.Status is SessionStatus.Joined)
            {
                if (session.ProcessId is null)
                {
                    // No PID tracked — join process never saved it or state is stale
                    _logger.LogInformation("Joined session {Key} has no tracked PID, resetting to Pending", key);
                    session.Status = SessionStatus.Pending;
                    session.FailureDetail = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    continue;
                }

                var joinResult = _processManager.CheckProcess(session);
                if (joinResult is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused)
                {
                    _logger.LogInformation("Joined session {Key} interactive process is gone, resetting to Pending", key);
                    session.Status = SessionStatus.Pending;
                    session.FailureDetail = null;
                    session.ProcessId = null;
                    session.ProcessStartTime = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                }
                continue;
            }

            var result = _processManager.CheckProcess(session);

            switch (result)
            {
                case ProcessLivenessResult.Alive:
                    session.LastVerifiedAt = DateTimeOffset.UtcNow;
                    _logger.LogDebug("Session {Key} PID {Pid} is alive", key, session.ProcessId);
                    break;

                case ProcessLivenessResult.Dead:
                    _logger.LogInformation("Session {Key} PID {Pid} is dead, marking orphaned", key, session.ProcessId);
                    session.Status = SessionStatus.Orphaned;
                    session.FailureDetail = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;

                case ProcessLivenessResult.PidReused:
                    _logger.LogWarning("Session {Key} PID {Pid} was reused by another process, marking orphaned", key, session.ProcessId);
                    session.Status = SessionStatus.Orphaned;
                    session.FailureDetail = null;
                    session.ProcessId = null;
                    session.ProcessStartTime = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
            }
        }
    }

    /// <summary>
    /// Step 2: Query GitHub for all issues that currently match configured issue rules.
    /// Returns the desired issue dispatches and the set of repos that were successfully queried.
    /// Only repos in the queried set should be used for termination decisions — if a repo
    /// query fails, existing sessions for that repo are preserved.
    /// </summary>
    private (Dictionary<string, (GitHubIssue Issue, string RuleName)> Desired, HashSet<string> QueriedRepos)
        ComputeDesiredIssueDispatches(CopilotdConfig config)
    {
        var desiredIssues = new Dictionary<string, (GitHubIssue, string)>(StringComparer.OrdinalIgnoreCase);
        var queriedIssueRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (issueRuleName, issueRule) in config.IssueRules)
        {
            foreach (var repo in issueRule.Repos)
            {
                try
                {
                    var issues = _ghCli.QueryIssues(repo, issueRule);
                    queriedIssueRepos.Add(repo);

                    foreach (var issue in issues)
                    {
                        // Double-check issue rule match (gh filters are best-effort)
                        // Pass HasWriteAccess for AuthorMode.WriteAccess checks
                        if (!issueRule.Matches(issue, _ghCli.HasWriteAccess))
                            continue;

                        if (!desiredIssues.ContainsKey(issue.Key))
                        {
                            desiredIssues[issue.Key] = (issue, issueRuleName);
                            _logger.LogDebug("Desired issue dispatch: {Key} via issue rule '{Rule}'", issue.Key, issueRuleName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error querying issues for {Repo} with issue rule '{Rule}', preserving existing sessions", repo, issueRuleName);
                }
            }
        }

        _logger.LogInformation("Found {Count} issues matching configured issue rules ({Queried}/{Total} repos queried successfully)",
            desiredIssues.Count, queriedIssueRepos.Count,
            config.IssueRules.Values.SelectMany(r => r.Repos).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return (desiredIssues, queriedIssueRepos);
    }

    /// <summary>
    /// Step 2b: Query GitHub for all pull requests that currently match configured PR rules.
    /// </summary>
    private (Dictionary<string, (GitHubPullRequest PullRequest, string RuleName)> Desired, HashSet<string> QueriedRepos)
        ComputeDesiredPullRequestDispatches(CopilotdConfig config)
    {
        var desiredPullRequests = new Dictionary<string, (GitHubPullRequest, string)>(StringComparer.OrdinalIgnoreCase);
        var queriedPullRequestRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pullRequestRuleName, pullRequestRule) in config.PullRequestRules)
        {
            foreach (var repo in pullRequestRule.Repos)
            {
                try
                {
                    var pullRequests = _ghCli.QueryPullRequests(repo, pullRequestRule);
                    queriedPullRequestRepos.Add(repo);

                    foreach (var pullRequest in pullRequests)
                    {
                        if (!pullRequestRule.Matches(pullRequest, _ghCli.HasWriteAccess))
                            continue;

                        if (!desiredPullRequests.ContainsKey(pullRequest.Key))
                        {
                            desiredPullRequests[pullRequest.Key] = (pullRequest, pullRequestRuleName);
                            _logger.LogDebug("Desired PR dispatch: {Key} via PR rule '{Rule}'", pullRequest.Key, pullRequestRuleName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error querying pull requests for {Repo} with PR rule '{Rule}', preserving existing sessions", repo, pullRequestRuleName);
                }
            }
        }

        _logger.LogInformation("Found {Count} pull requests matching configured PR rules ({Queried}/{Total} repos queried successfully)",
            desiredPullRequests.Count, queriedPullRequestRepos.Count,
            config.PullRequestRules.Values.SelectMany(r => r.Repos).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return (desiredPullRequests, queriedPullRequestRepos);
    }

    /// <summary>
    /// Step 3: Compare desired dispatches against tracked sessions.
    /// - Issues without active sessions → create pending
    /// - Tracked sessions whose subjects no longer match (and repo was queried) → mark completed
    /// - Orphaned sessions for still-matching issues → re-dispatch if retries remain
    /// - Completed sessions for still-matching issues → re-dispatch with new session ID
    /// </summary>
    private void ReconcileDesiredVsObserved(
        CopilotdConfig config,
        DaemonState state,
        Dictionary<string, (GitHubIssue Issue, string RuleName)> desiredIssues,
        HashSet<string> queriedIssueRepos,
        Dictionary<string, (GitHubPullRequest PullRequest, string RuleName)> desiredPullRequests,
        HashSet<string> queriedPullRequestRepos)
    {
        // Handle sessions for subjects that no longer match — only for successfully queried repos
        var toTerminate = new List<DispatchSession>();
        foreach (var (key, session) in state.Sessions)
        {
            var desiredContainsSession = session.SubjectKind == DispatchSubjectKind.PullRequest
                ? desiredPullRequests.ContainsKey(key)
                : desiredIssues.ContainsKey(key);
            var repoWasQueried = session.SubjectKind == DispatchSubjectKind.PullRequest
                ? queriedPullRequestRepos.Contains(session.Repo)
                : queriedIssueRepos.Contains(session.Repo);
            var subjectLabel = session.SubjectKind == DispatchSubjectKind.PullRequest ? "Pull request" : "Issue";

            // Clear CompletedBySession flag when the subject no longer matches issue/PR rules,
            // so that if the subject re-matches later it will be re-dispatched
            if (session.Status == SessionStatus.Completed && session.CompletedBySession
                && !desiredContainsSession && repoWasQueried)
            {
                _logger.LogInformation("{Subject} {Key} no longer matches issue/PR rules, clearing CompletedBySession flag", subjectLabel, key);
                session.CompletedBySession = false;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (session.IsTerminal)
            {
                if (!desiredContainsSession && repoWasQueried
                    && (!string.IsNullOrEmpty(session.WorktreePath) || !string.IsNullOrEmpty(session.BranchName)))
                {
                    _logger.LogInformation("{Subject} {Key} no longer matches issue/PR rules, cleaning up terminal session worktree", subjectLabel, key);
                    _processManager.CleanupWorktree(session, config, state);
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                }
                continue;
            }

            // Never terminate user-controlled sessions
            if (session.Status is SessionStatus.Joined)
                continue;

            if (!desiredContainsSession)
            {
                // Only act if the session's repo was successfully queried.
                // If the repo query failed, we don't know if the subject still matches.
                if (!repoWasQueried)
                {
                    _logger.LogDebug("Repo {Repo} query failed, preserving session {Key}", session.Repo, key);
                    continue;
                }

                // WaitingForFeedback sessions have no process to terminate
                if (session.Status is SessionStatus.WaitingForFeedback)
                {
                    _logger.LogInformation("{Subject} {Key} no longer matches issue/PR rules, completing waiting session", subjectLabel, key);
                    session.Status = SessionStatus.Completed;
                    session.FailureDetail = null;
                    session.WaitingSince = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    TransitionReactionOnCurrentAnchor(session, config, null);
                    _processManager.CleanupWorktree(session, config, state);
                    continue;
                }

                // WaitingForReview sessions have no process to terminate
                if (session.Status is SessionStatus.WaitingForReview)
                {
                    _logger.LogInformation("{Subject} {Key} no longer matches issue/PR rules, completing PR review session", subjectLabel, key);
                    session.Status = SessionStatus.Completed;
                    session.FailureDetail = null;
                    session.WaitingSince = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    TransitionReactionOnCurrentAnchor(session, config, null);
                    _processManager.CleanupWorktree(session, config, state);
                    continue;
                }

                _logger.LogInformation("{Subject} {Key} no longer matches issue/PR rules, terminating session", subjectLabel, key);
                toTerminate.Add(session);
            }
        }

        // Terminate in parallel to avoid N×20s blocking
        if (toTerminate.Count > 0)
        {
            Parallel.ForEach(toTerminate, new ParallelOptions { MaxDegreeOfParallelism = 4 }, session =>
            {
                _processManager.TerminateProcess(session);
            });

            foreach (var session in toTerminate)
            {
                session.Status = SessionStatus.Completed;
                session.FailureDetail = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                TransitionReactionOnCurrentAnchor(session, config, null);
                _processManager.CleanupWorktree(session, config, state);
            }
        }

        // Handle desired issues that need new or re-dispatched sessions
        foreach (var (issueKey, (issue, issueRuleName)) in desiredIssues)
        {
            if (state.Sessions.TryGetValue(issueKey, out var existing))
            {
                if (existing.SubjectKind != DispatchSubjectKind.Issue)
                    continue;

                // Backfill IssueAuthor for sessions created before this field existed
                if (existing.IssueAuthor is null && issue.Author is not null)
                {
                    existing.IssueAuthor = issue.Author;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                }

                switch (existing.Status)
                {
                    case SessionStatus.Running:
                    case SessionStatus.Dispatching:
                    case SessionStatus.Pending:
                    case SessionStatus.Joined:
                        // Already active or user-controlled, nothing to do
                        continue;

                    case SessionStatus.WaitingForFeedback:
                        // Check for new comments since the session started waiting
                        if (existing.WaitingSince is not null)
                        {
                            var commentInfo = _ghCli.GetNewCommentSince(existing.Repo, existing.IssueNumber, existing.WaitingSince.Value);
                            if (commentInfo is not null)
                            {
                                // Check re-dispatch rate limit
                                if (existing.RedispatchCount >= config.MaxRedispatches)
                                {
                                    _logger.LogWarning("Session {Key} has reached the maximum re-dispatch limit ({Max}). " +
                                        "Use 'copilotd session reset' to re-enable. Ignoring comment from {Author}",
                                        issueKey, config.MaxRedispatches, commentInfo.Author);
                                    continue;
                                }

                                // Check author trust level
                                var trusted = IsCommentTrusted(existing, commentInfo.Author, config, out var trustLevel);

                                if (trusted is null)
                                {
                                    _logger.LogWarning("Could not evaluate trust for {Author} on {Key} (trust_level={TrustLevel}), will retry next cycle",
                                        commentInfo.Author, issueKey, trustLevel);
                                    continue;
                                }

                                if (trusted == false)
                                {
                                    _logger.LogInformation("Ignoring comment from untrusted author {Author} on {Key} (trust_level={TrustLevel})",
                                        commentInfo.Author, issueKey, trustLevel);
                                    // Advance WaitingSince past this comment so the next poll can find later trusted comments
                                    existing.WaitingSince = commentInfo.CreatedAt;
                                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                                    continue;
                                }

                                _logger.LogInformation("New comment from {Author} detected on {Key}, re-dispatching waiting session (redispatch {N}/{Max})",
                                    commentInfo.Author, issueKey, existing.RedispatchCount + 1, config.MaxRedispatches);
                                // Keep the same resume target so --resume preserves context
                                existing.Status = SessionStatus.Pending;
                                existing.FailureDetail = null;
                                existing.RedispatchCount++;
                                existing.LastRedispatchWasIssueComment = true;
                                existing.WaitingSince = null;
                                existing.ProcessId = null;
                                existing.ProcessStartTime = null;
                                existing.UpdatedAt = DateTimeOffset.UtcNow;
                                if (commentInfo.IssueCommentId.HasValue)
                                    TransitionReactionToIssueComment(existing, config, commentInfo.IssueCommentId.Value, GhCliService.ReactionEyes);
                                else
                                    TransitionReactionToIssue(existing, config, GhCliService.ReactionEyes);
                            }
                            else
                            {
                                _logger.LogDebug("Session {Key} still waiting for feedback", issueKey);
                            }
                        }
                        continue;

                    case SessionStatus.WaitingForReview:
                        if (existing.PullRequestNumber is not null)
                        {
                            // Check if the PR has been merged or closed — auto-complete the session
                            var prState = _ghCli.GetPullRequestState(existing.Repo, existing.PullRequestNumber.Value);
                            if (prState is "MERGED" or "CLOSED")
                            {
                                _logger.LogInformation("PR #{Pr} for {Key} is {State}, completing session",
                                    existing.PullRequestNumber, issueKey, prState);
                                existing.Status = SessionStatus.Completed;
                                existing.FailureDetail = null;
                                existing.CompletedBySession = true;
                                existing.WaitingSince = null;
                                existing.ProcessId = null;
                                existing.ProcessStartTime = null;
                                existing.UpdatedAt = DateTimeOffset.UtcNow;
                                TransitionReactionOnCurrentAnchor(existing, config, GhCliService.ReactionThumbsUp);
                                _processManager.CleanupWorktree(existing, config, state);
                                continue;
                            }

                            // Check for new review comments on the PR
                            if (existing.WaitingSince is not null)
                            {
                                var reviewInfo = _ghCli.GetNewPrReviewCommentSince(existing.Repo, existing.PullRequestNumber.Value, existing.WaitingSince.Value);
                                if (reviewInfo is not null)
                                {
                                    // Check re-dispatch rate limit
                                    if (existing.RedispatchCount >= config.MaxRedispatches)
                                    {
                                        _logger.LogWarning("Session {Key} has reached the maximum re-dispatch limit ({Max}). " +
                                            "Use 'copilotd session reset' to re-enable. Ignoring PR review from {Author}",
                                            issueKey, config.MaxRedispatches, reviewInfo.Author);
                                        continue;
                                    }

                                    // Check author trust level
                                    var trusted = IsCommentTrusted(existing, reviewInfo.Author, config, out var trustLevel);

                                    if (trusted is null)
                                    {
                                        _logger.LogWarning("Could not evaluate trust for {Author} on {Key} (trust_level={TrustLevel}), will retry next cycle",
                                            reviewInfo.Author, issueKey, trustLevel);
                                        continue;
                                    }

                                    if (trusted == false)
                                    {
                                        _logger.LogInformation("Ignoring PR review from untrusted author {Author} on {Key} (trust_level={TrustLevel})",
                                            reviewInfo.Author, issueKey, trustLevel);
                                        // Advance WaitingSince past this review so the next poll can find later trusted reviews
                                        existing.WaitingSince = reviewInfo.CreatedAt;
                                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                                        continue;
                                    }

                                    _logger.LogInformation("New PR review from {Author} detected on PR #{Pr} for {Key}, re-dispatching session (redispatch {N}/{Max})",
                                        reviewInfo.Author, existing.PullRequestNumber, issueKey, existing.RedispatchCount + 1, config.MaxRedispatches);
                                    // Keep the same resume target so --resume preserves context
                                    existing.Status = SessionStatus.Pending;
                                    existing.FailureDetail = null;
                                    existing.RedispatchCount++;
                                    existing.LastRedispatchWasIssueComment = false;
                                    existing.WaitingSince = null;
                                    existing.ProcessId = null;
                                    existing.ProcessStartTime = null;
                                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                                    continue;
                                }
                            }
                        }

                        // Also check for new issue comments (maintainer may respond on the issue)
                        if (existing.WaitingSince is not null)
                        {
                            var issueCommentInfo = _ghCli.GetNewCommentSince(existing.Repo, existing.IssueNumber, existing.WaitingSince.Value);
                            if (issueCommentInfo is not null)
                            {
                                // Check re-dispatch rate limit
                                if (existing.RedispatchCount >= config.MaxRedispatches)
                                {
                                    _logger.LogWarning("Session {Key} has reached the maximum re-dispatch limit ({Max}). " +
                                        "Use 'copilotd session reset' to re-enable. Ignoring issue comment from {Author}",
                                        issueKey, config.MaxRedispatches, issueCommentInfo.Author);
                                    continue;
                                }

                                // Check author trust level
                                var trusted = IsCommentTrusted(existing, issueCommentInfo.Author, config, out var trustLevel);

                                if (trusted is null)
                                {
                                    _logger.LogWarning("Could not evaluate trust for {Author} on {Key} (trust_level={TrustLevel}), will retry next cycle",
                                        issueCommentInfo.Author, issueKey, trustLevel);
                                    continue;
                                }

                                if (trusted == false)
                                {
                                    _logger.LogInformation("Ignoring issue comment from untrusted author {Author} on {Key} while waiting for PR review (trust_level={TrustLevel})",
                                        issueCommentInfo.Author, issueKey, trustLevel);
                                    // Advance WaitingSince past this comment so the next poll can find later trusted comments
                                    existing.WaitingSince = issueCommentInfo.CreatedAt;
                                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                                    continue;
                                }

                                _logger.LogInformation("New issue comment from {Author} detected on {Key} while waiting for PR review, re-dispatching session (redispatch {N}/{Max})",
                                    issueCommentInfo.Author, issueKey, existing.RedispatchCount + 1, config.MaxRedispatches);
                                existing.Status = SessionStatus.Pending;
                                existing.FailureDetail = null;
                                existing.RedispatchCount++;
                                existing.LastRedispatchWasIssueComment = true;
                                existing.WaitingSince = null;
                                existing.ProcessId = null;
                                existing.ProcessStartTime = null;
                                existing.UpdatedAt = DateTimeOffset.UtcNow;
                                if (issueCommentInfo.IssueCommentId.HasValue)
                                    TransitionReactionToIssueComment(existing, config, issueCommentInfo.IssueCommentId.Value, GhCliService.ReactionEyes);
                                else
                                    TransitionReactionToIssue(existing, config, GhCliService.ReactionEyes);
                            }
                            else
                            {
                                _logger.LogDebug("Session {Key} still waiting for PR review feedback", issueKey);
                            }
                        }
                        continue;

                    case SessionStatus.Orphaned when existing.CanRetry:
                        _logger.LogInformation("Re-dispatching orphaned session {Key} (retry {N}/{Max})",
                            issueKey, existing.RetryCount + 1, DispatchSession.MaxRetries);
                        _processManager.CleanupWorktree(existing, config, state);
                        existing.Status = SessionStatus.Pending;
                        existing.FailureDetail = null;
                        existing.RetryCount++;
                        existing.PullRequestNumber = null;
                        existing.RedispatchCount = 0;
                        existing.LastRedispatchWasIssueComment = false;
                        existing.CopilotSessionId = Guid.NewGuid().ToString();
                        existing.CopilotSessionName = null;
                        existing.HasStarted = false;
                        existing.ProcessId = null;
                        existing.ProcessStartTime = null;
                        existing.LastVerifiedAt = null;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        existing.LastFailureAt = DateTimeOffset.UtcNow;
                        TransitionReactionOnCurrentAnchor(existing, config, GhCliService.ReactionEyes);
                        continue;

                    case SessionStatus.Orphaned:
                    case SessionStatus.Failed:
                        if (!existing.CanRetry)
                        {
                            _logger.LogWarning("Session {Key} exceeded max retries, marking failed", issueKey);
                            existing.Status = SessionStatus.Failed;
                            existing.FailureDetail ??= $"The session exceeded the maximum retry count ({DispatchSession.MaxRetries}). " +
                                $"Resolve the underlying issue, then run 'copilotd session reset {issueKey}'.";
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
                            TransitionReactionOnCurrentAnchor(existing, config, GhCliService.ReactionThumbsDown);
                            continue;
                        }
                        continue;

                    case SessionStatus.Completed:
                        // Don't re-dispatch if the session was explicitly completed by copilot
                        if (existing.CompletedBySession)
                        {
                            _logger.LogDebug("Session {Key} was explicitly completed by copilot, skipping re-dispatch", issueKey);
                            continue;
                        }
                        // Issue re-appeared after completion — re-dispatch with a fresh session
                        _logger.LogInformation("Issue {Key} re-matched after completion, re-dispatching", issueKey);
                        _processManager.CleanupWorktree(existing, config, state);
                        existing.Status = SessionStatus.Pending;
                        existing.FailureDetail = null;
                        existing.PullRequestNumber = null;
                        existing.RedispatchCount = 0;
                        existing.LastRedispatchWasIssueComment = false;
                        existing.CopilotSessionId = Guid.NewGuid().ToString();
                        existing.CopilotSessionName = null;
                        existing.HasStarted = false;
                        existing.ProcessId = null;
                        existing.ProcessStartTime = null;
                        existing.LastVerifiedAt = null;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        TransitionReactionToIssue(existing, config, GhCliService.ReactionEyes);
                        continue;
                }
            }
            else
            {
                // New issue, create pending session
                var newSession = new DispatchSession
                {
                    IssueKey = issueKey,
                    SubjectKind = DispatchSubjectKind.Issue,
                    Repo = issue.Repo,
                    IssueNumber = issue.Number,
                    RuleName = issueRuleName,
                    IssueAuthor = issue.Author,
                    SubjectTitle = issue.Title,
                    CopilotSessionId = Guid.NewGuid().ToString(),
                    HasStarted = false,
                    Status = SessionStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                state.Sessions[issueKey] = newSession;

                _logger.LogInformation("New issue {Key} matched by issue rule '{Rule}', creating pending dispatch", issueKey, issueRuleName);
                TransitionReactionToIssue(newSession, config, GhCliService.ReactionEyes);
            }
        }

        // Handle desired pull requests that need new or re-dispatched sessions
        foreach (var (prKey, (pullRequest, pullRequestRuleName)) in desiredPullRequests)
        {
            if (state.Sessions.TryGetValue(prKey, out var existing))
            {
                if (existing.SubjectKind != DispatchSubjectKind.PullRequest)
                    continue;

                UpdatePullRequestSessionMetadata(existing, pullRequest, pullRequestRuleName, config);

                switch (existing.Status)
                {
                    case SessionStatus.Running:
                    case SessionStatus.Dispatching:
                    case SessionStatus.Pending:
                    case SessionStatus.Joined:
                        continue;

                    case SessionStatus.WaitingForFeedback:
                        if (existing.WaitingSince is not null)
                        {
                            var feedbackInfo = _ghCli.GetNewPrReviewCommentSince(existing.Repo, existing.SubjectNumber, existing.WaitingSince.Value);
                            if (feedbackInfo is not null)
                            {
                                if (!TryQueueFeedbackRedispatch(existing, prKey, feedbackInfo, config, isIssueComment: false))
                                    continue;
                            }
                            else
                            {
                                _logger.LogDebug("PR-root session {Key} still waiting for feedback", prKey);
                            }
                        }
                        continue;

                    case SessionStatus.Orphaned when existing.CanRetry:
                        _logger.LogInformation("Re-dispatching orphaned PR-root session {Key} (retry {N}/{Max})",
                            prKey, existing.RetryCount + 1, DispatchSession.MaxRetries);
                        _processManager.CleanupWorktree(existing, config, state);
                        ResetForFreshDispatch(existing, preserveReviewPullRequest: true);
                        existing.RetryCount++;
                        existing.LastFailureAt = DateTimeOffset.UtcNow;
                        TransitionReactionOnCurrentAnchor(existing, config, GhCliService.ReactionEyes);
                        continue;

                    case SessionStatus.Orphaned:
                    case SessionStatus.Failed:
                        if (!existing.CanRetry)
                        {
                            _logger.LogWarning("PR-root session {Key} exceeded max retries, marking failed", prKey);
                            existing.Status = SessionStatus.Failed;
                            existing.FailureDetail ??= $"The session exceeded the maximum retry count ({DispatchSession.MaxRetries}). " +
                                $"Resolve the underlying issue, then run 'copilotd session reset {prKey}'.";
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
                            TransitionReactionOnCurrentAnchor(existing, config, GhCliService.ReactionThumbsDown);
                        }
                        continue;

                    case SessionStatus.Completed:
                        if (existing.CompletedBySession
                            && string.Equals(existing.PullRequestHeadSha, pullRequest.HeadSha, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("PR-root session {Key} was explicitly completed and head SHA did not change, skipping re-dispatch", prKey);
                            continue;
                        }

                        _logger.LogInformation("PR {Key} matched after completion or new head SHA, re-dispatching", prKey);
                        _processManager.CleanupWorktree(existing, config, state);
                        ResetForFreshDispatch(existing, preserveReviewPullRequest: true);
                        existing.PullRequestHeadSha = pullRequest.HeadSha;
                        TransitionReactionToIssue(existing, config, GhCliService.ReactionEyes);
                        continue;
                }
            }
            else
            {
                var newSession = new DispatchSession
                {
                    IssueKey = prKey,
                    SubjectKind = DispatchSubjectKind.PullRequest,
                    Repo = pullRequest.Repo,
                    IssueNumber = pullRequest.Number,
                    RuleName = pullRequestRuleName,
                    IssueAuthor = pullRequest.Author,
                    SubjectTitle = pullRequest.Title,
                    PullRequestBaseBranch = pullRequest.BaseBranch,
                    PullRequestHeadBranch = pullRequest.HeadBranch,
                    PullRequestHeadRepo = pullRequest.HeadRepo,
                    PullRequestHeadSha = pullRequest.HeadSha,
                    PullRequestBranchStrategy = config.PullRequestRules.GetValueOrDefault(pullRequestRuleName)?.BranchStrategy,
                    CopilotSessionId = Guid.NewGuid().ToString(),
                    HasStarted = false,
                    Status = SessionStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                state.Sessions[prKey] = newSession;

                _logger.LogInformation("New pull request {Key} matched by PR rule '{Rule}', creating pending dispatch", prKey, pullRequestRuleName);
                TransitionReactionToIssue(newSession, config, GhCliService.ReactionEyes);
            }
        }
    }

    private void UpdatePullRequestSessionMetadata(
        DispatchSession session,
        GitHubPullRequest pullRequest,
        string ruleName,
        CopilotdConfig config)
    {
        session.RuleName = ruleName;
        session.SubjectKind = DispatchSubjectKind.PullRequest;
        session.IssueKey = pullRequest.Key;
        session.Repo = pullRequest.Repo;
        session.IssueNumber = pullRequest.Number;
        session.IssueAuthor = pullRequest.Author;
        session.SubjectTitle = pullRequest.Title;
        session.PullRequestBaseBranch = pullRequest.BaseBranch;
        session.PullRequestHeadBranch = pullRequest.HeadBranch;
        session.PullRequestHeadRepo = pullRequest.HeadRepo;
        session.PullRequestBranchStrategy = config.PullRequestRules.GetValueOrDefault(ruleName)?.BranchStrategy;

        if (!string.IsNullOrWhiteSpace(pullRequest.HeadSha)
            && (session.Status != SessionStatus.Completed || !session.CompletedBySession))
        {
            session.PullRequestHeadSha = pullRequest.HeadSha;
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private bool TryQueueFeedbackRedispatch(
        DispatchSession session,
        string sessionKey,
        GhCliService.NewCommentInfo commentInfo,
        CopilotdConfig config,
        bool isIssueComment)
    {
        if (session.RedispatchCount >= config.MaxRedispatches)
        {
            _logger.LogWarning("Session {Key} has reached the maximum re-dispatch limit ({Max}). " +
                "Use 'copilotd session reset' to re-enable. Ignoring feedback from {Author}",
                sessionKey, config.MaxRedispatches, commentInfo.Author);
            return false;
        }

        var trusted = IsCommentTrusted(session, commentInfo.Author, config, out var trustLevel);
        if (trusted is null)
        {
            _logger.LogWarning("Could not evaluate trust for {Author} on {Key} (trust_level={TrustLevel}), will retry next cycle",
                commentInfo.Author, sessionKey, trustLevel);
            return false;
        }

        if (trusted == false)
        {
            _logger.LogInformation("Ignoring feedback from untrusted author {Author} on {Key} (trust_level={TrustLevel})",
                commentInfo.Author, sessionKey, trustLevel);
            session.WaitingSince = commentInfo.CreatedAt;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            return false;
        }

        _logger.LogInformation("New feedback from {Author} detected on {Key}, re-dispatching session (redispatch {N}/{Max})",
            commentInfo.Author, sessionKey, session.RedispatchCount + 1, config.MaxRedispatches);
        session.Status = SessionStatus.Pending;
        session.FailureDetail = null;
        session.RedispatchCount++;
        session.LastRedispatchWasIssueComment = isIssueComment;
        session.WaitingSince = null;
        session.ProcessId = null;
        session.ProcessStartTime = null;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (isIssueComment && commentInfo.IssueCommentId.HasValue)
            TransitionReactionToIssueComment(session, config, commentInfo.IssueCommentId.Value, GhCliService.ReactionEyes);
        else
            TransitionReactionOnCurrentAnchor(session, config, GhCliService.ReactionEyes);

        return true;
    }

    private static void ResetForFreshDispatch(DispatchSession session, bool preserveReviewPullRequest)
    {
        session.Status = SessionStatus.Pending;
        session.FailureDetail = null;
        session.CompletedBySession = false;
        if (!preserveReviewPullRequest)
            session.PullRequestNumber = null;
        session.RedispatchCount = 0;
        session.LastRedispatchWasIssueComment = false;
        session.CopilotSessionId = Guid.NewGuid().ToString();
        session.CopilotSessionName = null;
        session.HasStarted = false;
        session.ProcessId = null;
        session.ProcessStartTime = null;
        session.LastVerifiedAt = null;
        session.WaitingSince = null;
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Step 4: Launch copilot for pending sessions, respecting MaxInstances limit and retry backoff.
    /// </summary>
    private void DispatchPendingSessions(CopilotdConfig config, DaemonState state)
    {
        var runningCount = state.Sessions.Values.Count(s => s.Status == SessionStatus.Running);
        var availableSlots = Math.Max(0, config.MaxInstances - runningCount);

        var pending = state.Sessions.Values
            .Where(s => s.Status == SessionStatus.Pending)
            .Where(s => !IsInBackoffWindow(s))
            .OrderBy(s => s.CreatedAt)
            .ToList();

        if (pending.Count > 0 && availableSlots == 0)
        {
            _logger.LogInformation("{Count} session(s) queued but max instances ({Max}) reached",
                pending.Count, config.MaxInstances);
            return;
        }

        var toDispatch = pending.Take(availableSlots).ToList();
        var skippedByLimit = pending.Count - toDispatch.Count;

        foreach (var session in toDispatch)
        {
            var mainRepoPath = _repoResolver.ResolveRepoPath(session.Repo, config, state);
            if (!string.IsNullOrEmpty(mainRepoPath) && Directory.Exists(mainRepoPath))
            {
                var trustCheck = _copilotTrustService.CheckTrustedFolders(_copilotTrustService.GetRequiredTrustedFolders(mainRepoPath));
                switch (trustCheck.Status)
                {
                    case CopilotTrustStatus.Unknown:
                        _logger.LogWarning("Could not verify Copilot folder trust for {Key}: {Message}",
                            session.IssueKey, trustCheck.Message ?? "unknown trust verification error");
                        break;

                    case CopilotTrustStatus.Untrusted:
                        _logger.LogWarning("Copilot folder trust missing for {Key}: {Folders}",
                            session.IssueKey, string.Join(", ", trustCheck.MissingFolders));
                        MarkSessionFailed(session, BuildTrustFailureDetail(session, trustCheck));
                        TransitionReactionOnCurrentAnchor(session, config, GhCliService.ReactionThumbsDown);
                        _stateStore.SaveState(state);
                        continue;

                    case CopilotTrustStatus.Trusted:
                        break;
                }
            }

            session.Status = SessionStatus.Dispatching;
            session.FailureDetail = null;
            session.UpdatedAt = DateTimeOffset.UtcNow;

            // Create worktree for isolated working directory
            var worktreeResult = _processManager.PrepareWorktree(session, config, state);
            if (worktreeResult == WorktreeResult.Failed)
            {
                _logger.LogWarning("Failed to prepare worktree for {Key}", session.IssueKey);
                MarkSessionFailed(session,
                    $"Failed to prepare the git worktree for dispatch. Verify the local clone for {session.Repo} is healthy, then run 'copilotd session reset {session.IssueKey}'.");
                TransitionReactionOnCurrentAnchor(session, config, GhCliService.ReactionThumbsDown);
                _stateStore.SaveState(state);
                continue;
            }

            // For newly created issue worktrees, create the linked branch on GitHub first
            // (that mutation creates the remote branch), then push locally to set
            // upstream tracking. PR worktrees are pushed according to their branch strategy.
            if (worktreeResult == WorktreeResult.CreatedNew && !string.IsNullOrEmpty(session.BranchName))
            {
                if (session.SubjectKind == DispatchSubjectKind.Issue)
                {
                    var sha = _processManager.GetHeadSha(session);
                    if (sha is not null)
                    {
                        _ghCli.CreateLinkedBranchForIssue(session.Repo, session.IssueNumber, session.BranchName, sha);
                    }
                }

                _processManager.PushBranch(session, config, state);
            }

            // We need the issue data for prompt building
            var issue = new GitHubIssue
            {
                Number = session.IssueNumber,
                Repo = session.Repo,
            };

            var result = _processManager.LaunchCopilot(session, config, issue, state);
            if (result is null)
            {
                _logger.LogWarning("Failed to launch copilot for {Key}", session.IssueKey);
                MarkSessionFailed(session,
                    $"Failed to launch the Copilot CLI process. Review the copilotd logs, then run 'copilotd session reset {session.IssueKey}'.");
                TransitionReactionOnCurrentAnchor(session, config, GhCliService.ReactionThumbsDown);
                // Clean up the worktree we just created
                _processManager.CleanupWorktree(session, config, state);
            }
            else
            {
                // Session launched successfully — indicate active work
                TransitionReactionOnCurrentAnchor(session, config, GhCliService.ReactionRocket);
            }

            // Save state after each launch to prevent ghost processes on crash
            _stateStore.SaveState(state);
        }

        if (skippedByLimit > 0)
        {
            _logger.LogInformation("{Count} session(s) still queued, waiting for instance slots", skippedByLimit);
        }
    }

    /// <summary>
    /// Checks whether a comment author is trusted for re-dispatch based on the rule's trust level.
    /// Returns true if the comment should trigger re-dispatch, false if it should be ignored,
    /// null if trust could not be determined (e.g., transient API failure) and the comment should
    /// be retried on the next poll cycle rather than skipped.
    /// </summary>
    private bool? IsCommentTrusted(
        DispatchSession session,
        string commentAuthor,
        CopilotdConfig config,
        out CommentTrustLevel effectiveTrustLevel)
    {
        var issueRule = session.SubjectKind == DispatchSubjectKind.Issue
            ? config.IssueRules.GetValueOrDefault(session.RuleName)
            : null;
        var pullRequestRule = session.SubjectKind == DispatchSubjectKind.PullRequest
            ? config.PullRequestRules.GetValueOrDefault(session.RuleName)
            : null;
        var ruleOptions = (DispatchRuleOptions?)issueRule ?? pullRequestRule;
        effectiveTrustLevel = ruleOptions?.TrustLevel ?? CommentTrustLevel.Collaborators;

        switch (effectiveTrustLevel)
        {
            case CommentTrustLevel.All:
                return true;

            case CommentTrustLevel.Collaborators:
                return _ghCli.HasWriteAccess(session.Repo, commentAuthor);

            case CommentTrustLevel.IssueAuthor:
                return session.SubjectAuthor is not null
                    && string.Equals(session.SubjectAuthor, commentAuthor, StringComparison.OrdinalIgnoreCase);

            case CommentTrustLevel.Assignees:
                if (session.SubjectKind == DispatchSubjectKind.PullRequest)
                    return _ghCli.HasWriteAccess(session.Repo, commentAuthor);

                var assignees = _ghCli.GetIssueAssignees(session.Repo, session.SubjectNumber);
                if (assignees is null)
                    return null; // API failure — retry next cycle
                return assignees.Exists(a => string.Equals(a, commentAuthor, StringComparison.OrdinalIgnoreCase));

            case CommentTrustLevel.IssueAuthorAndCollaborators:
                if (session.SubjectAuthor is not null
                    && string.Equals(session.SubjectAuthor, commentAuthor, StringComparison.OrdinalIgnoreCase))
                    return true;
                return _ghCli.HasWriteAccess(session.Repo, commentAuthor);

            case CommentTrustLevel.MatchDispatchRule:
                if (issueRule is null && pullRequestRule is null)
                    return _ghCli.HasWriteAccess(session.Repo, commentAuthor);
                var authorMode = session.SubjectKind == DispatchSubjectKind.PullRequest
                    ? pullRequestRule!.AuthorMode
                    : issueRule!.AuthorMode;
                var authors = session.SubjectKind == DispatchSubjectKind.PullRequest
                    ? pullRequestRule!.Authors
                    : issueRule!.Authors;
                return authorMode switch
                {
                    // AuthorMode.Any means "no author filtering on dispatch" — but for re-dispatch
                    // trust, allowing anyone would be a security risk. Fall back to collaborators.
                    AuthorMode.Any => _ghCli.HasWriteAccess(session.Repo, commentAuthor),
                    AuthorMode.Allowed => authors.Contains(commentAuthor, StringComparer.OrdinalIgnoreCase),
                    AuthorMode.WriteAccess => _ghCli.HasWriteAccess(session.Repo, commentAuthor),
                    _ => _ghCli.HasWriteAccess(session.Repo, commentAuthor),
                };

            default:
                // Unknown trust level — fail-closed to collaborators
                return _ghCli.HasWriteAccess(session.Repo, commentAuthor);
        }
    }

    private static void MarkSessionFailed(DispatchSession session, string detail)
    {
        session.Status = SessionStatus.Failed;
        session.FailureDetail = detail;
        session.RetryCount++;
        session.LastFailureAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.ProcessId = null;
        session.ProcessStartTime = null;
    }

    private string BuildTrustFailureDetail(DispatchSession session, CopilotTrustCheckResult trustCheck)
    {
        var folders = trustCheck.MissingFolders.Count > 0
            ? string.Join(", ", trustCheck.MissingFolders)
            : string.Join(", ", trustCheck.RequiredFolders);

        return $"Copilot cannot dispatch this session because these folders are not trusted in {NormalizeDisplayPath(_copilotTrustService.ConfigPath)}: {folders}. " +
            $"Trust the folders for all sessions in Copilot, then run 'copilotd session reset {session.IssueKey}'.";
    }

    private static string NormalizeDisplayPath(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>
    /// Returns true if a session should wait before retrying, using exponential backoff.
    /// Backoff = min(2^RetryCount minutes, 30 minutes).
    /// </summary>
    private static bool IsInBackoffWindow(DispatchSession session)
    {
        if (session.RetryCount == 0 || session.LastFailureAt is null)
            return false;

        var backoffMinutes = Math.Min(Math.Pow(2, session.RetryCount), 30);
        var backoff = TimeSpan.FromMinutes(backoffMinutes);
        return DateTimeOffset.UtcNow - session.LastFailureAt.Value < backoff;
    }

    /// <summary>
    /// Checks whether reactions are enabled for a session based on rule + global config.
    /// </summary>
    private static bool AreReactionsEnabled(CopilotdConfig config, DispatchSession session)
    {
        var ruleOptions = session.SubjectKind == DispatchSubjectKind.PullRequest
            ? (DispatchRuleOptions?)config.PullRequestRules.GetValueOrDefault(session.RuleName)
            : config.IssueRules.GetValueOrDefault(session.RuleName);
        if (ruleOptions?.EnableReactions is { } enableReactions)
            return enableReactions;

        return config.EnableReactions;
    }

    /// <summary>
    /// Transitions the lifecycle reaction while keeping the current anchor.
    /// </summary>
    private void TransitionReactionOnCurrentAnchor(DispatchSession session, CopilotdConfig config, string? newContent)
        => TransitionReaction(session, config, session.GetReactionAnchor(), newContent);

    /// <summary>
    /// Transitions the lifecycle reaction onto the issue body.
    /// </summary>
    private void TransitionReactionToIssue(DispatchSession session, CopilotdConfig config, string? newContent)
        => TransitionReaction(session, config, ReactionAnchor.IssueBody, newContent);

    /// <summary>
    /// Transitions the lifecycle reaction onto a specific issue comment.
    /// </summary>
    private void TransitionReactionToIssueComment(DispatchSession session, CopilotdConfig config, long issueCommentId, string? newContent)
        => TransitionReaction(session, config, ReactionAnchor.ForIssueComment(issueCommentId), newContent);

    /// <summary>
    /// Transitions the lifecycle reaction to the requested anchor and content.
    /// Best-effort: failures are logged but do not block session lifecycle.
    /// </summary>
    private void TransitionReaction(DispatchSession session, CopilotdConfig config, ReactionAnchor anchor, string? newContent)
    {
        if (!AreReactionsEnabled(config, session))
            return;

        if (session.IssueReactionId.HasValue)
        {
            RemoveReaction(session, session.GetReactionAnchor(), session.IssueReactionId.Value);
            session.IssueReactionId = null;
        }

        session.SetReactionAnchor(anchor);
        if (newContent is null)
            return;

        session.IssueReactionId = AddReaction(session, anchor, newContent);
    }

    private long? AddReaction(DispatchSession session, ReactionAnchor anchor, string content)
        => anchor.TargetType == ReactionTargetType.IssueComment && anchor.IssueCommentId.HasValue
            ? _ghCli.AddIssueCommentReaction(session.Repo, anchor.IssueCommentId.Value, content)
            : _ghCli.AddIssueReaction(session.Repo, session.IssueNumber, content);

    private void RemoveReaction(DispatchSession session, ReactionAnchor anchor, long reactionId)
    {
        if (anchor.TargetType == ReactionTargetType.IssueComment && anchor.IssueCommentId.HasValue)
        {
            _ghCli.RemoveIssueCommentReaction(session.Repo, anchor.IssueCommentId.Value, reactionId);
            return;
        }

        _ghCli.RemoveIssueReaction(session.Repo, session.IssueNumber, reactionId);
    }
}
