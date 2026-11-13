using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Execution;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;
using UnityEngine;
using Rect = FrontierArchitect.Core.Model.Rect;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Turns plan actions into game orders through the recipes in gamemap.json. Every op needs a
    /// validator (Core + ValidateLive), a Native recipe, an Instant recipe and a completion test
    /// ("verify"); an op missing one of them is reported unavailable and shows as Manual.
    /// </summary>
    internal sealed class GameExecutor : IGameExecutor
    {
        private readonly GameAccess _g;
        private readonly ModConfig _cfg;
        private readonly RecipeRunner _runner;
        public TileGrid Grid;
        public KnowledgePack Knowledge;
        private readonly Dictionary<string, float> _issuedAt = new Dictionary<string, float>();

        public GameExecutor(GameAccess g, ModConfig cfg)
        {
            _g = g; _cfg = cfg; _runner = new RecipeRunner(g.Map);
        }

        private GameMap Map => _g.Map;

        public string ExecuteBlockedReason()
        {
            string mapped = Map.MappedGameVersion;
            if (string.IsNullOrEmpty(mapped))
                return "Execute is off until this game build is mapped (milestone M0): open Settings > Diagnostics, run the probe and test the ops.";
            if (mapped != Application.version)
                return "Game version " + Application.version + " differs from the mapped version " + mapped + ". Execute is off until the mapping is updated.";
            return null;
        }

        public string OpUnavailableReason(string op, ExecMode mode)
        {
            switch (Map.Status(op))
            {
                case OpStatus.Dropped: return op + " is dropped for this game version";
                case OpStatus.Unmapped: return op + " is not mapped for this game version yet";
                case OpStatus.Experimental:
                    if (_cfg == null || !_cfg.AllowExperimentalOps) return op + " is experimental (enable experimental ops in Settings to use it)";
                    break;
            }
            if (Map.Recipe(op, mode == ExecMode.Instant) == null) return "no " + mode.ToString().ToLowerInvariant() + " recipe for " + op;
            if (Map.Verify(op) == null) return "no completion test for " + op;
            return null;
        }

        // ---------------------------------------------------------------- lookups

        private TileGrid G()
        {
            if (Grid != null) return Grid;
            Bounds mb;
            Ground.MapBounds(Map, _g, out mb);
            Grid = new TileGrid(1, 1) { TileSize = Map.TileSize, OriginX = mb.min.x, OriginZ = mb.min.z };
            return Grid;
        }

        public Component FindBuilding(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var grid = G();
            foreach (var c in _g.Buildings())
            {
                var b = GameAccess.WorldBounds(c);
                var min = grid.WorldToCell(b.min.x + 0.01f, b.min.z + 0.01f);
                string bid = GameAccess.BuildingId(_g.BuildingType(c), min.X, min.Z);
                if (bid == id || id.StartsWith(bid + "#")) return c;
            }
            return null;
        }

        /// <summary>A building of a type whose footprint corner is within tol tiles (world bounds round differently than the grid).</summary>
        private Component FindBuildingNear(string type, int x, int z, int tol)
        {
            var grid = G();
            foreach (var c in _g.Buildings())
            {
                if (!string.Equals(_g.BuildingType(c), type, StringComparison.OrdinalIgnoreCase)) continue;
                var b = GameAccess.WorldBounds(c);
                var min = grid.WorldToCell(b.min.x + 0.01f, b.min.z + 0.01f);
                if (Math.Abs(min.X - x) <= tol && Math.Abs(min.Z - z) <= tol) return c;
            }
            return null;
        }

        private Component FindField(string id)
        {
            var grid = G();
            foreach (var c in _g.ObjectsOf("Field"))
            {
                var b = GameAccess.WorldBounds(c);
                var min = grid.WorldToCell(b.min.x + 0.01f, b.min.z + 0.01f);
                if ("field@" + min.X + "," + min.Z == id) return c;
            }
            return null;
        }

        private Vector3 World(Cell c)
        {
            float x, z;
            G().CellCenterWorld(c, out x, out z);
            return new Vector3(x, Ground.Height(x, z), z);
        }

        private Vector3 FootprintCentre(PlanAction a, int w, int h)
        {
            var grid = G();
            bool swap = (a.Rot & 1) == 1;
            float fw = (swap ? h : w) * grid.TileSize, fh = (swap ? w : h) * grid.TileSize;
            float x = grid.OriginX + a.X * grid.TileSize + fw / 2f;
            float z = grid.OriginZ + a.Z * grid.TileSize + fh / 2f;
            return new Vector3(x, Ground.Height(x, z), z);
        }

        private Dictionary<string, object> Vars(PlanAction a, out string error)
        {
            error = null;
            var v = new Dictionary<string, object>();
            foreach (var kv in a.Params)
            {
                if (kv.Value is long l) v[kv.Key] = (int)l;
                else if (kv.Value is double d) v[kv.Key] = (float)d;
                else v[kv.Key] = kv.Value;
            }
            v["target"] = a.Target;
            v["item"] = a.Target;
            v["profession"] = a.Target;
            v["rot_deg"] = (float)(a.Rot * 90);
            v["rotation"] = Quaternion.Euler(0, a.Rot * 90, 0);
            if (a.Params.ContainsKey("count")) v["count"] = a.Params.Int("count");
            if (a.Params.ContainsKey("limit")) v["limit"] = a.Params.Int("limit");
            if (a.Params.ContainsKey("paused")) v["paused"] = a.Params.Bool("paused");

            switch (a.Op)
            {
                case Ops.SetWorkers:
                case Ops.SetPaused:
                case Ops.SetWorkCircle:
                case Ops.Demolish:
                case Ops.Relocate:
                    {
                        var b = FindBuilding(a.Target);
                        if (b == null) { error = "building " + a.Target + " not found in the live village"; return null; }
                        v["building"] = b;
                        v["building_object"] = b.gameObject;
                        if (a.Op == Ops.SetWorkCircle) v["world_pos"] = World(new Cell(a.X, a.Z));
                        if (a.Op == Ops.Relocate)
                        {
                            var spec = Knowledge?.Spec(_g.BuildingType(b));
                            v["world_pos"] = FootprintCentre(a, spec?.W ?? 2, spec?.H ?? 2);
                        }
                        break;
                    }
                case Ops.Build:
                    {
                        var spec = Knowledge?.Spec(a.Target);
                        v["world_pos"] = FootprintCentre(a, spec?.W ?? 2, spec?.H ?? 2);
                        object data = null;
                        foreach (var bd in Reflect.Items(_g.Member(Map.ResolveType("GlobalAssets"), "global.building_data")))
                            if (string.Equals((Reflect.Text(_g.Member(bd, "building_data.identifier")) ?? "").Replace(" ", ""), a.Target, StringComparison.OrdinalIgnoreCase)) { data = bd; break; }
                        if (data == null) { error = "no building data for " + a.Target; return null; }
                        v["building_data"] = data;
                        v["prefab"] = Reflect.Get(Reflect.Items(_g.Member(data, "building_data.prefab_entries")).FirstOrDefault(), "prefab");
                        break;
                    }
                case Ops.SetRotation:
                case Ops.FieldRemove:
                case Ops.FieldResize:
                    {
                        var f = FindField(a.Target);
                        if (f == null) { error = "field " + a.Target + " not found"; return null; }
                        v["field"] = f;
                        v["crop_rotation"] = a.Rotation();
                        break;
                    }
            }
            Rect r;
            if (a.TryRect(out r))
            {
                var grid = G();
                v["rect_min"] = World(new Cell(r.X, r.Z)) - new Vector3(grid.TileSize / 2, 0, grid.TileSize / 2);
                v["rect_max"] = World(new Cell(r.MaxX, r.MaxZ)) + new Vector3(grid.TileSize / 2, 0, grid.TileSize / 2);
                v["rect_centre"] = World(r.Center);
            }
            var tiles = a.Tiles();
            if (tiles.Count > 0) v["tiles_world"] = tiles.Select(World).ToList();
            return v;
        }

        // ---------------------------------------------------------------- IGameExecutor

        public string ValidateLive(PlanAction a, ExecMode mode)
        {
            string err;
            var vars = Vars(a, out err);
            if (vars == null) return err;
            if (a.Op == Ops.Build && mode == ExecMode.Native && Knowledge != null)
            {
                var stock = _g.Stock();
                foreach (var kv in Knowledge.Spec(a.Target).Cost)
                {
                    double have; stock.TryGetValue(kv.Key, out have);
                    if (have < kv.Value) return "not enough " + kv.Key + " (" + Math.Round(have) + "/" + kv.Value + ")";
                }
            }
            return null;
        }

        public bool Issue(PlanAction a, ExecMode mode, out string error)
        {
            var why = OpUnavailableReason(a.Op, mode);
            if (why != null) { error = why; return false; }
            var vars = Vars(a, out error);
            if (vars == null) return false;
            bool ok = _runner.Run(Map.Recipe(a.Op, mode == ExecMode.Instant), vars, out error);
            if (ok) _issuedAt[a.Id] = Time.realtimeSinceStartup;
            return ok;
        }

        public OrderProgress Check(PlanAction a, ExecMode mode, out string note)
        {
            note = null;
            var verify = Map.Verify(a.Op);
            if (verify == null) { note = "no completion test"; return OrderProgress.Failed; }
            float started;
            _issuedAt.TryGetValue(a.Id, out started);
            double timeout = verify.Num("timeout_seconds", mode == ExecMode.Instant ? 20 : 3600 * 6);
            if (Time.realtimeSinceStartup - started > timeout) { note = "timed out waiting for completion"; return OrderProgress.Failed; }
            string builtin = verify.Str("builtin");
            switch (builtin)
            {
                case "immediate": return OrderProgress.Done;
                case "building_at_target":
                    {
                        string type = a.Op == Ops.Build ? a.Target : a.Target.Split('@')[0];
                        var c = FindBuildingNear(type, a.X, a.Z, 1);
                        if (c == null) return OrderProgress.Pending;
                        var uc = _g.Member(c, "building.under_construction");
                        return uc is bool u && u ? OrderProgress.Pending : OrderProgress.Done;
                    }
                case "building_gone": return FindBuilding(a.Target) == null ? OrderProgress.Done : OrderProgress.Pending;
                case "field_gone": return FindField(a.Target) == null ? OrderProgress.Done : OrderProgress.Pending;
                case "field_present":
                    {
                        Rect r;
                        if (!a.TryRect(out r)) return OrderProgress.Failed;
                        return FindField("field@" + r.X + "," + r.Z) != null ? OrderProgress.Done : OrderProgress.Pending;
                    }
            }
            string member = verify.Str("member");
            if (member != null)
            {
                string err;
                var vars = Vars(a, out err);
                if (vars == null) { note = err; return OrderProgress.Pending; }
                int dot = member.IndexOf('.');
                object root;
                if (!vars.TryGetValue(member.Substring(0, dot), out root)) { note = "verify target missing"; return OrderProgress.Failed; }
                var have = Reflect.Get(root, member.Substring(dot + 1));
                object want = verify["equals"];
                if (want is string ws && ws.StartsWith("$")) vars.TryGetValue(ws.Substring(1), out want);
                bool equal = have != null && want != null &&
                             (Reflect.Num(have) == Reflect.Num(want) || string.Equals(Reflect.Text(have), Reflect.Text(want), StringComparison.OrdinalIgnoreCase));
                return equal ? OrderProgress.Done : OrderProgress.Pending;
            }
            note = "verify has no test";
            return OrderProgress.Failed;
        }

        private JObject Procedure(string name) => Map.Doc.Obj("procedures")?.Obj(name);

        public bool CanSave(out string reason)
        {
            reason = null;
            var p = Procedure("save");
            var status = (p?.Str("status") ?? "unmapped").ToLowerInvariant();
            if (status == "unmapped" || p?.Arr("steps") == null) { reason = "saving is not mapped for this game version yet"; return false; }
            if (status == "experimental" && (_cfg == null || !_cfg.AllowExperimentalOps)) { reason = "saving is experimental (enable experimental ops in Settings)"; return false; }
            return true;
        }

        public bool Save(string name, out string error)
        {
            string why;
            if (!CanSave(out why)) { error = why; return false; }
            return _runner.Run(Procedure("save").Arr("steps"), new Dictionary<string, object> { { "name", name } }, out error);
        }

        public bool Load(string name, out string error)
        {
            var p = Procedure("load");
            if (p?.Arr("steps") == null || (p.Str("status") ?? "unmapped") == "unmapped") { error = "loading is not mapped for this game version yet; load '" + name + "' from the game menu"; return false; }
            return _runner.Run(p.Arr("steps"), new Dictionary<string, object> { { "name", name } }, out error);
        }

        public LiveStatus Status()
        {
            var s = new LiveStatus();
            try
            {
                s.Stock = _g.Stock();
                var meta = new SnapshotMeta();
                _g.ReadTime(meta);
                s.Season = meta.Season;
                s.SeasonProgress = meta.SeasonProgress;
                s.GroundFrozen = meta.GroundFrozen;
                s.GameDay = meta.Year * 360 + meta.Month * 30 + meta.Day;
                s.DateText = "year " + meta.Year + ", " + meta.Season + (meta.Day > 0 ? " day " + meta.Day : "");
                s.RaidActive = _g.RaidActive();
                int pop = _g.Villagers().Count;
                var a = Assumptions();
                double food = 0;
                var foodItems = SnapshotBuilder.FoodItems(s.Stock.Keys, a);
                foreach (var kv in s.Stock) if (foodItems.Contains(kv.Key)) food += kv.Value;
                // unknown stock (nothing readable) must never trigger the stock pause
                s.FoodMonths = pop == 0 || s.Stock.Count == 0 ? double.NaN : food / Math.Max(0.01, pop * a.Num("food_per_villager_per_month", 1.0));
                int houses = Knowledge == null ? 0 : _g.Buildings().Count(b => Knowledge.Spec(_g.BuildingType(b)).Category == "housing");
                double wood;
                s.Stock.TryGetValue(a.Str("firewood_item", "Firewood"), out wood);
                double burn = houses * a.Num("firewood_per_house_per_month", 2.0) * (s.Season == "winter" ? 2 : 1);
                s.FirewoodMonths = burn <= 0 || s.Stock.Count == 0 ? double.NaN : wood / burn;
            }
            catch (Exception e) { Log.Warn("live status: " + e.Message); }
            return s;
        }

        private JObject _assumptions;

        private JObject Assumptions()
        {
            if (_assumptions != null) return _assumptions;
            try
            {
                string by = System.IO.Path.Combine(System.IO.Path.Combine(Paths.Agents, "shared"), "buildings.yaml");
                _assumptions = System.IO.File.Exists(by) ? Core.Yaml.MiniYaml.ParseObject(System.IO.File.ReadAllText(by)).Obj("assumptions") ?? new JObject() : new JObject();
            }
            catch { _assumptions = new JObject(); }
            return _assumptions;
        }
    }
}
