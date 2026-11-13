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
            "route_network", "path_distance", "coverage", "evaluate_rotation", "score_draft", "forecast_stock", "submit_plan"
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

        public ArchitectToolHost(VillageSnapshot snapshot, RunSettings settings, IEnumerable<string> enabledTools,
            IDictionary<string, string> descriptionOverrides, double foodMonths, double woodMonths,
            double stopIfGainBelow = 0.5, int gainWindowTurns = 10)
        {
            _snap = snapshot;
            _s = settings;
            _foodMonths = foodMonths;
            _woodMonths = woodMonths;
            _stopIfGainBelow = stopIfGainBelow;
            _gainWindow = Math.Max(2, gainWindowTurns);
            Draft = new DraftLayout(snapshot);
            BaselineScore = new Scorer(_s).Score(Draft).Weighted;
            _scoreHistory.Add(BaselineScore);
            var enabled = new HashSet<string>(enabledTools ?? AllToolNames);
            enabled.Add("submit_plan");
            foreach (var spec in Specs())
            {
                if (!enabled.Contains(spec.Name)) continue;
                string d;
                if (descriptionOverrides != null && descriptionOverrides.TryGetValue(spec.Name, out d) && !string.IsNullOrWhiteSpace(d)) spec.Description = d.Trim();
                _tools.Add(spec);
            }
        }

        public IList<ToolSpec> Tools => _tools;

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
                new ToolSpec { Name = "submit_plan", Description =
                    "Submit the final scenario. The mod re-validates every action, the phase order, the offline cap, seasons, road connectivity and (Native) stocks. " +
                    "If anything fails you get the errors back and can fix and resubmit.",
                    InputSchema = S.Obj(S.Props(
                        "scenario", S.Obj(S.Props("ambition", S.Int("1-5"), "summary", S.Str("two or three sentences")), "summary"),
                        "phases", S.Arr(S.Obj(S.Props(
                            "n", S.Int("phase number from 1"), "name", S.Str("short name"), "goal", S.Str("what the phase achieves"),
                            "season_window", S.Arr(S.Enum("slot", "spring", "summer", "early_autumn", "late_autumn", "winter"), "when it may start"),
                            "depends_on", S.Arr(S.Int("phase"), "phases that must finish first"),
                            "actions", S.Arr(action, "actions"),
                            "offline", S.Arr(S.Str("building id"), "buildings offline during the phase"),
                            "est_cost", S.AnyObj("item -> amount"), "est_days", S.Num("estimated game days")), "n", "name", "actions"), "phases")),
                        "scenario", "phases") },
            };
        }

        // ------------------------------------------------------------------ dispatch

        public ToolOutcome Execute(string name, JObject input, CancellationToken cancel)
        {
            var spec = _tools.FirstOrDefault(t => t.Name == name);
            if (spec == null) return ToolOutcome.Error("unknown or disabled tool '" + name + "'");
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
            if (v is string id)
            {
                BuildingInfo b;
                if (Draft.Buildings.TryGetValue(id, out b)) { c = new RoadPlanner(Draft, _s).Terminals().FirstOrDefault(t => t.Id == id)?.Cell ?? b.EntranceCell; return true; }
                var f = Draft.Fields.FirstOrDefault(x => x.Id == id);
                if (f != null) { c = f.Rect.Center; return true; }
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
            return new JObject().Set("summary", Draft.Summary()).Set("buildings", b).Set("fields", f).Set("applied", applied)
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
            var fresh = net.Tiles.Where(c => !Draft.Roads.Contains(c)).ToList();
            var addSegments = Segments(fresh);
            var actions = new JArray();
            int n = 0;
            foreach (var seg in addSegments)
            {
                var t = new JArray(); foreach (var c in seg) t.Add(c.ToJson());
                actions.Add(new JObject().Set("id", "net" + (++n)).Set("op", Ops.RoadAdd).Set("params", new JObject().Set("road_type", "dirt").Set("tiles", t))
                    .Set("objective", "O6").Set("rationale", "Network segment, " + seg.Count + " tiles"));
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

        private ToolOutcome SubmitPlan(JObject input)
        {
            ArchitectPlan plan;
            try { plan = ArchitectPlan.FromJson(input); }
            catch (Exception e) { return ToolOutcome.Error("could not read the plan: " + e.Message); }
            plan.Ambition = _s.Ambition;
            var v = new PlanValidator(_s, _foodMonths, _woodMonths).Validate(plan, _snap);
            LastValidation = v;
            if (!v.Ok)
            {
                var o = new JObject().Set("accepted", false).Set("errors", v.Errors.Take(30).ToJArray())
                    .Set("warnings", v.Warnings.Take(15).ToJArray())
                    .Set("hint", "Fix the errors (use check_placement / route_network) and call submit_plan again with the whole plan.");
                return new ToolOutcome { Content = Json.Write(o), IsError = true };
            }
            plan.Warnings = v.Warnings.Take(50).ToList();
            AcceptedPlan = plan;
            return new ToolOutcome
            {
                Content = Json.Write(new JObject().Set("accepted", true).Set("score_before", plan.ScoreBefore).Set("score_after", plan.ScoreAfter)),
                Finished = true,
                Payload = new KeyValuePair<ArchitectPlan, PlanValidation>(plan, v)
            };
        }
    }
}
