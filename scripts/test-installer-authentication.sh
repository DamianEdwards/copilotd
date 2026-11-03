#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# Exported mocks are inherited by the installer subprocess, never calling real gh.
gh() {
    if [[ "$1" == auth ]]; then
        if [[ "$*" != 'auth status --active --hostname github.com' ]]; then
            echo "Stale credential from $SCENARIO was checked." >&2
            return 1
        fi

        case "$SCENARIO" in
            InactiveAccount|OtherHost)
                echo 'Active github.com account is valid.'
                return 0
                ;;
            InvalidActive)
                echo 'github.com active account'
                echo 'The active token is invalid (HTTP 401).' >&2
                ;;
            NetworkFailure)
                echo 'github.com connection attempt'
                echo 'dial tcp: network is unreachable' >&2
                ;;
            UnsupportedCli)
                echo 'unknown flag: --active' >&2
                ;;
        esac
        return 1
    fi

    if [[ "$1" == api ]]; then
        echo 'AUTH_PREFLIGHT_PASSED_API_BOUNDARY' >&2
        return 1
    fi

    echo "Unexpected gh invocation: $*" >&2
    return 1
}

jq() {
    echo 'Unexpected jq invocation past the mocked API boundary.' >&2
    return 1
}

uname() {
    case "$1" in
        -s) echo Linux ;;
        -m) echo x86_64 ;;
        *) return 1 ;;
    esac
}

export -f gh jq uname

for SCENARIO in InactiveAccount OtherHost InvalidActive NetworkFailure UnsupportedCli; do
    export SCENARIO
    exit_code=0
    output=$(bash "${script_dir}/install/install-copilotd.sh" --force --no-update-path 2>&1) || exit_code=$?

    case "$SCENARIO" in
        InactiveAccount|OtherHost) expected='AUTH_PREFLIGHT_PASSED_API_BOUNDARY' ;;
        InvalidActive) expected='The active token is invalid (HTTP 401).' ;;
        NetworkFailure) expected='dial tcp: network is unreachable' ;;
        UnsupportedCli) expected='unknown flag: --active' ;;
    esac

    if [[ $exit_code -eq 0 || "$output" != *"$expected"* ]]; then
        printf "Scenario '%s' failed (exit %s):\n%s\n" "$SCENARIO" "$exit_code" "$output" >&2
        exit 1
    fi

    if [[ "$SCENARIO" == InactiveAccount || "$SCENARIO" == OtherHost ]]; then
        if [[ "$output" == *'authentication check failed'* || "$output" == *'Active github.com account is valid.'* ]]; then
            printf "Scenario '%s' did not silently pass preflight:\n%s\n" "$SCENARIO" "$output" >&2
            exit 1
        fi
    else
        if [[ "$output" == *'AUTH_PREFLIGHT_PASSED_API_BOUNDARY'* ||
              "$output" == *'is not authenticated'* ||
              "$output" != *'authentication check failed for the active account on github.com'* ||
              "$output" != *'gh auth login --hostname github.com'* ||
              ( "$SCENARIO" == InvalidActive && "$output" != *'github.com active account'* ) ||
              ( "$SCENARIO" == NetworkFailure && "$output" != *'github.com connection attempt'* ) ]]; then
            printf "Scenario '%s' lost diagnostics or did not stop at preflight:\n%s\n" "$SCENARIO" "$output" >&2
            exit 1
        fi
    fi

    printf "Scenario '%s' passed.\n" "$SCENARIO"
done
