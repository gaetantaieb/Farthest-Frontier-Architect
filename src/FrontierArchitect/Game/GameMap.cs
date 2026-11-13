using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Game
{
    public enum OpStatus { Unmapped, Experimental, Proven, Dropped }

    /// <summary>
    /// gamemap.json: how the mod reaches the game's internals (types, member paths, method
    /// recipes per op). Shipped with the mod and synced to UserData like the agent files;
    /// gamemap.local.json holds the player's own results (ops proven on their machine) and wins.
    /// </summary>
    internal sealed class GameMap
    {
        public JObject Doc = new JObject();
        public string MappedGameVersion = "";
        public string Scene = "Frontier";
        public float TileSize = 1f;
        public float WaterHeight = float.NaN;
        public int MaxGrid = 512;

        public static GameMap Load()
        {
            var m = new GameMap();
            try
            {
                if (File.Exists(Paths.GameMap)) m.Doc = Json.ParseObject(File.ReadAllText(Paths.GameMap));
            }
            catch (Exception e) { Log.Warn("gamemap.json is invalid (" + e.Message + "); using the built-in copy."); }
            if (m.Doc.Count == 0)
            {
                var defaults = EmbeddedDefaults.Read("defaults/");
                string text;
                if (defaults.TryGetValue("gamemap.json", out text)) m.Doc = Json.ParseObject(text);
            }
            try
            {
                if (File.Exists(Paths.GameMapLocal)) AgentLoader.DeepMerge(m.Doc, Json.ParseObject(File.ReadAllText(Paths.GameMapLocal)));
            }
            catch (Exception e) { Log.Warn("gamemap.local.json is invalid: " + e.Message); }
            m.MappedGameVersion = m.Doc.Str("mapped_game_version", "");
            m.Scene = m.Doc.Str("scene", "Frontier");
            var grid = m.Doc.Obj("grid");
            m.TileSize = (float)grid.Num("tile_size", 1.0);
            m.WaterHeight = grid != null && grid.ContainsKey("water_height") && grid["water_height"] != null ? (float)grid.Num("water_height") : float.NaN;
            m.MaxGrid = grid.Int("max_cells_per_side", 512);
            return m;
        }

        public List<string> TypeCandidates(string concept) => Doc.Obj("types")?.Arr(concept)?.Strings() ?? new List<string> { concept };

        public List<string> MemberCandidates(string concept) => Doc.Obj("members")?.Arr(concept)?.Strings() ?? new List<string>();

        public Type ResolveType(string concept)
        {
            string matched;
            return Reflect.FindType(TypeCandidates(concept), out matched);
        }

        public JObject Op(string op) => Doc.Obj("ops")?.Obj(op);

        public OpStatus Status(string op)
        {
            var o = Op(op);
            switch ((o?.Str("status") ?? "unmapped").ToLowerInvariant())
            {
                case "proven": return OpStatus.Proven;
                case "experimental": return OpStatus.Experimental;
                case "dropped": return OpStatus.Dropped;
                default: return OpStatus.Unmapped;
            }
        }

        public JArray Recipe(string op, bool instant)
        {
            var o = Op(op);
            if (o == null) return null;
            var r = o[instant ? "instant" : "native"];
            if (r is string s && s == "same") r = o[instant ? "native" : "instant"];
            return r as JArray;
        }

        public JObject Verify(string op) => Op(op)?.Obj("verify");

        public JArray Procedure(string name) => Doc.Obj("procedures")?.Arr(name);

        /// <summary>Records an op result in gamemap.local.json (M0: proven / dropped on this install).</summary>
        public void RecordLocal(string op, OpStatus status, string note)
        {
            JObject local = new JObject();
            try { if (File.Exists(Paths.GameMapLocal)) local = Json.ParseObject(File.ReadAllText(Paths.GameMapLocal)); } catch { }
            local.ObjOrNew("ops").ObjOrNew(op).Set("status", status.ToString().ToLowerInvariant()).Set("note", note)
                 .Set("game_version", UnityEngine.Application.version);
            File.WriteAllText(Paths.GameMapLocal, Json.Write(local, true));
            Doc.ObjOrNew("ops").ObjOrNew(op).Set("status", status.ToString().ToLowerInvariant());
        }
    }

    /// <summary>Executes recipe steps from gamemap.json against live objects.</summary>
    internal sealed class RecipeRunner
    {
        private readonly GameMap _map;
        public RecipeRunner(GameMap map) { _map = map; }

        public bool Run(JArray steps, Dictionary<string, object> vars, out string error)
        {
            error = null;
            if (steps == null || steps.Count == 0) { error = "no recipe"; return false; }
            foreach (var s in steps.Objects())
            {
                if (s.Str("call") != null)
                {
                    object target; string method;
                    if (!Split(s.Str("call"), vars, out target, out method, out error)) return false;
                    var args = (s.Arr("args") ?? new JArray()).Select(a => Arg(a, vars)).ToArray();
                    object result;
                    if (!Reflect.Invoke(target, method, args, out result, out error)) return false;
                    if (s.Str("store") != null) vars[s.Str("store")] = result;
                    if (s.Bool("require_true") && !(result is bool b && b)) { error = s.Str("call") + " returned false"; return false; }
                }
                else if (s.Str("set") != null)
                {
                    object target; string member;
                    if (!Split(s.Str("set"), vars, out target, out member, out error)) return false;
                    if (!Reflect.Set(target, member, Arg(s["value"], vars))) { error = "could not set " + s.Str("set"); return false; }
                }
                else if (s.Str("get") != null)
                {
                    object target; string member;
                    if (!Split(s.Str("get"), vars, out target, out member, out error)) return false;
                    var v = Reflect.Get(target, member);
                    if (v == null && s.Bool("required", true)) { error = s.Str("get") + " is null"; return false; }
                    vars[s.Str("store") ?? "_"] = v;
                }
                else { error = "unknown recipe step " + Json.Write(s); return false; }
            }
            return true;
        }

        /// <summary>Checks that every call/set target and member of a recipe exists, without running it.</summary>
        public bool Resolves(JArray steps, Dictionary<string, object> vars, out string error)
        {
            error = null;
            if (steps == null || steps.Count == 0) { error = "no recipe"; return false; }
            foreach (var s in steps.Objects())
            {
                string expr = s.Str("call") ?? s.Str("set") ?? s.Str("get");
                if (expr == null) { error = "bad step"; return false; }
                object target; string last;
                if (!Split(expr, vars, out target, out last, out error)) return false;
                var t = target as Type ?? target?.GetType();
                if (t == null) { error = expr + ": no target"; return false; }
                bool found = s.Str("call") != null
                    ? t.GetMethods(Reflect.All).Any(m => m.Name == last)
                    : Reflect.HasMember(t, last);
                if (!found) { error = t.Name + " has no " + last; return false; }
            }
            return true;
        }

        /// <summary>"building.workPlace.SetWorkers" → target object + final name.</summary>
        private bool Split(string expr, Dictionary<string, object> vars, out object target, out string last, out string error)
        {
            target = null; error = null;
            var parts = expr.Split('.');
            last = parts[parts.Length - 1];
            if (parts.Length < 2) { error = "expression needs a target: " + expr; return false; }
            string head = parts[0];
            object root;
            if (head.StartsWith("$")) vars.TryGetValue(head.Substring(1), out root);
            else if (vars.TryGetValue(head, out root)) { }
            else
            {
                var t = _map.ResolveType(head) ?? Reflect.FindType(head);
                if (t == null) { error = "type " + head + " not found"; return false; }
                if (parts.Length >= 2 && parts[1] == "static") { root = t; parts = new[] { parts[0] }.Concat(parts.Skip(2)).ToArray(); }
                else root = (object)Reflect.FindObject(t) ?? t;
            }
            if (root == null) { error = head + " is not available"; return false; }
            string mid = string.Join(".", parts.Skip(1).Take(parts.Length - 2).ToArray());
            target = mid.Length == 0 ? root : Reflect.Get(root, mid);
            if (target == null) { error = expr + ": " + mid + " is null"; return false; }
            return true;
        }

        private static object Arg(object a, Dictionary<string, object> vars)
        {
            if (a is string s && s.StartsWith("$"))
            {
                object v;
                return vars.TryGetValue(s.Substring(1), out v) ? v : null;
            }
            if (a is long l) return (int)l;
            if (a is double d) return (float)d;
            return a;
        }
    }
}
