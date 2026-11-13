# Architecture

State flows from the game through the snapshot to Claude; Claude's choices come back through the tool server
and validator; only phases the player approves reach the executor.

```
Unity main thread                         background thread                     Anthropic API
-----------------                         -----------------                     -------------
SnapshotBuilder ──immutable snapshot──▶  AgentLoop (streaming, retries) ◀──SSE──▶ POST /v1/messages
                                          │  tool calls
                                          ▼
                                          ArchitectToolHost / AssistantToolHost
                                          (DraftLayout, PlacementValidator, Scorer,
                                           RoadPlanner, StockForecast, JsonPath)
                                          │  submit_plan / submit_report
                                          ▼
Panels + Overlay ◀──validated plan──────  PlanValidator
   │ approve phase
   ▼
PhaseRunner ──▶ GameExecutor ──recipes (gamemap.json)──▶ game code (reflection)
   ▲ guardrails (season, stock, raid)        save / load before each phase
```

## Components (spec section 6) and where they live

| Component | Code |
| --- | --- |
| HUD module | `src/FrontierArchitect/UI/Windows.cs` (buttons, report, Architect, Execute, settings, agent editor, diagnostics) |
| Snapshot builder | `src/FrontierArchitect/Game/SnapshotBuilder.cs` (main thread) |
| Claude client | `Core/Claude/ClaudeClient.cs`, `StreamAccumulator.cs`, `AgentLoop.cs` |
| Tool server | `Core/Tools/ArchitectTools.cs`, `AssistantTools.cs` |
| Validator | `Core/Layout/Draft.cs` (per action), `Core/Planning/PlanValidator.cs` (whole plan) |
| Scorer | `Core/Scoring/Scorer.cs` (O1–O6, rotation evaluator) |
| Road network (O6) | `Core/Roads/RoadNetwork.cs` (terminals → Steiner tree heuristic → shortcuts → reuse) |
| Overlay renderer | `src/FrontierArchitect/UI/Overlay.cs` |
| Executor | `Core/Execution/Executor.cs` (`PhaseRunner`) + `src/FrontierArchitect/Game/GameExecutor.cs` |
| Guardrail monitor | `Core/Execution/Executor.cs` (`Guardrails`) |
| Save manager | `GameExecutor.Save/Load` via `procedures` in `gamemap.json` |
| Plan store | `Core/Execution/Executor.cs` (`PlanStore`, `UserData/FrontierArchitect/plans/`) |
| Agent loader | `Core/Agents/AgentLoader.cs`, `AgentDefinition.cs`, `DefaultsSync.cs` |
| Updater | `src/FrontierArchitect.Updater/`, `Core/Updates/UpdateEngine.cs` |

## Data contracts (spec section 7)

- Snapshot: `VillageSnapshot.ToJson()` — `meta`, `metrics`, `buildings`, `fields`, `roads`, `resources`, `stock`,
  `unlocked_buildings`, plus `data_gaps` (what the mod could not read). The tile grid is read through `get_region`.
- Assistant report: `submit_report` input — `findings[{id, severity, domain, title, evidence[], recommendation,
  action{kind, ops[]}}]`.
- Architect plan: `submit_plan` input — `scenario{ambition, summary, score_before, score_after}`,
  `phases[{n, name, goal, season_window, depends_on, actions[{id, op, target, params, rationale, objective}],
  offline, est_cost, est_days}]`. The mod fills `score_before/after`, missing estimates and the traceability
  block (`meta.profile`, `meta.agent_hash`, model, mode).

## Deviations from the spec, and why

| Spec | Implementation | Reason |
| --- | --- | --- |
| `model.thinking_budget_tokens` | `model.effort` (low … max) | Current Claude models use adaptive thinking; a token budget is rejected by the API. |
| Plan returned as text JSON | `submit_plan` / `submit_report` tools (text JSON still accepted) | The mod validates on submit and returns the errors, which gives the "returned once for repair" step for free. |
| Coordinates | tile grid `[x, z]`, `rot` in quarter turns | The mod builds its own grid from the terrain (`gamemap.json → grid.tile_size`). |
| Stock forecast and months of food | Estimated from configurable rates (`agents/shared/buildings.yaml → assumptions`) and flagged `rates_estimated` | The game's consumption rates are not mapped yet. |
| Crop table | `agents/shared/crops.yaml`, values marked approximate | The official guide gives crop behaviour, not numbers. |
| "Build replacement before demolishing" (open question) | Enforced for tier 1–2 types, not for tier 3–5 | Answer recorded in the spec. |

## Threading and safety

All reads and writes of game objects happen on the Unity main thread; network calls never do. The snapshot is
immutable once built. The executor re-validates every action against the live village before issuing it. The
conversation with Claude is append-only (assistant turns are echoed back unchanged), which keeps prompt caching
and the thinking-block checks intact.
