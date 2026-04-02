# Testing copilotd

## Prerequisites

Before running end-to-end tests, ensure you have the following installed and configured:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated via `gh auth login`
- [Copilot CLI (`copilot`)](https://docs.github.com/copilot/how-tos/copilot-cli) — authenticated via `copilot login`

## Building

```bash
dotnet build
```

## End-to-end testing

End-to-end tests verify the full dispatch lifecycle: issue detection, rule matching, and `copilot --remote` session launch.

### 1. Initialize a test configuration

```bash
# macOS/Linux
./copilotd.sh init

# Windows
copilotd.cmd init
```

Follow the interactive prompts to configure a test repository and a default dispatch rule (e.g., matching issues with the `copilotd` label assigned to your user).

### 2. Create a test issue

Open a new issue in the configured repository that matches your dispatch rule. For example, if the default rule matches the label `copilotd` and is assigned to your user:

```bash
gh issue create --repo <org/repo> --title "E2E test issue" --label copilotd --assignee @me
```

### 3. Start the daemon

```bash
# macOS/Linux
./copilotd.sh run --log-level debug

# Windows
copilotd.cmd run --log-level debug
```

The daemon will poll the configured repository, match the test issue against dispatch rules, and launch a `copilot --remote` session.

### 4. Verify

- Check the console output (with `--log-level debug`) to confirm the issue was detected and a session was dispatched.
- Inspect `~/.copilotd/state.json` to verify the dispatched session is tracked.
- Confirm the spawned `copilot` process is running (e.g., via `ps` or Task Manager).

### 5. Clean up

- Close or delete the test issue.
- Stop the daemon with `Ctrl+C`.
- Optionally remove the test session entry from `~/.copilotd/state.json`.
