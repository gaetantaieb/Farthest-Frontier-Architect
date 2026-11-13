using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Yaml
{
    public sealed class YamlException : Exception
    {
        public int Line { get; }
        public YamlException(string message, int line) : base("line " + line + ": " + message) { Line = line; }
    }

    /// <summary>
    /// The YAML subset the agent files use: block maps and sequences, flow collections
    /// (which may span lines), comments, quoted/plain scalars and literal blocks (<c>|</c>).
    /// Produces the same JObject/JArray trees as <see cref="Json"/>.
    /// </summary>
    public static class MiniYaml
    {
        private sealed class Line
        {
            public int Number;
            public int Indent;
            public string Text; // without indent and comment
            public string Raw;  // original line (for literal blocks)
        }

        public static object Parse(string text)
        {
            var lines = Preprocess(text ?? "");
            if (lines.Count == 0) return new JObject();
            int pos = 0;
            var result = ParseBlock(lines, ref pos, lines[0].Indent);
            if (pos < lines.Count) throw new YamlException("Unexpected indentation", lines[pos].Number);
            return result;
        }

        public static JObject ParseObject(string text)
        {
            var v = Parse(text);
            if (v is JObject o) return o;
            throw new YamlException("Top level must be a mapping", 1);
        }

        // ------------------------------------------------------------ preprocessing

        private static List<Line> Preprocess(string text)
        {
            var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lines = new List<Line>();
            for (int i = 0; i < raw.Length; i++)
            {
                string r = raw[i];
                if (r.IndexOf('\t') >= 0 && r.TrimStart(' ').StartsWith("\t"))
                    throw new YamlException("Tabs are not allowed for indentation", i + 1);
                string stripped = StripComment(r);
                if (stripped.Trim().Length == 0)
                {
                    lines.Add(new Line { Number = i + 1, Indent = -1, Text = "", Raw = r });
                    continue;
                }
                if (stripped.Trim() == "---" || stripped.Trim() == "...") continue;
                int indent = 0;
                while (indent < stripped.Length && stripped[indent] == ' ') indent++;
                lines.Add(new Line { Number = i + 1, Indent = indent, Text = stripped.Substring(indent).TrimEnd(), Raw = r });
            }
            // Join flow collections that continue over several lines.
            var joined = new List<Line>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Indent < 0) { joined.Add(l); continue; }
                int balance = FlowBalance(l.Text);
                while (balance > 0 && i + 1 < lines.Count)
                {
                    i++;
                    if (lines[i].Indent < 0) continue;
                    l = new Line { Number = l.Number, Indent = l.Indent, Text = l.Text + " " + lines[i].Text.Trim(), Raw = l.Raw };
                    balance = FlowBalance(l.Text);
                }
                if (balance > 0) throw new YamlException("Unclosed flow collection", l.Number);
                joined.Add(l);
            }
            return joined;
        }

        private static string StripComment(string s)
        {
            bool sq = false, dq = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' && !dq) sq = !sq;
                else if (c == '"' && !sq) dq = !dq;
                else if (c == '#' && !sq && !dq && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t')) return s.Substring(0, i);
            }
            return s;
        }

        private static int FlowBalance(string s)
        {
            int depth = 0; bool sq = false, dq = false;
            // only count when the value part starts a flow collection
            foreach (char c in s)
            {
                if (c == '\'' && !dq) sq = !sq;
                else if (c == '"' && !sq) dq = !dq;
                else if (!sq && !dq)
                {
                    if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}') depth--;
                }
            }
            return depth;
        }

        // ------------------------------------------------------------ block parsing

        private static void SkipBlank(List<Line> lines, ref int pos)
        {
            while (pos < lines.Count && lines[pos].Indent < 0) pos++;
        }

        private static object ParseBlock(List<Line> lines, ref int pos, int indent)
        {
            SkipBlank(lines, ref pos);
            if (pos >= lines.Count) return null;
            var first = lines[pos];
            if (IsSeqItem(first.Text)) return ParseSeq(lines, ref pos, first.Indent);
            return ParseMap(lines, ref pos, first.Indent);
        }

        private static bool IsSeqItem(string t) => t == "-" || t.StartsWith("- ");

        private static JArray ParseSeq(List<Line> lines, ref int pos, int indent)
        {
            var arr = new JArray();
            while (true)
            {
                SkipBlank(lines, ref pos);
                if (pos >= lines.Count) break;
                var l = lines[pos];
                if (l.Indent < indent) break;
                if (l.Indent > indent) throw new YamlException("Bad indentation in sequence", l.Number);
                if (!IsSeqItem(l.Text)) break;
                string rest = l.Text.Length > 1 ? l.Text.Substring(2).Trim() : "";
                pos++;
                if (rest.Length == 0)
                {
                    SkipBlank(lines, ref pos);
                    if (pos < lines.Count && lines[pos].Indent > indent) arr.Add(ParseBlock(lines, ref pos, lines[pos].Indent));
                    else arr.Add(null);
                    continue;
                }
                if (IsMapEntry(rest) && !StartsFlow(rest))
                {
                    // "- key: value" starts an inline mapping; following keys are indented further.
                    int itemIndent = indent + 2;
                    var synthetic = new Line { Number = l.Number, Indent = itemIndent, Text = rest, Raw = l.Raw };
                    lines.Insert(pos, synthetic);
                    arr.Add(ParseMap(lines, ref pos, itemIndent));
                    continue;
                }
                arr.Add(ParseScalarOrFlow(rest, l.Number));
            }
            return arr;
        }

        private static bool StartsFlow(string s) => s.StartsWith("[") || s.StartsWith("{");

        private static bool IsMapEntry(string t)
        {
            int k = FindKeyColon(t);
            return k > 0;
        }

        /// <summary>Index of the ':' that ends a mapping key, or -1.</summary>
        private static int FindKeyColon(string t)
        {
            if (t.StartsWith("\"") || t.StartsWith("'"))
            {
                char q = t[0];
                int end = t.IndexOf(q, 1);
                if (end > 0 && end + 1 < t.Length && t[end + 1] == ':' && (end + 2 == t.Length || t[end + 2] == ' ')) return end + 1;
                return -1;
            }
            if (StartsFlow(t)) return -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == ':' && (i + 1 == t.Length || t[i + 1] == ' ')) return i;
                if (t[i] == '[' || t[i] == '{') return -1;
            }
            return -1;
        }

        private static JObject ParseMap(List<Line> lines, ref int pos, int indent)
        {
            var map = new JObject();
            while (true)
            {
                SkipBlank(lines, ref pos);
                if (pos >= lines.Count) break;
                var l = lines[pos];
                if (l.Indent < indent) break;
                if (l.Indent > indent) throw new YamlException("Bad indentation in mapping", l.Number);
                if (IsSeqItem(l.Text)) break;
                int colon = FindKeyColon(l.Text);
                if (colon <= 0) throw new YamlException("Expected 'key: value'", l.Number);
                string key = Unquote(l.Text.Substring(0, colon).Trim());
                string rest = l.Text.Substring(colon + 1).Trim();
                pos++;
                if (rest == "|" || rest == "|-" || rest == ">" || rest == ">-")
                {
                    map.Set(key, ReadLiteral(lines, ref pos, indent, rest));
                    continue;
                }
                if (rest.Length == 0)
                {
                    SkipBlank(lines, ref pos);
                    if (pos < lines.Count && (lines[pos].Indent > indent || (lines[pos].Indent == indent && IsSeqItem(lines[pos].Text))))
                        map.Set(key, ParseBlock(lines, ref pos, lines[pos].Indent));
                    else
                        map.Set(key, null);
                    continue;
                }
                map.Set(key, ParseScalarOrFlow(rest, l.Number));
            }
            return map;
        }

        private static string ReadLiteral(List<Line> lines, ref int pos, int parentIndent, string style)
        {
            var sb = new StringBuilder();
            int blockIndent = -1;
            while (pos < lines.Count)
            {
                var l = lines[pos];
                if (l.Indent >= 0 && l.Indent <= parentIndent) break;
                string raw = l.Raw;
                if (l.Indent < 0) { sb.Append('\n'); pos++; continue; }
                if (blockIndent < 0) blockIndent = l.Indent;
                sb.Append(raw.Length > blockIndent ? raw.Substring(blockIndent) : raw.TrimStart()).Append('\n');
                pos++;
            }
            string s = sb.ToString();
            if (style.StartsWith(">")) s = s.Replace("\n", " ").Trim() + "\n";
            if (style.EndsWith("-")) s = s.TrimEnd('\n');
            else s = s.TrimEnd('\n') + "\n";
            return s;
        }

        // ------------------------------------------------------------ scalars and flow

        private static object ParseScalarOrFlow(string s, int line)
        {
            s = s.Trim();
            if (StartsFlow(s))
            {
                int p = 0;
                var v = ParseFlow(s, ref p, line);
                while (p < s.Length && s[p] == ' ') p++;
                if (p != s.Length) throw new YamlException("Unexpected text after flow collection", line);
                return v;
            }
            return ParseScalar(s);
        }

        private static object ParseFlow(string s, ref int p, int line)
        {
            while (p < s.Length && s[p] == ' ') p++;
            if (p >= s.Length) throw new YamlException("Unexpected end in flow collection", line);
            char c = s[p];
            if (c == '[')
            {
                p++;
                var arr = new JArray();
                while (true)
                {
                    while (p < s.Length && s[p] == ' ') p++;
                    if (p < s.Length && s[p] == ']') { p++; return arr; }
                    arr.Add(ParseFlow(s, ref p, line));
                    while (p < s.Length && s[p] == ' ') p++;
                    if (p >= s.Length) throw new YamlException("Unclosed '['", line);
                    if (s[p] == ',') { p++; continue; }
                    if (s[p] == ']') { p++; return arr; }
                    throw new YamlException("Expected ',' or ']'", line);
                }
            }
            if (c == '{')
            {
                p++;
                var map = new JObject();
                while (true)
                {
                    while (p < s.Length && s[p] == ' ') p++;
                    if (p < s.Length && s[p] == '}') { p++; return map; }
                    string key = ReadFlowToken(s, ref p, true);
                    while (p < s.Length && s[p] == ' ') p++;
                    if (p >= s.Length || s[p] != ':') throw new YamlException("Expected ':' in flow mapping", line);
                    p++;
                    map.Set(Unquote(key.Trim()), ParseFlow(s, ref p, line));
                    while (p < s.Length && s[p] == ' ') p++;
                    if (p >= s.Length) throw new YamlException("Unclosed '{'", line);
                    if (s[p] == ',') { p++; continue; }
                    if (s[p] == '}') { p++; return map; }
                    throw new YamlException("Expected ',' or '}'", line);
                }
            }
            return ParseScalar(ReadFlowToken(s, ref p, false).Trim());
        }

        private static string ReadFlowToken(string s, ref int p, bool isKey)
        {
            var sb = new StringBuilder();
            if (p < s.Length && (s[p] == '"' || s[p] == '\''))
            {
                char q = s[p];
                sb.Append(q); p++;
                while (p < s.Length && s[p] != q) { if (s[p] == '\\' && q == '"' && p + 1 < s.Length) sb.Append(s[p++]); sb.Append(s[p++]); }
                if (p < s.Length) { sb.Append(q); p++; }
                return sb.ToString();
            }
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ',' || c == ']' || c == '}') break;
                if (isKey && c == ':') break;
                if (c == ':' && p + 1 < s.Length && s[p + 1] == ' ' && !isKey) break;
                sb.Append(c); p++;
            }
            return sb.ToString();
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                string inner = s.Substring(1, s.Length - 2);
                if (s[0] == '\'') return inner.Replace("''", "'");
                return inner.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
            }
            return s;
        }

        public static object ParseScalar(string s)
        {
            if (s.Length == 0) return null;
            if (s[0] == '"' || s[0] == '\'') return Unquote(s);
            switch (s)
            {
                case "~":
                case "null":
                case "Null":
                case "NULL": return null;
                case "true":
                case "True":
                case "TRUE":
                case "yes":
                case "on": return true;
                case "false":
                case "False":
                case "FALSE":
                case "no":
                case "off": return false;
            }
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            if ((char.IsDigit(s[0]) || s[0] == '-' || s[0] == '.' || s[0] == '+') &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return s;
        }

        // ------------------------------------------------------------ writing

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            if (value is JObject o) WriteMap(sb, o, 0);
            else if (value is JArray a) WriteSeq(sb, a, 0);
            else sb.Append(ScalarText(value)).Append('\n');
            return sb.ToString();
        }

        private static void WriteMap(StringBuilder sb, JObject o, int indent)
        {
            foreach (var kv in o)
            {
                sb.Append(' ', indent).Append(KeyText(kv.Key)).Append(':');
                WriteNested(sb, kv.Value, indent);
            }
        }

        private static void WriteNested(StringBuilder sb, object v, int indent)
        {
            if (v is JObject child)
            {
                if (child.Count == 0) { sb.Append(" {}\n"); return; }
                if (IsSmallFlowMap(child)) { sb.Append(' ').Append(FlowText(child)).Append('\n'); return; }
                sb.Append('\n');
                WriteMap(sb, child, indent + 2);
            }
            else if (v is JArray arr)
            {
                if (IsScalarList(arr)) { sb.Append(' ').Append(FlowText(arr)).Append('\n'); return; }
                sb.Append('\n');
                WriteSeq(sb, arr, indent + 2);
            }
            else if (v is string s && s.Contains("\n"))
            {
                sb.Append(" |\n");
                foreach (var line in s.TrimEnd('\n').Split('\n')) sb.Append(' ', indent + 2).Append(line).Append('\n');
            }
            else sb.Append(' ').Append(ScalarText(v)).Append('\n');
        }

        private static void WriteSeq(StringBuilder sb, JArray a, int indent)
        {
            foreach (var item in a)
            {
                sb.Append(' ', indent).Append('-');
                if (item is JObject o && o.Count > 0 && !IsSmallFlowMap(o))
                {
                    var inner = new StringBuilder();
                    WriteMap(inner, o, indent + 2);
                    string text = inner.ToString();
                    sb.Append(' ').Append(text.Substring(indent + 2));
                }
                else WriteNested(sb, item, indent);
            }
        }

        private static bool IsScalarList(JArray a)
        {
            foreach (var i in a) if (i is JObject || i is JArray) return false;
            return true;
        }

        private static bool IsSmallFlowMap(JObject o)
        {
            if (o.Count > 4) return false;
            foreach (var kv in o) if (kv.Value is JObject || (kv.Value is JArray a && !IsScalarList(a)) || (kv.Value is string s && s.Contains("\n"))) return false;
            return true;
        }

        private static string FlowText(object v)
        {
            if (v is JArray a)
            {
                var parts = new List<string>();
                foreach (var i in a) parts.Add(FlowText(i));
                return "[" + string.Join(", ", parts) + "]";
            }
            if (v is JObject o)
            {
                var parts = new List<string>();
                foreach (var kv in o) parts.Add(KeyText(kv.Key) + ": " + FlowText(kv.Value));
                return "{ " + string.Join(", ", parts) + " }";
            }
            return ScalarText(v, true);
        }

        private static string KeyText(string k) => NeedsQuote(k, true) ? Quote(k) : k;

        private static string ScalarText(object v, bool inFlow = false)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case string s: return NeedsQuote(s, inFlow) ? Quote(s) : s;
                case double d: return d.ToString("0.######", CultureInfo.InvariantCulture);
                case float f: return f.ToString("0.######", CultureInfo.InvariantCulture);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private static bool NeedsQuote(string s, bool inFlow)
        {
            if (s.Length == 0) return true;
            if (ParseScalar(s) is string == false) return true; // would read back as number/bool/null
            if (s != s.Trim()) return true;
            if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(s[0]) >= 0 && !(s[0] == '-' && s.Length > 1 && s[1] != ' ')) return true;
            if (s.Contains(": ") || s.Contains(" #")) return true;
            if (inFlow && (s.IndexOf(',') >= 0 || s.IndexOf(']') >= 0 || s.IndexOf('}') >= 0 || s.IndexOf('[') >= 0 || s.IndexOf('{') >= 0)) return true;
            return false;
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
}
