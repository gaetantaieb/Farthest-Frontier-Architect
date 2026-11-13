#!/usr/bin/env bash
# Builds the release payload in dist/:
#   FrontierArchitect-v<version>.zip   first-install archive (extract into "Farthest Frontier (Mono)")
#   FrontierArchitect.dll              mod        -> Mods/
#   FrontierArchitect.Updater.dll      updater    -> Plugins/
#   manifest.json                      what the in-game updater downloads and verifies (SHA-256)
#   notes.md                           release notes (from CHANGELOG.md)
# Usage: tools/package.sh [--skip-build]
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(grep -oPm1 '(?<=<Version>)[0-9][^<]*(?=</Version>)' Directory.Build.props)
TAG="v$VERSION"
if [[ "${1:-}" != "--skip-build" ]]; then
  dotnet build src/FrontierArchitect/FrontierArchitect.csproj -c Release --nologo
  dotnet build src/FrontierArchitect.Updater/FrontierArchitect.Updater.csproj -c Release --nologo
fi

MOD=src/FrontierArchitect/bin/Release/net472/FrontierArchitect.dll
UPD=src/FrontierArchitect.Updater/bin/Release/net472/FrontierArchitect.Updater.dll
rm -rf dist && mkdir -p dist/stage/Mods dist/stage/Plugins dist/stage/UserData/FrontierArchitect
cp "$MOD" dist/stage/Mods/
cp "$UPD" dist/stage/Plugins/
cat > dist/stage/UserData/FrontierArchitect/README.txt <<TXT
Frontier Architect $TAG
This folder holds your settings (config.json: put your Anthropic API key here or in the in-game
settings), the agent files (agents/), plans, reports and the updater settings (updater.json).
It is created and filled on the first game start. Nothing here is ever overwritten by an update
if you edited it.
TXT
(cd dist/stage && zip -qr "../FrontierArchitect-$TAG.zip" .)
cp "$MOD" "$UPD" dist/

sha() { sha256sum "$1" | cut -d' ' -f1; }
size() { stat -c%s "$1"; }
NOTES=$(awk -v v="$VERSION" '
  $0 ~ "^## \\[?"v"\\]?" {on=1; next}
  on && /^## / {exit}
  on {print}' CHANGELOG.md 2>/dev/null || true)
[[ -z "${NOTES//[[:space:]]/}" ]] && NOTES="Frontier Architect $TAG"
printf '%s\n' "$NOTES" > dist/notes.md

python3 - "$VERSION" "$TAG" "$(sha dist/FrontierArchitect.dll)" "$(size dist/FrontierArchitect.dll)" \
  "$(sha dist/FrontierArchitect.Updater.dll)" "$(size dist/FrontierArchitect.Updater.dll)" <<'PY'
import json, sys
version, tag, msha, msize, usha, usize = sys.argv[1:]
notes = open("dist/notes.md", encoding="utf-8").read().strip()
manifest = {
    "name": "FrontierArchitect",
    "version": version,
    "tag": tag,
    "min_melonloader": "0.6.0",
    "notes": notes,
    "files": [
        {"path": "Mods/FrontierArchitect.dll", "asset": "FrontierArchitect.dll", "sha256": msha, "size": int(msize)},
        {"path": "Plugins/FrontierArchitect.Updater.dll", "asset": "FrontierArchitect.Updater.dll", "sha256": usha, "size": int(usize)},
    ],
}
json.dump(manifest, open("dist/manifest.json", "w"), indent=2)
PY
rm -rf dist/stage
echo "Packaged $TAG:"; ls -la dist
