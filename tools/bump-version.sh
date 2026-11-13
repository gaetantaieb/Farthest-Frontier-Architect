#!/usr/bin/env bash
# Sets the mod version (Directory.Build.props). Merge the change to main and the release
# workflow publishes it; installed copies update themselves on their next game start.
# Usage: tools/bump-version.sh 0.2.0
set -euo pipefail
cd "$(dirname "$0")/.."
NEW="${1:?usage: tools/bump-version.sh <major.minor.patch[-pre]>}"
[[ "$NEW" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "not a version: $NEW"; exit 1; }
sed -i.bak -E "s#<Version>[0-9][^<]*</Version>#<Version>$NEW</Version>#" Directory.Build.props && rm -f Directory.Build.props.bak
grep -q "^## \[$NEW\]" CHANGELOG.md || sed -i.bak "0,/^## /s//## [$NEW] - $(date +%Y-%m-%d)\n\n- Describe the change here.\n\n## /" CHANGELOG.md && rm -f CHANGELOG.md.bak
echo "Version set to $NEW. Edit CHANGELOG.md, commit, and merge to main."
