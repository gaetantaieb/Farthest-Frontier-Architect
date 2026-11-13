using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FrontierArchitect.Core.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Milestone M0 tool: maps the current game build. Writes, under UserData/FrontierArchitect/probe/:
    ///  - types.txt       every Assembly-CSharp type with its fields, properties and methods
    ///  - candidates.md   types/members whose names match what the mod needs (relocate, road, field, save…)
    ///  - report.json     which gamemap.json entries resolve on this build, with live sample values
    /// Share these files (or run Claude Code on the repo with them) to complete gamemap.json.
    /// </summary>
    internal static class Probe
    {
        private static readonly string[] Keywords =
        {
            "relocat", "demolish", "deconstruct", "destroy", "construct", "buildsite", "placement", "place",
            "road", "path", "field", "crop", "rotation", "fertil", "weed", "farm", "worker", "employ", "profession",
            "job", "limit", "pause", "save", "load", "season", "month", "year", "time", "winter", "frozen", "raid",
            "storage", "item", "inventory", "townhall", "towncenter", "tier", "desirab", "radius", "circle", "terrain",
            "flatten", "clear", "tree", "rock", "ore", "resource"
        };

        public static string Run(GameAccess g)
        {
            Directory.CreateDirectory(Paths.Probe);
            var types = Reflect.GameTypes().Where(t => t != null).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder();
            sb.Append("# Assembly-CSharp types for game version ").Append(Application.version).Append(" (Unity ").Append(Application.unityVersion).Append(")\n");
            var candidates = new StringBuilder();
            candidates.Append("# Candidate members for gamemap.json — game ").Append(Application.version).Append("\n\n");
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                sb.Append('\n').Append(t.IsEnum ? "enum " : t.IsInterface ? "interface " : "class ").Append(t.FullName);
                if (t.BaseType != null && t.BaseType != typeof(object)) sb.Append(" : ").Append(t.BaseType.Name);
                sb.Append('\n');
                if (t.IsEnum)
                {
                    try { sb.Append("  = ").Append(string.Join(", ", Enum.GetNames(t))).Append('\n'); } catch { }
                    continue;
                }
                var hits = new List<string>();
                bool typeHit = Keywords.Any(k => t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                try
                {
                    foreach (var f in t.GetFields(F))
                    {
                        string line = "  field " + (f.IsStatic ? "static " : "") + Short(f.FieldType) + " " + f.Name;
                        sb.Append(line).Append('\n');
                        if (Keywords.Any(k => f.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) hits.Add(line);
                    }
                    foreach (var p in t.GetProperties(F))
                    {
                        string line = "  prop  " + Short(p.PropertyType) + " " + p.Name + (p.CanWrite ? " {get;set;}" : " {get;}");
                        sb.Append(line).Append('\n');
                        if (Keywords.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) hits.Add(line);
                    }
                    foreach (var m in t.GetMethods(F))
                    {
                        if (m.IsSpecialName) continue;
                        string line = "  method " + (m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " + m.Name + "(" +
                                      string.Join(", ", m.GetParameters().Select(x => Short(x.ParameterType) + " " + x.Name).ToArray()) + ")";
                        sb.Append(line).Append('\n');
                        if (Keywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) hits.Add(line);
                    }
                }
                catch (Exception e) { sb.Append("  (members unreadable: ").Append(e.Message).Append(")\n"); }
                if (typeHit || hits.Count >= 3)
                {
                    candidates.Append("## ").Append(t.FullName).Append(t.BaseType != null ? " : " + t.BaseType.Name : "").Append('\n');
                    foreach (var h in hits.Take(60)) candidates.Append(h).Append('\n');
                    candidates.Append('\n');
                }
            }
            File.WriteAllText(Path.Combine(Paths.Probe, "types.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(Paths.Probe, "candidates.md"), candidates.ToString());

            var report = Report(g);
            File.WriteAllText(Path.Combine(Paths.Probe, "report.json"), Json.Write(report, true));
            int resolvedTypes = report.Obj("types").Count(kv => (kv.Value as JObject)?.Str("resolved") != null);
            return "Probe written to " + Paths.Probe + ": " + types.Count + " types, " + resolvedTypes + "/" + report.Obj("types").Count + " gamemap types resolved.";
        }

        private static string Short(Type t)
        {
            if (t == null) return "void";
            if (!t.IsGenericType) return t.Name;
            return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Short).ToArray()) + ">";
        }

        public static JObject Report(GameAccess g)
        {
            var map = g.Map;
            var o = new JObject().Set("game_version", Application.version).Set("unity", Application.unityVersion)
                .Set("mapped_game_version", map.MappedGameVersion).Set("scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            var types = new JObject();
            foreach (var kv in map.Doc.Obj("types") ?? new JObject())
            {
                string matched;
                var t = Reflect.FindType((kv.Value as JArray).Strings(), out matched);
                int live = t != null && typeof(Object).IsAssignableFrom(t) ? Reflect.FindObjects(t).Count : 0;
                types.Set(kv.Key, new JObject().Set("resolved", matched).Set("live_objects", live));
            }
            o.Set("types", types);

            // members: test each candidate on a live sample of its owner concept
            var samples = new Dictionary<string, object>
            {
                { "game", g.Singleton("GameManager") },
                { "game_manager", g.Singleton("GameManager") },
                { "resource_manager", g.Singleton("ResourceManager") },
                { "time", g.Singleton("TimeManager") },
                { "weather", g.Singleton("WeatherManager") },
                { "agriculture", g.Singleton("AgricultureManager") },
                { "raid", g.Singleton("CombatManager") },
                { "global", map.ResolveType("GlobalAssets") },
            };
            var buildings = g.Buildings();
            samples["building"] = buildings.FirstOrDefault(b => g.Number(b, "building.workers_max", 0) > 0) ?? buildings.FirstOrDefault();
            samples["villager"] = g.Villagers().FirstOrDefault();
            samples["field"] = g.ObjectsOf("Field").FirstOrDefault();
            samples["road"] = g.ObjectsOf("Road").FirstOrDefault();
            samples["resource"] = g.ObjectsOf("NaturalResource").FirstOrDefault();
            var bdList = Reflect.Items(g.Member(map.ResolveType("GlobalAssets"), "global.building_data"));
            samples["building_data"] = bdList.FirstOrDefault();
            var members = new JObject();
            foreach (var kv in map.Doc.Obj("members") ?? new JObject())
            {
                string owner = kv.Key.Split('.')[0];
                object sample;
                samples.TryGetValue(owner, out sample);
                var entry = new JObject().Set("owner_sample", sample == null ? null : (sample as Type)?.Name ?? sample.GetType().Name);
                if (sample != null)
                {
                    foreach (var path in (kv.Value as JArray).Strings())
                    {
                        var v = Reflect.Get(sample, path);
                        if (v == null) continue;
                        entry.Set("resolved", path).Set("sample", Describe(v));
                        break;
                    }
                }
                members.Set(kv.Key, entry);
            }
            o.Set("members", members);

            var sampleDump = new JObject();
            foreach (var key in new[] { "building", "villager", "field", "road", "resource", "building_data", "time" })
            {
                object s;
                if (!samples.TryGetValue(key, out s) || s == null) continue;
                var f = new JObject();
                foreach (var kv in Reflect.Fields(s)) f.Set(kv.Key, kv.Value is string || kv.Value == null ? kv.Value : (object)kv.Value.ToString());
                sampleDump.Set(key, new JObject().Set("type", s.GetType().FullName).Set("fields", f));
            }
            o.Set("samples", sampleDump);

            var distinct = buildings.Select(b => g.BuildingType(b)).GroupBy(x => x).OrderBy(x => x.Key).Select(x => (object)(x.Key + " x" + x.Count()));
            o.Set("building_types_in_village", new JArray(distinct));
            var terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain != null)
            {
                var d = terrain.terrainData;
                o.Set("terrain", new JObject().Set("size", d.size.ToString()).Set("position", terrain.transform.position.ToString())
                    .Set("heightmap_resolution", d.heightmapResolution));
            }
            var ops = new JObject();
            var exec = new GameExecutor(g, null);
            foreach (var op in Core.Planning.Ops.All)
                ops.Set(op, new JObject().Set("status", map.Status(op).ToString()).Set("native", exec.OpUnavailableReason(op, Core.Execution.ExecMode.Native) ?? "available")
                    .Set("instant", exec.OpUnavailableReason(op, Core.Execution.ExecMode.Instant) ?? "available"));
            o.Set("ops", ops);
            return o;
        }

        private static string Describe(object v)
        {
            if (v == null) return null;
            if (v is System.Collections.ICollection c) return v.GetType().Name + "[" + c.Count + "]";
            if (v is Object uo) return v.GetType().Name + ":" + (uo != null ? uo.name : "null");
            string s = v.ToString();
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }
    }
}
