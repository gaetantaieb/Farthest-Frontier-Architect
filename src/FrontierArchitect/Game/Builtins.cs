using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Game operations that need more than one call, written after the game's own code paths in
    /// Farthest Frontier v1.1.2a (Mono). Recipes in gamemap.json name them with {"builtin": "..."}.
    ///  - construct: what Input_PlaceBuilding.Place does (ConstructionData → BuildManager.Construct)
    ///  - relocate:  what Input_RelocateObject does (two ConstructionData → BuildManager.Relocate)
    ///  - production_limit: ResourceManager.itemNameToItemInfoDict[item].maxQuota / areProductionLimitsEnabled
    ///  - crop_rotation: Cropfield.cropScheduler (WipeScheduledItem / AttemptToAddCropToSchedule)
    /// </summary>
    internal static class Builtins
    {
        public static bool Run(string name, GameAccess g, Dictionary<string, object> v, out string error)
        {
            error = null;
            try
            {
                switch (name)
                {
                    case "construct": return Construct(g, v, out error);
                    case "relocate": return Relocate(g, v, out error);
                    case "production_limit": return ProductionLimit(g, v, out error);
                    case "crop_rotation": return CropRotation(v, out error);
                }
                error = "unknown builtin " + name;
                return false;
            }
            catch (TargetInvocationException e) { error = name + ": " + (e.InnerException?.Message ?? e.Message); return false; }
            catch (Exception e) { error = name + ": " + e.Message; return false; }
        }

        // ------------------------------------------------------------------ helpers

        private static object Call(object target, string method, params object[] args)
        {
            object r; string err;
            if (!Reflect.Invoke(target, method, args, out r, out err)) throw new InvalidOperationException(err);
            return r;
        }

        private sealed class Struct
        {
            public readonly object Box;
            private readonly Type _t;
            public Struct(Type t) { _t = t; Box = Activator.CreateInstance(t); }
            public void Set(string field, object value)
            {
                var f = _t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) throw new MissingFieldException(_t.Name, field);
                if (value == null) { f.SetValue(Box, null); return; }
                f.SetValue(Box, f.FieldType.IsInstanceOfType(value) ? value : Reflect.Convert(value, f.FieldType));
            }
        }

        private static object Enum(string type, string value)
        {
            var t = Reflect.FindType(type);
            if (t == null) throw new TypeLoadException(type);
            return System.Enum.Parse(t, value);
        }

        private static object Nested(string outer, string inner, string value)
        {
            var o = Reflect.FindType(outer);
            var t = o?.GetNestedType(inner, BindingFlags.Public | BindingFlags.NonPublic);
            if (t == null) throw new TypeLoadException(outer + "." + inner);
            return System.Enum.Parse(t, value);
        }

        private static object BuildManager(GameAccess g) =>
            g.Singleton("BuildManager") ?? throw new InvalidOperationException("BuildManager not found");

        private static Transform Parent(object bm) => (Reflect.Get(bm, "parent") as GameObject)?.transform;

        private static Bounds FootprintBounds(object buildingData, Vector3 centre, float rotY, float cell)
        {
            var gs = Reflect.Get(buildingData, "gridSize") is Vector2 v ? v : new Vector2(2, 2);
            var size = new Vector3(gs.x * cell, 10f, gs.y * cell);
            float snapped = Mathf.Round(rotY / 90f) * 90f % 360f;
            if (Mathf.Approximately(snapped, 90f) || Mathf.Approximately(snapped, 270f)) size = new Vector3(size.z, size.y, size.x);
            return new Bounds(centre, size);
        }

        // ------------------------------------------------------------------ construct

        private static bool Construct(GameAccess g, Dictionary<string, object> v, out string error)
        {
            error = null;
            var data = v.ContainsKey("building_data") ? v["building_data"] : null;
            if (data == null) { error = "no building data"; return false; }
            var bm = BuildManager(g);
            float cell = (float)Reflect.Num(Reflect.Get(bm, "cellSize"), 5);
            var pos = (Vector3)v["world_pos"];
            float rotY = Convert.ToSingle(v["rot_deg"]);
            var entry = Call(data, "GetRandomPrefabEntry");
            var prefab = Call(entry, "PREFAB") as GameObject;
            if (prefab == null) { error = "prefab not found"; return false; }

            var cd = new Struct(Reflect.FindType("ConstructionData"));
            cd.Set("constructionType", Enum("ConstructionType", "CONSTRUCT"));
            cd.Set("buildGroup", Call(data, "GetBuildGroupString"));
            cd.Set("position", pos);
            cd.Set("rotation", Quaternion.Euler(0, rotY, 0));
            cd.Set("terrainPos", pos);
            cd.Set("terrainGridData", null);
            cd.Set("defaultNumBuilders", Reflect.Get(data, "defaultBuilders"));
            cd.Set("maxSimultaneousBuilders", Reflect.Get(data, "maxBuilders"));
            cd.Set("bounds", FootprintBounds(data, pos, rotY, cell));
            cd.Set("buildsiteClearingMode", Reflect.Get(data, "buildsiteClearingMode"));
            cd.Set("clearDetailsBorderWidth", Reflect.Get(data, "clearDetailsBorderWidth"));
            cd.Set("materialsRequired", Call(data, "GetRequiredMaterialsDict"));
            cd.Set("addToStorageAfterConstruction", Call(data, "GetAddToStorageAfterConstruction"));
            cd.Set("parent", Parent(bm));
            cd.Set("prefabToConstruct", prefab);
            // Native: a build site that builders complete. Instant: the finished building.
            bool instant = v.ContainsKey("instant") && v["instant"] is bool b && b;
            cd.Set("buildSitePrefab", instant ? null : Reflect.Get(data, "buildSitePrefab"));
            cd.Set("emptyLotPrefab", Reflect.Get(entry, "emptyLotPrefab"));
            cd.Set("constructionStartedPrefab", Reflect.Get(entry, "constructionStartedPrefab"));
            cd.Set("constructionProgressPrefabs", Reflect.Get(entry, "constructionProgressPrefabs"));
            cd.Set("placedInFreeBuild", Call(bm, "IsFreeBuildEnabled"));
            var go = Call(bm, "Construct", cd.Box) as GameObject;
            if (go == null) { error = "BuildManager.Construct returned nothing"; return false; }
            return true;
        }

        // ------------------------------------------------------------------ relocate

        private static bool Relocate(GameAccess g, Dictionary<string, object> v, out string error)
        {
            error = null;
            var building = v["building"] as Component;
            if (building == null) { error = "no building"; return false; }
            var sceneObject = building.gameObject;
            var data = Reflect.Get(building, "buildingData");
            if (data == null) { error = "building has no data"; return false; }
            var bm = BuildManager(g);
            float cell = (float)Reflect.Num(Reflect.Get(bm, "cellSize"), 5);
            bool instant = v.ContainsKey("instant") && v["instant"] is bool b && b;
            var pos = (Vector3)v["world_pos"];
            float rotY = Convert.ToSingle(v["rot_deg"]);

            // the entry this building was made from (by its CEGuid), else a random one
            object entry = null;
            var guidComp = sceneObject.GetComponent(Reflect.FindType("CEGuid") ?? typeof(Transform));
            var guid = guidComp == null ? null : Reflect.Get(guidComp, "guid");
            if (guid != null) { try { entry = Call(data, "GetPrefabEntry", guid.ToString()); } catch { } }
            if (entry == null) entry = Call(data, "GetRandomPrefabEntry");

            var workUnit = Activator.CreateInstance(Reflect.FindType("ItemWorkUnit"));
            var itemType = Reflect.FindType("Item");
            var dictType = typeof(Dictionary<,>).MakeGenericType(itemType, typeof(int));

            // deconstruction side (old site)
            var decon = new Struct(Reflect.FindType("ConstructionData"));
            decon.Set("constructionType", Enum("ConstructionType", "RELOCATE"));
            decon.Set("position", sceneObject.transform.position);
            decon.Set("rotation", sceneObject.transform.rotation);
            decon.Set("sceneObject", sceneObject);
            var cb = Reflect.Get(building, "constructionBounds") is Bounds bb ? bb : FootprintBounds(data, sceneObject.transform.position, sceneObject.transform.eulerAngles.y, cell);
            float yaw = Mathf.Round(sceneObject.transform.eulerAngles.y / 90f) * 90f % 360f;
            if (Mathf.Approximately(yaw, 90f) || Mathf.Approximately(yaw, 270f)) cb = new Bounds(sceneObject.transform.position, new Vector3(cb.size.z, cb.size.y, cb.size.x));
            decon.Set("bounds", cb);
            decon.Set("buildGroup", Call(data, "GetBuildGroupString"));
            decon.Set("defaultNumBuilders", Reflect.Get(data, "defaultBuilders"));
            decon.Set("maxSimultaneousBuilders", Reflect.Get(data, "maxBuilders"));
            bool wallOrRoad = sceneObject.CompareTag("Wall") || sceneObject.CompareTag("Road");
            decon.Set("buildSitePrefab", instant || wallOrRoad ? null : Reflect.Get(data, "deconstructSitePrefab"));
            decon.Set("constructionStartedPrefab", Reflect.Get(entry, "deconstructionStartedPrefab"));
            decon.Set("constructionProgressPrefabs", Reflect.Get(entry, "deconstructionProgressPrefabs"));
            int deconWork = (int)Reflect.Num(Reflect.Get(data, "workRequiredToDeconstruct"), 0);
            if (deconWork > 0)
            {
                var d = (IDictionary)Activator.CreateInstance(dictType);
                d[workUnit] = deconWork;
                decon.Set("materialsRequired", d);
            }
            decon.Set("buildsiteClearingMode", Nested("BuildingData", "SiteClearingMode", "NO_CLEAR"));
            var workArea = sceneObject.GetComponent(Reflect.FindType("WorkArea") ?? typeof(Transform));
            decon.Set("workAreaCenter", workArea != null && Reflect.FindType("WorkArea") != null ? Call(workArea, "GetCenter") : Vector3.zero);
            decon.Set("workEnabled", Reflect.Get(building, "isWorkEnabled") ?? true);
            decon.Set("userDefinedMaxWorkers", Reflect.Get(building, "userDefinedMaxWorkers") ?? 0);
            var date = Reflect.Get(building, "constructionDate");
            if (date != null) decon.Set("constructionDate", Activator.CreateInstance(date.GetType(), date));

            // construction side (new site)
            var con = new Struct(Reflect.FindType("ConstructionData"));
            con.Set("constructionType", Enum("ConstructionType", "RELOCATE"));
            con.Set("position", pos);
            con.Set("rotation", Quaternion.Euler(0, rotY, 0));
            con.Set("bounds", FootprintBounds(data, pos, rotY, cell));
            con.Set("terrainPos", pos);
            con.Set("terrainGridData", null);
            con.Set("buildGroup", Call(data, "GetBuildGroupString"));
            con.Set("defaultNumBuilders", Reflect.Get(data, "defaultBuilders"));
            con.Set("maxSimultaneousBuilders", Reflect.Get(data, "maxBuilders"));
            con.Set("mutePlacementSound", true);
            con.Set("buildsiteClearingMode", Reflect.Get(data, "buildsiteClearingMode"));
            con.Set("clearDetailsBorderWidth", Reflect.Get(data, "clearDetailsBorderWidth"));
            var mats = (IDictionary)Activator.CreateInstance(dictType);
            mats[Activator.CreateInstance(Reflect.FindType("ItemWorkUnit"))] = (int)Reflect.Num(Reflect.Get(data, "workRequiredToConstruct"), 0);
            int gold = (int)Reflect.Num(Reflect.Get(data, "goldRequiredToRelocate"), 0);
            if (gold > 0 && Reflect.FindType("ItemGoldIngot") != null) mats[Activator.CreateInstance(Reflect.FindType("ItemGoldIngot"))] = gold;
            con.Set("materialsRequired", mats);
            con.Set("parent", Parent(bm));
            con.Set("sceneObject", sceneObject);
            con.Set("prefabToConstruct", Call(entry, "PREFAB"));
            con.Set("buildSitePrefab", instant ? null : Reflect.Get(data, "buildSitePrefab"));
            con.Set("relocationDestinationPrefab", instant ? null : Reflect.Get(data, "destinationPrefab"));
            con.Set("emptyLotPrefab", Reflect.Get(entry, "emptyLotPrefab"));
            con.Set("constructionStartedPrefab", Reflect.Get(entry, "constructionStartedPrefab"));
            con.Set("constructionProgressPrefabs", Reflect.Get(entry, "constructionProgressPrefabs"));

            Call(bm, "Relocate", decon.Box, con.Box);
            return true;
        }

        // ------------------------------------------------------------------ production limit

        private static object ItemInfo(GameAccess g, string item)
        {
            var rm = g.Singleton("ResourceManager");
            var dict = Reflect.Get(rm, "itemNameToItemInfoDict") as IDictionary;
            if (dict == null) return null;
            if (dict.Contains(item)) return dict[item];
            foreach (DictionaryEntry e in dict)
                if (string.Equals(e.Key as string, item, StringComparison.OrdinalIgnoreCase)) return e.Value;
            return null;
        }

        private static bool ProductionLimit(GameAccess g, Dictionary<string, object> v, out string error)
        {
            error = null;
            string item = v["item"] as string;
            var info = ItemInfo(g, item);
            if (info == null) { error = "unknown item " + item; return false; }
            int limit = Convert.ToInt32(v["limit"]);
            Reflect.Set(info, "maxQuota", (uint)Math.Max(0, limit));
            Reflect.Set(info, "areProductionLimitsEnabled", limit > 0);
            return true;
        }

        public static bool ProductionLimitIs(GameAccess g, string item, int limit)
        {
            var info = ItemInfo(g, item);
            if (info == null) return false;
            bool on = Reflect.Get(info, "areProductionLimitsEnabled") is bool b && b;
            return limit > 0 ? on && (int)Reflect.Num(Reflect.Get(info, "maxQuota"), -1) == limit : !on;
        }

        // ------------------------------------------------------------------ crop rotation

        /// <summary>Crops per rotation year as the field's scheduler holds them (record names).</summary>
        public static List<List<string>> ReadRotation(object field)
        {
            var result = new List<List<string>>();
            var sched = Reflect.Get(field, "cropScheduler");
            foreach (var year in Reflect.Items(Reflect.Get(sched, "yearSchedulesRO")))
            {
                var crops = new List<string>();
                foreach (var item in Reflect.Items(Reflect.Get(year, "cropFieldScheduledItems")))
                {
                    string rn = Reflect.Text(Reflect.Get(item, "recordName"));
                    crops.Add(string.IsNullOrEmpty(rn) ? "maintenance" : rn);
                }
                result.Add(crops);
            }
            return result;
        }

        private static bool CropRotation(Dictionary<string, object> v, out string error)
        {
            error = null;
            var field = v["field"];
            var rotation = v["crop_rotation"] as List<List<string>>;
            if (field == null || rotation == null || rotation.Count == 0) { error = "field and rotation required"; return false; }
            var sched = Reflect.Get(field, "cropScheduler");
            var years = Reflect.Items(Reflect.Get(sched, "yearSchedulesRO"));
            for (int y = 0; y < years.Count && y < rotation.Count; y++)
            {
                foreach (var item in Reflect.Items(Reflect.Get(years[y], "cropFieldScheduledItems")))
                    Call(sched, "WipeScheduledItem", y, item);
                foreach (var crop in rotation[y])
                {
                    object added;
                    if (string.Equals(crop, "maintenance", StringComparison.OrdinalIgnoreCase) || string.Equals(crop, "fallow", StringComparison.OrdinalIgnoreCase))
                        added = Call(sched, "AttemptToAddFieldWorkToSchedule", y);
                    else added = Call(sched, "AttemptToAddCropToSchedule", y, crop);
                    if (added == null) { error = "no room for " + crop + " in year " + (y + 1); return false; }
                }
            }
            return true;
        }
    }
}
