using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Where the map is and how high the ground is. Uses Unity's Terrain when the game has one;
    /// otherwise (Farthest Frontier builds its own ground mesh) it finds the ground collider and
    /// raycasts onto it, the same way the game turns the mouse position into a map point.
    /// </summary>
    internal static class Ground
    {
        private static Collider _ground;
        private static Bounds _bounds;
        private static bool _resolved;
        private static readonly Dictionary<long, float> _heights = new Dictionary<long, float>();
        public static string Source = "unresolved";

        public static void Reset()
        {
            _resolved = false;
            _ground = null;
            _heights.Clear();
            Source = "unresolved";
        }

        /// <summary>The map rectangle in world space (min/max x,z), and how it was found.</summary>
        public static bool MapBounds(GameMap map, GameAccess g, out Bounds bounds)
        {
            Resolve(map, g);
            bounds = _bounds;
            return Source != "none";
        }

        private static void Resolve(GameMap map, GameAccess g)
        {
            if (_resolved) return;
            _resolved = true;
            var t = Terrain.activeTerrain ?? Object.FindObjectsOfType<Terrain>().FirstOrDefault();
            if (t != null && t.terrainData != null)
            {
                _bounds = new Bounds(t.transform.position + t.terrainData.size / 2f, t.terrainData.size);
                Source = "unity_terrain:" + t.name;
                return;
            }
            // the largest solid collider is the ground (or one named in gamemap.json grid.ground_name)
            string want = map.Doc.Obj("grid")?.Str("ground_name");
            if (string.IsNullOrWhiteSpace(want)) want = null;
            Collider best = null;
            float bestArea = 0;
            foreach (var c in Object.FindObjectsOfType<Collider>())
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (want != null && c.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string nm = c.name.ToLowerInvariant();
                if (want == null && (nm.Contains("water") || nm.Contains("ocean") || nm.Contains("sea") || nm.Contains("bound") || nm.Contains("fog") || nm.Contains("sky"))) continue;
                var b = c.bounds;
                float area = b.size.x * b.size.z;
                if (area > bestArea) { bestArea = area; best = c; }
            }
            if (best != null && bestArea > 10000f)
            {
                _ground = best;
                _bounds = best.bounds;
                Source = "collider:" + best.name;
                return;
            }
            // last resort: everything the village owns, plus a margin
            var pts = new List<Vector3>();
            foreach (var b in g.Buildings()) pts.Add(b.transform.position);
            foreach (var r in g.ObjectsOf("NaturalResource").Take(5000)) pts.Add(r.transform.position);
            if (pts.Count > 0)
            {
                var b = new Bounds(pts[0], Vector3.zero);
                foreach (var p in pts) b.Encapsulate(p);
                b.Expand(new Vector3(120, 0, 120));
                _bounds = b;
                Source = "village_extent";
                return;
            }
            Source = "none";
        }

        /// <summary>Ground hit under a world x,z: height, normal, and whether water lies above the ground there.</summary>
        public static bool Probe(float x, float z, float waterHeight, out float height, out Vector3 normal, out bool water)
        {
            height = 0; normal = Vector3.up; water = false;
            var t = Terrain.activeTerrain;
            if (t != null)
            {
                height = t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
                var d = t.terrainData;
                var local = new Vector3(x, 0, z) - t.transform.position;
                normal = d.GetInterpolatedNormal(local.x / d.size.x, local.z / d.size.z);
                water = !float.IsNaN(waterHeight) && height < waterHeight;
                return true;
            }
            var origin = new Vector3(x, _bounds.max.y + 500f, z);
            var hits = Physics.RaycastAll(origin, Vector3.down, 5000f, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0) return false;
            RaycastHit? ground = null;
            foreach (var h in hits)
                if (_ground != null ? h.collider == _ground : !h.collider.isTrigger)
                    if (ground == null || h.point.y < ground.Value.point.y) ground = h;
            if (ground == null) return false;
            height = ground.Value.point.y;
            normal = ground.Value.normal;
            foreach (var h in hits)
            {
                if (h.point.y < height - 0.01f) continue;
                string n = (h.collider.name + " " + LayerMask.LayerToName(h.collider.gameObject.layer)).ToLowerInvariant();
                if (n.Contains("water") || n.Contains("river") || n.Contains("lake") || n.Contains("ocean")) { water = true; break; }
            }
            if (!float.IsNaN(waterHeight) && height < waterHeight) water = true;
            return true;
        }

        /// <summary>Ground height for drawing (cached per half tile).</summary>
        public static float Height(float x, float z)
        {
            var t = Terrain.activeTerrain;
            if (t != null) return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
            long key = ((long)Mathf.RoundToInt(x * 2) << 32) ^ (uint)Mathf.RoundToInt(z * 2);
            float y;
            if (_heights.TryGetValue(key, out y)) return y;
            Vector3 n; bool w;
            if (!Probe(x, z, float.NaN, out y, out n, out w)) y = 0;
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
                .Set("unity_terrains", Object.FindObjectsOfType<Terrain>().Length);
            var big = Object.FindObjectsOfType<Collider>().Where(c => c != null && !c.isTrigger)
                .OrderByDescending(c => c.bounds.size.x * c.bounds.size.z).Take(8)
                .Select(c => (object)(c.name + " [" + c.GetType().Name + ", layer " + LayerMask.LayerToName(c.gameObject.layer) + "] " + c.bounds.size));
            o.Set("largest_colliders", new JArray(big));
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
