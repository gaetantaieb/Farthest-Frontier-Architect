# First install

About 10 minutes. You need Farthest Frontier on Steam (Windows) and an Anthropic API key.

## 1. Get an Anthropic API key

1. Go to <https://console.anthropic.com>, sign in, add credits under **Billing**.
2. **API keys → Create key**. Copy it (it starts with `sk-ant-`). You paste it into the game in step 7.

## 2. Get the mod files

Pick one:

- **From a release (normal way).** Open the repository's **Releases** page and download
  `FrontierArchitect-v<version>.zip`. Releases are published by the Release workflow when the version changes on
  `main` (see [UPDATES.md](UPDATES.md)). If no release exists yet: create the `main` branch on GitHub
  (**Branches → New branch → `main`**, from the branch that holds the code), then **Actions → Release → Run workflow**.
- **From a build of any branch.** **Actions → CI →** open the latest green run **→ Artifacts →
  `frontier-architect-build`**. Inside is the same `FrontierArchitect-v<version>.zip`.

## 3. Start the Mono build once

Mods only run in the Mono build, which ships with MelonLoader.

1. In Steam, press **Play** on Farthest Frontier and choose **Play Farthest Frontier (Mono)**.
2. Wait for the main menu (a MelonLoader console opens next to the game), then quit.

This creates the `Mods`, `Plugins` and `UserData` folders.

## 4. Find the game folder

Steam → right-click Farthest Frontier → **Manage → Browse local files** → open the folder
**`Farthest Frontier (Mono)`**. You should see `MelonLoader`, `Mods`, `Plugins` and `UserData` in it.

## 5. Extract the zip into that folder

Extract `FrontierArchitect-v<version>.zip` **into `Farthest Frontier (Mono)`** and accept merging folders. You get:

```
Farthest Frontier (Mono)/
  Mods/FrontierArchitect.dll                 the mod
  Plugins/FrontierArchitect.Updater.dll      keeps the mod up to date
  UserData/FrontierArchitect/README.txt
```

## 6. Let the updater reach GitHub (only while the repository is private)

GitHub answers anonymous requests for a **private** repository's releases with "not found", so the updater needs
a read-only token. Skip this step if the repository is public.

1. GitHub → **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**.
2. **Repository access: Only select repositories →** this repository. **Permissions → Repository → Contents: Read-only.**
3. Copy the token. You paste it in step 7 (Settings → Updates → GitHub token).

Making the repository public removes this step for you and for anyone you share the mod with (v2).

## 7. Launch, load a settlement, add your key

1. Launch **Play Farthest Frontier (Mono)**. The MelonLoader console shows `Frontier Architect Updater` and
   `Frontier Architect v<version> ready`.
2. Load or start a settlement. Four buttons appear at the top right: **Assistant · Architect · Execute · Settings**.
3. Click **Settings**, paste the **Anthropic API key** (and the GitHub token from step 6 if you made one), click **Save**.

## 8. First run

- **Assistant** → the button shows a spinner while Claude reads the village; the report opens when it is done.
- **Architect** → pick an ambition (1 Touch-up … 5 Rebuild) and Native or Instant, then **Generate scenario**.
  Ghosts appear on the map, coloured by phase (`F9` toggles the overlay, `F8` the buttons).
  Hover a building and press `L` to lock it before generating.
- **Execute** stays off until your game build is mapped: see [M0-MAPPING.md](M0-MAPPING.md). Until then, Apply
  buttons and phase actions show as **Manual** with the reason.

## Where things are

`Farthest Frontier (Mono)/UserData/FrontierArchitect/`:

| File | What |
| --- | --- |
| `config.json` | API key, model override, thresholds, guardrails, hotkeys |
| `updater.json` | auto-update on/off, channel, GitHub token |
| `agents/` | the Assistant and Architect agent files (editable in game: Architect → Agent...) |
| `gamemap.json`, `gamemap.local.json` | how the mod reaches the game's internals; your own test results |
| `plans/`, `reports/` | saved scenarios (survive a reload) and reports |
| `probe/` | output of Settings → Diagnostics → Run probe |
| `updates/status.json` | what the updater did on the last launch |

## Uninstall

Delete `Mods/FrontierArchitect.dll`, `Plugins/FrontierArchitect.Updater.dll` and, if you want, `UserData/FrontierArchitect/`.
