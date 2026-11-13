using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FrontierArchitect.Core.Data
{
    /// <summary>
    /// Ordered JSON object. Key order is preserved so serialised prompts are byte-stable
    /// between runs (prompt caching depends on identical prefixes).
    /// </summary>
    public sealed class JObject : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        public int Count => _keys.Count;
        public IReadOnlyList<string> Keys => _keys;

        public object this[string key]
        {
            get { object v; return _values.TryGetValue(key, out v) ? v : null; }
            set { Set(key, value); }
        }

        public JObject Set(string key, object value)
        {
            if (!_values.ContainsKey(key)) _keys.Add(key);
            _values[key] = value;
            return this;
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value);

        public bool Remove(string key)
        {
            if (!_values.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var k in _keys) yield return new KeyValuePair<string, object>(k, _values[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public JObject Clone() => (JObject)Json.DeepClone(this);

        public override string ToString() => Json.Write(this);
    }

    public sealed class JArray : List<object>
    {
        public JArray() { }
        public JArray(IEnumerable<object> items) : base(items) { }
        public override string ToString() => Json.Write(this);
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>Small dependency-free JSON reader/writer (Unity's Mono has no System.Text.Json).</summary>
    public static class Json
    {
        // ---------------------------------------------------------------- parse

        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("null input");
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue(0);
            p.SkipWs();
            if (!p.End) throw new JsonException("Unexpected trailing characters at " + p.Pos);
            return v;
        }

        public static JObject ParseObject(string text)
        {
            var o = Parse(text) as JObject;
            if (o == null) throw new JsonException("Expected a JSON object");
            return o;
        }

        public static bool TryParse(string text, out object value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (Exception e) { value = null; error = e.Message; return false; }
        }

        /// <summary>
        /// Finds the first balanced JSON object in free text (used when a model answers in prose
        /// with an embedded JSON block instead of calling the submit tool).
        /// </summary>
        public static JObject ExtractFirstObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
            {
                int depth = 0; bool inStr = false; bool esc = false;
                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') inStr = true;
                    else if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            object v; string err;
                            if (TryParse(text.Substring(start, i - start + 1), out v, out err) && v is JObject jo) return jo;
                            break;
                        }
                    }
                }
            }
            return null;
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') Pos++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > 512) throw new JsonException("JSON nested too deeply");
                if (End) throw new JsonException("Unexpected end of JSON");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new JsonException("Unexpected character '" + c + "' at " + Pos);
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0)
                    throw new JsonException("Expected '" + word + "' at " + Pos);
                Pos += word.Length;
            }

            private JObject ReadObject(int depth)
            {
                var o = new JObject();
                Pos++; SkipWs();
                if (!End && _s[Pos] == '}') { Pos++; return o; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[Pos] != '"') throw new JsonException("Expected property name at " + Pos);
                    string key = ReadString();
                    SkipWs();
                    if (End || _s[Pos] != ':') throw new JsonException("Expected ':' at " + Pos);
                    Pos++; SkipWs();
                    o.Set(key, ReadValue(depth + 1));
                    SkipWs();
                    if (End) throw new JsonException("Unterminated object");
                    char c = _s[Pos++];
                    if (c == ',') continue;
                    if (c == '}') return o;
                    throw new JsonException("Expected ',' or '}' at " + (Pos - 1));
                }
            }

            private JArray ReadArray(int depth)
            {
                var a = new JArray();
                Pos++; SkipWs();
                if (!End && _s[Pos] == ']') { Pos++; return a; }
                while (true)
                {
                    SkipWs();
                    a.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (End) throw new JsonException("Unterminated array");
                    char c = _s[Pos++];
                    if (c == ',') continue;
                    if (c == ']') return a;
                    throw new JsonException("Expected ',' or ']' at " + (Pos - 1));
                }
            }

            private string ReadString()
            {
                Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new JsonException("Unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new JsonException("Unterminated escape");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new JsonException("Bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new JsonException("Bad escape '\\" + e + "'");
                    }
                }
            }

            private object ReadNumber()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                bool isInt = true;
                while (!End)
                {
                    char c = _s[Pos];
                    if (c >= '0' && c <= '9') { Pos++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isInt = false; Pos++; continue; }
                    break;
                }
                string num = _s.Substring(start, Pos - start);
                if (isInt && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw new JsonException("Bad number '" + num + "'");
            }
        }

        // ---------------------------------------------------------------- write

        public static string Write(object value, bool pretty = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, bool pretty, int level)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteValue(StringBuilder sb, object v, bool pretty, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case JObject o: WriteObject(sb, o, pretty, level); return;
                case double d: WriteDouble(sb, d); return;
                case float f: WriteDouble(sb, f); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case sbyte _:
                case uint _:
                case ulong _:
                case ushort _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case char c: WriteString(sb, c.ToString()); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case IJsonWritable w: WriteValue(sb, w.ToJson(), pretty, level); return;
                case IDictionary<string, object> dict:
                    {
                        var o = new JObject();
                        foreach (var kv in dict) o.Set(kv.Key, kv.Value);
                        WriteObject(sb, o, pretty, level);
                        return;
                    }
                case IEnumerable en:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (var item in en)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            Indent(sb, pretty, level + 1);
                            WriteValue(sb, item, pretty, level + 1);
                        }
                        if (!first) Indent(sb, pretty, level);
                        sb.Append(']');
                        return;
                    }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void WriteObject(StringBuilder sb, JObject o, bool pretty, int level)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in o)
            {
                if (!first) sb.Append(',');
                first = false;
                Indent(sb, pretty, level + 1);
                WriteString(sb, kv.Key);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, kv.Value, pretty, level + 1);
            }
            if (!first) Indent(sb, pretty, level);
            sb.Append('}');
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (Math.Abs(d - Math.Round(d)) < 1e-12 && Math.Abs(d) < 1e15)
            {
                sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- helpers

        public static object DeepClone(object v)
        {
            switch (v)
            {
                case JObject o:
                    {
                        var c = new JObject();
                        foreach (var kv in o) c.Set(kv.Key, DeepClone(kv.Value));
                        return c;
                    }
                case JArray a:
                    {
                        var c = new JArray();
                        foreach (var item in a) c.Add(DeepClone(item));
                        return c;
                    }
                default: return v;
            }
        }

        public static bool IsNumber(object v) =>
            v is long || v is int || v is double || v is float || v is decimal || v is short || v is byte || v is uint || v is ulong;

        public static double ToDouble(object v, double def = 0)
        {
            if (v == null) return def;
            if (IsNumber(v)) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (v is bool b) return b ? 1 : 0;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return def;
        }

        public static long ToLong(object v, long def = 0)
        {
            if (v == null) return def;
            if (v is long l) return l;
            if (IsNumber(v)) return (long)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r)) return r;
            return def;
        }
    }

    /// <summary>Types that know how to turn themselves into JObject/JArray/primitive trees.</summary>
    public interface IJsonWritable
    {
        object ToJson();
    }

    public static class JsonExtensions
    {
        public static string Str(this JObject o, string key, string def = null)
        {
            if (o == null) return def;
            var v = o[key];
            if (v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(this JObject o, string key, double def = 0) => o == null ? def : Json.ToDouble(o[key], def);
        public static int Int(this JObject o, string key, int def = 0) => o == null ? def : (int)Json.ToLong(o[key], def);
        public static long Long(this JObject o, string key, long def = 0) => o == null ? def : Json.ToLong(o[key], def);

        public static bool Bool(this JObject o, string key, bool def = false)
        {
            if (o == null) return def;
            var v = o[key];
            if (v is bool b) return b;
            if (v is string s) return s == "true" || s == "on" || s == "yes";
            if (Json.IsNumber(v)) return Json.ToDouble(v) != 0;
            return def;
        }

        public static JObject Obj(this JObject o, string key) => o?[key] as JObject;
        public static JArray Arr(this JObject o, string key) => o?[key] as JArray;

        public static JObject ObjOrNew(this JObject o, string key)
        {
            var v = o[key] as JObject;
            if (v == null) { v = new JObject(); o.Set(key, v); }
            return v;
        }

        public static IEnumerable<JObject> Objects(this JArray a)
        {
            if (a == null) yield break;
            foreach (var item in a) if (item is JObject o) yield return o;
        }

        public static List<string> Strings(this JArray a)
        {
            var list = new List<string>();
            if (a == null) return list;
            foreach (var item in a) if (item != null) list.Add(item as string ?? Convert.ToString(item, CultureInfo.InvariantCulture));
            return list;
        }

        public static JArray ToJArray<T>(this IEnumerable<T> items)
        {
            var a = new JArray();
            foreach (var i in items) a.Add(i);
            return a;
        }
    }
}
