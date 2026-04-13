#!/usr/bin/env bash
# Updates or creates the 'dev' draft release with the given assets and version state.
# Uses --clobber on upload to atomically replace existing assets.
#
# Required environment variables:
#   GH_TOKEN          - GitHub token for gh CLI
#   GITHUB_REPOSITORY - owner/repo
#   GITHUB_SHA        - commit SHA
#   GITHUB_RUN_NUMBER - workflow run number
#   GITHUB_RUN_ID     - workflow run ID
#   GITHUB_SERVER_URL - e.g. https://github.com
#
# Usage: update-dev-release.sh <version> <rc_version> <next_state> <bundle_dir> <description>

set -euo pipefail

VERSION="$1"
RC_VERSION="$2"
NEXT_STATE="$3"
BUNDLE_DIR="$4"
DESCRIPTION="${5:-"This draft release contains the latest native development assets and carries the workflow-managed version state."}"

NOTES=$(cat <<EOF
## Development Build

**Dev Version:** ${VERSION}
**RC Version:** ${RC_VERSION}
**Commit:** ${GITHUB_SHA}
**Build:** [#${GITHUB_RUN_NUMBER}](${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID})

${DESCRIPTION}

<!-- VERSION_STATE: ${NEXT_STATE} -->
<!-- RC_VERSION: ${RC_VERSION} -->
<!-- CI_RUN_ID: ${GITHUB_RUN_ID} -->
EOF
)

if gh release view dev --repo "${GITHUB_REPOSITORY}" --json isDraft &>/dev/null; then
  gh release edit dev \
    --repo "${GITHUB_REPOSITORY}" \
    --draft \
    --prerelease \
    --title "Development Build" \
    --notes "$NOTES"

  gh release upload dev \
    --repo "${GITHUB_REPOSITORY}" \
    "${BUNDLE_DIR}"/* \
    --clobber
else
  gh release create dev \
    --repo "${GITHUB_REPOSITORY}" \
    --target "${GITHUB_SHA}" \
    --draft \
    --prerelease \
    --title "Development Build" \
    --notes "$NOTES" \
    "${BUNDLE_DIR}"/*
fi
