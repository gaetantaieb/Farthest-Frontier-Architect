using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Where the map is and what each cell holds. Farthest Frontier has no Unity Terrain: it uses its
    /// own Terrain2 with a fixed cell grid (384×384 cells of 5 world units on v1.1.x, the size of its
    /// fertility map), mouse-picking colliders on the "ClickCubes" layer, water on the "Water" layer,
    /// roads on the "Roads" layer, and a sea level on the terrain manager. All of these come from
    /// gamemap.json → grid, with fallbacks when a build differs.
    /// </summary>
    internal static class Ground
    {
        private static Bounds _bounds;
        private static bool _resolved;
        private static int _groundMask = ~0, _waterMask, _roadMask;
        private static float _seaLevel = float.NaN;
        private static Func<float, float, float> _heightFn;
        private static readonly Dictionary<long, float> _heights = new Dictionary<long, float>();
        public static string Source = "unresolved";
        public static int Cells;
        public static float CellSize = 5f;

        public static void Reset()
        {
            _resolved = false;
            _heights.Clear();
            Source = "unresolved";
        }

        public static float SeaLevel => _seaLevel;
        public static bool HasRoadLayer => _roadMask != 0;

        private static int Mask(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return 0;
            int l = LayerMask.NameToLayer(layer);
            return l < 0 ? 0 : 1 << l;
        }

        public static bool MapBounds(GameMap map, GameAccess g, out Bounds bounds)
        {
            Resolve(map, g);
            bounds = _bounds;
            return Source != "none";
        }

        /// <summary>Why the last resolution found no map (shown to the player).</summary>
        public static string Problem = "";

        private static void Resolve(GameMap map, GameAccess g)
        {
            if (_resolved) return;
            ResolveOnce(map, g);
            // A failure is not remembered: during loading the managers may not be ready yet.
            _resolved = Source != "none";
        }

        private static void ResolveOnce(GameMap map, GameAccess g)
        {
            Problem = "";
            var grid = map.Doc.Obj("grid") ?? new JObject();
            _groundMask = Mask(grid.Str("ground_layer", "ClickCubes"));
            if (_groundMask == 0) _groundMask = ~(Mask(grid.Str("water_layer", "Water")) | Mask("UI") | Mask("Ignore Raycast"));
            _waterMask = Mask(grid.Str("water_layer", "Water"));
            _roadMask = Mask(grid.Str("road_layer", "Roads"));

            // sea level: gamemap water_height, else the terrain manager's seaLevel
            _seaLevel = map.WaterHeight;
            var tm = g.Singleton("TerrainManager");
            _heightFn = null;
            if (tm != null)
            {
                // Terrain2Manager.GetHeightInWorldSpace(x, z): the game's own height lookup
                var mi = tm.GetType().GetMethods(Reflect.All).FirstOrDefault(m => m.Name == grid.Str("height_method", "GetHeightInWorldSpace") &&
                    m.GetParameters().Length == 2 && m.GetParameters().All(p2 => p2.ParameterType == typeof(float)) && m.ReturnType == typeof(float));
                if (mi != null)
                {
                    var target = mi.IsStatic ? null : tm;
                    _heightFn = (x, z) => (float)mi.Invoke(target, new object[] { x, z });
                }
            }
            if (float.IsNaN(_seaLevel) && tm != null)
            {
                double sl = Reflect.Num(Reflect.Get(tm, grid.Str("sea_level_member", "seaLevel")), double.NaN);
                if (!double.IsNaN(sl)) _seaLevel = (float)sl;
            }

            var t = Terrain.activeTerrain ?? Object.FindObjectsOfType<Terrain>().FirstOrDefault();
            if (t != null && t.terrainData != null)
            {
                _bounds = new Bounds(t.transform.position + t.terrainData.size / 2f, t.terrainData.size);
                Source = "unity_terrain:" + t.name;
                return;
            }

            // fixed cell grid: cells per side from gamemap, else from the fertility map's size
            CellSize = (float)grid.Num("tile_size", 5);
            Cells = grid.Int("cells_per_side", 0);
            if (Cells <= 0)
            {
                var fert = g.Member(g.Singleton("AgricultureManager"), "agriculture.fertility");
                if (fert is float[,] f2) Cells = f2.GetLength(0);
                else if (fert is Array a && a.Length > 0) Cells = (int)Math.Round(Math.Sqrt(a.Length));
                else Problem = "fertility map not ready";
            }
            if (Cells <= 0)
            {
                // the map size category the game itself uses (Minimap: Small 1280, Medium 1920, Large 2560 world units)
                var sizeEnum = Reflect.Get(Reflect.FindType("SettingsManager"), "mapSizeValue") ??
                               Reflect.Get(tm, "terrainGenerator.mapSettings.size");
                string name = sizeEnum?.ToString() ?? "";
                float world = name == "Small" ? 1280f : name == "Medium" ? 1920f : name == "Large" ? 2560f : 0f;
                var custom = grid.Obj("map_world_size");
                if (custom != null && custom.ContainsKey(name)) world = (float)custom.Num(name, world);
                if (world > 0) Cells = (int)Math.Round(world / CellSize);
                else Problem += (Problem.Length > 0 ? "; " : "") + "map size setting not readable";
            }
            if (tm == null) Problem += (Problem.Length > 0 ? "; " : "") + "terrain manager not found";
            if (Cells > 0)
            {
                Vector3 origin = Vector3.zero;
                if (tm != null && Reflect.Get(tm, "position") is Vector3 p) origin = p;
                var o = grid.Arr("origin");
                if (o != null && o.Count >= 2) origin = new Vector3((float)Json.ToDouble(o[0]), 0, (float)Json.ToDouble(o[1]));
                float size = Cells * CellSize;
                _bounds = new Bounds(origin + new Vector3(size / 2, 0, size / 2), new Vector3(size, 200, size));
                Source = "cell_grid:" + Cells + "x" + Cells + "@" + CellSize;
                return;
            }

            // last resort: everything the village owns, plus a margin
            var pts = new List<Vector3>();
            foreach (var b in g.Buildings()) pts.Add(b.transform.position);
            foreach (var r in g.ObjectsOf("NaturalResource").Take(5000)) pts.Add(r.transform.position);
            if (pts.Count > 0)
            {
                var b = new Bounds(pts[0], Vector3.zero);
                foreach (var pt in pts) b.Encapsulate(pt);
                b.Expand(new Vector3(120, 200, 120));
                _bounds = b;
                Source = "village_extent";
                return;
            }
            Source = "none";
            if (Problem.Length == 0) Problem = "no terrain, cell grid or buildings found";
        }

        private static float Top => Math.Max(_bounds.max.y, 100f) + 300f;

        /// <summary>Ground under a world x,z: height, normal, water, road.</summary>
        public static bool Probe(float x, float z, float waterHeight, out float height, out Vector3 normal, out bool water, out bool road)
        {
            height = 0; normal = Vector3.up; water = false; road = false;
            float sea = float.IsNaN(waterHeight) ? _seaLevel : waterHeight;
            var t = Terrain.activeTerrain;
            if (t != null)
            {
                height = t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
                var d = t.terrainData;
                var local = new Vector3(x, 0, z) - t.transform.position;
                normal = d.GetInterpolatedNormal(local.x / d.size.x, local.z / d.size.z);
                water = !float.IsNaN(sea) && height < sea;
            }
            else if (_heightFn != null)
            {
                try
                {
                    height = _heightFn(x, z);
                    // slope from neighbouring heights (half a cell away)
                    float d = Math.Max(0.5f, CellSize / 2f);
                    float hx = _heightFn(x + d, z) - _heightFn(x - d, z), hz = _heightFn(x, z + d) - _heightFn(x, z - d);
                    normal = new Vector3(-hx / (2 * d), 1f, -hz / (2 * d)).normalized;
                    water = !float.IsNaN(sea) && height < sea - 0.05f;
                }
                catch { return false; }
            }
            else
            {
                var origin = new Vector3(x, Top, z);
                RaycastHit hit;
                if (!Physics.Raycast(origin, Vector3.down, out hit, Top + 500f, _groundMask, QueryTriggerInteraction.Ignore))
                {
                    // no ground collider here: water if a water chunk covers the cell
                    if (_waterMask != 0 && Physics.Raycast(origin, Vector3.down, out hit, Top + 500f, _waterMask, QueryTriggerInteraction.Collide))
                    {
                        height = hit.point.y; water = true; return true;
                    }
                    return false;
                }
                height = hit.point.y;
                normal = hit.normal;
                if (!float.IsNaN(sea) && height < sea - 0.05f) water = true;
                if (!water && _waterMask != 0)
                {
                    RaycastHit wh;
                    if (Physics.Raycast(origin, Vector3.down, out wh, Top + 500f, _waterMask, QueryTriggerInteraction.Collide) && wh.point.y >= height - 0.05f) water = true;
                }
            }
            if (_roadMask != 0)
            {
                RaycastHit rh;
                road = Physics.Raycast(new Vector3(x, Top, z), Vector3.down, out rh, Top + 500f, _roadMask, QueryTriggerInteraction.Collide);
            }
            return true;
        }

        /// <summary>True when a road collider (Roads layer) covers the world point.</summary>
        public static bool IsRoad(float x, float z)
        {
            if (_roadMask == 0) return false;
            RaycastHit rh;
            return Physics.Raycast(new Vector3(x, Top, z), Vector3.down, out rh, Top + 500f, _roadMask, QueryTriggerInteraction.Collide);
        }

        /// <summary>Ground height for drawing (cached per half cell).</summary>
        public static float Height(float x, float z)
        {
            var t = Terrain.activeTerrain;
            if (t != null) return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
            if (_heightFn != null) { try { return _heightFn(x, z); } catch { } }
            long key = ((long)Mathf.RoundToInt(x * 0.4f) << 32) ^ (uint)Mathf.RoundToInt(z * 0.4f);
            float y;
            if (_heights.TryGetValue(key, out y)) return y;
            RaycastHit hit;
            y = Physics.Raycast(new Vector3(x, Top, z), Vector3.down, out hit, Top + 500f, _groundMask, QueryTriggerInteraction.Ignore) ? hit.point.y : (float.IsNaN(_seaLevel) ? 0 : _seaLevel);
            if (_heights.Count > 200000) _heights.Clear();
            _heights[key] = y;
            return y;
        }

        public static JObject Describe(GameMap map, GameAccess g)
        {
            Reset();
            Bounds b;
            MapBounds(map, g, out b);
            var o = new JObject().Set("source", Source).Set("min", b.min.ToString()).Set("max", b.max.ToString())
                .Set("sea_level", float.IsNaN(_seaLevel) ? null : (object)Math.Round(_seaLevel, 3))
                .Set("height_source", _heightFn != null ? "terrain_manager" : "raycast")
                .Set("ground_mask", _groundMask).Set("water_mask", _waterMask).Set("road_mask", _roadMask)
                .Set("unity_terrains", Object.FindObjectsOfType<Terrain>().Length);
            // sample the centre cell
            float h; Vector3 n; bool w, r;
            if (Probe(b.center.x, b.center.z, float.NaN, out h, out n, out w, out r))
                o.Set("centre_sample", new JObject().Set("height", Math.Round(h, 2)).Set("slope", Math.Round(Vector3.Angle(n, Vector3.up), 1)).Set("water", w).Set("road", r));
            var big = Object.FindObjectsOfType<Collider>().Where(c => c != null && !c.isTrigger)
                .OrderByDescending(c => c.bounds.size.x * c.bounds.size.z).Take(8)
                .Select(c => (object)(c.name + " [" + c.GetType().Name + ", layer " + LayerMask.LayerToName(c.gameObject.layer) + "] " + c.bounds.size));
            o.Set("largest_colliders", new JArray(big));
            var layers = new JArray();
            for (int i = 0; i < 32; i++) { var nm = LayerMask.LayerToName(i); if (!string.IsNullOrEmpty(nm)) layers.Add(i + ":" + nm); }
            o.Set("layers", layers);
            var tm = g.Singleton("TerrainManager");
            if (tm != null)
            {
                var f = new JObject();
                foreach (var kv in Reflect.Fields(tm)) f.Set(kv.Key, kv.Value is string || kv.Value == null ? kv.Value : (object)kv.Value.ToString());
                o.Set("terrain_manager", new JObject().Set("type", tm.GetType().FullName).Set("fields", f));
            }
            return o;
        }
    }
}
