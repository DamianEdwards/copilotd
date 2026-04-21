#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/copilotd"
APP_HOST="$SCRIPT_DIR/artifacts/bin/copilotd/debug/copilotd"

if [[ -z "${COPILOTD_HOME:-}" ]]; then
  export COPILOTD_HOME="$SCRIPT_DIR/.copilotd-home"
fi

if [[ "${1-}" == "run" ]]; then
  if [[ ! -f "$APP_HOST" ]]; then
    echo "Built app host not found at '$APP_HOST'. Run ./build.sh first." >&2
    exit 1
  fi

  exec "$APP_HOST" "$@"
fi

exec dotnet run --project "$PROJECT_DIR" --no-build -- "$@"
