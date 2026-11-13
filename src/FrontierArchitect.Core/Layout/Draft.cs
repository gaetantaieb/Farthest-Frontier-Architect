using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;

namespace FrontierArchitect.Core.Layout
{
    /// <summary>Per-run constraints derived from agent.yaml, the ambition level and the player's choices.</summary>
    public sealed class RunSettings
    {
        public int Ambition = 3;
        public string Mode = "native";
        public HashSet<string> LockedIds = new HashSet<string>();
        public HashSet<string> AllowedOps = new HashSet<string>(Ops.All);
        public int MaxRelocations = 30; // -1 = unlimited
        public HashSet<string> Movable = new HashSet<string>();
        public float MaxSlopeDeg = 12f;
        public bool ReserveNextTier = true;
        public double OfflineCap = 0.5;
        public double MinGain = 2;
        public int PhaseMaxActions = 12;
        public double DetourLimit = 1.3;
        /// <summary>roads.connectivity: required = every building joined by road at the end of the plan.</summary>
        public bool ConnectivityRequired = true;
        public List<string> KeyTrips = new List<string> { "house_market", "house_well", "producer_storage", "storage_market", "field_barn" };
        public bool ReuseExistingRoads = true;
        /// <summary>Yearly walking (tiles) a new road tile must save before a shortcut is kept.</summary>
        public double RoadTileWeight = 100;
        public double RelocationCostFactor = 0.5;
        public int EdgeMargin = 1;
        public bool BuildReplacementFirst = true;
        public Dictionary<string, double> Weights = new Dictionary<string, double>
        {
            { "O1", 1.0 }, { "O2", 1.0 }, { "O3", 1.0 }, { "O4", 0.8 }, { "O5", 0.6 }, { "O6", 0.8 }
        };
    }

    public sealed class CheckResult
    {
        public bool Valid = true;
        public readonly List<string> Reasons = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly Dictionary<string, double> Cost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Cell> RoadTilesRemoved = new List<Cell>();
        public Rect? Footprint;
        public string NewId;

        public CheckResult Fail(string reason) { Valid = false; Reasons.Add(reason); return this; }

        public JObject ToJson()
        {
            var cost = new JObject(); foreach (var kv in Cost) cost.Set(kv.Key, Math.Round(kv.Value, 1));
            var o = new JObject().Set("valid", Valid).Set("reasons", Reasons.ToJArray()).Set("warnings", Warnings.ToJArray()).Set("cost", cost);
            if (Footprint.HasValue) o.Set("tiles_taken", Footprint.Value.ToJson());
            if (RoadTilesRemoved.Count > 0) o.Set("road_tiles_removed", RoadTilesRemoved.Count);
            if (NewId != null) o.Set("new_id", NewId);
            return o;
        }
    }

    /// <summary>
    /// Scratch copy of the layout that Claude builds a scenario on. Every change goes through
    /// <see cref="Check"/>; undo replays the applied actions from the snapshot (simple and exact).
    /// </summary>
    public sealed class DraftLayout
    {
        public readonly VillageSnapshot Snapshot;
        public readonly TileGrid Grid;
        public readonly KnowledgePack Knowledge;

        public Dictionary<string, BuildingInfo> Buildings { get; private set; }
        public List<FieldInfo> Fields { get; private set; }
        public HashSet<Cell> Roads { get; private set; }
        public HashSet<Cell> Flattened { get; private set; }
        public HashSet<Cell> Cleared { get; private set; }
        public HashSet<string> Demolished { get; private set; }
        public HashSet<string> Relocated { get; private set; }
        public List<PlanAction> Applied { get; private set; }
        /// <summary>Owner of each tile: building id, "field:{id}", or null.</summary>
        private string[] _occ;
        private int _newCounter;

        public DraftLayout(VillageSnapshot snapshot)
        {
            Snapshot = snapshot;
            Grid = snapshot.Grid ?? new TileGrid(1, 1);
            Knowledge = snapshot.Knowledge;
            Reset();
        }

        private void Reset()
        {
            Buildings = new Dictionary<string, BuildingInfo>();
            foreach (var b in Snapshot.Buildings) Buildings[b.Id] = b.Clone();
            Fields = Snapshot.Fields.Select(f => f.Clone()).ToList();
            Roads = Snapshot.RoadCells();
            Flattened = new HashSet<Cell>();
            Cleared = new HashSet<Cell>();
            Demolished = new HashSet<string>();
            Relocated = new HashSet<string>();
            Applied = new List<PlanAction>();
            _newCounter = 0;
            _occ = new string[Grid.Width * Grid.Height];
            foreach (var b in Buildings.Values) Stamp(b.Footprint, b.Id);
            foreach (var f in Fields) Stamp(f.Rect, "field:" + f.Id);
        }

        private void Stamp(Rect r, string owner)
        {
            foreach (var c in r.Cells()) if (Grid.In(c)) _occ[Grid.Idx(c)] = owner;
        }

        private void Unstamp(Rect r, string owner)
        {
            foreach (var c in r.Cells())
                if (Grid.In(c) && _occ[Grid.Idx(c)] == owner) _occ[Grid.Idx(c)] = null;
        }

        public string OwnerAt(Cell c) => Grid.In(c) ? _occ[Grid.Idx(c)] : "edge";

        public IEnumerable<BuildingInfo> LiveBuildings => Buildings.Values.Where(b => !Demolished.Contains(b.Id));

        public int RelocationCount => Relocated.Count;

        // ---------------------------------------------------------------- validation

        public CheckResult Check(PlanAction a, RunSettings s)
        {
            var r = new CheckResult();
            if (a == null || string.IsNullOrEmpty(a.Op)) return r.Fail("missing op");
            if (!Ops.IsKnown(a.Op)) return r.Fail("unknown op '" + a.Op + "'");
            if (!s.AllowedOps.Contains(a.Op)) return r.Fail("op '" + a.Op + "' is not allowed at ambition " + s.Ambition);

            switch (a.Op)
            {
                case Ops.Build: return CheckPlacement(a, s, r, null);
                case Ops.Relocate:
                    {
                        var b = Existing(a.Target, r);
                        if (b == null) return r;
                        if (s.LockedIds.Contains(b.Id) || b.Locked) return r.Fail(b.Id + " is locked by the player");
                        if (!MovableCategory(b, s)) return r.Fail(b.Type + " may only move at ambition 5");
                        var spec = Knowledge.Spec(b.Type);
                        if (spec.FixedResource != null) return r.Fail(b.Type + " is bound to its resource (" + spec.FixedResource + ") and cannot be relocated");
                        if (s.MaxRelocations >= 0 && !Relocated.Contains(b.Id) && Relocated.Count >= s.MaxRelocations)
                            return r.Fail("relocation limit reached (" + s.MaxRelocations + " at ambition " + s.Ambition + ")");
                        return CheckPlacement(a, s, r, b);
                    }
                case Ops.Demolish:
                    {
                        var b = Existing(a.Target, r);
                        if (b == null) return r;
                        if (s.LockedIds.Contains(b.Id) || b.Locked) return r.Fail(b.Id + " is locked by the player");
                        if (!MovableCategory(b, s)) return r.Fail(b.Type + " may only be removed at ambition 5");
                        return r;
                    }
                case Ops.ClearArea:
                case Ops.Flatten:
                    {
                        Rect rect;
                        if (!a.TryRect(out rect)) return r.Fail("params.rect [x,z,w,h] is required");
                        if (!InMap(rect, 0)) return r.Fail("rect is outside the map");
                        if (rect.Area > 900) return r.Fail("rect too large (max 900 tiles per action)");
                        int water = rect.Cells().Count(c => Grid.Terrain[Grid.Idx(c)] == Terrain.Water);
                        if (a.Op == Ops.Flatten && water > 0) r.Warnings.Add(water + " water tiles in the rect stay water");
                        r.Footprint = rect;
                        r.Cost["labour_days"] = rect.Area * (a.Op == Ops.Flatten ? 0.5 : 0.2);
                        return r;
                    }
                case Ops.RoadAdd:
                    {
                        var tiles = a.Tiles();
                        if (tiles.Count == 0) return r.Fail("params.tiles is required");
                        for (int i = 0; i < tiles.Count; i++)
                        {
                            var c = tiles[i];
                            if (!Grid.In(c)) return r.Fail("tile " + c + " is outside the map");
                            if (!Grid.IsLand(c) && !Flattened.Contains(c)) return r.Fail("tile " + c + " is not buildable (" + Grid.Terrain[Grid.Idx(c)] + ")");
                            var owner = _occ[Grid.Idx(c)];
                            if (owner != null) return r.Fail("tile " + c + " is occupied by " + owner);
                        }
                        int fresh = tiles.Count(t => !Roads.Contains(t));
                        r.Cost["road_tiles"] = fresh;
                        return r;
                    }
                case Ops.RoadRemove:
                    {
                        var tiles = a.Tiles();
                        if (tiles.Count == 0) return r.Fail("params.tiles is required");
                        int missing = tiles.Count(t => !Roads.Contains(t));
                        if (missing == tiles.Count) return r.Fail("none of these tiles is a road on the draft");
                        if (missing > 0) r.Warnings.Add(missing + " tiles are not roads and are ignored");
                        return r;
                    }
                case Ops.FieldCreate:
                case Ops.FieldResize:
                    {
                        Rect rect;
                        if (!a.TryRect(out rect)) return r.Fail("params.rect [x,z,w,h] is required");
                        if (rect.W < 5 || rect.H < 5 || rect.W > 12 || rect.H > 12) return r.Fail("fields are 5x5 to 12x12 tiles");
                        if (!InMap(rect, s.EdgeMargin)) return r.Fail("rect is outside the map");
                        string self = null;
                        if (a.Op == Ops.FieldResize)
                        {
                            var f = Fields.FirstOrDefault(x => x.Id == a.Target);
                            if (f == null) return r.Fail("unknown field " + a.Target);
                            if (f.Locked || s.LockedIds.Contains(f.Id)) return r.Fail(f.Id + " is locked by the player");
                            self = "field:" + f.Id;
                        }
                        foreach (var c in rect.Cells())
                        {
                            int i = Grid.Idx(c);
                            if (Grid.Terrain[i] != Terrain.Land && !Flattened.Contains(c)) return r.Fail("tile " + c + " is not farmable (" + Grid.Terrain[i] + ")");
                            if (Grid.Slope[i] > s.MaxSlopeDeg && !Flattened.Contains(c)) return r.Fail("tile " + c + " is too steep (" + Math.Round(Grid.Slope[i]) + "°)");
                            var owner = _occ[i];
                            if (owner != null && owner != self) return r.Fail("tile " + c + " is occupied by " + owner);
                            if (Roads.Contains(c)) return r.Fail("tile " + c + " is a road; remove the road first or move the field");
                        }
                        int cluttered = rect.Cells().Count(c => Grid.Clutter[Grid.Idx(c)] != 0 && !Cleared.Contains(c));
                        if (cluttered > 0) r.Warnings.Add(cluttered + " tiles have trees or rocks; plan clear_area first or expect slow tilling");
                        if (a.Op == Ops.FieldCreate)
                        {
                            var rotErr = CheckRotation(a.Rotation(), true);
                            if (rotErr != null) return r.Fail(rotErr);
                        }
                        r.Footprint = rect;
                        return r;
                    }
                case Ops.FieldRemove:
                    {
                        var f = Fields.FirstOrDefault(x => x.Id == a.Target);
                        if (f == null) return r.Fail("unknown field " + a.Target);
                        if (f.Locked || s.LockedIds.Contains(f.Id)) return r.Fail(f.Id + " is locked by the player");
                        if (!a.Params.Bool("after_harvest", true)) r.Warnings.Add("removing before harvest loses the crop");
                        return r;
                    }
                case Ops.SetRotation:
                    {
                        var f = Fields.FirstOrDefault(x => x.Id == a.Target);
                        if (f == null) return r.Fail("unknown field " + a.Target);
                        var err = CheckRotation(a.Rotation(), false);
                        return err == null ? r : r.Fail(err);
                    }
                case Ops.SetWorkCircle:
                    {
                        var b = Existing(a.Target, r);
                        if (b == null) return r;
                        if (b.WorkRadius <= 0) return r.Fail(b.Id + " has no work circle");
                        if (!Grid.In(new Cell(a.X, a.Z))) return r.Fail("centre is outside the map");
                        if (new Cell(a.X, a.Z).DistanceTo(b.Center) > b.WorkRadius * 2.5) r.Warnings.Add("centre is far from the building; walks will be long");
                        return r;
                    }
                case Ops.SetWorkers:
                    {
                        var b = Existing(a.Target, r);
                        if (b == null) return r;
                        if (!a.Params.ContainsKey("count")) return r.Fail("params.count is required");
                        int n = a.Params.Int("count");
                        if (n < 0) return r.Fail("count must be >= 0");
                        if (b.WorkersMax > 0 && n > b.WorkersMax) return r.Fail(b.Id + " allows at most " + b.WorkersMax + " workers");
                        return r;
                    }
                case Ops.SetProductionLimit:
                    if (string.IsNullOrEmpty(a.Target)) return r.Fail("target (item) is required");
                    if (!a.Params.ContainsKey("limit") || a.Params.Num("limit") < 0) return r.Fail("params.limit >= 0 is required");
                    return r;
                case Ops.SetProfessionTarget:
                    if (string.IsNullOrEmpty(a.Target)) return r.Fail("target (profession) is required");
                    if (!a.Params.ContainsKey("count") || a.Params.Int("count") < 0) return r.Fail("params.count >= 0 is required");
                    return r;
                case Ops.SetPaused:
                    {
                        var b = Existing(a.Target, r);
                        if (b == null) return r;
                        if (!a.Params.ContainsKey("paused")) return r.Fail("params.paused (true/false) is required");
                        return r;
                    }
            }
            return r;
        }

        private BuildingInfo Existing(string id, CheckResult r)
        {
            BuildingInfo b;
            if (id == null || !Buildings.TryGetValue(id, out b)) { r.Fail("unknown building '" + id + "'"); return null; }
            if (Demolished.Contains(id)) { r.Fail(id + " is demolished in this draft"); return null; }
            return b;
        }

        private bool MovableCategory(BuildingInfo b, RunSettings s)
        {
            string cat = b.Category;
            if (cat == "town_center" || cat == "wall" || cat == "gate")
                return s.Movable.Contains(cat) || s.Movable.Contains(cat + "s") || s.Movable.Contains("all");
            return true;
        }

        private bool InMap(Rect r, int margin) =>
            r.X >= margin && r.Z >= margin && r.MaxX < Grid.Width - margin && r.MaxZ < Grid.Height - margin;

        private string CheckRotation(List<List<string>> rot, bool allowEmpty)
        {
            if (rot.Count == 0) return allowEmpty ? null : "params.rotation must list 3 years";
            if (rot.Count != 3) return "a rotation has exactly 3 years";
            if (Knowledge.Crops.Count == 0) return null;
            foreach (var year in rot)
                foreach (var crop in year)
                    if (!string.Equals(crop, "fallow", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(crop, "maintenance", StringComparison.OrdinalIgnoreCase) &&
                        Knowledge.Crop(crop) == null)
                        return "unknown crop '" + crop + "' (known: " + string.Join(", ", Knowledge.Crops.Keys) + ", fallow, maintenance)";
            return null;
        }

        private CheckResult CheckPlacement(PlanAction a, RunSettings s, CheckResult r, BuildingInfo existing)
        {
            string type = existing?.Type ?? a.Target;
            if (string.IsNullOrEmpty(type)) return r.Fail("target (building type) is required");
            if (!a.Params.ContainsKey("x") || !a.Params.ContainsKey("z")) return r.Fail("params.x and params.z are required");
            var spec = Knowledge.Spec(type);
            if (existing == null)
            {
                if (Snapshot.Unlocked.Count > 0 && !Snapshot.Unlocked.Contains(type, StringComparer.OrdinalIgnoreCase))
                    return r.Fail(type + " is not unlocked at town tier " + Snapshot.Meta.TownTier);
                if (spec.Category == "town_center" && !s.Movable.Contains("town_center")) return r.Fail("a second Town Center cannot be built");
            }
            var probe = existing != null ? existing.Clone() : new BuildingInfo { Type = type, W = spec.W, H = spec.H };
            probe.X = a.X; probe.Z = a.Z; probe.Rot = a.Rot;
            var rect = probe.Footprint;
            r.Footprint = rect;
            if (!InMap(rect, s.EdgeMargin)) return r.Fail("footprint " + rect + " is outside the map");
            int clutter = 0;
            foreach (var c in rect.Cells())
            {
                int i = Grid.Idx(c);
                var t = Grid.Terrain[i];
                if (t != Terrain.Land && !Flattened.Contains(c)) return r.Fail("tile " + c + " is " + t);
                if (Grid.Slope[i] > s.MaxSlopeDeg && !Flattened.Contains(c)) return r.Fail("tile " + c + " is too steep (" + Math.Round(Grid.Slope[i]) + "°); plan flatten first");
                var owner = _occ[i];
                if (owner != null && (existing == null || owner != existing.Id)) return r.Fail("tile " + c + " overlaps " + owner);
                if (Roads.Contains(c)) r.RoadTilesRemoved.Add(c);
                if (Grid.Clutter[i] != 0 && !Cleared.Contains(c)) clutter++;
            }
            var door = probe.EntranceCell;
            if (!Grid.IsLand(door) && !Flattened.Contains(door)) r.Warnings.Add("the entrance " + door + " is not on buildable land; rotate the building");
            else if (_occ[Grid.Idx(door)] != null && _occ[Grid.Idx(door)] != existing?.Id) r.Warnings.Add("the entrance " + door + " is blocked by " + _occ[Grid.Idx(door)]);
            if (clutter > 0) r.Warnings.Add(clutter + " tiles have trees or rocks (clear_area first)");
            if (r.RoadTilesRemoved.Count > 0) r.Warnings.Add(r.RoadTilesRemoved.Count + " road tiles under the footprint will be removed");

            Func<string, bool> kindMatches = t => t != null && spec.FixedResource != null &&
                (string.Equals(t, spec.FixedResource, StringComparison.OrdinalIgnoreCase) || t.StartsWith(spec.FixedResource + ":", StringComparison.OrdinalIgnoreCase));
            bool resourceKnown = spec.FixedResource == "water" || Snapshot.Resources.Any(res => kindMatches(res.Type));
            if (spec.FixedResource != null && existing == null && !resourceKnown)
                r.Warnings.Add("cannot verify that " + type + " sits on " + spec.FixedResource + " (no such resource in the snapshot); check it in game");
            else if (spec.FixedResource != null && existing == null)
            {
                bool near = false;
                foreach (var res in Snapshot.Resources)
                {
                    if (!kindMatches(res.Type)) continue;
                    if (Distance(rect, res.Pos) <= 3) { near = true; break; }
                }
                if (spec.FixedResource == "water")
                    near = rect.Cells().Any(c => Neighbours(c).Any(n => Grid.In(n) && Grid.Terrain[Grid.Idx(n)] == Terrain.Water)) ||
                           Border(rect, 2).Any(c => Grid.In(c) && Grid.Terrain[Grid.Idx(c)] == Terrain.Water);
                if (!near) return r.Fail(type + " must sit on or beside " + spec.FixedResource);
            }

            double factor = existing != null ? s.RelocationCostFactor : 1.0;
            foreach (var kv in spec.Cost) r.Cost[kv.Key] = kv.Value * factor;
            r.Cost["build_days"] = spec.BuildDays * (existing != null ? 0.6 : 1.0);
            if (existing == null) r.NewId = "new:" + type + ":" + (_newCounter + 1);
            return r;
        }

        private static double Distance(Rect r, Cell c)
        {
            double dx = Math.Max(0, Math.Max(r.X - c.X, c.X - r.MaxX));
            double dz = Math.Max(0, Math.Max(r.Z - c.Z, c.Z - r.MaxZ));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public static IEnumerable<Cell> Neighbours(Cell c)
        {
            yield return new Cell(c.X + 1, c.Z);
            yield return new Cell(c.X - 1, c.Z);
            yield return new Cell(c.X, c.Z + 1);
            yield return new Cell(c.X, c.Z - 1);
        }

        public static IEnumerable<Cell> Border(Rect r, int ring)
        {
            var outer = new Rect(r.X - ring, r.Z - ring, r.W + 2 * ring, r.H + 2 * ring);
            foreach (var c in outer.Cells()) if (!r.Contains(c)) yield return c;
        }

        // ---------------------------------------------------------------- mutation

        public CheckResult Apply(PlanAction a, RunSettings s)
        {
            var r = Check(a, s);
            if (!r.Valid) return r;
            Mutate(a, r);
            Applied.Add(a);
            return r;
        }

        private void Mutate(PlanAction a, CheckResult r)
        {
            switch (a.Op)
            {
                case Ops.Build:
                    {
                        var spec = Knowledge.Spec(a.Target);
                        _newCounter++;
                        var b = new BuildingInfo
                        {
                            Id = r.NewId ?? ("new:" + a.Target + ":" + _newCounter),
                            Type = a.Target, X = a.X, Z = a.Z, Rot = a.Rot, W = spec.W, H = spec.H,
                            Category = spec.Category, Tier = spec.Tier, WorkRadius = spec.WorkRadius,
                            ServiceRadius = spec.ServiceRadius, ServiceType = spec.ServiceType, WorkersMax = spec.MaxWorkers,
                            IsNew = true, Status = "planned"
                        };
                        a.Params.Set("new_id", b.Id);
                        Buildings[b.Id] = b;
                        foreach (var c in r.RoadTilesRemoved) Roads.Remove(c);
                        Stamp(b.Footprint, b.Id);
                        break;
                    }
                case Ops.Relocate:
                    {
                        var b = Buildings[a.Target];
                        Unstamp(b.Footprint, b.Id);
                        if (b.WorkCircleCenter.HasValue)
                        {
                            // move the work circle with the building
                            int dx = a.X - b.X, dz = a.Z - b.Z;
                            b.WorkCircleCenter = new Cell(b.WorkCircleCenter.Value.X + dx, b.WorkCircleCenter.Value.Z + dz);
                        }
                        b.X = a.X; b.Z = a.Z; b.Rot = a.Rot;
                        foreach (var c in r.RoadTilesRemoved) Roads.Remove(c);
                        Stamp(b.Footprint, b.Id);
                        Relocated.Add(b.Id);
                        break;
                    }
                case Ops.Demolish:
                    {
                        var b = Buildings[a.Target];
                        Unstamp(b.Footprint, b.Id);
                        Demolished.Add(b.Id);
                        break;
                    }
                case Ops.ClearArea:
                    foreach (var c in r.Footprint.Value.Cells()) Cleared.Add(c);
                    break;
                case Ops.Flatten:
                    foreach (var c in r.Footprint.Value.Cells())
                        if (Grid.Terrain[Grid.Idx(c)] != Terrain.Water) { Flattened.Add(c); Cleared.Add(c); }
                    break;
                case Ops.RoadAdd:
                    foreach (var c in a.Tiles()) Roads.Add(c);
                    break;
                case Ops.RoadRemove:
                    foreach (var c in a.Tiles()) Roads.Remove(c);
                    break;
                case Ops.FieldCreate:
                    {
                        _newCounter++;
                        var f = new FieldInfo { Id = "newfield:" + _newCounter, Rect = r.Footprint.Value };
                        var rot = a.Rotation();
                        if (rot.Count == 3) f.Rotation = rot;
                        f.Fertility = AverageFertility(f.Rect);
                        a.Params.Set("new_id", f.Id);
                        Fields.Add(f);
                        Stamp(f.Rect, "field:" + f.Id);
                        break;
                    }
                case Ops.FieldResize:
                    {
                        var f = Fields.First(x => x.Id == a.Target);
                        Unstamp(f.Rect, "field:" + f.Id);
                        f.Rect = r.Footprint.Value;
                        f.Fertility = AverageFertility(f.Rect);
                        Stamp(f.Rect, "field:" + f.Id);
                        break;
                    }
                case Ops.FieldRemove:
                    {
                        var f = Fields.First(x => x.Id == a.Target);
                        Unstamp(f.Rect, "field:" + f.Id);
                        Fields.Remove(f);
                        break;
                    }
                case Ops.SetRotation:
                    {
                        var f = Fields.First(x => x.Id == a.Target);
                        f.Rotation = a.Rotation();
                        break;
                    }
                case Ops.SetWorkCircle:
                    Buildings[a.Target].WorkCircleCenter = new Cell(a.X, a.Z);
                    break;
                case Ops.SetWorkers:
                    Buildings[a.Target].WorkersAssigned = a.Params.Int("count");
                    break;
                case Ops.SetPaused:
                    Buildings[a.Target].Status = a.Params.Bool("paused") ? "paused" : "active";
                    break;
            }
        }

        public float AverageFertility(Rect r)
        {
            double sum = 0; int n = 0;
            foreach (var c in r.Cells())
            {
                if (!Grid.In(c)) continue;
                float v = Grid.Fertility[Grid.Idx(c)];
                if (float.IsNaN(v)) continue;
                sum += v; n++;
            }
            return n == 0 ? float.NaN : (float)(sum / n);
        }

        /// <summary>Undo the last applied action (replays the rest from the snapshot).</summary>
        public PlanAction Undo(RunSettings s)
        {
            if (Applied.Count == 0) return null;
            var last = Applied[Applied.Count - 1];
            var keep = Applied.Take(Applied.Count - 1).ToList();
            Reset();
            foreach (var a in keep) Apply(a, s);
            return last;
        }

        /// <summary>Removes one action (by id) and replays; actions that no longer validate are returned.</summary>
        public List<PlanAction> RemoveAndReplay(string actionId, RunSettings s)
        {
            var keep = Applied.Where(a => a.Id != actionId).ToList();
            Reset();
            var broken = new List<PlanAction>();
            foreach (var a in keep)
            {
                var r = Apply(a, s);
                if (!r.Valid) { a.StatusNote = string.Join("; ", r.Reasons); broken.Add(a); }
            }
            return broken;
        }

        public DraftLayout Fork(RunSettings s)
        {
            var d = new DraftLayout(Snapshot);
            foreach (var a in Applied) d.Apply(a, s);
            return d;
        }

        public JObject Summary()
        {
            int added = Buildings.Values.Count(b => b.IsNew);
            return new JObject()
                .Set("actions_applied", Applied.Count)
                .Set("relocations", Relocated.Count)
                .Set("buildings_added", added)
                .Set("buildings_demolished", Demolished.Count)
                .Set("fields", Fields.Count)
                .Set("road_tiles", Roads.Count);
        }
    }
}
