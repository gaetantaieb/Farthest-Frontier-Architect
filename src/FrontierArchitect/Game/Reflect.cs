using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FrontierArchitect.Game
{
    /// <summary>
    /// Reflection over the game's Assembly-CSharp. The mod never links against game types, so it
    /// builds without the game and survives renames: names come from gamemap.json.
    /// </summary>
    internal static class Reflect
    {
        public const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private static readonly Dictionary<string, MemberInfo> _members = new Dictionary<string, MemberInfo>();
        private static Assembly _game;

        public static Assembly GameAssembly
        {
            get
            {
                if (_game != null) return _game;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Assembly-CSharp") { _game = a; break; }
                return _game;
            }
        }

        public static IEnumerable<Type> GameTypes()
        {
            var asm = GameAssembly;
            if (asm == null) yield break;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types) yield return t;
        }

        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type t;
            lock (_types) if (_types.TryGetValue(name, out t)) return t;
            t = GameAssembly?.GetType(name, false);
            if (t == null) t = GameTypes().FirstOrDefault(x => x.Name == name || x.FullName == name);
            if (t == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = a.GetType(name, false);
                    if (t != null) break;
                }
            lock (_types) _types[name] = t;
            return t;
        }

        public static Type FindType(IEnumerable<string> candidates, out string matched)
        {
            matched = null;
            foreach (var c in candidates ?? Enumerable.Empty<string>())
            {
                var t = FindType(c);
                if (t != null) { matched = c; return t; }
            }
            return null;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            string key = type.FullName + "|" + name;
            MemberInfo m;
            lock (_members) if (_members.TryGetValue(key, out m)) return m;
            m = null;
            for (var cur = type; cur != null && m == null; cur = cur.BaseType)
            {
                m = cur.GetField(name, All | BindingFlags.DeclaredOnly);
                if (m == null)
                {
                    try { m = cur.GetProperty(name, All | BindingFlags.DeclaredOnly); }
                    catch (AmbiguousMatchException) { m = cur.GetProperties(All | BindingFlags.DeclaredOnly).FirstOrDefault(p => p.Name == name); }
                }
            }
            lock (_members) _members[key] = m;
            return m;
        }

        public static bool HasMember(Type type, string name) => type != null && FindMember(type, name) != null;

        /// <summary>
        /// Reads a dotted member path ("buildingData.identifier", "workers.Count"). Supports
        /// fields, properties, zero-argument methods ("GetName()") and Unity's GetComponent via
        /// "&lt;TypeName&gt;". Returns null when any step is missing.
        /// </summary>
        public static object Get(object root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;
            object cur = root;
            foreach (var raw in path.Split('.'))
            {
                if (cur == null) return null;
                string step = raw.Trim();
                if (step.Length == 0) continue;
                try
                {
                    if (step.StartsWith("<") && step.EndsWith(">"))
                    {
                        var ct = FindType(step.Substring(1, step.Length - 2));
                        var comp = cur as Component;
                        var go = cur as GameObject ?? comp?.gameObject;
                        cur = ct == null || go == null ? null : go.GetComponent(ct) ?? go.GetComponentInChildren(ct);
                        continue;
                    }
                    if (step.EndsWith("()"))
                    {
                        var mi = cur.GetType().GetMethods(All).FirstOrDefault(x => x.Name == step.Substring(0, step.Length - 2) && x.GetParameters().Length == 0);
                        cur = mi?.Invoke(mi.IsStatic ? null : cur, null);
                        continue;
                    }
                    if (step == "Count" && cur is ICollection col) { cur = col.Count; continue; }
                    if (step == "Length" && cur is Array arr) { cur = arr.Length; continue; }
                    var type = cur as Type ?? cur.GetType();
                    var target = cur is Type ? null : cur;
                    var m = FindMember(type, step);
                    if (m is FieldInfo f) cur = f.GetValue(f.IsStatic ? null : target);
                    else if (m is PropertyInfo p && p.GetIndexParameters().Length == 0) cur = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : target, null);
                    else return null;
                }
                catch { return null; }
            }
            return cur;
        }

        public static object GetFirst(object root, IEnumerable<string> paths, out string matched)
        {
            matched = null;
            if (root == null || paths == null) return null;
            foreach (var p in paths)
            {
                var v = Get(root, p);
                if (v != null) { matched = p; return v; }
            }
            return null;
        }

        public static bool Set(object root, string path, object value)
        {
            if (root == null || string.IsNullOrEmpty(path)) return false;
            int dot = path.LastIndexOf('.');
            object owner = dot < 0 ? root : Get(root, path.Substring(0, dot));
            string name = dot < 0 ? path : path.Substring(dot + 1);
            if (owner == null) return false;
            var type = owner as Type ?? owner.GetType();
            var target = owner is Type ? null : owner;
            var m = FindMember(type, name);
            try
            {
                if (m is FieldInfo f) { f.SetValue(f.IsStatic ? null : target, Convert(value, f.FieldType)); return true; }
                if (m is PropertyInfo p && p.CanWrite) { p.SetValue(target, Convert(value, p.PropertyType), null); return true; }
            }
            catch (Exception e) { Log.Warn("set " + path + " failed: " + e.Message); }
            return false;
        }

        /// <summary>Invokes a method by name, picking the overload whose arity matches and whose args convert.</summary>
        public static bool Invoke(object targetOrType, string method, object[] args, out object result, out string error)
        {
            result = null; error = null;
            var type = targetOrType as Type ?? targetOrType?.GetType();
            if (type == null) { error = "no target for " + method; return false; }
            var target = targetOrType is Type ? null : targetOrType;
            var candidates = new List<MethodInfo>();
            for (var cur = type; cur != null; cur = cur.BaseType)
                candidates.AddRange(cur.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == method && m.GetParameters().Length == args.Length));
            if (candidates.Count == 0)
            {
                // also allow trailing optional parameters
                for (var cur = type; cur != null; cur = cur.BaseType)
                    candidates.AddRange(cur.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == method &&
                        m.GetParameters().Length > args.Length && m.GetParameters().Skip(args.Length).All(p => p.IsOptional)));
            }
            if (candidates.Count == 0) { error = type.Name + "." + method + "(" + args.Length + " args) not found"; return false; }
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                var converted = new object[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i >= args.Length) { converted[i] = ps[i].DefaultValue; continue; }
                    try { converted[i] = Convert(args[i], ps[i].ParameterType); }
                    catch { ok = false; break; }
                }
                if (!ok) continue;
                if (!m.IsStatic && target == null) { error = method + " needs an instance"; continue; }
                try
                {
                    result = m.Invoke(m.IsStatic ? null : target, converted);
                    return true;
                }
                catch (TargetInvocationException e) { error = method + " threw " + (e.InnerException?.Message ?? e.Message); return false; }
                catch (Exception e) { error = method + ": " + e.Message; }
            }
            if (error == null) error = "arguments do not fit " + type.Name + "." + method;
            return false;
        }

        public static object Convert(object v, Type to)
        {
            if (to == typeof(object)) return v;
            if (v == null) return to.IsValueType ? Activator.CreateInstance(to) : null;
            if (to.IsInstanceOfType(v)) return v;
            if (to.IsEnum)
            {
                if (v is string s) return Enum.Parse(to, s, true);
                return Enum.ToObject(to, System.Convert.ToInt64(v, CultureInfo.InvariantCulture));
            }
            if (to == typeof(Vector3) && v is double[] d3) return new Vector3((float)d3[0], (float)d3[1], (float)d3[2]);
            if (to == typeof(Quaternion) && (v is double || v is float || v is int || v is long)) return Quaternion.Euler(0, System.Convert.ToSingle(v, CultureInfo.InvariantCulture), 0);
            if (to == typeof(string)) return System.Convert.ToString(v, CultureInfo.InvariantCulture);
            if (v is IConvertible) return System.Convert.ChangeType(v, to, CultureInfo.InvariantCulture);
            throw new InvalidCastException(v.GetType().Name + " -> " + to.Name);
        }

        public static double Num(object v, double def = double.NaN)
        {
            if (v == null) return def;
            try
            {
                if (v is bool b) return b ? 1 : 0;
                if (v is Enum) return System.Convert.ToDouble((int)v);
                if (v is IConvertible) return System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch { }
            return def;
        }

        public static string Text(object v) => v == null ? null : (v as string ?? v.ToString());

        public static List<object> Items(object enumerable)
        {
            var list = new List<object>();
            if (enumerable is string || !(enumerable is IEnumerable e)) return list;
            try { foreach (var x in e) list.Add(x); } catch { }
            return list;
        }

        /// <summary>Live Unity objects of a game type (scene objects only).</summary>
        public static List<Object> FindObjects(Type t)
        {
            var list = new List<Object>();
            if (t == null || !typeof(Object).IsAssignableFrom(t)) return list;
            try { list.AddRange(Object.FindObjectsOfType(t)); } catch { }
            return list;
        }

        public static Object FindObject(Type t)
        {
            if (t == null) return null;
            var inst = Get(t, "Instance") ?? Get(t, "instance") ?? Get(t, "_instance");
            if (inst is Object o && o != null) return o;
            try { return Object.FindObjectOfType(t); } catch { return null; }
        }

        /// <summary>Primitive-ish fields of an object (for probes and the knowledge pack).</summary>
        public static Dictionary<string, object> Fields(object o, int maxCollections = 20)
        {
            var d = new Dictionary<string, object>();
            if (o == null) return d;
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Component) && t != typeof(Object) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (d.ContainsKey(f.Name)) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    var ft = f.FieldType;
                    if (v == null) { d[f.Name] = null; continue; }
                    if (ft.IsPrimitive || ft.IsEnum || v is string || v is decimal) d[f.Name] = v is Enum ? v.ToString() : v;
                    else if (v is Vector3 vec) d[f.Name] = "(" + vec.x.ToString("0.##", CultureInfo.InvariantCulture) + "," + vec.y.ToString("0.##", CultureInfo.InvariantCulture) + "," + vec.z.ToString("0.##", CultureInfo.InvariantCulture) + ")";
                    else if (v is Vector2Int v2) d[f.Name] = "(" + v2.x + "," + v2.y + ")";
                    else if (v is ICollection c) d[f.Name] = ft.Name + "[" + c.Count + "]";
                    else if (v is Object uo) d[f.Name] = ft.Name + ":" + (uo != null ? uo.name : "null");
                    else d[f.Name] = ft.Name;
                }
            return d;
        }
    }
}
