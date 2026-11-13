# Changelog

Every version listed here is published by the release workflow (`.github/workflows/release.yml`) when it reaches `main`.
The in-game updater shows the section of the version it installs.

## [0.3.0] - 2026-11-13

Game map built from the decompiled Assembly-CSharp of Farthest Frontier v1.1.2a (Mono). Every type, member and
method in `gamemap.json` was checked against that build; `mapped_game_version` is set, so Execute is available for
the mapped ops once "Allow experimental ops" is ticked.

Reading (exact game data):
- Heights from `Terrain2Manager.GetHeightInWorldSpace`; the 5-unit cell grid matches `BuildManager.cellSize`.
- Village stock from `ResourceManager.itemNameToItemInfoDict`; months of food from the game's own
  `estimatedMonthsOfFoodLeft`; firewood from `firewoodItemInfo`; date from `TimeManager.currentDate`.
- Crop fields (`Cropfield`): fertility, weeds, rockiness, soil mix, diseases, allocated farmers, and the real
  3-year schedule from `cropScheduler`.
- Crop table from the game's `VegetableFieldsRecord` data (fertility depletion, weed and fertility multipliers)
  and `CropDiseaseRecord` (which crops each disease targets), replacing the estimates.
- Natural resources: trees, stone, forage and mineral deposits (iron, gold, coal).
- Building costs from `BuildingData.GetRequiredMaterialsDict()`; work circles from each building's `WorkArea`;
  service radius from `strategicPlanningRadius`; villager jobs from `occupation`.

Ops (experimental, following the game's code paths):
- set_workers (`userDefinedMaxWorkers`), set_paused (`SetWorkEnabled`), set_work_circle (`WorkArea.SetCenterPoint`),
  set_production_limit (item quota), set_rotation (crop scheduler), demolish (`OnDeleteConfirmed`),
  field_remove (`Cropfield.OnConfirmDelete`), build (`BuildManager.Construct`), relocate (`BuildManager.Relocate`).
- Auto-save (`SaveManager.Save`) and rollback (`CESceneManager.LoadFromWithinGame`).
- set_profession_target dropped (the game has no such setting). Not mapped yet: field_create, field_resize,
  road_add, road_remove, clear_area, flatten (they show as Manual).

## [0.2.0] - 2026-11-13

Game reading rebuilt from a v1.1.2a (Mono) probe report.

- Map: the game's own cell grid (384×384 cells of 5 units, detected from the fertility map), ground on the
  `ClickCubes` layer, water on the `Water` layer and below the terrain manager's `seaLevel`.
- Roads: read cell by cell from the `Roads` layer (none were read before, so every plan failed connectivity).
- Footprints from each building's placement collider (`gridCollider`) and `BuildingData.gridSize`.
- Workers per building counted from villagers' place of work; worker limit and paused state from
  `_userDefinedMaxWorkers` / `_playerDesiresWorkEnabled`.
- Stock from storages' `items` + `itemQtys` lists; year and sub-season from the time manager.
- Building catalogue: costs (`buildingMaterials`), build work, max workers, UI category, tier from the name.
- Wall pieces are summarised instead of listed one by one; categories and service radii fitted to the game's
  building names and 5-unit cells.
- Connectivity: a building counts as connected when any edge of its footprint touches the road network; a
  village that starts below 95% connected must not get worse (warning instead of a rejected plan).
- set_workers and set_paused have experimental recipes (test them in Settings > Diagnostics).

## [0.1.3] - 2026-11-13

- Run logs: every Assistant and Architect run (and phase repair) is written to `UserData/FrontierArchitect/runs/` as one
  JSON-lines file: run settings, the prompts sent (system and task with the snapshot), per turn Claude's reasoning
  summary, text and tool calls, every tool result, notes (model fallback, repair prompts, wrap-up hints), and the outcome
  with the plan or report and token usage. The API key is never written.
- New **Runs** button in the Assistant and Architect panels to browse the logs turn by turn.
- `config.json → run_log`: `enabled` (default true) and `keep` (newest 50 kept).

## [0.1.2] - 2026-11-13

- Map reading no longer needs Unity's Terrain component: when the game has none, the mod finds the ground collider and
  raycasts onto it (height, slope, water), or falls back to the extent of the village. Fixes Architect failing with
  "no active terrain; grid is empty".
- Architect stops before calling Claude when no map could be read, and says why.
- The probe report now describes the ground (source, largest colliders, TerrainManager fields).

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
