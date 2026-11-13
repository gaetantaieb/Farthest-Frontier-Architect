using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrontierArchitect.Core.Model;
using UnityEngine;
using Terrain = UnityEngine.Terrain;
using Rect = FrontierArchitect.Core.Model.Rect;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Reads the live game through <see cref="GameMap"/>. Main thread only. Every lookup has a
    /// fallback, and what cannot be found is reported as a data gap instead of guessed.
    /// </summary>
    internal sealed class GameAccess
    {
        public readonly GameMap Map;
        public readonly List<string> Gaps = new List<string>();
        private readonly Dictionary<string, object> _singletons = new Dictionary<string, object>();

        public GameAccess(GameMap map) { Map = map; }

        public void ResetCache() { _singletons.Clear(); Gaps.Clear(); }

        public object Singleton(string concept)
        {
            object o;
            if (_singletons.TryGetValue(concept, out o) && (o as Object) != null) return o;
            var t = Map.ResolveType(concept);
            o = t == null ? null : Reflect.FindObject(t);
            if (o == null)
            {
                // reachable from GameManager? (e.g. gameManager.terrainManager)
                var gm = concept == "GameManager" ? null : Singleton("GameManager");
                if (gm != null)
                {
                    string field = char.ToLowerInvariant(concept[0]) + concept.Substring(1);
                    o = Reflect.Get(gm, field);
                }
            }
            _singletons[concept] = o;
            return o;
        }

        public object Member(object target, string concept)
        {
            string matched;
            return Reflect.GetFirst(target, Map.MemberCandidates(concept), out matched);
        }

        public double Number(object target, string concept, double def = double.NaN) => Reflect.Num(Member(target, concept), def);

        public string Text(object target, string concept) => Reflect.Text(Member(target, concept));

        public bool InGame => Singleton("GameManager") != null;

        // ---------------------------------------------------------------- collections

        public List<Component> Buildings()
        {
            var list = new List<Component>();
            var rm = Singleton("ResourceManager");
            foreach (var o in Reflect.Items(Member(rm, "resource_manager.buildings")))
                if (o is Component c && c != null) list.Add(c);
            if (list.Count == 0)
            {
                var t = Map.ResolveType("Building");
                foreach (var o in Reflect.FindObjects(t)) if (o is Component c && c != null) list.Add(c);
                if (t == null) Gaps.Add("building type not found (types.Building in gamemap.json)");
            }
            return list;
        }

        public List<object> Villagers()
        {
            var rm = Singleton("ResourceManager");
            var list = Reflect.Items(Member(rm, "resource_manager.villagers"));
            if (list.Count == 0) list.AddRange(Reflect.FindObjects(Map.ResolveType("Villager")).Cast<object>());
            return list;
        }

        public List<Component> ObjectsOf(string concept)
        {
            var list = new List<Component>();
            var t = Map.ResolveType(concept);
            foreach (var o in Reflect.FindObjects(t)) if (o is Component c && c != null) list.Add(c);
            return list;
        }

        // ---------------------------------------------------------------- building facts

        public string BuildingType(Component b)
        {
            var s = Text(b, "building.type");
            if (string.IsNullOrEmpty(s))
            {
                s = b.gameObject.name;
                int i = s.IndexOf("(Clone)", StringComparison.Ordinal);
                if (i >= 0) s = s.Substring(0, i);
            }
            return s.Trim().Replace(" ", "");
        }

        public static Bounds WorldBounds(Component c)
        {
            Collider[] cols = null;
            try { cols = c.GetComponentsInChildren<Collider>(); } catch { }
            if (cols != null && cols.Length > 0)
            {
                var b = cols[0].bounds;
                for (int i = 1; i < cols.Length; i++) if (!cols[i].isTrigger) b.Encapsulate(cols[i].bounds);
                return b;
            }
            Renderer[] rs = null;
            try { rs = c.GetComponentsInChildren<Renderer>(); } catch { }
            if (rs != null && rs.Length > 0)
            {
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                return b;
            }
            return new Bounds(c.transform.position, new Vector3(2, 2, 2));
        }

        /// <summary>Stable id across snapshots and reloads: type + footprint corner.</summary>
        public static string BuildingId(string type, int x, int z) => type + "@" + x + "," + z;

        // ---------------------------------------------------------------- stock

        public Dictionary<string, double> Stock()
        {
            var total = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in ObjectsOf("StorageBuilding"))
            {
                var items = Member(b, "building.storage_items");
                if (items is IDictionary dict)
                {
                    foreach (DictionaryEntry e in dict)
                    {
                        string key = Reflect.Text(e.Key);
                        double n = Reflect.Num(e.Value, double.NaN);
                        if (double.IsNaN(n)) n = Reflect.Num(Reflect.Get(e.Value, "count") ?? Reflect.Get(e.Value, "amount") ?? Reflect.Get(e.Value, "Count"), 0);
                        if (key == null) continue;
                        double cur; total.TryGetValue(key, out cur);
                        total[key] = cur + n;
                    }
                }
                else
                {
                    foreach (var it in Reflect.Items(items))
                    {
                        string key = Reflect.Text(Reflect.Get(it, "item") ?? Reflect.Get(it, "itemID") ?? Reflect.Get(it, "id"));
                        double n = Reflect.Num(Reflect.Get(it, "count") ?? Reflect.Get(it, "amount") ?? Reflect.Get(it, "quantity"), 0);
                        if (key == null) continue;
                        double cur; total.TryGetValue(key, out cur);
                        total[key] = cur + n;
                    }
                }
            }
            if (total.Count == 0) Gaps.Add("stock not readable (members.building.storage_items)");
            return total;
        }

        // ---------------------------------------------------------------- time & world

        public void ReadTime(SnapshotMeta meta)
        {
            var tm = Singleton("TimeManager");
            if (tm == null) { Gaps.Add("time manager not found"); return; }
            meta.Year = (int)Number(tm, "time.year", 1);
            meta.Day = (int)Number(tm, "time.day", 0);
            meta.Month = (int)Number(tm, "time.month", 0);
            string season = Text(tm, "time.season");
            if (string.IsNullOrEmpty(season) && meta.Month > 0)
                season = meta.Month <= 3 ? "spring" : meta.Month <= 6 ? "summer" : meta.Month <= 9 ? "autumn" : "winter";
            meta.Season = NormaliseSeason(season);
            double prog = Number(tm, "time.season_progress", double.NaN);
            if (double.IsNaN(prog) && meta.Month > 0) prog = ((meta.Month - 1) % 3) / 3.0 + 0.15;
            meta.SeasonProgress = double.IsNaN(prog) ? 0.5f : (float)Math.Max(0, Math.Min(1, prog > 1 ? prog / 100.0 : prog));
            var weather = Singleton("WeatherManager");
            var frozen = Member(weather, "weather.frozen");
            meta.GroundFrozen = frozen is bool fb ? fb : meta.Season == "winter";
        }

        public static string NormaliseSeason(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            if (s.Contains("spring")) return "spring";
            if (s.Contains("summer")) return "summer";
            if (s.Contains("autumn") || s.Contains("fall")) return "autumn";
            if (s.Contains("winter")) return "winter";
            int n;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return new[] { "spring", "summer", "autumn", "winter" }[Math.Abs(n) % 4];
            return "spring";
        }

        public bool RaidActive()
        {
            foreach (var concept in new[] { "CombatManager", "RaidManager" })
            {
                var m = Singleton(concept);
                var v = Member(m, "raid.active");
                if (v is bool b) return b;
            }
            return false;
        }

        public string SettlementName()
        {
            var gm = Singleton("GameManager");
            return Text(gm, "game.settlement_name") ?? "";
        }

        public int TownTier()
        {
            var gm = Singleton("GameManager");
            double t = Number(gm, "game.town_tier", double.NaN);
            if (double.IsNaN(t))
                foreach (var b in Buildings())
                    if (BuildingType(b).IndexOf("TownCenter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        BuildingType(b).IndexOf("TownHall", StringComparison.OrdinalIgnoreCase) >= 0)
                    { t = Number(b, "building.tier", 1); break; }
            return double.IsNaN(t) ? 1 : Math.Max(1, (int)t);
        }

        // ---------------------------------------------------------------- cursor

        public static bool CursorWorldPoint(out Vector3 p)
        {
            p = default(Vector3);
            var cam = Camera.main;
            if (cam == null) return false;
            RaycastHit hit;
            if (Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit, 5000f)) { p = hit.point; return true; }
            var terrain = Terrain.activeTerrain;
            if (terrain == null) return false;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, new Vector3(0, terrain.transform.position.y, 0));
            float d;
            if (plane.Raycast(ray, out d)) { p = ray.GetPoint(d); return true; }
            return false;
        }
    }
}
