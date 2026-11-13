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

        /// <summary>Readable name of a game item object (Item, ItemID enum, record…).</summary>
        public static string ItemName(object item)
        {
            if (item == null) return null;
            if (item is string str) return str;
            if (item is Enum) return item.ToString();
            foreach (var m in new[] { "identifier", "itemID", "id", "itemName", "displayName", "name" })
            {
                var v = Reflect.Get(item, m);
                if (v == null) continue;
                string t = v is UnityEngine.Object uo ? uo.name : Reflect.Text(v);
                if (!string.IsNullOrEmpty(t) && t != item.GetType().Name) return t;
            }
            var s = item is UnityEngine.Object o ? o.name : item.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static void Add(Dictionary<string, double> total, string key, double n)
        {
            if (string.IsNullOrEmpty(key) || double.IsNaN(n)) return;
            double cur; total.TryGetValue(key, out cur);
            total[key] = cur + n;
        }

        /// <summary>Items held by one storage: an items/itemQtys pair of lists (Farthest Frontier), a dictionary, or a list of stacks.</summary>
        public void ReadItems(object owner, Dictionary<string, double> total)
        {
            var names = Reflect.Items(Reflect.Get(owner, "items"));
            var qtys = Reflect.Items(Reflect.Get(owner, "itemQtys"));
            if (names.Count > 0 && names.Count == qtys.Count)
            {
                for (int i = 0; i < names.Count; i++) Add(total, ItemName(names[i]), Reflect.Num(qtys[i], 0));
                return;
            }
            var items = Member(owner, "building.storage_items");
            if (items is IDictionary dict)
            {
                foreach (DictionaryEntry e in dict)
                {
                    double n = Reflect.Num(e.Value, double.NaN);
                    if (double.IsNaN(n)) n = Reflect.Num(Reflect.Get(e.Value, "count") ?? Reflect.Get(e.Value, "amount") ?? Reflect.Get(e.Value, "Count"), 0);
                    Add(total, ItemName(e.Key), n);
                }
                return;
            }
            foreach (var it in Reflect.Items(items))
            {
                var nameObj = Reflect.Get(it, "item") ?? Reflect.Get(it, "itemID") ?? Reflect.Get(it, "id");
                double n = Reflect.Num(Reflect.Get(it, "count") ?? Reflect.Get(it, "amount") ?? Reflect.Get(it, "quantity") ?? Reflect.Get(it, "qty"), double.NaN);
                if (nameObj != null && !double.IsNaN(n)) Add(total, ItemName(nameObj), n);
            }
        }

        public Dictionary<string, double> Stock()
        {
            var total = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // v1.1.x: ResourceManager.itemNameToItemInfoDict holds the village-wide count per item
            var registry = Member(Singleton("ResourceManager"), "resource_manager.item_infos") as IDictionary;
            if (registry != null)
            {
                foreach (DictionaryEntry e in registry)
                {
                    double n = Reflect.Num(Reflect.Get(e.Value, "count"), double.NaN);
                    if (!double.IsNaN(n) && n > 0) Add(total, Reflect.Text(e.Key), n);
                }
                if (total.Count > 0) return total;
            }
            foreach (var b in ObjectsOf("StorageBuilding"))
            {
                ReadItems(b, total);
                var storage = Reflect.Get(b, "storage");
                if (storage != null && total.Count == 0) ReadItems(storage, total);
            }
            if (total.Count == 0) Gaps.Add("stock not readable (members.building.storage_items)");
            return total;
        }

        // ---------------------------------------------------------------- time & world

        public void ReadTime(SnapshotMeta meta)
        {
            var tm = Singleton("TimeManager");
            if (tm == null) { Gaps.Add("time manager not found"); return; }
            double yr = Number(tm, "time.year", double.NaN);
            meta.Year = double.IsNaN(yr) || yr < 1 ? 1 : (int)yr;
            var date = Member(tm, "time.date");
            if (date != null)
            {
                double y2 = Reflect.Num(Reflect.Get(date, "year"), double.NaN), m2 = Reflect.Num(Reflect.Get(date, "month"), double.NaN), d2 = Reflect.Num(Reflect.Get(date, "dayOfMonth"), double.NaN);
                if (!double.IsNaN(y2) && y2 >= 1) meta.Year = (int)y2;
                if (!double.IsNaN(m2)) meta.Month = (int)m2;
                if (!double.IsNaN(d2)) meta.Day = (int)d2;
            }
            string label = Text(tm, "time.label") ?? "";
            var ym = System.Text.RegularExpressions.Regex.Match(label, @"Year\s+(\d+)");
            if (ym.Success) meta.Year = int.Parse(ym.Groups[1].Value, CultureInfo.InvariantCulture);
            meta.Day = (int)Number(tm, "time.day", 0);
            meta.Month = (int)Number(tm, "time.month", 0);
            string season = Text(tm, "time.season");
            if (string.IsNullOrEmpty(season) && meta.Month > 0)
                season = meta.Month <= 3 ? "spring" : meta.Month <= 6 ? "summer" : meta.Month <= 9 ? "autumn" : "winter";
            meta.Season = NormaliseSeason(season);
            double prog = Number(tm, "time.season_progress", double.NaN);
            string sub = (Text(tm, "time.subseason") ?? "").ToLowerInvariant();
            if (double.IsNaN(prog) && sub.Length > 0) prog = sub.Contains("early") ? 0.17 : sub.Contains("late") ? 0.83 : 0.5;
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

        /// <summary>World bounds of a crop field (Cropfield.cachedCombinedBounds, else colliders).</summary>
        public static Bounds FieldBounds(Component c)
        {
            if (Reflect.Get(c, "cachedCombinedBounds") is Bounds b && b.size.x > 0.1f) return b;
            var list = Reflect.Items(Reflect.Get(c, "boundsList"));
            if (list.Count > 0 && list[0] is Bounds first)
            {
                foreach (var o in list) if (o is Bounds bb) first.Encapsulate(bb);
                return first;
            }
            return WorldBounds(c);
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
