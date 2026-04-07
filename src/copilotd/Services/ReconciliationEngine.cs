using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// The reconciliation engine. Compares desired dispatches (from rules + issues)
/// against observed state (persisted sessions + live processes) and takes
/// corrective action to converge.
///
/// Truth sources (in order of precedence for conflicts):
/// 1. Live process status (ground truth for "is it actually running?")
/// 2. Current GitHub issue matches (ground truth for "should it be running?")
/// 3. Persisted state (bookkeeping, used as starting point)
/// </summary>
public sealed class ReconciliationEngine
{
    private readonly ProcessManager _processManager;
    private readonly GhCliService _ghCli;
    private readonly StateStore _stateStore;
    private readonly ILogger<ReconciliationEngine> _logger;

    public ReconciliationEngine(
        ProcessManager processManager,
        GhCliService ghCli,
        StateStore stateStore,
        ILogger<ReconciliationEngine> logger)
    {
        _processManager = processManager;
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

        // Step 0: Prune terminal sessions older than 7 days
        PruneTerminalSessions(state);

        // Step 1: Verify all tracked non-terminal sessions against live processes
        VerifyTrackedSessions(state);

        // Step 2: Gather all matching issues from configured rules
        var (desiredDispatches, queriedRepos) = ComputeDesiredDispatches(config);

        // Step 3: Reconcile desired vs observed
        ReconcileDesiredVsObserved(config, state, desiredDispatches, queriedRepos);

        // Step 4: Dispatch pending sessions (respects MaxInstances)
        DispatchPendingSessions(config, state);

        // Step 5: Persist corrected state
        state.LastPollTime = DateTimeOffset.UtcNow;
        _stateStore.SaveState(state);

        _logger.LogInformation("Reconciliation complete: {Active} active, {Pending} pending, {Waiting} waiting, {Terminal} terminal sessions",
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Running),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.Pending),
            state.Sessions.Values.Count(s => s.Status == SessionStatus.WaitingForFeedback),
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

            // WaitingForFeedback sessions have no running process — skip liveness check
            if (session.Status is SessionStatus.WaitingForFeedback)
                continue;

            // For Joined sessions, check if the interactive process is still alive.
            // If it exited (e.g., terminal killed without cleanup), reset to Pending.
            if (session.Status is SessionStatus.Joined)
            {
                if (session.ProcessId is null)
                {
                    // No PID tracked — join process never saved it or state is stale
                    _logger.LogInformation("Joined session {Key} has no tracked PID, resetting to Pending", key);
                    session.Status = SessionStatus.Pending;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    continue;
                }

                var joinResult = _processManager.CheckProcess(session);
                if (joinResult is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused)
                {
                    _logger.LogInformation("Joined session {Key} interactive process is gone, resetting to Pending", key);
                    session.Status = SessionStatus.Pending;
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
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;

                case ProcessLivenessResult.PidReused:
                    _logger.LogWarning("Session {Key} PID {Pid} was reused by another process, marking orphaned", key, session.ProcessId);
                    session.Status = SessionStatus.Orphaned;
                    session.ProcessId = null;
                    session.ProcessStartTime = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
            }
        }
    }

    /// <summary>
    /// Step 2: Query GitHub for all issues that currently match configured rules.
    /// Returns the desired dispatches and the set of repos that were successfully queried.
    /// Only repos in the queried set should be used for termination decisions — if a repo
    /// query fails, existing sessions for that repo are preserved.
    /// </summary>
    private (Dictionary<string, (GitHubIssue Issue, string RuleName)> Desired, HashSet<string> QueriedRepos)
        ComputeDesiredDispatches(CopilotdConfig config)
    {
        var desired = new Dictionary<string, (GitHubIssue, string)>(StringComparer.OrdinalIgnoreCase);
        var queriedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ruleName, rule) in config.Rules)
        {
            foreach (var repo in rule.Repos)
            {
                try
                {
                    var issues = _ghCli.QueryIssues(repo, rule);
                    queriedRepos.Add(repo);

                    foreach (var issue in issues)
                    {
                        // Double-check rule match (gh filters are best-effort)
                        if (!rule.Matches(issue))
                            continue;

                        if (!desired.ContainsKey(issue.Key))
                        {
                            desired[issue.Key] = (issue, ruleName);
                            _logger.LogDebug("Desired dispatch: {Key} via rule '{Rule}'", issue.Key, ruleName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error querying issues for {Repo} with rule '{Rule}', preserving existing sessions", repo, ruleName);
                }
            }
        }

        _logger.LogInformation("Found {Count} issues matching configured rules ({Queried}/{Total} repos queried successfully)",
            desired.Count, queriedRepos.Count,
            config.Rules.Values.SelectMany(r => r.Repos).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return (desired, queriedRepos);
    }

    /// <summary>
    /// Step 3: Compare desired dispatches against tracked sessions.
    /// - Issues without active sessions → create pending
    /// - Tracked sessions whose issues no longer match (and repo was queried) → mark completed
    /// - Orphaned sessions for still-matching issues → re-dispatch if retries remain
    /// - Completed sessions for still-matching issues → re-dispatch with new session ID
    /// </summary>
    private void ReconcileDesiredVsObserved(
        CopilotdConfig config,
        DaemonState state,
        Dictionary<string, (GitHubIssue Issue, string RuleName)> desired,
        HashSet<string> queriedRepos)
    {
        // Handle sessions for issues that no longer match — only for successfully queried repos
        var toTerminate = new List<DispatchSession>();
        foreach (var (key, session) in state.Sessions)
        {
            // Clear CompletedBySession flag when the issue no longer matches rules,
            // so that if the issue re-matches later it will be re-dispatched
            if (session.Status == SessionStatus.Completed && session.CompletedBySession
                && !desired.ContainsKey(key) && queriedRepos.Contains(session.Repo))
            {
                _logger.LogInformation("Issue {Key} no longer matches rules, clearing CompletedBySession flag", key);
                session.CompletedBySession = false;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (session.IsTerminal)
                continue;

            // Never terminate user-controlled sessions
            if (session.Status is SessionStatus.Joined)
                continue;

            if (!desired.ContainsKey(key))
            {
                // Only act if the session's repo was successfully queried.
                // If the repo query failed, we don't know if the issue still matches.
                if (!queriedRepos.Contains(session.Repo))
                {
                    _logger.LogDebug("Repo {Repo} query failed, preserving session {Key}", session.Repo, key);
                    continue;
                }

                // WaitingForFeedback sessions have no process to terminate
                if (session.Status is SessionStatus.WaitingForFeedback)
                {
                    _logger.LogInformation("Issue {Key} no longer matches rules, completing waiting session", key);
                    session.Status = SessionStatus.Completed;
                    session.WaitingSince = null;
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    _processManager.CleanupWorktree(session, config, state);
                    continue;
                }

                _logger.LogInformation("Issue {Key} no longer matches rules, terminating session", key);
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
                session.UpdatedAt = DateTimeOffset.UtcNow;
                _processManager.CleanupWorktree(session, config, state);
            }
        }

        // Handle desired issues that need new or re-dispatched sessions
        foreach (var (issueKey, (issue, ruleName)) in desired)
        {
            if (state.Sessions.TryGetValue(issueKey, out var existing))
            {
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
                        if (existing.WaitingSince is not null
                            && _ghCli.HasNewCommentsSince(existing.Repo, existing.IssueNumber, existing.WaitingSince.Value))
                        {
                            _logger.LogInformation("New comment detected on {Key}, re-dispatching waiting session", issueKey);
                            // Keep same CopilotSessionId so --resume preserves context
                            existing.Status = SessionStatus.Pending;
                            existing.WaitingSince = null;
                            existing.ProcessId = null;
                            existing.ProcessStartTime = null;
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                        else
                        {
                            _logger.LogDebug("Session {Key} still waiting for feedback", issueKey);
                        }
                        continue;

                    case SessionStatus.Orphaned when existing.CanRetry:
                        _logger.LogInformation("Re-dispatching orphaned session {Key} (retry {N}/{Max})",
                            issueKey, existing.RetryCount + 1, DispatchSession.MaxRetries);
                        _processManager.CleanupWorktree(existing, config, state);
                        existing.Status = SessionStatus.Pending;
                        existing.RetryCount++;
                        existing.CopilotSessionId = Guid.NewGuid().ToString();
                        existing.ProcessId = null;
                        existing.ProcessStartTime = null;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        existing.LastFailureAt = DateTimeOffset.UtcNow;
                        continue;

                    case SessionStatus.Orphaned:
                    case SessionStatus.Failed:
                        if (!existing.CanRetry)
                        {
                            _logger.LogWarning("Session {Key} exceeded max retries, marking failed", issueKey);
                            existing.Status = SessionStatus.Failed;
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
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
                        existing.CopilotSessionId = Guid.NewGuid().ToString();
                        existing.ProcessId = null;
                        existing.ProcessStartTime = null;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        continue;
                }
            }
            else
            {
                // New issue, create pending session
                _logger.LogInformation("New issue {Key} matched by rule '{Rule}', creating pending dispatch", issueKey, ruleName);
                state.Sessions[issueKey] = new DispatchSession
                {
                    IssueKey = issueKey,
                    Repo = issue.Repo,
                    IssueNumber = issue.Number,
                    RuleName = ruleName,
                    CopilotSessionId = Guid.NewGuid().ToString(),
                    Status = SessionStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }
        }
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
            session.Status = SessionStatus.Dispatching;
            session.UpdatedAt = DateTimeOffset.UtcNow;

            // Create worktree for isolated working directory
            if (!_processManager.PrepareWorktree(session, config, state))
            {
                _logger.LogWarning("Failed to prepare worktree for {Key}", session.IssueKey);
                session.Status = SessionStatus.Failed;
                session.RetryCount++;
                session.LastFailureAt = DateTimeOffset.UtcNow;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                _stateStore.SaveState(state);
                continue;
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
                session.Status = SessionStatus.Failed;
                session.RetryCount++;
                session.LastFailureAt = DateTimeOffset.UtcNow;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                // Clean up the worktree we just created
                _processManager.CleanupWorktree(session, config, state);
            }
            // else: session was updated in-place by LaunchCopilot

            // Save state after each launch to prevent ghost processes on crash
            _stateStore.SaveState(state);
        }

        if (skippedByLimit > 0)
        {
            _logger.LogInformation("{Count} session(s) still queued, waiting for instance slots", skippedByLimit);
        }
    }

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
}
