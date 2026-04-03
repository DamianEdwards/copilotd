# Copilot Instructions for copilotd

## Build & Run

```bash
# Build
dotnet build src/copilotd/copilotd.csproj

# Run from source (passes all args through)
./copilotd.sh <command>          # macOS/Linux
copilotd.cmd <command>           # Windows
```

No test suite exists yet. Verify changes manually via `copilotd status`, `copilotd session list`, and `copilotd run --interval 15 --log-level debug`.

## Architecture

**Reconciliation-loop daemon.** The core is `ReconciliationEngine.Reconcile()` which runs every poll cycle with five steps:

1. Prune terminal sessions older than 7 days
2. Verify tracked processes are alive (PID + start time to detect PID reuse)
3. Query GitHub for issues matching rules via `gh` CLI
4. Reconcile desired vs. observed state (terminate unmatched, create/retry matched)
5. Dispatch pending sessions up to `MaxInstances` limit

State converges through this loop — every inconsistency (crashed process, stale session, failed API call) self-heals on the next cycle.

**Three truth sources** are reconciled each cycle: persisted state (`~/.copilotd/state.json`) → live OS process status → current GitHub issue matches.

**Session lifecycle states:** Pending → Dispatching → Running → Completed/Failed/Orphaned, plus Joined (user-controlled interactive takeover). `CompletedBySession` flag distinguishes explicit copilot completion from automatic (issue unmatched) completion.

## Key Conventions

### System.CommandLine v2.0.5 API

This version has non-obvious differences from older/newer versions:

- Use `Hidden = true` on Command (not `IsHidden`)
- `Option<T>` has no `IsRequired` property — it doesn't exist in this version
- `SetAction` takes `Func<ParseResult, CancellationToken, Task<int>>`
- `Argument<T>` constructor takes only a name string — set `Description` via property
- Check explicit option presence via `parseResult.GetResult(option) is not null`
- Commands use a static `Create(IServiceProvider services)` factory pattern

### JSON Serialization (AOT-safe)

All serialized types must be registered in `Models/JsonContext.cs` via `[JsonSerializable(typeof(T))]` on `CopilotdJsonContext`. Use the typed context accessor for serialization:

```csharp
JsonSerializer.Serialize(obj, CopilotdJsonContext.Default.TypeName);
JsonSerializer.Deserialize(json, CopilotdJsonContext.Default.TypeName);
```

Reflection-based serialization is disabled. Adding a new persisted model requires adding a `[JsonSerializable]` attribute to `CopilotdJsonContext`.

### Process Management (Platform-Specific)

- **Windows:** Copilot processes are launched via `CreateProcessW` P/Invoke with `CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP` flags. This is required — using `Process.Start` shares the daemon's ConPTY and causes console corruption.
- **Unix:** Standard `Process.Start` with `CreateNoWindow = true`.
- **Graceful shutdown on Windows:** Must happen in a separate process (`copilotd shutdown-instance --pid <PID>`) because `FreeConsole()` on the daemon disrupts its own ConPTY sessions. The helper attaches to the target's console and sends Ctrl+Break → Ctrl+C → Kill.
- **Graceful shutdown on Unix:** Direct SIGINT → SIGKILL via `libc` `kill()`.

### State Persistence

- Atomic writes via temp-file-then-rename pattern in `StateStore`
- State is saved immediately after each process launch to prevent ghost processes on crash
- Corrupt/missing files are treated as empty (self-healing) with a logged warning
- Single-instance guard via exclusive `FileStream` on `~/.copilotd/.lock`
- Prompt template loaded from `~/.copilotd/prompt.md` (falls back to `config.Prompt`)

### Worktree Isolation

Each dispatched session works in its own git worktree:
- Layout: `<repo_home>/org/repo_sessions/issue-N/` (sibling to main checkout)
- Created via `git -C <main-repo> worktree add <path> -b copilotd/issue-N origin/<default-branch>`
- Cleaned up via `git worktree remove` on completion, reset, or pruning
- `WorktreePath` is tracked on `DispatchSession` and used as working directory + `--add-dir` target
- `PrepareWorktree` fetches origin first to ensure latest default branch HEAD

### Console Output

All user-facing output goes through `ConsoleOutput` (wraps Spectre.Console). Always escape user data with `Markup.Escape()` before passing to `AnsiConsole`. Use the `RunWithErrorHandling` wrapper for command actions — it catches exceptions and renders them cleanly.

### Command Structure

Commands follow a consistent pattern:
- Static class with `public static Command Create(IServiceProvider services)`
- Services resolved from DI inside the `SetAction` handler
- Sub-commands (e.g., `rules list`, `session join`) are separate `Command` instances added to the parent's `Subcommands`
- Shared rendering logic is extracted as public static methods (e.g., `SessionCommand.RenderSessionList`)
