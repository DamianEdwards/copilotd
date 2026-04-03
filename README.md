# copilotd

An orchestration daemon that watches configured GitHub repositories for issues matching dispatch rules and spawns [Copilot CLI](https://docs.github.com/copilot/how-tos/copilot-cli) sessions to work on them automatically.

## Features

- **Issue watching** — polls GitHub repos for issues matching configurable rules (assigned user, labels, milestone, issue type)
- **Automatic dispatch** — launches `copilot --remote` sessions with templated prompts derived from issue metadata
- **Named dispatch rules** — flexible, composable rules with per-rule launch options (`--yolo`, `--allow-all-tools`, `--allow-all-urls`, extra prompts, repo assignments)
- **Session lifecycle** — full state machine with retry, backoff, orphan recovery, and explicit completion signaling
- **Self-healing state** — reconciles persisted state, live process status, and GitHub issue matches on every poll cycle and at startup
- **Crash-resilient** — dispatched `copilot` sessions run as independent processes that survive daemon restarts; state is persisted atomically
- **Interactive takeover** — join any orchestrated session interactively with `copilotd session join`, then hand it back automatically
- **Cross-platform** — works on Windows, macOS, and Linux; publishes as native AOT

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated via `gh auth login`
- [Copilot CLI (`copilot`)](https://docs.github.com/copilot/how-tos/copilot-cli) — authenticated via `copilot login`

## Getting started

```bash
# Clone and build
git clone https://github.com/DamianEdwards/copilotd.git
cd copilotd
dotnet build

# First-run setup (checks dependencies, prompts for config)
./copilotd.sh init          # macOS/Linux
copilotd.cmd init           # Windows

# Start the daemon
./copilotd.sh run           # macOS/Linux
copilotd.cmd run            # Windows
```

Convenience scripts `copilotd.sh` and `copilotd.cmd` in the repo root run the project from source via `dotnet run`, passing all arguments through.

## Commands

| Command | Description |
|---------|-------------|
| `copilotd init` | Interactive first-run setup (dependency checks, auth, repo selection, default rule) |
| `copilotd run` | Start the polling daemon |
| `copilotd status` | Show daemon health, watched repos, and session list |
| `copilotd session` | List dispatched sessions (alias for `session list`) |
| `copilotd session list` | List dispatched sessions with optional filtering |
| `copilotd session join <issue>` | Take over a session interactively |
| `copilotd session complete <issue>` | Mark a session as completed (callable from within a copilot session) |
| `copilotd session reset <issue>` | Reset a completed/failed session to pending for re-dispatch |
| `copilotd config` | Display current configuration |
| `copilotd config --set key=value` | Set a config value (`repo_home`, `prompt`, `max_instances`) |
| `copilotd rules list` | List all dispatch rules |
| `copilotd rules add <name>` | Add a new dispatch rule |
| `copilotd rules update <name>` | Update an existing rule |
| `copilotd rules delete <name>` | Delete a rule (the `Default` rule cannot be deleted) |

### Run options

```
--interval <seconds>   Polling interval (default: 60)
--log-level <level>    Enable console logging (debug, info, warning, error)
```

### Status & session options

```
--filter <status>   Filter by status (pending, running, joined, completed, failed, orphaned)
--all               Include ended (completed/failed) sessions
```

### Rules options

Rules support conditions (`--user`, `--label`, `--milestone`, `--type`) and launch options (`--yolo`, `--allow-all-tools`, `--allow-all-urls`, `--prompt`, `--repo`). All conditions are logical AND.

```bash
# Add a rule
copilotd rules add "Dashboard issues" --label area-dashboard --yolo --repo "org/repo"

# Update labels on a rule
copilotd rules update Default --delete-label copilotd --add-label dispatch

# Add/remove repos from a rule
copilotd rules update Default --add-repo "org/repo" --delete-repo "org/old-repo"
```

### Prompt templating

The base prompt supports token replacement:

| Token | Value |
|-------|-------|
| `$(issue.repo)` | Repository in `org/repo` format |
| `$(issue.id)` | Issue number |
| `$(issue.type)` | Issue type (e.g., `bug`) |
| `$(issue.milestone)` | Milestone title |

Per-rule extra prompts are appended after the base prompt.

## Session lifecycle

Each dispatched copilot session follows a state machine:

```
                 ┌──────────────────────────────────────────────┐
                 │                                              │
  new issue      ▼     launch        process alive              │
  matched ──► Pending ──► Dispatching ──► Running               │
                 ▲                        │   │                 │
                 │                        │   │  user takes     │
      retry      │   process died         │   │  over           │
   (≤3 times)    │◄──── Orphaned ◄────────┘   │                 │
                 │          │                  ▼                 │
                 │          │ max          Joined                │
                 │          │ retries       │  │                 │
                 │          ▼               │  │ user exits      │
                 │       Failed            │  └────────────────►│
                 │                         │                     │
                 │   issue closed/         │  copilot calls      │
                 │   unmatched             │  session complete    │
                 │◄──── Completed ◄────────┴─────────────────────┘
                 │      (auto)                   (explicit)
                 │
          re-opened / re-matched
          (only if not explicitly completed)
```

### State transitions

| From | To | Trigger |
|------|----|---------|
| *(new)* | **Pending** | Issue matches a dispatch rule |
| **Pending** | **Dispatching** | Daemon launches copilot process (respects `max_instances` limit and retry backoff) |
| **Dispatching** | **Running** | Process successfully started, PID tracked |
| **Dispatching** | **Failed** | Process launch failed (increments retry count) |
| **Running** | **Completed** | Issue no longer matches rules (closed, relabeled, etc.) — process gracefully terminated |
| **Running** | **Orphaned** | Process died unexpectedly (PID gone or reused) |
| **Running** | **Joined** | User runs `copilotd session join` — process terminated, user takes over |
| **Orphaned** | **Pending** | Retry eligible (retry count < 3) — exponential backoff: 2^n minutes, capped at 30m |
| **Orphaned** | **Failed** | Max retries (3) exceeded |
| **Failed** | **Pending** | Issue still matches and retry count < 3 |
| **Joined** | **Pending** | User exits interactive session — automatically re-queued for dispatch |
| **Completed** | **Pending** | Issue re-matches rules (e.g., reopened) — only if not explicitly completed by copilot |
| Any non-terminal | **Completed** | Copilot calls `copilotd session complete` — sets `CompletedBySession` flag, prevents re-dispatch |

### Session completion

Sessions can reach the **Completed** state in two ways:

1. **Automatic** — the issue no longer matches dispatch rules (closed, label removed, etc.). The process is gracefully terminated. If the issue later re-matches, the session is re-dispatched.

2. **Explicit** — the copilot session calls `copilotd session complete <issue>` when it finishes its work. This sets the `CompletedBySession` flag, which prevents automatic re-dispatch while the issue still matches rules. If the issue later *stops matching* (e.g., label removed, issue closed) and then *re-matches* (e.g., label re-added, issue reopened), the flag is automatically cleared and the session is re-dispatched.

3. **Manual reset** — use `copilotd session reset <issue>` to force a completed or failed session back to pending with a fresh session ID, regardless of the `CompletedBySession` flag.

The default prompt instructs copilot sessions to call `copilotd session complete` when they finish their work.

### Interactive takeover

Use `copilotd session join <issue>` to take over any tracked session:

1. If the session is currently running, the orchestrated process is gracefully terminated
2. The session is marked as **Joined** — the daemon won't interfere
3. An interactive `copilot --resume=<session_id>` is launched with full terminal access
4. When you exit, the session is automatically re-queued as **Pending** for orchestrated dispatch

### Terminal states and pruning

- **Completed** and **Failed** are terminal states
- Terminal sessions are automatically pruned from state after 7 days
- All running sessions are gracefully terminated when the daemon shuts down

## Configuration

Stored in `~/.copilotd/`:

- `config.json` — user-managed settings (repo home, prompt template, rules)
- `state.json` — runtime session tracking (auto-managed, self-healing)
- `.lock` — single-instance guard (present while daemon is running)

### Config options

| Key | Default | Description |
|-----|---------|-------------|
| `repo_home` | — | Root directory where repos are cloned (`<org>/<repo>` sub-folders expected) |
| `prompt` | *(see below)* | Base prompt template with token replacement |
| `max_instances` | `3` | Maximum concurrent copilot processes; excess sessions queue as Pending |

### Rule settings

| Setting | Default | Description |
|---------|---------|-------------|
| `user` | *(any)* | Assigned user to match |
| `labels` | *(none)* | Labels that must all be present (logical AND) |
| `milestone` | *(any)* | Milestone the issue must belong to |
| `type` | *(any)* | Issue type filter (e.g., `bug`, `feature`) |
| `repos` | *(none)* | Repositories this rule applies to (`org/repo` format) |
| `yolo` | `false` | Pass `--yolo` to copilot (implies `allow_all_tools` and `allow_all_urls`) |
| `allow_all_tools` | `true` | Pass `--allow-all-tools` to copilot |
| `allow_all_urls` | `false` | Pass `--allow-all-urls` to copilot |
| `extra_prompt` | *(none)* | Additional prompt text appended when this rule triggers |

Log files are written to `$TEMP/copilotd/logs/` with daily rollover.

## Architecture

- **System.CommandLine** for CLI parsing
- **Spectre.Console** for interactive prompts and formatted output
- **Native AOT** compatible (source-generated JSON serialization)
- Dispatched `copilot` processes are fully independent (not child processes)
- Each session gets its own **git worktree** for isolation — multiple sessions on the same repo work in parallel without conflicts
- State reconciliation uses three truth sources: persisted state → live process status → current GitHub issue matches
- Graceful process shutdown via platform-specific signals (Ctrl+Break/Ctrl+C on Windows via helper process, SIGINT/SIGKILL on Unix)

### Worktree isolation

Each dispatched session works in its own git worktree on a new branch, ensuring:
- **No conflicts** between concurrent sessions on the same repo
- **Clean starting state** from the latest default branch HEAD
- **Isolated branches** — each session creates `copilotd/issue-<N>`

Directory layout:

```
<repo_home>/org/repo/                    ← main checkout (fetch-only base)
<repo_home>/org/repo_sessions/issue-1/   ← worktree for issue #1
<repo_home>/org/repo_sessions/issue-5/   ← worktree for issue #5
```

Worktrees are created before dispatch (`git fetch` + `git worktree add`) and cleaned up
when sessions complete, are reset, or are pruned.

### Prompt customization

The prompt template is stored at `~/.copilotd/prompt.md` (created during `copilotd init`).
Edit this file to customize the instructions given to dispatched copilot sessions. Token
replacement is applied at dispatch time. Per-rule extra prompts are appended after the base
prompt.

## License

MIT
