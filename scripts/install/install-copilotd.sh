#!/usr/bin/env bash
# install-copilotd.sh — Linux & macOS installer for copilotd
# Usage:
#   gh release download install-scripts -R DamianEdwards/copilotd -p install.sh -O - | bash -s -- [options]
#
# Options:
#   --quality <Dev|PreRelease|Stable>   Release quality (default: Stable)
#   --force                             Skip confirmation prompts
#   --target-path <path>                Install directory (default: ~/.copilotd/bin)
#   --no-update-path                    Do not update PATH in shell profile
#   --repository <owner/repo>           Repository to download from (default: DamianEdwards/copilotd)
#   --skip-provenance                   Skip artifact attestation verification
#   --verbose                           Enable verbose output

set -euo pipefail

# ─── Defaults ────────────────────────────────────────────────────────────────

QUALITY="Stable"
FORCE=false
TARGET_PATH="${HOME}/.copilotd/bin"
UPDATE_PATH=true
REPOSITORY="DamianEdwards/copilotd"
SKIP_PROVENANCE=false
VERBOSE=false

# ─── Argument parsing ────────────────────────────────────────────────────────

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quality)
            QUALITY="$2"
            shift 2
            ;;
        --force)
            FORCE=true
            shift
            ;;
        --target-path)
            TARGET_PATH="$2"
            shift 2
            ;;
        --no-update-path)
            UPDATE_PATH=false
            shift
            ;;
        --repository)
            REPOSITORY="$2"
            shift 2
            ;;
        --skip-provenance)
            SKIP_PROVENANCE=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        -h|--help)
            cat <<'HELPTEXT'
install-copilotd.sh — Linux & macOS installer for copilotd
Usage:
  gh release download install-scripts -R DamianEdwards/copilotd -p install.sh -O - | bash -s -- [options]

Options:
  --quality <Dev|PreRelease|Stable>   Release quality (default: Stable)
  --force                             Skip confirmation prompts
  --target-path <path>                Install directory (default: ~/.copilotd/bin)
  --no-update-path                    Do not update PATH in shell profile
  --repository <owner/repo>           Repository to download from (default: DamianEdwards/copilotd)
  --skip-provenance                   Skip artifact attestation verification
  --verbose                           Enable verbose output
HELPTEXT
            exit 0
            ;;
        *)
            echo "Error: Unknown option '$1'." >&2
            exit 1
            ;;
    esac
done

case "$QUALITY" in
    Dev|PreRelease|Stable) ;;
    *) echo "Error: --quality must be Dev, PreRelease, or Stable." >&2; exit 1 ;;
esac

# ─── Helpers ─────────────────────────────────────────────────────────────────

log_verbose() {
    if [[ "$VERBOSE" == true ]]; then
        echo "[verbose] $*" >&2
    fi
}

die() {
    echo "Error: $*" >&2
    exit 1
}

status_step() {
    local message="$1"
    shift
    printf '%s... ' "$message"
    "$@"
    echo 'done'
}

# ─── Prerequisites ───────────────────────────────────────────────────────────

assert_gh_cli_available() {
    if ! command -v gh &>/dev/null; then
        echo ''
        echo 'Error: GitHub CLI (gh) is required but was not found on PATH.' >&2
        echo ''
        echo 'Install it from: https://cli.github.com/' >&2
        echo 'Then authenticate:  gh auth login' >&2
        echo ''
        exit 1
    fi

    if ! gh auth status &>/dev/null; then
        echo ''
        echo 'Error: GitHub CLI (gh) is not authenticated.' >&2
        echo ''
        echo 'Run the following to authenticate:  gh auth login' >&2
        echo ''
        exit 1
    fi

    if ! command -v jq &>/dev/null; then
        echo ''
        echo 'Error: jq is required but was not found on PATH.' >&2
        echo ''
        echo 'Install it from: https://jqlang.github.io/jq/download/' >&2
        echo '  macOS:  brew install jq' >&2
        echo '  Ubuntu: sudo apt install jq' >&2
        echo ''
        exit 1
    fi
}

# ─── GitHub API helpers ──────────────────────────────────────────────────────

invoke_github_api() {
    local uri="$1"
    local output
    local exit_code=0

    output=$(gh api "$uri" 2>&1) || exit_code=$?

    if [[ $exit_code -ne 0 ]]; then
        die "GitHub API request failed for '$uri': $output"
    fi

    if [[ -z "${output:-}" ]]; then
        echo ""
        return
    fi

    echo "$output"
}

invoke_github_asset_download() {
    local repo="$1"
    local tag="$2"
    local asset_name="$3"
    local dest_dir="$4"
    local output
    local exit_code=0

    output=$(gh release download "$tag" -R "$repo" -p "$asset_name" -D "$dest_dir" --clobber 2>&1) || exit_code=$?

    if [[ $exit_code -ne 0 ]]; then
        die "Failed to download asset '$asset_name' from release '$tag' in repository '$repo': $output"
    fi
}

# ─── Release resolution ─────────────────────────────────────────────────────

get_release_by_tag() {
    local repo="$1"
    local tag="$2"
    local uri="https://api.github.com/repos/${repo}/releases/tags/${tag}"
    local output
    local exit_code=0

    output=$(gh api "$uri" 2>&1) || exit_code=$?

    if [[ $exit_code -ne 0 ]]; then
        if echo "$output" | grep -q '404'; then
            echo ""
            return
        fi
        die "GitHub API request failed for '$uri': $output"
    fi

    echo "$output"
}

get_release_asset_url() {
    local release_json="$1"
    local asset_name="$2"

    echo "$release_json" | jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .browser_download_url // empty' | head -n 1
}

has_release_asset() {
    local release_json="$1"
    local asset_name="$2"

    local url
    url=$(get_release_asset_url "$release_json" "$asset_name")
    [[ -n "$url" ]]
}

get_release_tag() {
    local release_json="$1"
    echo "$release_json" | jq -r '.tag_name // empty'
}

get_release_name() {
    local release_json="$1"
    echo "$release_json" | jq -r '.name // empty'
}

get_release_for_quality() {
    local repo="$1"
    local selected_quality="$2"
    local asset_name="$3"

    if [[ "$selected_quality" == "Dev" ]]; then
        local dev_release
        dev_release=$(get_release_by_tag "$repo" "dev")

        if [[ -z "$dev_release" ]]; then
            local releases
            releases=$(invoke_github_api "https://api.github.com/repos/${repo}/releases?per_page=100")
            dev_release=$(echo "$releases" | jq -c '[.[] | select(.name == "Development Build")] | first // empty')
        fi

        if [[ -z "$dev_release" || "$dev_release" == "null" ]]; then
            die "Could not locate the standing Development Build release (tag 'dev' or title 'Development Build')."
        fi

        if ! has_release_asset "$dev_release" "$asset_name"; then
            die "Development Build release does not contain asset '$asset_name'."
        fi

        echo "$dev_release"
        return
    fi

    local all_releases
    all_releases=$(invoke_github_api "https://api.github.com/repos/${repo}/releases?per_page=100")

    local candidate_release=""
    local release_count
    release_count=$(echo "$all_releases" | jq 'length')

    for (( i = 0; i < release_count; i++ )); do
        local release
        release=$(echo "$all_releases" | jq -c ".[$i]")

        local is_draft
        is_draft=$(echo "$release" | jq -r '.draft')
        if [[ "$is_draft" == "true" ]]; then
            continue
        fi

        local tag_name
        tag_name=$(echo "$release" | jq -r '.tag_name')
        if [[ "$tag_name" == "dev" || "$tag_name" == "install-scripts" ]]; then
            continue
        fi

        if ! has_release_asset "$release" "$asset_name"; then
            continue
        fi

        local is_prerelease
        is_prerelease=$(echo "$release" | jq -r '.prerelease')

        if [[ "$selected_quality" == "Stable" && "$is_prerelease" == "true" ]]; then
            if [[ -z "$candidate_release" ]]; then
                candidate_release="$release"
            fi
            continue
        fi

        echo "$release"
        return
    done

    if [[ "$selected_quality" == "Stable" && -n "$candidate_release" ]]; then
        echo "Warning: No stable release containing '$asset_name' was found. Falling back to latest prerelease." >&2
        echo "$candidate_release"
        return
    fi

    die "No '$selected_quality' release containing '$asset_name' was found in '$repo'."
}

# ─── Checksum verification ──────────────────────────────────────────────────

get_expected_sha256() {
    local checksums_path="$1"
    local asset_name="$2"

    local line
    line=$(grep -E "\s\*?${asset_name}$" "$checksums_path" | head -n 1)

    if [[ -z "$line" ]]; then
        die "checksums.txt did not contain an entry for '$asset_name'."
    fi

    local hash
    hash=$(echo "$line" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')

    if [[ ! "$hash" =~ ^[0-9a-f]{64}$ ]]; then
        die "Invalid checksum line format for '$asset_name' in checksums.txt."
    fi

    echo "$hash"
}

assert_archive_integrity() {
    local archive_path="$1"
    local asset_name="$2"
    local checksums_path="$3"
    local release_metadata_path="${4:-}"

    log_verbose "Validating archive SHA256 for '$asset_name' using '$checksums_path'."
    local expected_sha
    expected_sha=$(get_expected_sha256 "$checksums_path" "$asset_name")

    local actual_sha
    actual_sha=$(shasum -a 256 "$archive_path" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')

    log_verbose "checksums.txt expected SHA256 for '$asset_name': '$expected_sha'."
    log_verbose "Actual SHA256 for '$archive_path': '$actual_sha'."

    if [[ "$expected_sha" != "$actual_sha" ]]; then
        die "SHA256 mismatch for '$asset_name'. Expected '$expected_sha' but got '$actual_sha'."
    fi

    if [[ -n "$release_metadata_path" && -f "$release_metadata_path" ]]; then
        log_verbose "Validating release metadata for '$asset_name' using '$release_metadata_path'."
        local metadata_sha
        metadata_sha=$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .sha256 // empty' "$release_metadata_path" | head -n 1 | tr '[:upper:]' '[:lower:]')

        if [[ -z "$metadata_sha" ]]; then
            die "release-metadata.json did not contain an entry for '$asset_name'."
        fi

        log_verbose "release-metadata.json SHA256 for '$asset_name': '$metadata_sha'."
        if [[ "$metadata_sha" != "$expected_sha" ]]; then
            die "release-metadata.json SHA256 for '$asset_name' did not match checksums.txt."
        fi
    fi

    echo "$actual_sha"
}

# ─── Attestation verification ───────────────────────────────────────────────

assert_artifact_attestation() {
    local file_path="$1"
    local repo="$2"
    local description="${3:-artifact}"

    log_verbose "Verifying $description attestation for '$file_path' from '$repo'."

    local output
    local exit_code=0
    output=$(gh attestation verify "$file_path" \
        -R "$repo" \
        --signer-repo "$repo" \
        --signer-workflow "${repo}/.github/workflows/ci.yml" \
        --source-ref "refs/heads/main" \
        2>&1) || exit_code=$?

    if [[ $exit_code -ne 0 ]]; then
        die "Artifact attestation verification failed for $description '$file_path': $output"
    fi

    log_verbose "Artifact attestation verification succeeded for $description '$file_path'."
}

# ─── Platform detection ─────────────────────────────────────────────────────

get_platform() {
    local os
    os=$(uname -s)
    case "$os" in
        Linux)  echo "linux" ;;
        Darwin) echo "osx" ;;
        *)      die "Unsupported operating system '$os'. Only Linux and macOS are supported." ;;
    esac
}

get_architecture() {
    local arch
    arch=$(uname -m)
    case "$arch" in
        x86_64|amd64)  echo "x64" ;;
        arm64|aarch64) echo "arm64" ;;
        *)             die "Unsupported architecture '$arch'. Only x64 and arm64 are supported." ;;
    esac
}

# ─── Version helpers ─────────────────────────────────────────────────────────

get_copilotd_version_string() {
    local binary_path="$1"
    local output
    local exit_code=0

    output=$("$binary_path" --version 2>/dev/null) || exit_code=$?

    if [[ $exit_code -eq 0 && -n "$output" ]]; then
        local version
        version=$(echo "$output" | head -n 1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.\-]+)?' | head -n 1)
        if [[ -n "$version" ]]; then
            echo "$version"
            return
        fi
    fi

    die "Could not determine version for '$binary_path'."
}

# Compares two semantic versions. Returns via stdout:
#   "1"  if left > right
#   "0"  if left == right
#   "-1" if left < right
compare_semantic_version() {
    local left="$1"
    local right="$2"

    # Parse major.minor.patch (optional .build) (-prerelease)
    local left_core left_pre right_core right_pre

    left_core="${left%%-*}"
    right_core="${right%%-*}"

    if [[ "$left" == *-* ]]; then
        left_pre="${left#*-}"
    else
        left_pre=""
    fi

    if [[ "$right" == *-* ]]; then
        right_pre="${right#*-}"
    else
        right_pre=""
    fi

    # Compare major.minor.patch numerically
    IFS='.' read -r -a left_parts <<< "$left_core"
    IFS='.' read -r -a right_parts <<< "$right_core"

    for i in 0 1 2; do
        local l=$((10#${left_parts[$i]:-0}))
        local r=$((10#${right_parts[$i]:-0}))
        if (( l > r )); then echo "1"; return; fi
        if (( l < r )); then echo "-1"; return; fi
    done

    # Both have no prerelease — equal
    if [[ -z "$left_pre" && -z "$right_pre" ]]; then
        echo "0"
        return
    fi

    # No prerelease wins over prerelease
    if [[ -z "$left_pre" ]]; then echo "1"; return; fi
    if [[ -z "$right_pre" ]]; then echo "-1"; return; fi

    # Segment-by-segment prerelease comparison (per SemVer spec)
    IFS='.' read -r -a left_ids <<< "$left_pre"
    IFS='.' read -r -a right_ids <<< "$right_pre"
    local max_len=${#left_ids[@]}
    if (( ${#right_ids[@]} > max_len )); then
        max_len=${#right_ids[@]}
    fi

    for (( i = 0; i < max_len; i++ )); do
        # Fewer identifiers = lower precedence
        if (( i >= ${#left_ids[@]} )); then echo "-1"; return; fi
        if (( i >= ${#right_ids[@]} )); then echo "1"; return; fi

        local l_id="${left_ids[$i]}"
        local r_id="${right_ids[$i]}"
        local l_is_num=false r_is_num=false

        [[ "$l_id" =~ ^[0-9]+$ ]] && l_is_num=true
        [[ "$r_id" =~ ^[0-9]+$ ]] && r_is_num=true

        if [[ "$l_is_num" == true && "$r_is_num" == true ]]; then
            local l_num=$((10#$l_id))
            local r_num=$((10#$r_id))
            if (( l_num > r_num )); then echo "1"; return; fi
            if (( l_num < r_num )); then echo "-1"; return; fi
            continue
        fi

        # Numeric identifiers always have lower precedence than alphanumeric
        if [[ "$l_is_num" == true && "$r_is_num" == false ]]; then echo "-1"; return; fi
        if [[ "$l_is_num" == false && "$r_is_num" == true ]]; then echo "1"; return; fi

        # Lexicographic comparison for alphanumeric identifiers
        if [[ "$l_id" > "$r_id" ]]; then echo "1"; return; fi
        if [[ "$l_id" < "$r_id" ]]; then echo "-1"; return; fi
    done

    echo "0"
}

# ─── Release label ───────────────────────────────────────────────────────────

get_release_status_label() {
    local selected_quality="$1"
    local release_json="$2"

    local is_prerelease
    is_prerelease=$(echo "$release_json" | jq -r '.prerelease')

    local release_label
    case "$selected_quality" in
        Stable)
            if [[ "$is_prerelease" == "true" ]]; then
                release_label="latest prerelease (no stable release available)"
            else
                release_label="latest stable release"
            fi
            ;;
        PreRelease) release_label="latest prerelease" ;;
        Dev)        release_label="latest development build" ;;
        *)          release_label="release" ;;
    esac

    local tag_name
    tag_name=$(get_release_tag "$release_json")
    local name
    name=$(get_release_name "$release_json")

    local release_version="${tag_name:-${name:-unknown version}}"
    echo "${release_label} (${release_version})"
}

# ─── PATH management ────────────────────────────────────────────────────────

get_shell_profile() {
    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    case "$shell_name" in
        zsh)  echo "${HOME}/.zshrc" ;;
        bash)
            if [[ -f "${HOME}/.bash_profile" ]]; then
                echo "${HOME}/.bash_profile"
            else
                echo "${HOME}/.bashrc"
            fi
            ;;
        fish) echo "${HOME}/.config/fish/config.fish" ;;
        *)    echo "${HOME}/.profile" ;;
    esac
}

ensure_path_contains() {
    local entry="$1"

    # Check current session PATH
    if echo ":${PATH}:" | grep -q ":${entry}:"; then
        log_verbose "'$entry' is already in the current session PATH."
        return 1
    fi

    # Update current session
    export PATH="${entry}:${PATH}"

    # Update shell profile
    local profile
    profile=$(get_shell_profile)

    if [[ -f "$profile" ]] && grep -q "$entry" "$profile" 2>/dev/null; then
        log_verbose "'$entry' is already in '$profile'."
        echo "Added '$entry' to current session PATH."
        return 0
    fi

    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    local path_line
    if [[ "$shell_name" == "fish" ]]; then
        path_line="fish_add_path ${entry}"
    else
        path_line="export PATH=\"${entry}:\$PATH\""
    fi

    {
        echo ""
        echo "# Added by copilotd installer"
        echo "$path_line"
    } >> "$profile"

    echo "Added '$entry' to current session PATH."
    echo "Added '$entry' to '$profile' (will take effect in new terminal sessions)."
    return 0
}

# ─── Extract ─────────────────────────────────────────────────────────────────

expand_release_archive() {
    local archive_path="$1"
    local dest_path="$2"

    log_verbose "Expanding '$archive_path' to '$dest_path'."
    mkdir -p "$dest_path"
    tar -xzf "$archive_path" -C "$dest_path"

    local binary_path="${dest_path}/copilotd"
    if [[ ! -f "$binary_path" ]]; then
        die "Downloaded archive '$(basename "$archive_path")' did not contain 'copilotd'."
    fi

    chmod +x "$binary_path"
    log_verbose "Found extracted binary '$binary_path'."
    echo "$binary_path"
}

# ─── Main install ────────────────────────────────────────────────────────────

install_copilotd() {
    assert_gh_cli_available

    local platform
    platform=$(get_platform)
    local architecture
    architecture=$(get_architecture)
    local rid="${platform}-${architecture}"
    local asset_name="copilotd-${rid}.tar.gz"

    log_verbose "Selecting release asset '$asset_name' for quality '$QUALITY' from '$REPOSITORY'."

    local release
    release=$(get_release_for_quality "$REPOSITORY" "$QUALITY" "$asset_name")
    log_verbose "Selected release '$(get_release_name "$release")' ($(get_release_tag "$release"))."
    local release_status_label
    release_status_label=$(get_release_status_label "$QUALITY" "$release")

    local release_tag
    release_tag=$(get_release_tag "$release")

    if [[ "$QUALITY" == "Dev" && "$FORCE" != true ]]; then
        if [[ -e /dev/tty ]]; then
            printf "Dev quality disables checksum/provenance verification. Type YES to continue: "
            local confirmation
            read -r confirmation < /dev/tty
            if [[ "$confirmation" != "YES" ]]; then
                die "Installation canceled by user."
            fi
        else
            die "Dev quality requires --force when running in non-interactive mode (no terminal available)."
        fi
    fi

    local temp_root
    temp_root=$(mktemp -d "${TMPDIR:-/tmp}/copilotd-install-XXXXXX")
    local download_path="${temp_root}/${asset_name}"
    local extract_path="${temp_root}/extract"

    log_verbose "Using temporary workspace '$temp_root'."

    cleanup() {
        if [[ -d "$temp_root" ]]; then
            log_verbose "Cleaning up temporary workspace '$temp_root'."
            rm -rf "$temp_root"
        fi
    }
    trap cleanup EXIT

    status_step "Downloading ${release_status_label}" \
        invoke_github_asset_download "$REPOSITORY" "$release_tag" "$asset_name" "$temp_root"

    if [[ "$QUALITY" != "Dev" ]]; then
        # Download and verify checksums
        if ! has_release_asset "$release" "checksums.txt"; then
            die "Release '$release_tag' did not include checksums.txt."
        fi

        local checksums_path="${temp_root}/checksums.txt"
        log_verbose "Downloading checksums from release '$release_tag' to '$checksums_path'."
        invoke_github_asset_download "$REPOSITORY" "$release_tag" "checksums.txt" "$temp_root"

        local release_metadata_path=""
        if has_release_asset "$release" "release-metadata.json"; then
            release_metadata_path="${temp_root}/release-metadata.json"
            log_verbose "Downloading release metadata from release '$release_tag' to '$release_metadata_path'."
            invoke_github_asset_download "$REPOSITORY" "$release_tag" "release-metadata.json" "$temp_root"
        fi

        status_step "Verifying asset checksums" \
            assert_archive_integrity "$download_path" "$asset_name" "$checksums_path" "$release_metadata_path"

        # Verify artifact attestation
        if [[ "$SKIP_PROVENANCE" == true ]]; then
            echo "Skipping provenance verification (--skip-provenance)."
        else
            status_step "Verifying archive provenance" \
                assert_artifact_attestation "$download_path" "$REPOSITORY" "archive"
        fi
    else
        echo "Skipping checksum and provenance verification for development build."
    fi

    local downloaded_binary_path
    downloaded_binary_path=$(expand_release_archive "$download_path" "$extract_path")

    if [[ "$QUALITY" != "Dev" && "$SKIP_PROVENANCE" != true ]]; then
        status_step "Verifying executable provenance" \
            assert_artifact_attestation "$downloaded_binary_path" "$REPOSITORY" "executable"
    fi

    local install_directory
    install_directory=$(cd "$TARGET_PATH" 2>/dev/null && pwd || echo "$TARGET_PATH")
    # Resolve to absolute path
    install_directory=$(mkdir -p "$TARGET_PATH" && cd "$TARGET_PATH" && pwd)
    log_verbose "Installing to '$install_directory'."

    local destination_path="${install_directory}/copilotd"
    local downloaded_version
    downloaded_version=$(get_copilotd_version_string "$downloaded_binary_path")
    local installed_version="$downloaded_version"
    log_verbose "Downloaded copilotd version: '$downloaded_version'."

    local should_install=true
    if [[ -f "$destination_path" ]]; then
        local existing_version
        existing_version=$(get_copilotd_version_string "$destination_path") || true
        if [[ -n "${existing_version:-}" ]]; then
            local comparison
            comparison=$(compare_semantic_version "$downloaded_version" "$existing_version")
            log_verbose "Existing copilotd version at '$destination_path': '$existing_version'. Comparison result: $comparison."
            if [[ "$comparison" != "1" ]]; then
                should_install=false
                installed_version="$existing_version"
                echo "Existing copilotd version '$existing_version' is newer than or equal to downloaded version '$downloaded_version'; skipping overwrite."
            fi
        fi
    fi

    if [[ "$should_install" == true ]]; then
        status_step "Installing ${downloaded_version} to '${install_directory}'" \
            cp "$downloaded_binary_path" "$destination_path"
        chmod +x "$destination_path"
    fi

    if [[ "$UPDATE_PATH" == true ]]; then
        ensure_path_contains "$install_directory" || true
    else
        echo "Skipped PATH updates because --no-update-path was specified."
    fi

    echo "copilotd ${installed_version} is ready to use from '${install_directory}'."
}

install_copilotd
