---
name: copilotd-e2e-verification
description: Perform real end-to-end validation of copilotd issue and pull request orchestration using live GitHub artifacts.
---

# copilotd end-to-end verification

Use this skill when validating substantial copilotd orchestration changes, especially changes to dispatch rules, reconciliation, session lifecycle, worktree handling, prompt callbacks, or GitHub issue/PR feedback loops.

## Goal

Verify copilotd as a live reconciliation daemon, not just by build or command smoke tests. A good verification proves that real GitHub issues and pull requests move through the expected lifecycle, Copilot sessions can call back into copilotd, worktrees and state converge correctly, and temporary artifacts are cleaned up.

## Optimum approach

1. **Use an isolated copilotd home.** Set `COPILOTD_HOME` to a disposable directory such as `.\copilotd-home-e2e` or another task-specific path. Do not use the user's normal `~\.copilotd` state.
2. **Start with local CLI validation.** Build the branch, then verify `config`, `rules add/list/update/delete`, invalid rule arguments, `status`, `session list --all`, and JSON persistence before creating live artifacts.
3. **Use a real but safe target repository.** Prefer a repo already cloned locally and writable by the user. Configure `repo_home` so copilotd resolves the existing clone and creates sibling `<repo>_sessions` worktrees.
4. **Create explicit temporary labels.** Use unique labels such as `copilotd-e2e-ready`, `copilotd-e2e-clarify`, and `copilotd-e2e-pr` so rules match only the verification artifacts.
5. **Create at least two issue scenarios.**
   - A ready-to-implement issue with small, deterministic acceptance criteria that should lead to a branch, commit, PR, `session pr`, and `WaitingForReview`.
   - An intentionally ambiguous issue that should lead to `session comment`, `WaitingForFeedback`, a real clarification reply, and re-dispatch.
6. **Create a PR rule that observes the issue-created PRs.** Add a PR dispatch rule using `--kind pr`, a temporary PR label, `--base`, and a safe branch strategy such as `read-only` for validation-only sessions.
7. **Drive the full feedback loop.** After issue sessions create PRs, let the PR rule launch PR-root validation sessions. Ensure those sessions comment on the PR. Then add a manual PR comment or review from a trusted user without a copilotd marker so the original issue-owned `WaitingForReview` session re-dispatches, pushes a follow-up commit, and returns to `WaitingForReview`.
8. **Observe state and GitHub together.** Cross-check `state.json`, `copilotd session list --all`, issue comments, PR comments, PR labels, PR commits, and local git worktrees. Do not trust a single surface.
9. **Keep a journal.** Record every issue, workaround, and stop-worthy finding as it happens, including exact issue/PR numbers and whether the behavior was expected or a product problem.
10. **Clean up aggressively.** Close temporary PRs without merging, close temporary issues, delete temporary branches/labels, stop daemon processes, remove isolated homes and temporary publish folders, prune worktrees, and verify the target repo returns to a clean default branch.

## Important setup details

- Run from source via `.\copilotd.ps1` first, as this matches normal development validation.
- If unattended Copilot sessions block on callback commands like `dotnet run --project <copilotd-source> --no-build -- session ...`, publish copilotd outside the source tree and prepend that publish directory to `PATH`. Starting the daemon from a path outside the source tree makes callback prompts use `copilotd` instead of a `dotnet run --project ...` command outside the target repo trust scope.
- Ensure Copilot trusted folders include both the target repo and the sibling `<repo>_sessions` directory. Missing trust should be treated as a real preflight/environment finding, then fixed so validation can continue.
- Disable control sessions in the isolated config unless the control session itself is under test. This avoids extra remote sessions and unrelated worktrees.
- Use low polling intervals such as `--interval 15` during verification, and enable `--log-level debug` so reconciliation decisions are visible.
- If testing self-update is not in scope, set `COPILOTD_DISABLE_SELF_UPDATES=1` or pass `--disable-self-updates`.

## Suggested live flow

1. Build the branch under test.
2. Create isolated `config.json` with:
   - `repoHome` pointing at the parent folder that contains the target clone.
   - `maxInstances` set high enough for the planned issue and PR sessions.
   - `enableControlSession` set to `false` unless needed.
   - one or more issue rules scoped to temporary labels and the target repo.
   - one PR rule scoped to a temporary PR label, target repo, and base branch.
3. Create temporary labels in the target repo.
4. Create the ready and ambiguous issues.
5. Start copilotd with the isolated home.
6. Confirm the daemon matches the issues, creates pending sessions, creates worktrees, and launches Copilot processes.
7. For the ambiguous issue, confirm a copilotd-authored issue comment appears and state enters `WaitingForFeedback`; reply with precise requirements and confirm re-dispatch.
8. For ready issue sessions, confirm commits are pushed, PRs are opened, PRs get the PR-rule label, and issue sessions run `session pr` and enter `WaitingForReview`.
9. Confirm PR-root sessions are created for the PRs and use the expected branch strategy/worktree layout.
10. Confirm PR-root sessions complete and leave useful validation comments.
11. Add manual trusted PR feedback to the PRs and confirm the original issue-owned sessions re-dispatch with `RedispatchCount` incremented, pushes follow-up commits, and returns to `WaitingForReview`.
12. Close PRs and issues, then confirm reconciliation marks sessions completed and cleans up issue and PR worktrees.

## What to inspect

- `copilotd status`
- `copilotd session list --all`
- `copilotd session info <owner/repo#number>`
- `COPILOTD_HOME\state.json`
- daemon logs under `COPILOTD_HOME\logs\daemon_*`
- recent Copilot logs under `~\.copilot\logs` when a session appears stuck
- target repo `git worktree list`
- target repo branch lists for stale `copilotd/*` branches
- GitHub issue comments, PR comments, PR labels, PR commits, and PR state

## Success criteria

- Issue rules and PR rules match only intended temporary artifacts.
- Ready issues produce working branches, commits, PRs, and `WaitingForReview` state.
- Ambiguous issues ask a clarifying question, wait, and resume from a real issue reply.
- PR rules dispatch PR-root sessions with the expected PR context and branch strategy.
- PR feedback from a trusted non-copilotd comment re-dispatches the original issue-owned session.
- Follow-up commits update the existing PR, not a new PR.
- Closing or unmatching subjects completes sessions and cleans up local worktrees/branches.
- All temporary GitHub and local artifacts are removed or explicitly retained for root-cause analysis.

## Cleanup checklist

- Close temporary PRs with a "do not merge" comment.
- Close temporary issues with a verification cleanup comment.
- Delete temporary remote branches and run `git fetch --prune`.
- Delete temporary labels.
- Stop copilotd and any shell wrappers used to host it.
- Remove isolated `COPILOTD_HOME` folders and temporary publish folders.
- Run `git worktree list` in the target repo and remove only worktrees created by this verification.
- Confirm the target repo and copilotd repo have clean `git status` output.

## Reporting findings

Lead with what was verified, then list findings separately from environment prerequisites. For each issue, include:

- the observed behavior,
- the expected behavior,
- whether it was blocking or worked around,
- the exact workaround used, and
- any artifacts retained for diagnosis.

Do not fix product bugs during verification unless the user explicitly asks for follow-up implementation work.
