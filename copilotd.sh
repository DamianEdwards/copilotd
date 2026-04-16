#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/copilotd"

if [[ "${1-}" == "run" ]]; then
  dotnet build "$PROJECT_DIR/copilotd.csproj" -nologo
  exec "$SCRIPT_DIR/artifacts/bin/copilotd/debug/copilotd" "$@"
fi

exec dotnet run --project "$PROJECT_DIR" -- "$@"
