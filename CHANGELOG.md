# Changelog

Every version listed here is published by the release workflow (`.github/workflows/release.yml`) when it reaches `main`.
The in-game updater shows the section of the version it installs.

## [0.5.0] - 2026-11-13

District-scale redesign: the Architect can now replan whole districts instead of moving buildings one by one.

- **Zones** (`set_zones`): Claude first draws the districts (housing, farming, industry, storage, services, defence,
  mixed, reserve) and gets free land, fertility, slope and the buildings already inside each one. Zones are saved with
  the plan, listed in the Architect panel and drawn on the overlay in one colour per purpose.
- **District templates** (`agents/shared/districts.yaml`, editable and hot-reloaded): housing_block, compact_housing,
  service_core, industry_cluster, storage_hub, farm_belt, defence_line. A template says which buildings are the hub
  (centre), which go on the edge facing the Town Center, which stay away from housing, and how many free tiles to keep
  for roads.
- **`place_group`**: lays out a whole district in one call — up to 150 builds and relocations (`{type, count}`,
  `{move: [ids]}`, `{move_type, count}`), each a checked action on the draft. Members go inside the hub's service
  radius; buildings that already stand well are kept in place; blocked relocations are retried; `apply: false` previews.
- **`submit_plan` without retyping**: `auto_phases: true` phases every draft action in the standard order (splitting at
  the max actions per phase and the offline cap, falling back to draft order when the standard order would break), or
  phases can list `action_ids`. The last run spent 8,000 output tokens retyping 92 actions.
- **Longer runs for bigger redesigns**: ambition levels can set `max_turns` / `max_minutes` (3: 150 / 15 min,
  4: 200 / 25 min, 5: 260 / 35 min).
- **Continue plan** button: a new run starts from the current plan's unfinished actions (and zones), works on the
  districts it did not reach and adds its phases after the existing ones. Completed phases are not repeated.
- `set_zones` and `place_group` are enabled even when a customised `agent.yaml` lists older tools.

## [0.4.0] - 2026-11-13

From the first successful Architect run (ambition 2, 244 villagers, Medium map). The run cost 320k prompt tokens and
spent its turns on road holes and parse errors; this release fixes what it ran into.

Prompt about 7x smaller (roughly 320k → 45k tokens):
- The building catalogue no longer carries a raw dump of every BuildingData field (280 KB of the 355 KB knowledge pack).
- Natural resources are summarised: totals per kind, 24x24-tile clusters and single deposits (ore, clay…) instead of
  4,500 individual trees and bushes. `get_region` still lists every node in an area.
- Empty building fields (inputs, outputs, limits, "locked": false, "status": "active") are left out; houses no longer
  show residents as "workers", and the staffing list skips housing.

Exact game data:
- Roads: grid roads are read from the game's road pieces (`Building.roadMode` + `gridCellLocations`) and spline roads
  and bridges from the pathfinding grid (`AIGridGraph` nodes with a road speed bonus). The per-tile raycast left holes
  in diagonal roads, so route_network "filled" them with 92 one-tile road actions.
- Footprints from `Building.gridCellLocations` (the cells the game itself occupies); houses were read 4x4 instead of 3x3.
- Buildings the game cannot reach (`pathExistsForVillage` false) are flagged and listed in `metrics.no_path_for_villagers`.
- Service radii: `strategicPlanningRadius` (50 units on every building) is used only where the game uses it — market and
  well catchment, desirability bonuses — plus entertainment and spirituality radii and tower attack range. Before, every
  house and shop got a 10-tile "service", which made the O3/O5 coverage notes meaningless.
- Housing capacity from `residencyComponent.maxVacancyAvailable` (was a guessed 4 per house: "housing short by 40").

Roads and connectivity:
- Gates are openings: roads and walking pass through gate tiles (fenced fields and walled districts were "unconnected").
- Fields are no longer road terminals: farmers walk onto fields from anywhere.
- route_network reports the same connectivity as score_draft (it said 66% while the scorer said 99%), and groups new
  tiles into a few area actions instead of one action per run.
- `roads.connectivity` in agent.yaml is honoured (`required`, or anything else for "do not lower it").

Tools:
- Points sent as text ("[163,195]", "163,195") and ids like "field:field@162,186" are accepted everywhere; JSON sent as a
  string, numbers as text and "true"/"false" are repaired before validation. These cost the run four turns.
- Probe report: new `roads_and_paths` section (road pieces, path-grid nodes with a road bonus, buildings without a path).

## [0.3.1] - 2026-11-13

- Fix "Architect needs the map but none could be read": a map lookup that ran while the save was still loading
  was remembered for the whole session. Failed lookups are no longer cached, Architect reads the map again before
  giving up, and the map size falls back to the game's own setting (Small 1280 / Medium 1920 / Large 2560 world
  units). The message now says which source failed and why.

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
