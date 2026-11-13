# Changelog

Every version listed here is published by the release workflow (`.github/workflows/release.yml`) when it reaches `main`.
The in-game updater shows the section of the version it installs.

## [0.1.1] - 2026-11-13

- Buttons are always drawn (top right, below the game's own bar); a status line shows until a settlement is detected.
- The settlement is detected from the game's GameManager every few seconds, not only from the scene name at load.
- UI errors are logged once to the MelonLoader console instead of silently hiding the panels; scene names are logged.

## [0.1.0] - 2026-11-13

First release, built from the Frontier Architect product and technical specification.

- Assistant: ranked village report (critical / important / optimisation) with evidence; one-click Apply
  for settings ops with undo while the panel is open; spatial findings go to the Architect.
- Architect: one redesign scenario at ambition 1-5, built by Claude through mod tools (get_region,
  find_sites, check_placement, apply_to_draft, route_network, score_draft, forecast_stock, query, ...);
  ghost overlay coloured by phase; before/after scores O1-O6; lock buildings, reject actions, regenerate.
- Execute: phase-by-phase approval, Native or Instant mode, season-aware windows, stock-threshold pause,
  offline cap, auto-save and rollback per phase, repair of a phase that no longer fits the village.
- Agent files in UserData (agent.yaml, system.md, task.md, knowledge, profiles, versions) with in-game
  editor, schema check and hot reload.
- Automatic updates from GitHub Releases (updater plugin, SHA-256 verified), agent-file sync that never
  overwrites your edits.
- Milestone M0 tools: probe of the game's classes, Claude-assisted mapping proposal, in-game op tests.
  Ops stay Manual until mapped and tested on your game build.
