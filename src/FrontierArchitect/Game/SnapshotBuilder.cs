using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Yaml;
using UnityEngine;
using Rect = FrontierArchitect.Core.Model.Rect;
using Terrain = UnityEngine.Terrain;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Builds the immutable <see cref="VillageSnapshot"/> on the main thread: metrics, layout,
    /// tile grid and the knowledge pack. Values the game does not expose are estimated and say so
    /// (keys ending in _estimated), or listed in data_gaps.
    /// </summary>
    internal sealed class SnapshotBuilder
    {
        private readonly GameAccess _g;
        private readonly string _modVersion;
        private KnowledgePack _cachedKnowledge;
        private string _cachedKnowledgeVersion;

        public SnapshotBuilder(GameAccess g, string modVersion) { _g = g; _modVersion = modVersion; }

        public VillageSnapshot Build()
        {
            _g.ResetCache();
            var snap = new VillageSnapshot();
            snap.Meta.GameVersion = Application.version;
            snap.Meta.ModVersion = _modVersion;
            snap.Meta.SettlementName = _g.SettlementName();
            snap.Meta.TownTier = _g.TownTier();
            _g.ReadTime(snap.Meta);

            snap.Knowledge = Knowledge();
            snap.Grid = BuildGrid();
            var components = _g.Buildings();
            CountWorkers();
            foreach (var c in components)
            {
                try
                {
                    var b = ToBuilding(c, snap.Grid, snap.Knowledge);
                    if (b != null) snap.Buildings.Add(b);
                }
                catch (Exception e) { _g.Gaps.Add("building " + c.name + ": " + e.Message); }
            }
            // ids must be unique
            foreach (var dup in snap.Buildings.GroupBy(b => b.Id).Where(g => g.Count() > 1))
            {
                int i = 0;
                foreach (var b in dup) if (i++ > 0) b.Id += "#" + i;
            }
            ReadFields(snap);
            ReadRoads(snap);
            ReadResources(snap);
            snap.Stock = _g.Stock();
            snap.Unlocked = Unlocked(snap.Knowledge, snap.Meta.TownTier);
            snap.Metrics = Metrics(snap);
            snap.Gaps.AddRange(_g.Gaps.Distinct());
            return snap;
        }

        // ---------------------------------------------------------------- knowledge

        public KnowledgePack Knowledge()
        {
            if (_cachedKnowledge != null && _cachedKnowledgeVersion == Application.version) return _cachedKnowledge;
            var k = new KnowledgePack { GameVersion = Application.version };
            string shared = System.IO.Path.Combine(Paths.Agents, "shared");
            try
            {
                string by = System.IO.Path.Combine(shared, "buildings.yaml");
                if (System.IO.File.Exists(by)) k.LoadRules(MiniYaml.ParseObject(System.IO.File.ReadAllText(by)));
                string cy = System.IO.Path.Combine(shared, "crops.yaml");
                if (System.IO.File.Exists(cy)) k.LoadCrops(MiniYaml.ParseObject(System.IO.File.ReadAllText(cy)));
            }
            catch (Exception e) { Log.Warn("agents/shared yaml: " + e.Message); }
            ExtractBuildingData(k);
            _cachedKnowledge = k;
            _cachedKnowledgeVersion = Application.version;
            return k;
        }

        /// <summary>Building catalogue from GlobalAssets.buildingSetupData.buildingData (game data).</summary>
        private void ExtractBuildingData(KnowledgePack k)
        {
            var ga = _g.Map.ResolveType("GlobalAssets");
            var list = Reflect.Items(_g.Member(ga, "global.building_data"));
            if (list.Count == 0) { _g.Gaps.Add("building catalogue not readable (members.global.building_data); using buildings.yaml only"); return; }
            float tile = _g.Map.TileSize;
            foreach (var bd in list)
            {
                string id = Reflect.Text(_g.Member(bd, "building_data.identifier"));
                if (string.IsNullOrEmpty(id)) continue;
                id = id.Replace(" ", "");
                BuildingSpec spec;
                if (!k.Buildings.TryGetValue(id, out spec)) spec = new BuildingSpec { Type = id };
                double tier = _g.Number(bd, "building_data.tier", double.NaN);
                if (!double.IsNaN(tier)) spec.Tier = Math.Max(1, (int)tier);
                foreach (var c in Reflect.Items(_g.Member(bd, "building_data.cost")))
                {
                    string item = GameAccess.ItemName(Reflect.Get(c, "item") ?? Reflect.Get(c, "itemID") ?? Reflect.Get(c, "itemData") ?? Reflect.Get(c, "key"));
                    double n = Reflect.Num(Reflect.Get(c, "count") ?? Reflect.Get(c, "amount") ?? Reflect.Get(c, "quantity") ?? Reflect.Get(c, "qty") ?? Reflect.Get(c, "value"), 0);
                    if (item != null && n > 0) spec.Cost[item] = n;
                }
                double days = _g.Number(bd, "building_data.build_time", double.NaN);
                if (!double.IsNaN(days) && days > 0) spec.BuildDays = days;
                var gs = _g.Member(bd, "building_data.grid_size");
                bool sized = false;
                if (gs is Vector2 v2 && v2.x > 0 && v2.y > 0)
                {
                    // gridSize is in game cells; the mod grid uses the same cells when tile_size equals the game cell size
                    float scale = Ground.CellSize / tile;
                    spec.W = Math.Max(1, Mathf.RoundToInt(v2.x * scale));
                    spec.H = Math.Max(1, Mathf.RoundToInt(v2.y * scale));
                    sized = true;
                }
                string cat = Reflect.Text(_g.Member(bd, "building_data.category"));
                if (!string.IsNullOrEmpty(cat)) spec.Raw.Set("ui_category", cat);
                double maxW = _g.Number(bd, "building_data.max_workers", double.NaN);
                if (!double.IsNaN(maxW)) spec.MaxWorkers = (int)maxW;
                var tierMatch = System.Text.RegularExpressions.Regex.Match(id, "_?[Tt]ier_?(\\d)");
                if (double.IsNaN(tier) && tierMatch.Success) spec.Tier = int.Parse(tierMatch.Groups[1].Value);
                var prefab = Reflect.Get(Reflect.Items(_g.Member(bd, "building_data.prefab_entries")).FirstOrDefault(), "prefab") as GameObject;
                if (prefab != null)
                {
                    var comp = prefab.GetComponent(_g.Map.ResolveType("Building") ?? typeof(Transform)) as Component ?? prefab.transform;
                    var bounds = GameAccess.WorldBounds(comp);
                    if (!sized)
                    {
                        spec.W = Math.Max(1, Mathf.RoundToInt(bounds.size.x / tile));
                        spec.H = Math.Max(1, Mathf.RoundToInt(bounds.size.z / tile));
                    }
                    double wr = _g.Number(comp, "building.work_radius", double.NaN);
                    if (!double.IsNaN(wr) && wr > 0) spec.WorkRadius = (float)(wr / tile);
                    double sr = _g.Number(comp, "building.service_radius", double.NaN);
                    if (!double.IsNaN(sr) && sr > 0) spec.ServiceRadius = (float)(sr / tile);
                    double mw = _g.Number(comp, "building.workers_max", double.NaN);
                    if (!double.IsNaN(mw)) spec.MaxWorkers = (int)mw;
                }
                foreach (var kv in Reflect.Fields(bd).Take(40))
                    if (kv.Value != null && !(kv.Value is string s && s.Length > 80)) spec.Raw.Set(kv.Key, kv.Value is IConvertible && !(kv.Value is string) ? Reflect.Num(kv.Value, 0) : (object)Reflect.Text(kv.Value));
                k.Classify(spec);
                k.Buildings[id] = spec;
                if (!k.TierUnlocks.ContainsKey(spec.Tier)) k.TierUnlocks[spec.Tier] = new List<string>();
                k.TierUnlocks[spec.Tier].Add(id);
            }
        }

        private static List<string> Unlocked(KnowledgePack k, int tier) =>
            k.Buildings.Values.Where(b => b.Tier <= tier).Select(b => b.Type).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---------------------------------------------------------------- grid

        private TileGrid _terrainCache;
        private string _terrainCacheKey;

        /// <summary>Terrain-derived layers are cached (the ground only changes when something is flattened).</summary>
        private TileGrid BuildGrid()
        {
            Bounds mb;
            bool have = Ground.MapBounds(_g.Map, _g, out mb);
            string key = Ground.Source + "|" + mb.min + "|" + mb.max;
            if (have && _terrainCache != null && _terrainCacheKey == key)
            {
                var c = _terrainCache;
                var g = new TileGrid(c.Width, c.Height) { TileSize = c.TileSize, OriginX = c.OriginX, OriginZ = c.OriginZ };
                Array.Copy(c.Terrain, g.Terrain, c.Terrain.Length);
                Array.Copy(c.Slope, g.Slope, c.Slope.Length);
                Array.Copy(c.Elevation, g.Elevation, c.Elevation.Length);
                var fert = FertilitySampler();
                if (fert != null)
                    for (int z = 0; z < g.Height; z++)
                        for (int x = 0; x < g.Width; x++)
                            g.Fertility[g.Idx(x, z)] = fert((x + 0.5f) / g.Width, (z + 0.5f) / g.Height);
                return g;
            }
            var built = BuildGridFresh();
            if (built.Width > 1) { _terrainCache = built; _terrainCacheKey = key; }
            return built;
        }

        public void InvalidateTerrain() { _terrainCache = null; Ground.Reset(); }


        private TileGrid BuildGridFresh()
        {
            Bounds mb;
            if (!Ground.MapBounds(_g.Map, _g, out mb))
            {
                _g.Gaps.Add("map extent not found (no terrain, no ground collider, no buildings); grid is empty");
                return new TileGrid(1, 1);
            }
            float tile = Math.Max(0.25f, _g.Map.TileSize);
            int w = Mathf.CeilToInt(mb.size.x / tile), h = Mathf.CeilToInt(mb.size.z / tile);
            while (w > _g.Map.MaxGrid || h > _g.Map.MaxGrid) { tile *= 2; w = Mathf.CeilToInt(mb.size.x / tile); h = Mathf.CeilToInt(mb.size.z / tile); }
            var grid = new TileGrid(w, h) { TileSize = tile, OriginX = mb.min.x, OriginZ = mb.min.z };
            var fert = FertilitySampler();
            int misses = 0;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = grid.Idx(x, z);
                    float wx = grid.OriginX + (x + 0.5f) * tile, wz = grid.OriginZ + (z + 0.5f) * tile;
                    float height; Vector3 normal; bool water, road;
                    bool edge = x == 0 || z == 0 || x == w - 1 || z == h - 1;
                    if (!Ground.Probe(wx, wz, _g.Map.WaterHeight, out height, out normal, out water, out road))
                    {
                        misses++;
                        grid.Terrain[i] = Core.Model.Terrain.Edge;
                        continue;
                    }
                    grid.Elevation[i] = height;
                    grid.Slope[i] = Vector3.Angle(normal, Vector3.up);
                    grid.Terrain[i] = edge ? Core.Model.Terrain.Edge : water ? Core.Model.Terrain.Water : grid.Slope[i] > 45 ? Core.Model.Terrain.Cliff : Core.Model.Terrain.Land;
                    if (fert != null) grid.Fertility[i] = fert((x + 0.5f) / w, (z + 0.5f) / h);
                }
            if (misses > w * h / 2) _g.Gaps.Add("ground not found under " + misses + " of " + (w * h) + " tiles (source " + Ground.Source + ")");
            Log.Info("Map grid " + w + "x" + h + " tiles of " + tile + " units from " + Ground.Source + (misses > 0 ? ", " + misses + " tiles without ground" : ""));
            return grid;
        }

        /// <summary>Fertility from the agriculture data when it is a 2D or square 1D float array.</summary>
        private Func<float, float, float> FertilitySampler()
        {
            var ag = _g.Singleton("AgricultureManager");
            var data = _g.Member(ag, "agriculture.fertility");
            if (data is float[,] f2)
            {
                int a = f2.GetLength(0), b = f2.GetLength(1);
                float max = 0; foreach (var v in f2) max = Math.Max(max, v);
                float scale = max > 1.5f ? 1f / max : 1f;
                return (nx, nz) => f2[Math.Min(a - 1, (int)(nx * a)), Math.Min(b - 1, (int)(nz * b))] * scale;
            }
            if (data is float[] f1)
            {
                int side = (int)Math.Round(Math.Sqrt(f1.Length));
                if (side * side == f1.Length && side > 0)
                {
                    float max = f1.Max();
                    float scale = max > 1.5f ? 1f / max : 1f;
                    return (nx, nz) => f1[Math.Min(side - 1, (int)(nz * side)) * side + Math.Min(side - 1, (int)(nx * side))] * scale;
                }
            }
            if (data is byte[] b1)
            {
                int side = (int)Math.Round(Math.Sqrt(b1.Length));
                if (side * side == b1.Length && side > 0)
                    return (nx, nz) => b1[Math.Min(side - 1, (int)(nz * side)) * side + Math.Min(side - 1, (int)(nx * side))] / 255f;
            }
            _g.Gaps.Add("fertility map not readable (members.agriculture.fertility)");
            return null;
        }

        // ---------------------------------------------------------------- layout

        private BuildingInfo ToBuilding(Component c, TileGrid grid, KnowledgePack k)
        {
            string type = _g.BuildingType(c);
            // the game's placement footprint collider ("gridCollider") is the exact footprint
            var gridCol = Reflect.Get(c, "gridCollider") as Collider;
            var bounds = gridCol != null ? gridCol.bounds : GameAccess.WorldBounds(c);
            var min = grid.WorldToCell(bounds.min.x + 0.01f, bounds.min.z + 0.01f);
            var max = grid.WorldToCell(bounds.max.x - 0.01f, bounds.max.z - 0.01f);
            int rw = Math.Max(1, max.X - min.X + 1), rh = Math.Max(1, max.Z - min.Z + 1);
            int rot = Mathf.RoundToInt(c.transform.eulerAngles.y / 90f) & 3;
            var b = new BuildingInfo
            {
                Type = type,
                X = min.X, Z = min.Z, Rot = rot,
                W = (rot & 1) == 1 ? rh : rw,
                H = (rot & 1) == 1 ? rw : rh,
                Handle = c,
            };
            b.Id = GameAccess.BuildingId(type, b.X, b.Z);
            int counted;
            b.WorkersAssigned = _workersByBuilding.TryGetValue(c, out counted) ? counted : (int)_g.Number(c, "building.workers_assigned", 0);
            b.WorkersMax = (int)_g.Number(c, "building.workers_max", 0);
            double tier = _g.Number(c, "building.tier", double.NaN);
            b.Tier = double.IsNaN(tier) ? 0 : Math.Max(1, (int)tier);
            double wr = _g.Number(c, "building.work_radius", double.NaN);
            if (!double.IsNaN(wr) && wr > 0) b.WorkRadius = (float)(wr / grid.TileSize);
            var centre = _g.Member(c, "building.work_center");
            if (centre is Vector3 v) b.WorkCircleCenter = grid.WorldToCell(v.x, v.z);
            double sr = _g.Number(c, "building.service_radius", double.NaN);
            if (!double.IsNaN(sr) && sr > 0) b.ServiceRadius = (float)(sr / grid.TileSize);
            var paused = _g.Member(c, "building.paused");
            if (paused is bool p && p) b.Status = "paused";
            var enabled = _g.Member(c, "building.work_enabled");
            if (enabled is bool en && !en && b.WorkersMax > 0) b.Status = "paused";
            double desired = _g.Number(c, "building.workers_desired", double.NaN);
            if (!double.IsNaN(desired)) b.Extra.Set("worker_limit", desired);
            var uc = _g.Member(c, "building.under_construction");
            if (uc is bool u && u) b.Status = "under_construction";
            double res = _g.Number(c, "building.residents", double.NaN);
            if (!double.IsNaN(res)) b.Extra.Set("residents", res);
            double cap = _g.Number(c, "building.capacity", double.NaN);
            if (!double.IsNaN(cap)) b.Extra.Set("capacity", cap);
            k.ApplyTo(b);
            if (b.Tier <= 0) b.Tier = 1;
            return b;
        }

        private readonly Dictionary<object, int> _workersByBuilding = new Dictionary<object, int>();

        /// <summary>Workers per building, counted from each villager's place of work.</summary>
        private void CountWorkers()
        {
            _workersByBuilding.Clear();
            foreach (var v in _g.Villagers())
            {
                var w = _g.Member(v, "villager.workplace");
                if (w == null) continue;
                var comp = w as Component;
                object key = comp != null ? (object)(comp.GetComponent(_g.Map.ResolveType("Building") ?? typeof(Transform)) ?? comp) : w;
                int n; _workersByBuilding.TryGetValue(key, out n);
                _workersByBuilding[key] = n + 1;
            }
        }

        private void ReadFields(VillageSnapshot snap)
        {
            var grid = snap.Grid;
            foreach (var c in _g.ObjectsOf("Field"))
            {
                try
                {
                    var bounds = GameAccess.WorldBounds(c);
                    var min = grid.WorldToCell(bounds.min.x + 0.01f, bounds.min.z + 0.01f);
                    var max = grid.WorldToCell(bounds.max.x - 0.01f, bounds.max.z - 0.01f);
                    var f = new FieldInfo
                    {
                        Rect = new Rect(min.X, min.Z, Math.Max(1, max.X - min.X + 1), Math.Max(1, max.Z - min.Z + 1)),
                        Handle = c,
                    };
                    f.Id = "field@" + f.Rect.X + "," + f.Rect.Z;
                    f.Fertility = (float)_g.Number(c, "field.fertility", double.NaN);
                    if (f.Fertility > 1.5f) f.Fertility /= 100f;
                    f.Weeds = (float)Math.Max(0, _g.Number(c, "field.weeds", 0));
                    if (f.Weeds > 1.5f) f.Weeds /= 100f;
                    f.Rocks = (float)Math.Max(0, _g.Number(c, "field.rocks", 0));
                    if (f.Rocks > 1.5f) f.Rocks /= 100f;
                    f.CropInGround = _g.Text(c, "field.crop_in_ground") ?? "";
                    var rot = Reflect.Items(_g.Member(c, "field.rotation"));
                    if (rot.Count > 0)
                    {
                        f.Rotation = new List<List<string>>();
                        foreach (var year in rot.Take(3))
                        {
                            var crops = Reflect.Items(year);
                            f.Rotation.Add(crops.Count > 0 ? crops.Select(Reflect.Text).Where(s => s != null).ToList() : new List<string> { Reflect.Text(year) });
                        }
                        while (f.Rotation.Count < 3) f.Rotation.Add(new List<string>());
                    }
                    f.CurrentYear = (int)Math.Max(1, _g.Number(c, "field.current_year", 1));
                    foreach (var d in Reflect.Items(_g.Member(c, "field.diseases"))) f.Diseases.Add(Reflect.Text(d));
                    f.Workers = (int)_g.Number(c, "building.workers_assigned", 0);
                    if (float.IsNaN(f.Fertility))
                    {
                        double sum = 0; int n = 0;
                        foreach (var cell in f.Rect.Cells()) if (grid.In(cell) && !float.IsNaN(grid.Fertility[grid.Idx(cell)])) { sum += grid.Fertility[grid.Idx(cell)]; n++; }
                        if (n > 0) f.Fertility = (float)(sum / n);
                    }
                    snap.Fields.Add(f);
                }
                catch (Exception e) { _g.Gaps.Add("field: " + e.Message); }
            }
            if (_g.Map.ResolveType("Field") == null) _g.Gaps.Add("field type not found (types.Field)");
        }

        private void ReadRoads(VillageSnapshot snap)
        {
            var grid = snap.Grid;
            var cells = new HashSet<Cell>();
            // Farthest Frontier: road meshes sit on the "Roads" layer; probe every cell (roads change, so no cache)
            if (Ground.HasRoadLayer && grid.Width > 1)
            {
                for (int z = 0; z < grid.Height; z++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        float wx, wz;
                        grid.CellCenterWorld(new Cell(x, z), out wx, out wz);
                        if (Ground.IsRoad(wx, wz)) cells.Add(new Cell(x, z));
                    }
                if (cells.Count > 0)
                {
                    snap.Roads.Add(new RoadInfo { Type = "existing", Tiles = cells.OrderBy(c => c.Z).ThenBy(c => c.X).ToList() });
                    return;
                }
            }
            foreach (var c in _g.ObjectsOf("Road"))
            {
                var pts = Reflect.Items(_g.Member(c, "road.points"));
                if (pts.Count > 0)
                {
                    foreach (var p in pts) if (p is Vector3 v) cells.Add(grid.WorldToCell(v.x, v.z));
                    continue;
                }
                var b = GameAccess.WorldBounds(c);
                var min = grid.WorldToCell(b.min.x + 0.01f, b.min.z + 0.01f);
                var max = grid.WorldToCell(b.max.x - 0.01f, b.max.z - 0.01f);
                if ((max.X - min.X + 1) * (max.Z - min.Z + 1) <= 64)
                    for (int z = min.Z; z <= max.Z; z++) for (int x = min.X; x <= max.X; x++) cells.Add(new Cell(x, z));
                else cells.Add(grid.WorldToCell(c.transform.position.x, c.transform.position.z));
            }
            if (cells.Count == 0) _g.Gaps.Add("no roads found (grid.road_layer / types.Road)");
            if (cells.Count > 0) snap.Roads.Add(new RoadInfo { Type = "existing", Tiles = cells.OrderBy(c => c.Z).ThenBy(c => c.X).ToList() });
        }

        private void ReadResources(VillageSnapshot snap)
        {
            var grid = snap.Grid;
            int count = 0;
            foreach (var c in _g.ObjectsOf("NaturalResource"))
            {
                if (count++ > 6000) break;
                string type = _g.Text(c, "resource.type") ?? c.GetType().Name;
                var p = c.transform.position;
                var cell = grid.WorldToCell(p.x, p.z);
                double rem = _g.Number(c, "resource.remaining", 1);
                snap.Resources.Add(new ResourceNode { Type = type, Pos = cell, Remaining = rem });
                if (grid.In(cell))
                {
                    string t = type.ToLowerInvariant();
                    if (t.Contains("tree")) grid.Clutter[grid.Idx(cell)] = 1;
                    else if (t.Contains("rock") || t.Contains("stone")) grid.Clutter[grid.Idx(cell)] = 2;
                }
            }
        }

        // ---------------------------------------------------------------- metrics

        private JObject Metrics(VillageSnapshot snap)
        {
            var k = snap.Knowledge;
            var vill = _g.Villagers();
            var m = new JObject();

            int pop = vill.Count, homeless = 0, idle = 0, builders = 0, children = 0, elderly = 0, soldiers = 0;
            foreach (var v in vill)
            {
                if (_g.Member(v, "villager.home") == null) homeless++;
                string prof = Reflect.Text(_g.Member(v, "villager.profession")) ?? "";
                if (prof.Length == 0 || prof.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("unassigned", StringComparison.OrdinalIgnoreCase) >= 0 || prof.IndexOf("labor", StringComparison.OrdinalIgnoreCase) >= 0) idle++;
                if (prof.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0) builders++;
                if (prof.IndexOf("soldier", StringComparison.OrdinalIgnoreCase) >= 0) soldiers++;
                double age = _g.Number(v, "villager.age", double.NaN);
                if (!double.IsNaN(age)) { if (age < 14) children++; else if (age >= 60) elderly++; }
            }
            double housingCap = snap.Buildings.Where(b => b.Category == "housing").Sum(b => b.Extra.Num("capacity", k.Spec(b.Type).Raw.Num("capacity", 4)));
            m.Set("population", new JObject().Set("count", pop).Set("children", children).Set("elderly", elderly)
                .Set("housing_capacity", Math.Round(housingCap)).Set("homeless", homeless).Set("idle_or_labourers", idle).Set("builders", builders));

            var foodItems = FoodItems(snap);
            double foodStock = 0; var byType = new JObject(); int variety = 0;
            foreach (var kv in snap.Stock.Where(s => foodItems.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                foodStock += kv.Value; byType.Set(kv.Key, Math.Round(kv.Value)); if (kv.Value > 0) variety++;
            }
            double perCapita = KnowledgeNumber("food_per_villager_per_month", 1.0);
            double consumption = Math.Max(0.01, pop * perCapita);
            double prodEstimate = snap.Buildings.Where(b => k.Spec(b.Type).ProducesFood).Sum(b => Math.Max(1, b.WorkersAssigned)) * KnowledgeNumber("food_per_worker_per_month", 2.5);
            bool stockKnown = snap.Stock.Count > 0;
            m.Set("food", new JObject().Set("stock_total", stockKnown ? Math.Round(foodStock) : (object)null).Set("by_type", byType).Set("variety", variety)
                .Set("consumption_per_month", Math.Round(consumption, 1)).Set("production_per_month", Math.Round(prodEstimate, 1))
                .Set("months_of_food", stockKnown ? Math.Round(foodStock / consumption, 1) : (object)null)
                .Set("rates_estimated", true));

            double firewood = snap.StockOf(KnowledgeText("firewood_item", "Firewood"));
            int houses = snap.Buildings.Count(b => b.Category == "housing");
            double burn = Math.Max(0.01, houses * KnowledgeNumber("firewood_per_house_per_month", 2.0) * (snap.Meta.Season == "winter" ? 2 : 1));
            double woodProd = snap.Buildings.Where(b => k.Spec(b.Type).ProducesFirewood).Sum(b => Math.Max(1, b.WorkersAssigned)) * KnowledgeNumber("firewood_per_worker_per_month", 6);
            m.Set("winter", new JObject().Set("firewood", Math.Round(firewood)).Set("firewood_burn_per_month", Math.Round(burn, 1))
                .Set("firewood_production_per_month", Math.Round(woodProd, 1))
                .Set("months_of_firewood", stockKnown ? Math.Round(firewood / burn, 1) : (object)null)
                .Set("clothing", Math.Round(SumStock(snap, "cloth", "coat", "clothes"))).Set("shoes", Math.Round(SumStock(snap, "shoe", "boot")))
                .Set("rates_estimated", true));

            double storageCap = snap.Buildings.Where(b => b.Category == "storage").Sum(b =>
            {
                var comp = b.Handle as Component;
                return comp == null ? 0 : Math.Max(0, _g.Number(comp, "building.storage_capacity", 0));
            });
            double used = snap.Stock.Values.Sum();
            m.Set("storage", new JObject().Set("capacity", Math.Round(storageCap)).Set("used", Math.Round(used))
                .Set("fill_ratio", storageCap > 0 ? Math.Round(used / storageCap, 2) : (object)null));

            var prod = new JArray();
            foreach (var b in snap.Buildings.Where(b => b.WorkersMax > 0).OrderBy(b => (double)b.WorkersAssigned / Math.Max(1, b.WorkersMax)).Take(25))
                prod.Add(new JObject().Set("id", b.Id).Set("assigned", b.WorkersAssigned).Set("max", b.WorkersMax).Set("status", b.Status));
            m.Set("production", new JObject().Set("staffing_lowest_first", prod)
                .Set("buildings_paused", snap.Buildings.Count(b => b.Status == "paused")));

            m.Set("health", new JObject()
                .Set("wells", snap.Buildings.Count(b => b.ServiceType == "well"))
                .Set("healers", snap.Buildings.Count(b => b.ServiceType == "healer"))
                .Set("soap", Math.Round(SumStock(snap, "soap"))).Set("herbs", Math.Round(SumStock(snap, "herb"))));
            m.Set("economy", new JObject().Set("gold", Math.Round(SumStock(snap, "gold", "coin")))
                .Set("goods_at_zero", snap.Stock.Where(s => s.Value <= 0).Select(s => s.Key).OrderBy(s => s).Take(30).ToJArray()));
            m.Set("defence", new JObject().Set("soldiers", soldiers)
                .Set("towers", snap.Buildings.Count(b => b.Category == "defence"))
                .Set("wall_pieces", snap.Buildings.Count(b => b.Category == "wall" || b.Category == "gate"))
                .Set("weapons", Math.Round(SumStock(snap, "sword", "spear", "bow", "arrow", "weapon"))));
            m.Set("happiness", new JObject().Set("services_present", snap.Buildings.Where(b => b.ServiceType != null).Select(b => b.ServiceType).Distinct().OrderBy(s => s).ToJArray()));
            m.Set("farming", new JObject().Set("fields", snap.Fields.Count)
                .Set("field_tiles", snap.Fields.Sum(f => f.Rect.Area))
                .Set("avg_fertility", snap.Fields.Count == 0 ? null : (object)Math.Round(snap.Fields.Where(f => !float.IsNaN(f.Fertility)).Select(f => (double)f.Fertility).DefaultIfEmpty(double.NaN).Average(), 2)));
            return m;
        }

        private JObject _assumptions;

        private JObject Assumptions()
        {
            if (_assumptions != null) return _assumptions;
            _assumptions = new JObject();
            try
            {
                string by = System.IO.Path.Combine(System.IO.Path.Combine(Paths.Agents, "shared"), "buildings.yaml");
                if (System.IO.File.Exists(by)) _assumptions = MiniYaml.ParseObject(System.IO.File.ReadAllText(by)).Obj("assumptions") ?? new JObject();
            }
            catch { }
            return _assumptions;
        }

        private double KnowledgeNumber(string key, double def) => Assumptions().Num(key, def);
        private string KnowledgeText(string key, string def) => Assumptions().Str(key, def);

        private HashSet<string> FoodItems(VillageSnapshot snap) => FoodItems(snap.Stock.Keys, Assumptions());

        private static readonly string[] FoodWords =
        {
            "bread", "meat", "fish", "berr", "root", "veg", "egg", "cheese", "milk", "apple", "fruit", "bean", "cabbage",
            "carrot", "turnip", "leek", "pie", "sausage", "mushroom", "greens", "nut", "honey", "preserve", "pumpkin"
        };

        /// <summary>Food items = the configured list plus any stock key that looks like food (names differ between builds).</summary>
        public static HashSet<string> FoodItems(IEnumerable<string> stockKeys, JObject assumptions)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in assumptions.Arr("food_items").Strings()) set.Add(s);
            foreach (var key in stockKeys)
            {
                string t = key.ToLowerInvariant();
                if (FoodWords.Any(t.Contains)) set.Add(key);
            }
            return set;
        }

        private static double SumStock(VillageSnapshot snap, params string[] parts) =>
            snap.Stock.Where(s => parts.Any(p => s.Key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).Sum(s => s.Value);
    }
}
