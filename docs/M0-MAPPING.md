# Mapping a game build (milestone M0)

**Status:** v1.1.2a (Mono) is mapped from its decompiled `Assembly-CSharp.dll`: every name in `gamemap.json` exists in that build, and the recipes follow the game's own code. Ops are `experimental` until you test them (below); field creation/resizing, roads, clearing and flattening are not mapped yet. When the game updates, repeat this page.

The spec makes this the first engineering task: find the game's own classes for buildings, relocation,
fields, roads and saves on the current Mono build, and mark each op **proven**, **Manual** or **dropped**.
The mod does it with data, not code: [`gamemap.json`](../gamemap/gamemap.json) holds type names, member paths
and per-op "recipes" (which method to call with which arguments, and how to check it worked).

Until an op is mapped it shows as **Manual** with its reason. Execute stays off until `mapped_game_version`
equals your game version.

## In game (about 10 minutes, on a throwaway save)

1. Load a settlement. **Settings → Diagnostics → Run probe.** This writes to `UserData/FrontierArchitect/probe/`:
   - `types.txt` — every class of the game with its fields, properties and methods;
   - `candidates.md` — the members whose names match what the mod needs (relocate, road, field, save, …);
   - `report.json` — which `gamemap.json` entries resolve on your build, with live sample values.
2. **Ask Claude to propose mappings.** Claude reads `candidates.md`, `report.json` and the current `gamemap.json` and writes `gamemap.proposed.json`
   (recipes marked `experimental`). Read it if you like, then **Apply proposal**: it is merged into
   `gamemap.local.json`.
3. Tick **Allow experimental ops**, then test: click **set_workers** (or **set_paused**), hover a building and
   press `L`. The op runs, its completion test is checked, the change is reverted, and the op is marked
   **proven** in `gamemap.local.json` if it worked.
4. For spatial ops (build, relocate, demolish, roads, fields), use Architect at ambition 1–2 in **Instant** mode
   on the throwaway save, then Execute one phase. Ops that worked can be set to `"status": "proven"` in
   `gamemap.local.json`; ops that cannot work get `"status": "dropped"`.
5. **Mark game <version> as mapped.** Execute is now available for proven ops.

## Share the mapping with everyone

Send the `probe/` folder and your `gamemap.local.json` to the maintainer (or open the repository with Claude Code
and give it the files). The confirmed entries go into `gamemap/gamemap.json` with `mapped_game_version` set,
and the next release delivers them to every install. When the game updates, the version check turns Execute
off again until the mapping is confirmed on the new build.

## Recipe format

```json
"set_workers": {
  "status": "experimental",
  "native": [{ "call": "building.SetDesiredWorkerCount", "args": ["$count"] }],
  "instant": "same",
  "verify": { "member": "building.desiredWorkerCount", "equals": "$count", "timeout_seconds": 30 }
}
```

- Steps: `{"call": "<target>.<Method>", "args": [...]}`, `{"set": "<target>.<member>", "value": ...}`,
  `{"get": "<target>.<member>", "store": "name"}`.
- Targets: `building`, `field`, `$stored`, or a type concept from `types` (its live singleton; add `.static`
  for static methods).
- Variables: `$count $limit $paused $item $profession $world_pos $rot_deg $rotation $prefab $building_data
  $rect_min $rect_max $rect_centre $tiles_world $crop_rotation $name`.
- `verify`: `{"member": ..., "equals": ...}` or `{"builtin": "building_at_target" | "building_gone" |
  "field_present" | "field_gone" | "immediate"}`.
- Every op needs a validator (built into the mod), a `native` recipe, an `instant` recipe and a `verify`;
  an op missing one of them is not run.
- Saves: `procedures.save` / `procedures.load` with the same step format, using `$name`.
