# Frontier Architect

A MelonLoader mod for **Farthest Frontier (Mono build)** that adds three buttons to the game:

| Button | What you get | Changes the game? |
| --- | --- | --- |
| **Assistant** | A ranked report on your village (critical / important / optimisation), each finding with its numbers, and one-click **Apply** for simple fixes | Only when you click Apply (undo while the panel is open) |
| **Architect** | One redesign scenario shown as ghost buildings on the map, with a phase list, before/after scores on six objectives and a rationale line per action | No, proposal only |
| **Execute** | The scenario carried out phase by phase, each phase approved by you, in Native (real cost, builders, time) or Instant mode | Yes |

Each button sends a snapshot of the village to Claude through the Anthropic API, using **your own API key**.
Built from the product and technical specification "Frontier Architect — Product & Technical Specification".

- **First install:** [docs/INSTALL.md](docs/INSTALL.md)
- **Updates (automatic):** [docs/UPDATES.md](docs/UPDATES.md)
- **Mapping a game build (milestone M0):** [docs/M0-MAPPING.md](docs/M0-MAPPING.md)
- **How it is built:** [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## Status

| Milestone | State |
| --- | --- |
| M0 Feasibility | Tooling done: probe of the game's classes, Claude-assisted mapping proposal, in-game op tests, `gamemap.json`. **The mapping itself must be done on a real game install** (see below). |
| M1 Assistant | Done: snapshot, Claude client, report panel, Apply/undo for settings ops (once those ops are mapped). |
| M2 Architect | Done: tool server, validator, scorer, road network, overlay and panel, agent files with in-game editor. |
| M3/M4 Execute | Done in the mod: phase runner, approval cards, Native/Instant, guardrails, auto-save, rollback, repair. Off until the game build is mapped. |
| M5 Radical plans | Ambition 4–5 rules (flatten, Town Center/walls/gates) are implemented; they need the corresponding ops mapped. |

**Read this before playing:** the mod reaches the game's internals by reflection, driven by
[`gamemap/gamemap.json`](gamemap/gamemap.json). Its class and member names were seeded from two open-source
Farthest Frontier mods and could not be checked against the game from where this was written. On your
first run, the Assistant and Architect work with whatever the snapshot can read (anything missing is listed
to Claude as a data gap), and every game-changing op shows as **Manual** until it is mapped and tested on
your build. That is the spec's milestone M0, and the mod ships the tools to do it in a few minutes.

## Safety rules the mod enforces

- Nothing changes in your village without a click (Apply, or approving a phase).
- Claude proposes, the mod verifies: every action is re-checked (terrain, overlap, locked buildings, tier
  unlocks, cost, road connectivity, phase order, offline cap, seasons, stocks) before it is shown or run.
- Before each phase: a named auto-save; one click rolls back to it.
- Execution pauses when food or firewood falls below 3 months (editable) or during a raid.
- A version check disables Execute when the game build differs from the mapped one.
- The API key stays in `UserData/FrontierArchitect/config.json` — never in saves, plans, reports or logs.

## Repository layout

```
src/FrontierArchitect.Core/     game-independent logic (Claude client, planner, validator, roads, scoring, executor)
src/FrontierArchitect/          the MelonLoader mod (reflection game layer, IMGUI panels, overlay)
src/FrontierArchitect.Updater/  MelonLoader plugin that installs new releases before the mod loads
agents/                         default agent files (copied to UserData, editable in game)
gamemap/gamemap.json            how the mod reaches the game's internals (per game build)
tests/                          unit tests for the Core (dotnet test)
tools/                          package.sh (release payload), bump-version.sh
.github/workflows/              CI (build, test, package) and Release (publish on version bump)
```

## Develop

Requires the .NET 8 SDK; the game is not needed to build (MelonLoader and Unity are compile-time NuGet
references, game code is reached by reflection).

```bash
dotnet build FrontierArchitect.sln -c Release
```

```bash
dotnet test tests/FrontierArchitect.Tests -c Release
```

```bash
tools/package.sh
```

The mod DLL is `src/FrontierArchitect/bin/Release/net472/FrontierArchitect.dll` (copy it to
`Farthest Frontier (Mono)/Mods/` to try a local build).

## Claude usage

- Endpoint: Anthropic Messages API, streaming, called from the mod over HTTPS (raw HTTP: the official C#
  SDK's dependencies do not load reliably inside the game's Unity Mono runtime).
- Model: `most-capable` resolves to Claude Fable 5.2 when your key can use it, otherwise Claude Opus 5.5
  (override in Settings or in `agent.yaml`). Adaptive thinking; depth is set by `model.effort`.
- Server-side refusal fallback is enabled (`fallbacks: "default"`), so a declined request is retried on a
  fallback model inside the same call. Remove it in `Core/Claude/AgentLoop.cs` if you do not want it.
- Prompt caching: the knowledge pack is a cached system block; the conversation uses automatic caching.
