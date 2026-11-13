# Updates

The mod updates itself. Nothing to download by hand after the first install.

## What happens on each game start

1. MelonLoader loads **plugins before mods**. `Plugins/FrontierArchitect.Updater.dll` runs first.
2. It asks GitHub for the latest release of the repository (`updater.json → repository`).
3. It downloads the release's `manifest.json` (version + SHA-256 of every file).
4. If that version is newer than the installed `Mods/FrontierArchitect.dll`, it downloads each file, checks its
   SHA-256 against the manifest, and only then replaces the files. A mismatch changes nothing.
5. The mod is not loaded yet, so the new version runs **on this same launch**. A file the system keeps locked is
   staged under `UserData/FrontierArchitect/updates/staged/` and installed on the next launch.
6. The result is written to `updates/status.json` and shown in the HUD and in Settings.

The check has a short timeout; an offline game starts normally.

## Agent files and game map

The default agent files and `gamemap.json` ship inside the mod DLL. On start the mod copies the new defaults
into `UserData`, **except files you edited**: those are kept and the new default is written next to them as
`<file>.new-default` so you can compare. A ledger (`agents/.shipped.json`) tells the two cases apart. Your own
op tests live in `gamemap.local.json`, which updates never touch.

## Settings (`UserData/FrontierArchitect/updater.json`)

```json
{
  "auto_update": true,
  "channel": "stable",
  "repository": "gaetantaieb/Farthest-Frontier-Architect",
  "github_token": "",
  "timeout_seconds": 20,
  "check_interval_hours": 0
}
```

- `channel`: `stable` (latest release) or `prerelease` (newest release including pre-releases).
- `github_token`: only while the repository is private (fine-grained, Contents: Read-only, this repository only).
  The token is sent to `api.github.com` only, never to the download host.
- `check_interval_hours`: `0` checks on every launch.

## Publishing an update (maintainer)

1. Change the code or the agent files.
2. Bump the version and describe the change:

   ```bash
   tools/bump-version.sh 0.2.0
   ```

   then edit the new section at the top of `CHANGELOG.md`.
3. Merge to `main`.

The **Release** workflow (`.github/workflows/release.yml`) sees a version without a tag, builds, runs the tests,
packages (`tools/package.sh`) and publishes release `v0.2.0` with `FrontierArchitect-v0.2.0.zip`,
`FrontierArchitect.dll`, `FrontierArchitect.Updater.dll` and `manifest.json`. A version with a suffix
(`0.2.0-beta.1`) is published as a pre-release, which only `channel: prerelease` installs.
Pushing to `main` without a version bump builds nothing new; **Actions → Release → Run workflow** publishes
on demand.
