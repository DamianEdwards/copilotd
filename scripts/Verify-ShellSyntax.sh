#!/usr/bin/env bash
# Verify-ShellSyntax.sh — Validates syntax of all .sh scripts in the repository.
# Analogous to Verify-PowerShellSyntax.ps1 for PowerShell scripts.

set -euo pipefail

ROOT_PATH="${1:-$(cd "$(dirname "$0")/.." && pwd)}"

if [[ ! -d "$ROOT_PATH" ]]; then
    echo "Error: Root path '$ROOT_PATH' is not a directory." >&2
    exit 1
fi

mapfile -t script_files < <(find "$ROOT_PATH" -name '*.sh' -type f -not -path '*/.git/*' | sort)

if [[ ${#script_files[@]} -eq 0 ]]; then
    echo "Error: No shell scripts were found under '$ROOT_PATH'." >&2
    exit 1
fi

error_count=0

for script_file in "${script_files[@]}"; do
    output=$(bash -n "$script_file" 2>&1) || {
        echo "$script_file: $output" >&2
        ((error_count++))
    }
done

if [[ $error_count -gt 0 ]]; then
    echo "Shell syntax validation failed with $error_count error(s)." >&2
    exit 1
fi

echo "Shell syntax validation passed for ${#script_files[@]} script(s)."
