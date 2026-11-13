using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using FrontierArchitect.Core.Claude;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Roads;
using FrontierArchitect.Core.Scoring;

namespace FrontierArchitect.Core.Tools
{
    /// <summary>
    /// Answers Claude's tool calls during an Architect run. Spatial precision lives here: Claude
    /// chooses intent and trade-offs, the mod supplies exact tiles and the verdicts.
    /// Everything works on the immutable snapshot plus a scratch draft, so it is thread-safe with
    /// respect to the game (no Unity calls).
    /// </summary>
    public sealed class ArchitectToolHost : IToolHost
    {
        public static readonly string[] AllToolNames =
        {
            "get_region", "query", "find_sites", "check_placement", "apply_to_draft", "undo_draft", "route_road",
            "route_network", "path_distance", "coverage", "evaluate_rotation", "score_draft", "forecast_stock", "submit_plan",
            "set_zones", "place_group"
        };

        private readonly VillageSnapshot _snap;
        private readonly RunSettings _s;
        private readonly double _foodMonths, _woodMonths;
        private readonly List<ToolSpec> _tools = new List<ToolSpec>();
        private readonly List<double> _scoreHistory = new List<double>();
        private readonly double _stopIfGainBelow;
        private readonly int _gainWindow;
        private int _applyCalls;
        private JObject _snapshotJson;

        public DraftLayout Draft { get; }
        public ArchitectPlan AcceptedPlan { get; private set; }
        public PlanValidation LastValidation { get; private set; }
        public double BaselineScore { get; }
        public List<Zone> Zones { get; } = new List<Zone>();
        /// <summary>Unfinished phases of the plan this run continues (kept in front of the new phases).</summary>
        private readonly List<Phase> _priorPhases = new List<Phase>();
        private int _priorActions;
        private int _groupCounter;
        public int PriorActionCount => _priorActions;
        public int PriorPhaseCount => _priorPhases.Count;
        public List<string> PriorNotes { get; } = new List<string>();

        public ArchitectToolHost(VillageSnapshot snapshot, RunSettings settings, IEnumerable<string> enabledTools,
            IDictionary<string, string> descriptionOverrides, double foodMonths, double woodMonths,
            double stopIfGainBelow = 0.5, int gainWindowTurns = 10, ArchitectPlan continueFrom = null)
        {
            _snap = snapshot;
            _s = settings;
            _foodMonths = foodMonths;
            _woodMonths = woodMonths;
            _stopIfGainBelow = stopIfGainBelow;
            _gainWindow = Math.Max(2, gainWindowTurns);
            Draft = new DraftLayout(snapshot);
            BaselineScore = new Scorer(_s).Score(Draft).Weighted;
            if (continueFrom != null) LoadPrior(continueFrom);
            _scoreHistory.Add(_priorActions > 0 ? new Scorer(_s).Score(Draft).Weighted : BaselineScore);
            var enabled = new HashSet<string>(enabledTools ?? AllToolNames);
            enabled.Add("submit_plan");
            // tools added in 0.5: on even when an older or customised agent.yaml lists the enabled tools
            enabled.Add("set_zones");
            enabled.Add("place_group");
            foreach (var spec in Specs())
            {
                if (!enabled.Contains(spec.Name)) continue;
                string d;
                if (descriptionOverrides != null && descriptionOverrides.TryGetValue(spec.Name, out d) && !string.IsNullOrWhiteSpace(d)) spec.Description = d.Trim();
                _tools.Add(spec);
            }
        }

        public IList<ToolSpec> Tools => _tools;

        /// <summary>Applies the unfinished actions of an earlier plan to the draft (completed phases are already in the village).</summary>
        private void LoadPrior(ArchitectPlan prior)
        {
            foreach (var z in prior.Zones) Zones.Add(z);
            foreach (var p in prior.Phases)
            {
                if (p.Status == PhaseStatus.Completed || p.Status == PhaseStatus.Skipped) continue;
                var copy = Phase.FromJson(p.ToJson());
                copy.Status = PhaseStatus.NotStarted;
                copy.Actions.RemoveAll(a => a.Status == ActionStatus.Rejected);
                var kept = new List<PlanAction>();
                foreach (var a in copy.Actions)
                {
                    var r = Draft.Apply(a, _s);
                    if (r.Valid) kept.Add(a);
                    else PriorNotes.Add("previous action " + a.Id + " (" + a.Op + " " + a.Target + ") no longer fits and was dropped: " + string.Join("; ", r.Reasons));
                }
                copy.Actions = kept;
                if (kept.Count > 0) _priorPhases.Add(copy);
            }
            _priorActions = Draft.Applied.Count;
        }

        public string MissingSubmitMessage =>
            "You ended your turn without calling submit_plan. Call submit_plan now with the complete plan (scenario + phases).";

        // ------------------------------------------------------------------ tool definitions

        public static List<ToolSpec> Specs()
        {
            var action = S.Action();
            return new List<ToolSpec>
            {
                new ToolSpec { Name = "get_region", Description =
                    "Read a window of the tile grid (max 64x64). Layers: terrain (.=land ~=water ^=cliff #=edge), slope (0-9, x10 degrees), " +
                    "fertility (0-9 = 0.0-0.9, ?=unknown), occupancy (.=free, letters = occupants listed in the legend), clutter (t=trees r=rocks), " +
                    "roads (=road on the draft), soil (clay share 0-9). Rows run from z (top) to z+h-1; columns from x to x+w-1.",
                    InputSchema = S.Obj(S.Props("x", S.Int("min x"), "z", S.Int("min z"), "w", S.Int("width, max 64"), "h", S.Int("height, max 64"),
                        "layers", S.Arr(S.Enum("layer", "terrain", "slope", "fertility", "occupancy", "clutter", "roads", "soil"), "layers to return")), "x", "z", "w", "h") },
                new ToolSpec { Name = "query", Description =
                    "Read any field of the snapshot, the draft or the knowledge pack by path. Roots: snapshot, draft, knowledge. " +
                    "Examples: snapshot.metrics.food, snapshot.buildings[type=Storehouse], snapshot.buildings[category=housing].length, " +
                    "draft.buildings[*].id, knowledge.buildings[type=Well]. Filters support = != < > <= >= and ~ (contains).",
                    InputSchema = S.Obj(S.Props("path", S.Str("path expression")), "path") },
                new ToolSpec { Name = "find_sites", Description =
                    "Ranked valid positions for a building type on the current draft. 'near' is a point [x,z] or a building id; " +
                    "radius limits the search (default 30). Constraints: min_fertility (farms), near_type (stay close to a building type), " +
                    "max_distance (to near_type), rotations (subset of 0-3). Returns up to 'limit' sites (default 8) with x, z, rot, distance and warnings.",
                    InputSchema = S.Obj(S.Props("building_type", S.Str("building type (see knowledge.buildings)"),
                        "near", new JObject().Set("description", "[x,z] point or building id"),
                        "radius", S.Int("search radius in tiles"), "limit", S.Int("max results"),
                        "constraints", S.AnyObj("min_fertility, near_type, max_distance, rotations, exclude_road_tiles (bool)")), "building_type") },
                new ToolSpec { Name = "check_placement", Description =
                    "Validate one action against the draft without applying it. Returns valid, reasons, warnings, cost, tiles taken and the score effect.",
                    InputSchema = S.Obj(S.Props("action", action), "action") },
                new ToolSpec { Name = "apply_to_draft", Description =
                    "Validate and apply one action to the draft the scenario is built on. Returns the verdict and the new weighted score. Invalid actions are not applied.",
                    InputSchema = S.Obj(S.Props("action", action), "action") },
                new ToolSpec { Name = "undo_draft", Description = "Undo the last applied draft action.", InputSchema = S.Obj(new JObject()) },
                new ToolSpec { Name = "route_road", Description =
                    "A buildable road path between two points or building ids on the draft, reusing existing road where possible. Returns tiles, new tile count and a ready road_add action.",
                    InputSchema = S.Obj(S.Props("from", new JObject().Set("description", "[x,z] or building id"), "to", new JObject().Set("description", "[x,z] or building id")), "from", "to") },
                new ToolSpec { Name = "route_network", Description =
                    "Design the whole road network for the draft: approximate minimum Steiner tree over all building entrances and field access points, " +
                    "plus shortcuts for key trips over the detour limit. Existing segments on the network are reused for free. Returns tile count, " +
                    "connectivity, detour ratios, and the road_add / road_remove actions to include in the plan (set apply=true to apply them to the draft).",
                    InputSchema = S.Obj(S.Props("apply", S.Bool("apply the returned road actions to the draft"),
                        "shortcuts", S.Bool("add key-trip shortcuts (default true)"),
                        "remove_unused", S.Bool("also return road_remove for old segments outside the network (default true)"))) },
                new ToolSpec { Name = "path_distance", Description = "Walking distance in tiles between two points or building ids on the draft, roads included.",
                    InputSchema = S.Obj(S.Props("a", new JObject().Set("description", "[x,z] or building id"), "b", new JObject().Set("description", "[x,z] or building id")), "a", "b") },
                new ToolSpec { Name = "coverage", Description = "Houses covered and uncovered by a service type (market, well, school, healer, chapel, entertainment, …) on the draft.",
                    InputSchema = S.Obj(S.Props("service_type", S.Str("service type")), "service_type") },
                new ToolSpec { Name = "evaluate_rotation", Description =
                    "Projected fertility, weeds, disease risk and yield over 3 years for a rotation on a field (existing field id, or rect [x,z,w,h] for a new one).",
                    InputSchema = S.Obj(S.Props("field", new JObject().Set("description", "field id or rect [x,z,w,h]"),
                        "rotation", S.Arr(S.Arr(S.Str("crop"), "crops in that year"), "3 years")), "field", "rotation") },
                new ToolSpec { Name = "score_draft", Description = "Objective scores O1..O6 (0-100), weighted total, and constraint violations of the current draft.",
                    InputSchema = S.Obj(new JObject()) },
                new ToolSpec { Name = "forecast_stock", Description =
                    "Projected food months, firewood months and materials through a list of phases in Native mode. Pass the phases you plan (same shape as submit_plan phases).",
                    InputSchema = S.Obj(S.Props("phases", S.Arr(S.AnyObj("phase"), "phases")), "phases") },
                new ToolSpec { Name = "set_zones", Description =
                    "Declare the districts of the redesign before placing anything: each zone has a name, a purpose (" + string.Join(", ", Zone.Purposes) +
                    ") and a rect [x,z,w,h]. Replaces the previous zones. Returns each zone's free land, average fertility, slope, water and the buildings now inside it, " +
                    "and how well current buildings fit their zones. place_group can then fill a zone by name.",
                    InputSchema = S.Obj(S.Props("zones", S.Arr(S.Obj(S.Props("name", S.Str("short unique name, e.g. north_housing"),
                        "purpose", S.Enum("what the zone is for", Zone.Purposes), "rect", new JObject().Set("description", "[x,z,w,h] in tiles")), "name", "purpose", "rect"), "zones")), "zones") },
                new ToolSpec { Name = "place_group", Description =
                    "Lay out a whole district in one call. Give a zone name (from set_zones) or an area [x,z,w,h], a district template (see the District templates " +
                    "section of the knowledge pack; it decides the arrangement: hub at the centre, members around it, edge buildings toward the Town Center, polluters " +
                    "away from housing) and the items: {\"type\": T, \"count\": n} to build, {\"move\": [ids]} to relocate given buildings, or {\"move_type\": T, " +
                    "\"count\": n} to relocate the n buildings of a type farthest from the area. Buildings keep 'spacing' free tiles around them for roads. " +
                    "Each placement is a checked build/relocate action applied to the draft (apply=false to preview). Returns placed, kept-in-place and unplaced items, " +
                    "the action ids and the score change. Follow with route_network once the districts are placed.",
                    InputSchema = S.Obj(S.Props(
                        "zone", S.Str("zone name from set_zones"),
                        "area", new JObject().Set("description", "[x,z,w,h] when no zone is given"),
                        "template", S.Str("district template name (optional; the zone purpose picks a default)"),
                        "items", S.Arr(S.AnyObj("{type, count} | {move: [ids]} | {move_type, count}"), "what to place"),
                        "anchor", new JObject().Set("description", "[x,z] centre of the layout (default: the area centre)"),
                        "spacing", S.Int("free tiles kept around each building (0-3; default from the template)"),
                        "objective", S.Enum("objective tag for the actions", "O1", "O2", "O3", "O4", "O5", "O6"),
                        "rationale", S.Str("one line for every action of the group"),
                        "apply", S.Bool("apply to the draft (default true)")), "items") },
                new ToolSpec { Name = "submit_plan", Description =
                    "Submit the final scenario. The mod re-validates every action, the phase order, the offline cap, seasons, road connectivity and (Native) stocks. " +
                    "If anything fails you get the errors back and can fix and resubmit. Shortcuts: auto_phases=true phases every draft action for you (standard order, " +
                    "max actions per phase, offline cap); or give each phase \"action_ids\" (ids of applied draft actions) instead of repeating the actions.",
                    InputSchema = S.Obj(S.Props(
                        "scenario", S.Obj(S.Props("ambition", S.Int("1-5"), "summary", S.Str("two or three sentences")), "summary"),
                        "auto_phases", S.Bool("let the mod build the phases from the draft actions"),
                        "phases", S.Arr(S.Obj(S.Props(
                            "n", S.Int("phase number from 1"), "name", S.Str("short name"), "goal", S.Str("what the phase achieves"),
                            "season_window", S.Arr(S.Enum("slot", "spring", "summer", "early_autumn", "late_autumn", "winter"), "when it may start"),
                            "depends_on", S.Arr(S.Int("phase"), "phases that must finish first"),
                            "actions", S.Arr(action, "actions"),
                            "action_ids", S.Arr(S.Str("draft action id"), "ids of applied draft actions, in order (instead of actions)"),
                            "offline", S.Arr(S.Str("building id"), "buildings offline during the phase"),
                            "est_cost", S.AnyObj("item -> amount"), "est_days", S.Num("estimated game days")), "n", "name"), "phases")),
                        "scenario") },
            };
        }

        // ------------------------------------------------------------------ dispatch

        public ToolOutcome Execute(string name, JObject input, CancellationToken cancel)
        {
            var spec = _tools.FirstOrDefault(t => t.Name == name);
            if (spec == null) return ToolOutcome.Error("unknown or disabled tool '" + name + "'");
            input = ToolValidation.Coerce(spec.InputSchema, input) as JObject ?? input;
            var err = ToolValidation.Validate(spec.InputSchema, input);
            if (err != null) return ToolOutcome.Error(err);
            switch (name)
            {
                case "get_region": return GetRegion(input);
                case "query": return Query(input);
                case "find_sites": return FindSites(input, cancel);
                case "check_placement": return CheckPlacement(input);
                case "apply_to_draft": return ApplyToDraft(input);
                case "undo_draft": return UndoDraft();
                case "route_road": return RouteRoad(input);
                case "route_network": return RouteNetwork(input);
                case "path_distance": return PathDistance(input);
                case "coverage": return Coverage(input);
                case "evaluate_rotation": return EvaluateRotation(input);
                case "score_draft": return ScoreDraft();
                case "forecast_stock": return ForecastStock(input);
                case "submit_plan": return SubmitPlan(input);
                case "set_zones": return SetZones(input);
                case "place_group": return PlaceGroup(input, cancel);
            }
            return ToolOutcome.Error("not implemented: " + name);
        }

        public string WrapUpHint(AgentProgress progress)
        {
            if (_applyCalls < 3 || _scoreHistory.Count <= _gainWindow) return null;
            double recent = _scoreHistory[_scoreHistory.Count - 1] - _scoreHistory[_scoreHistory.Count - 1 - _gainWindow];
            if (recent < _stopIfGainBelow)
                return "Gains have stalled (" + Math.Round(recent, 2) + " weighted points over the last " + _gainWindow +
                       " scored steps, threshold " + _stopIfGainBelow + "). Stop placing: connect roads, phase the actions and call submit_plan.";
            return null;
        }

        public bool TryAcceptText(string text, out ToolOutcome outcome)
        {
            outcome = null;
            var o = Json.ExtractFirstObject(text);
            if (o == null || o.Arr("phases") == null) return false;
            outcome = SubmitPlan(o);
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private bool TryPoint(object v, out Cell c)
        {
            if (Cell.TryParse(v, out c)) return true;
            if (v is string raw)
            {
                string id = raw.Trim();
                if (id.StartsWith("field:", StringComparison.OrdinalIgnoreCase)) id = id.Substring(6);
                BuildingInfo b;
                if (!Draft.Buildings.TryGetValue(id, out b))
                    b = Draft.Buildings.Values.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (b != null) { c = new RoadPlanner(Draft, _s).Terminals().FirstOrDefault(t => t.Id == b.Id)?.Cell ?? b.EntranceCell; return true; }
                var f = Draft.Fields.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (f != null)
                {
                    // a tile just outside the field that a road can use, else its centre
                    var planner = new RoadPlanner(Draft, _s);
                    var access = DraftLayout.Border(f.Rect, 1).Where(x => !double.IsInfinity(planner.LayCost(x, null)))
                        .OrderBy(x => x.DistanceTo(f.Rect.Center)).Cast<Cell?>().FirstOrDefault();
                    c = access ?? f.Rect.Center;
                    return true;
                }
            }
            return false;
        }

        private double Weighted() => new Scorer(_s).Score(Draft).Weighted;

        private ToolOutcome GetRegion(JObject input)
        {
            int x = input.Int("x"), z = input.Int("z"), w = Math.Min(64, input.Int("w")), h = Math.Min(64, input.Int("h"));
            var g = Draft.Grid;
            if (w <= 0 || h <= 0) return ToolOutcome.Error("w and h must be > 0");
            var layers = input.Arr("layers")?.Strings() ?? new List<string> { "terrain", "occupancy", "fertility" };
            var res = new JObject().Set("x", x).Set("z", z).Set("w", w).Set("h", h);
            var legend = new JObject();
            var letters = new Dictionary<string, char>();
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            foreach (var layer in layers)
            {
                var rows = new JArray();
                for (int zz = z; zz < z + h; zz++)
                {
                    var sb = new StringBuilder(w);
                    for (int xx = x; xx < x + w; xx++)
                    {
                        var c = new Cell(xx, zz);
                        if (!g.In(c)) { sb.Append('#'); continue; }
                        int i = g.Idx(c);
                        switch (layer)
                        {
                            case "terrain":
                                var t = Draft.Flattened.Contains(c) ? Terrain.Land : g.Terrain[i];
                                sb.Append(t == Terrain.Land ? '.' : t == Terrain.Water ? '~' : t == Terrain.Cliff ? '^' : '#');
                                break;
                            case "slope": sb.Append(Draft.Flattened.Contains(c) ? '0' : Digit(g.Slope[i] / 10f)); break;
                            case "fertility": sb.Append(float.IsNaN(g.Fertility[i]) ? '?' : Digit(g.Fertility[i] * 10f)); break;
                            case "soil": sb.Append(float.IsNaN(g.Clay[i]) ? '?' : Digit(g.Clay[i] * 10f)); break;
                            case "clutter":
                                byte cl = Draft.Cleared.Contains(c) ? (byte)0 : g.Clutter[i];
                                sb.Append(cl == 1 ? 't' : cl == 2 ? 'r' : '.');
                                break;
                            case "roads": sb.Append(Draft.Roads.Contains(c) ? '=' : '.'); break;
                            case "occupancy":
                                {
                                    var owner = Draft.OwnerAt(c);
                                    if (owner == null) { sb.Append(Draft.Roads.Contains(c) ? '=' : '.'); break; }
                                    char ch;
                                    if (!letters.TryGetValue(owner, out ch))
                                    {
                                        ch = letters.Count < alphabet.Length ? alphabet[letters.Count] : '*';
                                        letters[owner] = ch;
                                        legend.Set(ch.ToString(), owner);
                                    }
                                    sb.Append(ch);
                                    break;
                                }
                            default: sb.Append(' '); break;
                        }
                    }
                    rows.Add(sb.ToString());
                }
                res.Set(layer, rows);
            }
            if (legend.Count > 0) res.Set("legend", legend);
            var resources = Draft.Snapshot.Resources.Where(r => r.Pos.X >= x && r.Pos.X < x + w && r.Pos.Z >= z && r.Pos.Z < z + h).Take(60).Select(r => (object)r.ToJson());
            res.Set("resources", new JArray(resources));
            return ToolOutcome.Ok(res);
        }

        private static char Digit(float v) => (char)('0' + Math.Max(0, Math.Min(9, (int)Math.Floor(v))));

        private JObject DraftJson()
        {
            var b = new JArray();
            foreach (var x in Draft.LiveBuildings.OrderBy(x => x.Id, StringComparer.Ordinal)) b.Add(x.ToJson());
            var f = new JArray();
            foreach (var x in Draft.Fields) f.Add(x.ToJson());
            var applied = new JArray();
            foreach (var a in Draft.Applied) applied.Add(a.ToJson());
            var zones = new JArray(); foreach (var z in Zones) zones.Add(z.ToJson());
            return new JObject().Set("summary", Draft.Summary()).Set("zones", zones).Set("buildings", b).Set("fields", f).Set("applied", applied)
                .Set("demolished", Draft.Demolished.ToJArray()).Set("road_tile_count", Draft.Roads.Count);
        }

        private ToolOutcome Query(JObject input)
        {
            string path = input.Str("path") ?? "";
            if (_snapshotJson == null) _snapshotJson = _snap.ToJson();
            var kb = new JArray();
            foreach (var kv in _snap.Knowledge.Buildings.OrderBy(k => k.Key, StringComparer.Ordinal)) kb.Add(kv.Value.ToJson());
            var root = new JObject()
                .Set("snapshot", _snapshotJson)
                .Set("draft", DraftJson())
                .Set("knowledge", new JObject().Set("buildings", kb));
            object v;
            try { v = JsonPath.Select(root, path); }
            catch (Exception e) { return ToolOutcome.Error("bad path: " + e.Message); }
            string text = Json.Write(new JObject().Set("path", path).Set("value", v));
            if (text.Length > 60000) text = text.Substring(0, 60000) + "… (truncated: narrow the path or add a filter)";
            return ToolOutcome.Ok(text);
        }

        private ToolOutcome FindSites(JObject input, CancellationToken cancel)
        {
            string type = input.Str("building_type");
            var spec = _snap.Knowledge.Spec(type);
            var con = input.Obj("constraints") ?? new JObject();
            int radius = Math.Max(3, Math.Min(80, input.Int("radius", 30)));
            int limit = Math.Max(1, Math.Min(20, input.Int("limit", 8)));
            Cell near;
            if (!TryPoint(input["near"], out near))
            {
                var tc = Draft.LiveBuildings.FirstOrDefault(b => b.Category == "town_center");
                near = tc != null ? tc.Center : new Cell(Draft.Grid.Width / 2, Draft.Grid.Height / 2);
            }
            var rots = con.Arr("rotations") != null ? con.Arr("rotations").Select(r => (int)Json.ToLong(r) & 3).Distinct().ToList() : new List<int> { 0, 1, 2, 3 };
            double minFert = con.Num("min_fertility", -1);
            string nearType = con.Str("near_type");
            double maxDist = con.Num("max_distance", double.MaxValue);
            var anchors = nearType == null ? null : Draft.LiveBuildings.Where(b => string.Equals(b.Type, nearType, StringComparison.OrdinalIgnoreCase) || b.Category == nearType).ToList();

            var probeSettings = CloneSettingsAllowing(Ops.Build);
            var results = new List<JObject>();
            var tried = new HashSet<long>();
            for (int ring = 0; ring <= radius && results.Count < limit * 4; ring++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int dx = -ring; dx <= ring; dx++)
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        int x = near.X + dx, z = near.Z + dz;
                        foreach (int rot in rots)
                        {
                            long key = ((long)x << 34) ^ ((long)z << 4) ^ rot;
                            if (!tried.Add(key)) continue;
                            var a = new PlanAction { Op = Ops.Build, Target = type, Params = new JObject().Set("x", x).Set("z", z).Set("rot", rot) };
                            var r = Draft.Check(a, probeSettings);
                            if (!r.Valid) continue;
                            if (con.Bool("exclude_road_tiles") && r.RoadTilesRemoved.Count > 0) continue;
                            var rect = r.Footprint.Value;
                            double fert = Draft.AverageFertility(rect);
                            if (minFert >= 0 && (double.IsNaN(fert) || fert < minFert)) continue;
                            double anchorDist = 0;
                            if (anchors != null && anchors.Count > 0)
                            {
                                anchorDist = anchors.Min(b => b.Center.DistanceTo(rect.Center));
                                if (anchorDist > maxDist) continue;
                            }
                            double dist = rect.Center.DistanceTo(near);
                            double score = dist + anchorDist * 0.5 + r.Warnings.Count * 2 - (double.IsNaN(fert) ? 0 : fert * 5);
                            results.Add(new JObject().Set("x", x).Set("z", z).Set("rot", rot).Set("rect", rect.ToJson())
                                .Set("distance", Math.Round(dist, 1)).Set("fertility", double.IsNaN(fert) ? null : (object)Math.Round(fert, 2))
                                .Set("warnings", r.Warnings.ToJArray()).Set("_score", score));
                        }
                    }
            }
            var top = results.OrderBy(o => o.Num("_score")).Take(limit).ToList();
            foreach (var t in top) t.Remove("_score");
            return ToolOutcome.Ok(new JObject().Set("building_type", type).Set("footprint", new JArray { (long)spec.W, (long)spec.H })
                .Set("sites", new JArray(top.Cast<object>())).Set("searched_radius", radius));
        }

        private RunSettings CloneSettingsAllowing(string op)
        {
            var s = _s;
            if (s.AllowedOps.Contains(op)) return s;
            return new RunSettings
            {
                Ambition = s.Ambition, Mode = s.Mode, LockedIds = s.LockedIds, AllowedOps = new HashSet<string>(s.AllowedOps) { op },
                MaxRelocations = s.MaxRelocations, Movable = s.Movable, MaxSlopeDeg = s.MaxSlopeDeg, EdgeMargin = s.EdgeMargin, Weights = s.Weights
            };
        }

        private PlanAction ParseAction(JObject input) => PlanAction.FromJson(input.Obj("action") ?? input);

        private ToolOutcome CheckPlacement(JObject input)
        {
            var a = ParseAction(input);
            var r = Draft.Check(a, _s);
            var o = r.ToJson();
            if (r.Valid)
            {
                var fork = Draft.Fork(_s);
                fork.Apply(a, _s);
                double after = new Scorer(_s).Score(fork).Weighted;
                double now = Weighted();
                o.Set("score_effect", Math.Round(after - now, 2));
            }
            return ToolOutcome.Ok(o);
        }

        private ToolOutcome ApplyToDraft(JObject input)
        {
            var a = ParseAction(input);
            if (string.IsNullOrEmpty(a.Id)) a.Id = "d" + (Draft.Applied.Count + 1);
            double before = _scoreHistory[_scoreHistory.Count - 1];
            var r = Draft.Apply(a, _s);
            var o = r.ToJson().Set("action_id", a.Id);
            if (r.Valid)
            {
                _applyCalls++;
                double now = Weighted();
                _scoreHistory.Add(now);
                o.Set("weighted_score", Math.Round(now, 2)).Set("delta", Math.Round(now - before, 2))
                 .Set("relocations_used", Draft.RelocationCount)
                 .Set("relocations_allowed", _s.MaxRelocations < 0 ? "all" : (object)_s.MaxRelocations);
                if (a.Params.Str("new_id") != null) o.Set("new_id", a.Params.Str("new_id"));
            }
            return new ToolOutcome { Content = Json.Write(o), IsError = !r.Valid };
        }

        private ToolOutcome UndoDraft()
        {
            if (Draft.Applied.Count <= _priorActions) return ToolOutcome.Error(_priorActions > 0 ? "only the previous plan's actions are left; they cannot be undone in this run" : "nothing to undo");
            var undone = Draft.Undo(_s);
            if (undone == null) return ToolOutcome.Error("nothing to undo");
            double now = Weighted();
            _scoreHistory.Add(now);
            return ToolOutcome.Ok(new JObject().Set("undone", undone.ToJson()).Set("weighted_score", Math.Round(now, 2)));
        }

        private ToolOutcome RouteRoad(JObject input)
        {
            Cell a, b;
            if (!TryPoint(input["from"], out a)) return ToolOutcome.Error("from must be [x,z] or a building id");
            if (!TryPoint(input["to"], out b)) return ToolOutcome.Error("to must be [x,z] or a building id");
            var planner = new RoadPlanner(Draft, _s);
            var path = planner.RouteRoad(a, b);
            if (path == null) return ToolOutcome.Error("no buildable road path between " + a + " and " + b);
            var fresh = path.Where(c => !Draft.Roads.Contains(c)).ToList();
            var tiles = new JArray(); foreach (var c in path) tiles.Add(c.ToJson());
            var freshTiles = new JArray(); foreach (var c in fresh) freshTiles.Add(c.ToJson());
            return ToolOutcome.Ok(new JObject().Set("length", path.Count).Set("new_tiles", fresh.Count).Set("tiles", tiles)
                .Set("action", new JObject().Set("op", Ops.RoadAdd).Set("params", new JObject().Set("road_type", "dirt").Set("tiles", freshTiles))
                    .Set("objective", "O6").Set("rationale", "Connects " + a + " to " + b + " with " + fresh.Count + " new tiles")));
        }

        private ToolOutcome RouteNetwork(JObject input)
        {
            var planner = new RoadPlanner(Draft, _s);
            var net = planner.Design(null, input.Bool("shortcuts", true));
            var fresh = net.Tiles.Where(c => !Draft.Roads.Contains(c) && !planner.Gates.Contains(c)).ToList();
            var addSegments = Clusters(Segments(fresh), 8, 240);
            var actions = new JArray();
            int n = 0;
            foreach (var seg in addSegments)
            {
                var t = new JArray(); foreach (var c in seg) t.Add(c.ToJson());
                actions.Add(new JObject().Set("id", "net" + (++n)).Set("op", Ops.RoadAdd).Set("params", new JObject().Set("road_type", "dirt").Set("tiles", t))
                    .Set("objective", "O6").Set("rationale", "Road network, " + seg.Count + " tiles near " + seg[seg.Count / 2]));
            }
            if (input.Bool("remove_unused", true))
            {
                var unused = Draft.Roads.Where(c => !net.Tiles.Contains(c)).ToList();
                foreach (var seg in Segments(unused))
                {
                    var t = new JArray(); foreach (var c in seg) t.Add(c.ToJson());
                    actions.Add(new JObject().Set("id", "net" + (++n)).Set("op", Ops.RoadRemove).Set("params", new JObject().Set("tiles", t))
                        .Set("objective", "O6").Set("rationale", "Old segment off the new network, " + seg.Count + " tiles"));
                }
            }
            var o = net.ToJson(false).Set("road_tiles_before", Draft.Roads.Count).Set("actions", actions);
            if (input.Bool("apply"))
            {
                int applied = 0;
                var failed = new JArray();
                foreach (var act in actions.Objects())
                {
                    var pa = PlanAction.FromJson(act);
                    if (pa.Op == Ops.RoadRemove) continue; // removals belong to clean-up; Claude places them in the last phase
                    var r = Draft.Apply(pa, _s);
                    if (r.Valid) applied++; else failed.Add(pa.Id + ": " + string.Join("; ", r.Reasons));
                }
                double now = Weighted();
                _scoreHistory.Add(now);
                o.Set("applied_road_add_actions", applied).Set("failed", failed).Set("weighted_score", Math.Round(now, 2))
                 .Set("note", "road_add actions were applied to the draft; include them (and the road_remove actions, in Clean-up) in your phases");
            }
            string text = Json.Write(o);
            if (text.Length > 80000) text = text.Substring(0, 80000) + "…";
            return ToolOutcome.Ok(text);
        }

        /// <summary>
        /// Groups road runs that lie within <paramref name="gap"/> tiles of each other into one action
        /// (up to <paramref name="maxTiles"/> tiles), so a network is a handful of actions, not one per run.
        /// </summary>
        private static List<List<Cell>> Clusters(List<List<Cell>> runs, int gap, int maxTiles)
        {
            var result = new List<List<Cell>>();
            var left = runs.OrderByDescending(r => r.Count).ToList();
            while (left.Count > 0)
            {
                var group = new List<Cell>(left[0]);
                left.RemoveAt(0);
                bool grew = true;
                while (grew && group.Count < maxTiles)
                {
                    grew = false;
                    for (int i = 0; i < left.Count && group.Count < maxTiles; i++)
                    {
                        var run = left[i];
                        if (group.Count + run.Count > maxTiles) continue;
                        bool near = run.Any(a => group.Any(b => Math.Abs(a.X - b.X) <= gap && Math.Abs(a.Z - b.Z) <= gap));
                        if (!near) continue;
                        group.AddRange(run);
                        left.RemoveAt(i--);
                        grew = true;
                    }
                }
                result.Add(group);
            }
            return result;
        }

        /// <summary>Splits a tile set into connected runs, each ordered as a walkable path where possible.</summary>
        private static List<List<Cell>> Segments(List<Cell> tiles)
        {
            var left = new HashSet<Cell>(tiles);
            var result = new List<List<Cell>>();
            while (left.Count > 0)
            {
                // start at an end tile (fewest neighbours) for a clean ordering
                var start = left.OrderBy(c => DraftLayout.Neighbours(c).Count(left.Contains)).ThenBy(c => c.Z).ThenBy(c => c.X).First();
                var seg = new List<Cell>();
                var stack = new Stack<Cell>();
                stack.Push(start);
                left.Remove(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    seg.Add(c);
                    foreach (var nb in DraftLayout.Neighbours(c))
                        if (left.Remove(nb)) stack.Push(nb);
                    if (seg.Count >= 200) break;
                }
                foreach (var c in stack) left.Add(c);
                result.Add(seg);
            }
            return result;
        }

        private ToolOutcome PathDistance(JObject input)
        {
            Cell a, b;
            if (!TryPoint(input["a"], out a) || !TryPoint(input["b"], out b)) return ToolOutcome.Error("a and b must be [x,z] or building ids");
            var planner = new RoadPlanner(Draft, _s);
            List<Cell> path;
            double d = planner.PathDistance(a, b, Draft.Roads, out path);
            if (d < 0) return ToolOutcome.Ok(new JObject().Set("reachable", false));
            int onRoad = path.Count(Draft.Roads.Contains);
            return ToolOutcome.Ok(new JObject().Set("reachable", true).Set("walk_cost", Math.Round(d, 1)).Set("tiles", path.Count)
                .Set("tiles_on_road", onRoad).Set("straight_line", Math.Round(a.DistanceTo(b), 1)));
        }

        private ToolOutcome Coverage(JObject input)
        {
            string svc = input.Str("service_type");
            var services = Draft.LiveBuildings.Where(b => string.Equals(b.ServiceType, svc, StringComparison.OrdinalIgnoreCase) && b.ServiceRadius > 0).ToList();
            var houses = Draft.LiveBuildings.Where(b => b.Category == "housing").ToList();
            var covered = new JArray(); var uncovered = new JArray();
            foreach (var h in houses)
            {
                var by = services.Where(s => s.Center.DistanceTo(h.Center) <= s.ServiceRadius).Select(s => s.Id).ToList();
                if (by.Count > 0) covered.Add(new JObject().Set("house", h.Id).Set("by", by.ToJArray()));
                else uncovered.Add(new JObject().Set("house", h.Id).Set("pos", h.Center.ToJson())
                    .Set("nearest", services.Count == 0 ? null : (object)Math.Round(services.Min(s => s.Center.DistanceTo(h.Center)), 1)));
            }
            return ToolOutcome.Ok(new JObject().Set("service_type", svc).Set("providers", services.Count)
                .Set("houses", houses.Count).Set("covered", covered.Count).Set("uncovered_count", uncovered.Count).Set("uncovered", uncovered)
                .Set("radius", services.Count > 0 ? (object)services[0].ServiceRadius : _snap.Knowledge.Rules.FirstOrDefault(r => r.ServiceType == svc)?.ServiceRadius));
        }

        private ToolOutcome EvaluateRotation(JObject input)
        {
            FieldInfo field;
            Rect rect;
            if (input["field"] is string fid)
            {
                field = Draft.Fields.FirstOrDefault(f => f.Id == fid);
                if (field == null) return ToolOutcome.Error("unknown field " + fid);
            }
            else if (Rect.TryParse(input["field"], out rect))
                field = new FieldInfo { Id = "proposed", Rect = rect, Fertility = Draft.AverageFertility(rect) };
            else return ToolOutcome.Error("field must be an id or [x,z,w,h]");
            var rot = new List<List<string>>();
            foreach (var y in input.Arr("rotation") ?? new JArray()) rot.Add(y is JArray ya ? ya.Strings() : new List<string> { Convert.ToString(y) });
            if (rot.Count != 3) return ToolOutcome.Error("rotation must list 3 years");
            var neighbours = Draft.Fields.Where(f => f.Id != field.Id && f.Rect.Center.DistanceTo(field.Rect.Center) < 16);
            return ToolOutcome.Ok(RotationEvaluator.Evaluate(field, rot, _snap.Knowledge, neighbours).ToJson());
        }

        private ToolOutcome ScoreDraft()
        {
            var card = new Scorer(_s).Score(Draft);
            _scoreHistory.Add(card.Weighted);
            return ToolOutcome.Ok(card.ToJson().Set("baseline_weighted", Math.Round(BaselineScore, 2))
                .Set("gain", Math.Round(card.Weighted - BaselineScore, 2)).Set("draft", Draft.Summary()));
        }

        private ToolOutcome ForecastStock(JObject input)
        {
            var phases = new List<Phase>();
            int n = 0;
            foreach (var p in input.Arr("phases").Objects())
            {
                var ph = Phase.FromJson(p);
                if (ph.N == 0) ph.N = ++n;
                phases.Add(ph);
            }
            // fill missing estimates exactly as submit_plan will
            var tmp = new ArchitectPlan { Phases = phases };
            var v = new PlanValidator(_s, _foodMonths, _woodMonths).Validate(tmp, _snap);
            var fc = new JArray(); foreach (var f in v.Forecast) fc.Add(f.ToJson());
            return ToolOutcome.Ok(new JObject().Set("forecast", fc)
                .Set("thresholds", new JObject().Set("food_months", _foodMonths).Set("firewood_months", _woodMonths))
                .Set("mode", _s.Mode));
        }

        // ------------------------------------------------------------------ zones

        private ToolOutcome SetZones(JObject input)
        {
            var list = new List<Zone>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var g = Draft.Grid;
            foreach (var zo in input.Arr("zones").Objects())
            {
                var z = Zone.FromJson(zo);
                if (string.IsNullOrWhiteSpace(z.Name)) return ToolOutcome.Error("every zone needs a name");
                if (!names.Add(z.Name)) return ToolOutcome.Error("zone name '" + z.Name + "' is used twice");
                if (!Zone.Purposes.Contains(z.Purpose)) return ToolOutcome.Error("zone " + z.Name + ": purpose must be one of " + string.Join(", ", Zone.Purposes));
                if (z.Rect.W < 3 || z.Rect.H < 3) return ToolOutcome.Error("zone " + z.Name + ": rect must be [x,z,w,h] with w and h of at least 3");
                int x0 = Math.Max(0, z.Rect.X), z0 = Math.Max(0, z.Rect.Z);
                int x1 = Math.Min(g.Width - 1, z.Rect.MaxX), z1 = Math.Min(g.Height - 1, z.Rect.MaxZ);
                if (x1 < x0 || z1 < z0) return ToolOutcome.Error("zone " + z.Name + " is outside the map");
                z.Rect = new Rect(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
                list.Add(z);
            }
            if (list.Count == 0) return ToolOutcome.Error("give at least one zone");
            Zones.Clear();
            Zones.AddRange(list);
            var report = new JArray();
            foreach (var z in Zones) report.Add(ZoneReport(z));
            return ToolOutcome.Ok(new JObject().Set("zones", report).Set("zone_fit", ZoneFit())
                .Set("next", "fill each zone with place_group (largest district first), then route_network apply=true"));
        }

        private JObject ZoneReport(Zone z)
        {
            var g = Draft.Grid;
            int land = 0, free = 0, water = 0;
            double fert = 0, slope = 0; int fertN = 0;
            foreach (var c in z.Rect.Cells())
            {
                int i = g.Idx(c);
                if (g.Terrain[i] == Terrain.Water) { water++; continue; }
                if (g.Terrain[i] != Terrain.Land) continue;
                land++;
                slope += g.Slope[i];
                if (!float.IsNaN(g.Fertility[i])) { fert += g.Fertility[i]; fertN++; }
                if (Draft.OwnerAt(c) == null) free++;
            }
            var inside = Draft.LiveBuildings.Where(b => z.Rect.Contains(b.Center) && b.Category != "wall")
                .GroupBy(b => b.Category).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Aggregate(new JObject(), (o, grp) => o.Set(grp.Key, grp.Count()));
            return z.ToJson().Set("land_tiles", land).Set("free_tiles", free).Set("water_tiles", water)
                .Set("avg_fertility", fertN == 0 ? null : (object)Math.Round(fert / fertN, 2))
                .Set("avg_slope_deg", land == 0 ? null : (object)Math.Round(slope / land, 1))
                .Set("buildings_inside", inside)
                .Set("fields_inside", Draft.Fields.Count(f => z.Rect.Contains(f.Rect.Center)));
        }

        /// <summary>Share of buildings that stand in a zone meant for their category (zones of purpose mixed accept all).</summary>
        private JObject ZoneFit()
        {
            if (Zones.Count == 0) return new JObject().Set("note", "no zones set");
            int total = 0, fit = 0;
            var misplaced = new JArray();
            foreach (var b in Draft.LiveBuildings)
            {
                if (b.Category == "wall" || b.Category == "gate" || b.Category == "town_center") continue;
                var zone = Zones.FirstOrDefault(z => z.Rect.Contains(b.Center));
                if (zone == null) continue;
                total++;
                var cats = Zone.Categories(zone.Purpose);
                if (cats == null || cats.Contains(b.Category)) fit++;
                else if (misplaced.Count < 25) misplaced.Add(b.Id + " (" + b.Category + ") in " + zone.Name + " (" + zone.Purpose + ")");
            }
            return new JObject().Set("buildings_in_zones", total).Set("fitting", fit)
                .Set("fit_ratio", total == 0 ? null : (object)Math.Round((double)fit / total, 2)).Set("misplaced", misplaced);
        }

        // ------------------------------------------------------------------ district placement

        private sealed class GroupEntry
        {
            public string Op;          // build | relocate
            public string Target;      // type for build, id for relocate
            public string Type;
            public string Role = "member";
            public BuildingSpec Spec;
            public int PreferredRot = -1;
        }

        private JObject Template(string name) => string.IsNullOrEmpty(name) ? null : _snap.Knowledge.Districts.Obj(name);

        private static bool Matches(JObject tpl, string key, string type, string category)
        {
            foreach (var m in tpl?.Arr(key)?.Strings() ?? new List<string>())
            {
                if (string.Equals(m, type, StringComparison.OrdinalIgnoreCase) || string.Equals(m, category, StringComparison.OrdinalIgnoreCase)) return true;
                if (m.StartsWith("category:", StringComparison.OrdinalIgnoreCase) && string.Equals(m.Substring(9), category, StringComparison.OrdinalIgnoreCase)) return true;
                if (m.EndsWith("*") && type != null && type.StartsWith(m.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private ToolOutcome PlaceGroup(JObject input, CancellationToken cancel)
        {
            Rect area;
            Zone zone = null;
            string zoneName = input.Str("zone");
            if (!string.IsNullOrEmpty(zoneName))
            {
                zone = Zones.FirstOrDefault(z => string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase));
                if (zone == null) return ToolOutcome.Error("unknown zone '" + zoneName + "'" + (Zones.Count == 0 ? "; call set_zones first" : "; zones: " + string.Join(", ", Zones.Select(z => z.Name))));
                area = zone.Rect;
            }
            else if (!Rect.TryParse(input["area"], out area)) return ToolOutcome.Error("give zone (a name from set_zones) or area [x,z,w,h]");

            var districts = _snap.Knowledge.Districts;
            string tplName = input.Str("template");
            if (string.IsNullOrEmpty(tplName) && zone != null)
                tplName = districts.Keys.FirstOrDefault(k => string.Equals(districts.Obj(k)?.Str("purpose"), zone.Purpose, StringComparison.OrdinalIgnoreCase) && districts.Obj(k).Bool("default"));
            if (!string.IsNullOrEmpty(tplName) && Template(tplName) == null)
                return ToolOutcome.Error("unknown template '" + tplName + "'; templates: " + string.Join(", ", districts.Keys));
            bool apply = !input.ContainsKey("apply") || input.Bool("apply");

            // ---- what to place
            var entries = new List<GroupEntry>();
            var problems = new JArray();
            var k = _snap.Knowledge;
            var taken = new HashSet<string>();
            foreach (var item in input.Arr("items").Objects())
            {
                int rot = item.ContainsKey("rot") ? item.Int("rot") & 3 : -1;
                var ids = new List<string>();
                var mv = item["move"];
                if (mv is string one) ids.Add(one);
                else if (mv is JArray many) ids.AddRange(many.Strings());
                string moveType = item.Str("move_type");
                if (moveType != null)
                {
                    int n = Math.Max(1, item.Int("count", 1000));
                    var c = area.Center;
                    ids.AddRange(Draft.LiveBuildings.Where(b => string.Equals(b.Type, moveType, StringComparison.OrdinalIgnoreCase) && !b.Locked && !_s.LockedIds.Contains(b.Id) && !taken.Contains(b.Id))
                        .OrderByDescending(b => b.Center.DistanceTo(c)).Take(n).Select(b => b.Id));
                    if (ids.Count == 0) problems.Add("no movable " + moveType + " found");
                }
                foreach (var id in ids)
                {
                    BuildingInfo b;
                    if (!Draft.Buildings.TryGetValue(id, out b) || Draft.Demolished.Contains(id)) { problems.Add("unknown building " + id); continue; }
                    if (!taken.Add(b.Id)) continue;
                    entries.Add(new GroupEntry { Op = Ops.Relocate, Target = b.Id, Type = b.Type, Spec = k.Spec(b.Type), PreferredRot = rot });
                }
                string type = item.Str("type");
                if (type != null)
                {
                    int n = Math.Max(1, Math.Min(60, item.Int("count", 1)));
                    if (!k.Buildings.ContainsKey(type)) { problems.Add("unknown building type " + type); continue; }
                    for (int i = 0; i < n; i++) entries.Add(new GroupEntry { Op = Ops.Build, Target = type, Type = type, Spec = k.Spec(type), PreferredRot = rot });
                }
            }
            if (entries.Count == 0) return ToolOutcome.Error("nothing to place" + (problems.Count > 0 ? ": " + string.Join("; ", problems.Strings()) : "; items need type+count, move or move_type"));
            if (entries.Count > 150) return ToolOutcome.Error("at most 150 buildings per call (got " + entries.Count + "); split the district");

            if (string.IsNullOrEmpty(tplName))
            {
                // no zone and no template: the default template for what is mostly being placed
                string purpose = PurposeOf(entries.GroupBy(e => e.Spec.Category).OrderByDescending(x => x.Count()).First().Key);
                tplName = districts.Keys.FirstOrDefault(k2 => string.Equals(districts.Obj(k2)?.Str("purpose"), purpose, StringComparison.OrdinalIgnoreCase) && districts.Obj(k2).Bool("default"));
            }
            var tpl = Template(tplName);
            int spacing = Math.Max(0, Math.Min(3, input.Int("spacing", tpl?.Int("spacing", 1) ?? 1)));
            foreach (var e in entries)
            {
                string cat = e.Spec.Category;
                e.Role = Matches(tpl, "hub", e.Type, cat) ? "hub" : Matches(tpl, "edge", e.Type, cat) ? "edge" : Matches(tpl, "away", e.Type, cat) ? "away" : "member";
            }
            int RoleOrder(string r) => r == "hub" ? 0 : r == "member" ? 1 : r == "edge" ? 2 : 3;
            entries = entries.OrderBy(e => RoleOrder(e.Role)).ThenByDescending(e => e.Spec.W * e.Spec.H).ToList();

            // ---- reference points
            Cell anchor;
            if (!TryPoint(input["anchor"], out anchor)) anchor = area.Center;
            var tc = Draft.LiveBuildings.FirstOrDefault(b => b.Category == "town_center");
            var edgePoint = tc == null ? anchor : new Cell(Math.Max(area.X, Math.Min(area.MaxX, tc.Center.X)), Math.Max(area.Z, Math.Min(area.MaxZ, tc.Center.Z)));
            var houses = Draft.LiveBuildings.Where(b => b.Category == "housing" && !area.Contains(b.Center)).ToList();
            Cell? housingCentre = houses.Count == 0 ? (Cell?)null : new Cell((int)houses.Average(h => h.Center.X), (int)houses.Average(h => h.Center.Z));

            var target = apply ? Draft : Draft.Fork(_s);
            double before = new Scorer(_s).Score(target).Weighted;
            string objective = input.Str("objective") ?? ObjectiveFor(zone?.Purpose ?? tpl?.Str("purpose"));
            string rationale = input.Str("rationale") ?? ((tplName ?? "district") + " layout in " + (zone?.Name ?? area.ToString()));
            int group = ++_groupCounter;
            var hubs = new List<BuildingInfo>();
            var placed = new JArray();
            var kept = new JArray();
            var unplaced = new JArray();
            var pending = entries;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int seq = 0;
            // relocations inside a packed district can block each other: retry the leftovers twice
            for (int pass = 0; pass < 3 && pending.Count > 0; pass++)
            {
                var retry = new List<GroupEntry>();
                foreach (var e in pending)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (sw.Elapsed.TotalSeconds > 25) { retry.Add(e); continue; }
                    string why;
                    var act = PlaceOne(target, e, area, spacing, anchor, edgePoint, housingCentre, hubs, out why);
                    if (act == null)
                    {
                        if (why == "kept") { kept.Add(e.Target); var kb = target.Buildings[e.Target]; if (e.Role == "hub") hubs.Add(kb); continue; }
                        if (pass < 2) retry.Add(e); else unplaced.Add(new JObject().Set("item", e.Target).Set("reason", why));
                        continue;
                    }
                    act.Id = "g" + group + "_" + (++seq);
                    act.Objective = objective;
                    act.Rationale = rationale;
                    var r = target.Apply(act, _s);
                    if (!r.Valid) { if (pass < 2) retry.Add(e); else unplaced.Add(new JObject().Set("item", e.Target).Set("reason", string.Join("; ", r.Reasons))); continue; }
                    string newId = act.Op == Ops.Build ? r.NewId : act.Target;
                    if (e.Role == "hub" && newId != null && target.Buildings.ContainsKey(newId)) hubs.Add(target.Buildings[newId]);
                    placed.Add(new JObject().Set("id", act.Id).Set("op", act.Op).Set("target", act.Target).Set("building", newId).Set("role", e.Role)
                        .Set("rect", r.Footprint?.ToJson()));
                }
                if (sw.Elapsed.TotalSeconds > 25 && retry.Count > 0)
                {
                    foreach (var e in retry) unplaced.Add(new JObject().Set("item", e.Target).Set("reason", "time limit for one call; place the rest with another place_group"));
                    retry.Clear();
                }
                pending = retry;
            }
            double after = new Scorer(_s).Score(target).Weighted;
            if (apply) { _applyCalls++; _scoreHistory.Add(after); }
            var o = new JObject().Set("applied", apply).Set("template", tplName).Set("area", area.ToJson()).Set("spacing", spacing)
                .Set("placed", placed).Set("kept_in_place", kept).Set("unplaced", unplaced)
                .Set("weighted_before", Math.Round(before, 2)).Set("weighted_after", Math.Round(after, 2)).Set("delta", Math.Round(after - before, 2))
                .Set("relocations_used", target.RelocationCount).Set("relocation_limit", _s.MaxRelocations < 0 ? "unlimited" : (object)_s.MaxRelocations);
            if (problems.Count > 0) o.Set("problems", problems);
            if (Zones.Count > 0) o.Set("zone_fit", ZoneFit());
            if (apply && after < before - 0.01) o.Set("hint", "the group lowers the score; undo_draft removes its actions one by one, or try another area/template");
            else o.Set("hint", "next: the other districts, then route_network apply=true, then submit_plan auto_phases=true");
            string text = Json.Write(o);
            if (text.Length > 60000) text = text.Substring(0, 60000) + "…";
            return ToolOutcome.Ok(text);
        }

        private static string PurposeOf(string category)
        {
            switch (category)
            {
                case "housing": return "housing";
                case "storage": return "storage";
                case "production":
                case "resource": return "industry";
                case "farm":
                case "food": return "farming";
                case "defence":
                case "wall":
                case "gate": return "defence";
                default: return "services";
            }
        }

        private static string ObjectiveFor(string purpose)
        {
            switch (purpose)
            {
                case "farming": return "O1";
                case "industry":
                case "storage": return "O2";
                case "housing":
                case "services": return "O3";
                case "defence": return "O5";
                default: return "O2";
            }
        }

        /// <summary>
        /// Best position in the area for one entry, by its template role: hubs nearest the anchor,
        /// members nearest a hub (inside its service radius when possible), edge buildings nearest the
        /// side facing the Town Center, polluters farthest from the village's housing. Null with
        /// why = "kept" when a relocated building already stands about as well as it could.
        /// </summary>
        private PlanAction PlaceOne(DraftLayout d, GroupEntry e, Rect area, int spacing, Cell anchor, Cell edgePoint, Cell? housing,
            List<BuildingInfo> hubs, out string why)
        {
            why = "no free site in the area";
            int w = e.Spec.W, h = e.Spec.H;
            BuildingInfo self = null;
            if (e.Op == Ops.Relocate) { d.Buildings.TryGetValue(e.Target, out self); if (self != null) { w = self.W; h = self.H; } }
            int sp = e.Spec.Category == "decoration" ? 0 : spacing;
            Func<Rect, double> score = rect =>
            {
                var c = rect.Center;
                switch (e.Role)
                {
                    case "hub": return c.DistanceTo(anchor);
                    case "edge": return c.DistanceTo(edgePoint);
                    case "away": return housing.HasValue ? -c.DistanceTo(housing.Value) : c.DistanceTo(anchor);
                    default:
                        if (hubs.Count == 0) return c.DistanceTo(anchor);
                        double best = double.MaxValue;
                        foreach (var hb in hubs)
                        {
                            double dist = c.DistanceTo(hb.Center);
                            if (hb.ServiceRadius > 0 && dist > hb.ServiceRadius) dist += 40; // outside the hub's catchment
                            best = Math.Min(best, dist);
                        }
                        return best;
                }
            };
            var rots = e.PreferredRot >= 0 ? new[] { e.PreferredRot } : new[] { 0, 1, 2, 3 };
            var cands = new List<KeyValuePair<double, int[]>>();
            foreach (int rot in rots)
            {
                int rw = (rot & 1) == 1 ? h : w, rh = (rot & 1) == 1 ? w : h;
                for (int z = area.Z; z + rh - 1 <= area.MaxZ; z++)
                    for (int x = area.X; x + rw - 1 <= area.MaxX; x++)
                        cands.Add(new KeyValuePair<double, int[]>(score(new Rect(x, z, rw, rh)) + rot * 0.001, new[] { x, z, rot }));
            }
            cands.Sort((a, b) => a.Key.CompareTo(b.Key));
            double current = double.MaxValue;
            if (self != null && area.Contains(self.Center)) current = score(self.Footprint);
            int checks = 0;
            foreach (var cand in cands)
            {
                if (self != null && cand.Key >= current - 1.5) { why = "kept"; return null; }
                if (++checks > 4000) { why = "no valid site among the 4000 best positions"; break; }
                int x = cand.Value[0], z = cand.Value[1], rot = cand.Value[2];
                int rw = (rot & 1) == 1 ? h : w, rh = (rot & 1) == 1 ? w : h;
                var rect = new Rect(x, z, rw, rh);
                if (sp > 0 && !Clearance(d, rect, sp, e.Target)) continue;
                var a = new PlanAction { Op = e.Op, Target = e.Target, Params = new JObject().Set("x", x).Set("z", z).Set("rot", rot) };
                var r = d.Check(a, _s);
                if (!r.Valid)
                {
                    // limits are not position problems: stop at once
                    var reason = string.Join("; ", r.Reasons);
                    if (reason.Contains("limit") || reason.Contains("locked") || reason.Contains("ambition") || reason.Contains("bound to") || reason.Contains("not allowed")) { why = reason; return null; }
                    continue;
                }
                return a;
            }
            if (self != null && current < double.MaxValue) { why = "kept"; return null; }
            return null;
        }

        /// <summary>No other building within <paramref name="ring"/> tiles of the rect (roads and fields are fine).</summary>
        private static bool Clearance(DraftLayout d, Rect rect, int ring, string self)
        {
            for (int i = 1; i <= ring; i++)
                foreach (var c in DraftLayout.Border(rect, i))
                {
                    var owner = d.OwnerAt(c);
                    if (owner == null || owner == self || owner.StartsWith("field:")) continue;
                    if (owner == "edge") return false;
                    BuildingInfo b;
                    if (d.Buildings.TryGetValue(owner, out b) && (b.Category == "wall" || b.Category == "gate")) continue;
                    return false;
                }
            return true;
        }

        // ------------------------------------------------------------------ submit

        private static string StageName(Stage st)
        {
            switch (st)
            {
                case Stage.Settings: return "Settings";
                case Stage.Prepare: return "Prepare";
                case Stage.AddCapacity: return "Add capacity";
                case Stage.StorageLogistics: return "Storage and logistics";
                case Stage.ProductionChains: return "Production chains";
                case Stage.Farming: return "Farming";
                case Stage.Residential: return "Residential and services";
                default: return "Clean-up";
            }
        }

        /// <summary>
        /// Phases from draft actions: by standard stage order (byStage) or in draft order, split at
        /// the max actions per phase and wherever the offline cap for a tier 1-2 type would be passed.
        /// Road actions stay with the district they follow.
        /// </summary>
        private List<Phase> AutoPhases(List<PlanAction> actions, bool byStage)
        {
            var k = _snap.Knowledge;
            var keyed = new List<Tuple<Stage, int, PlanAction>>();
            Stage last = Stage.Prepare;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                Stage st;
                if (a.Op == Ops.RoadRemove) st = Stage.CleanUp;
                else if (a.Op == Ops.RoadAdd) st = byStage ? Stage.Prepare : last;
                else { st = a.Stage(k, _snap); last = st; }
                keyed.Add(Tuple.Create(st, i, a));
            }
            var ordered = byStage ? keyed.OrderBy(t => t.Item1).ThenBy(t => t.Item2).ToList() : keyed;
            var totals = Draft.Snapshot.Buildings.GroupBy(b => b.Type).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var phases = new List<Phase>();
            Phase cur = null;
            Stage curStage = 0;
            var offline = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perStage = new Dictionary<Stage, int>();
            foreach (var t in ordered)
            {
                var a = t.Item3;
                bool split = cur == null || cur.Actions.Count >= _s.PhaseMaxActions || (byStage && t.Item1 != curStage);
                string offType = null;
                if ((a.Op == Ops.Relocate || a.Op == Ops.Demolish) && a.Target != null)
                {
                    var b = _snap.Building(a.Target);
                    if (b != null && k.IsTierOneTwo(b.Type))
                    {
                        offType = b.Type;
                        int total; totals.TryGetValue(b.Type, out total);
                        int allowed = Math.Max(1, (int)Math.Floor(_s.OfflineCap * total));
                        int n; offline.TryGetValue(b.Type, out n);
                        if (!split && n + 1 > allowed) split = true;
                    }
                }
                if (split)
                {
                    curStage = t.Item1;
                    int idx; perStage.TryGetValue(curStage, out idx); perStage[curStage] = ++idx;
                    cur = new Phase { Name = StageName(curStage) + (idx > 1 ? " " + idx : ""), Goal = "" };
                    phases.Add(cur);
                    offline.Clear();
                }
                cur.Actions.Add(a);
                if (offType != null) { int n; offline.TryGetValue(offType, out n); offline[offType] = n + 1; }
            }
            if (!byStage)
                foreach (var p in phases)
                {
                    var main = p.Actions.Where(a => a.Op != Ops.RoadAdd && a.Op != Ops.RoadRemove).Select(a => a.Stage(k, _snap)).DefaultIfEmpty(Stage.Prepare)
                        .GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                    p.Name = StageName(main) + " (draft order)";
                }
            return phases;
        }

        private static PlanAction Clone(PlanAction a) => PlanAction.FromJson(a.ToJson());

        private ToolOutcome SubmitPlan(JObject input)
        {
            bool auto = input.Bool("auto_phases");
            var newActions = Draft.Applied.Skip(_priorActions).Select(Clone).ToList();
            // resolve "action_ids" against the applied draft actions
            if (!auto)
            {
                var phasesIn = input.Arr("phases");
                if (phasesIn == null || phasesIn.Count == 0) return ToolOutcome.Error("give phases, or auto_phases: true to let the mod build them");
                var byId = new Dictionary<string, PlanAction>(StringComparer.Ordinal);
                foreach (var a in Draft.Applied) byId[a.Id] = a;
                foreach (var p in phasesIn.Objects())
                {
                    var ids = p.Arr("action_ids");
                    if (ids == null || ids.Count == 0) continue;
                    var acts = p.Arr("actions") ?? new JArray();
                    foreach (var id in ids.Strings())
                    {
                        PlanAction a;
                        if (!byId.TryGetValue(id, out a)) return ToolOutcome.Error("action_ids: '" + id + "' is not an applied draft action");
                        acts.Add(a.ToJson());
                    }
                    p.Set("actions", acts);
                }
            }
            ArchitectPlan plan;
            try { plan = ArchitectPlan.FromJson(input); }
            catch (Exception e) { return ToolOutcome.Error("could not read the plan: " + e.Message); }
            plan.Ambition = _s.Ambition;
            plan.Zones = new List<Zone>(Zones);
            if (auto)
            {
                if (newActions.Count == 0 && _priorPhases.Count == 0) return ToolOutcome.Error("the draft has no actions to phase");
                plan.Phases = AutoPhases(newActions, true);
            }
            var prior = _priorPhases.Select(p => Phase.FromJson(p.ToJson())).ToList();
            plan.Phases.Sort((a, b) => a.N.CompareTo(b.N));
            plan.Phases.InsertRange(0, prior);
            for (int i = 0; i < plan.Phases.Count; i++) plan.Phases[i].N = i + 1;

            var validator = new PlanValidator(_s, _foodMonths, _woodMonths);
            var v = validator.Validate(plan, _snap);
            if (!v.Ok && auto)
            {
                // the standard order can break dependencies between districts: keep the draft order instead
                var alt = ArchitectPlan.FromJson(plan.ToJson());
                alt.Zones = plan.Zones;
                alt.Phases = prior.Select(p => Phase.FromJson(p.ToJson())).ToList();
                alt.Phases.AddRange(AutoPhases(newActions.Select(Clone).ToList(), false));
                for (int i = 0; i < alt.Phases.Count; i++) alt.Phases[i].N = i + 1;
                var v2 = new PlanValidator(_s, _foodMonths, _woodMonths) { StageOrderIsWarning = true }.Validate(alt, _snap);
                if (v2.Ok)
                {
                    v2.Warnings.Insert(0, "auto-phases keep the draft order (the standard stage order would break: " + string.Join("; ", v.Errors.Take(3)) + ")");
                    plan = alt; v = v2;
                }
            }
            LastValidation = v;
            if (!v.Ok)
            {
                var o = new JObject().Set("accepted", false).Set("errors", v.Errors.Take(30).ToJArray())
                    .Set("warnings", v.Warnings.Take(15).ToJArray())
                    .Set("hint", auto
                        ? "Fix the draft (undo_draft, place_group, route_network) and submit again with auto_phases: true."
                        : "Fix the errors (use check_placement / route_network) and call submit_plan again with the whole plan.");
                return new ToolOutcome { Content = Json.Write(o), IsError = true };
            }
            plan.Warnings = v.Warnings.Take(50).ToList();
            AcceptedPlan = plan;
            return new ToolOutcome
            {
                Content = Json.Write(new JObject().Set("accepted", true).Set("phases", plan.Phases.Count).Set("actions", plan.AllActions().Count())
                    .Set("score_before", plan.ScoreBefore).Set("score_after", plan.ScoreAfter)),
                Finished = true,
                Payload = new KeyValuePair<ArchitectPlan, PlanValidation>(plan, v)
            };
        }
    }
}
