using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FrontierArchitect.Core.Data
{
    /// <summary>
    /// Minimal path query over JObject/JArray trees, used by the <c>query</c> tool so Claude can
    /// read any snapshot field without a new tool. Syntax:
    /// <list type="bullet">
    /// <item><c>metrics.food.months_of_food</c> – member access</item>
    /// <item><c>buildings[3]</c> – index; <c>buildings[-1]</c> – from the end</item>
    /// <item><c>buildings[*].type</c> – map over all items</item>
    /// <item><c>buildings[type=Storehouse]</c> – filter (string or number equality)</item>
    /// <item><c>buildings[workers.assigned&lt;2]</c> – numeric filters: &lt; &gt; &lt;= &gt;= !=</item>
    /// <item><c>buildings.length</c> / <c>.keys</c> – count or key list</item>
    /// </list>
    /// </summary>
    public static class JsonPath
    {
        public static object Select(object root, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Trim() == "$" || path.Trim() == ".") return root;
            var tokens = Tokenize(path.Trim().TrimStart('$').TrimStart('.'));
            object current = root;
            bool mapped = false; // once a wildcard/filter applies, later steps map over the list
            foreach (var t in tokens)
            {
                if (mapped && current is JArray list && !(t.Kind == TokKind.Member && (t.Text == "length" || t.Text == "count")))
                {
                    var next = new JArray();
                    foreach (var item in list)
                    {
                        var r = Apply(item, t, out bool m2);
                        if (m2 && r is JArray inner) next.AddRange(inner);
                        else if (r != null) next.Add(r);
                    }
                    current = next;
                    continue;
                }
                current = Apply(current, t, out bool m);
                mapped |= m;
                if (current == null) return null;
            }
            return current;
        }

        private enum TokKind { Member, Index, Wildcard, Filter }

        private struct Tok
        {
            public TokKind Kind;
            public string Text;
            public int Index;
            public string FilterPath, FilterOp, FilterValue;
        }

        private static object Apply(object current, Tok t, out bool mapped)
        {
            mapped = false;
            switch (t.Kind)
            {
                case TokKind.Member:
                    if (current is JObject o)
                    {
                        if (o.ContainsKey(t.Text)) return o[t.Text];
                        if (t.Text == "keys") return o.Keys.ToJArray();
                        if (t.Text == "length" || t.Text == "count") return (long)o.Count;
                        return null;
                    }
                    if (current is JArray a && (t.Text == "length" || t.Text == "count")) return (long)a.Count;
                    if (current is string s && t.Text == "length") return (long)s.Length;
                    return null;
                case TokKind.Index:
                    if (current is JArray arr)
                    {
                        int i = t.Index < 0 ? arr.Count + t.Index : t.Index;
                        return i >= 0 && i < arr.Count ? arr[i] : null;
                    }
                    return null;
                case TokKind.Wildcard:
                    mapped = true;
                    if (current is JArray wa) return wa;
                    if (current is JObject wo)
                    {
                        var vals = new JArray();
                        foreach (var kv in wo) vals.Add(kv.Value);
                        return vals;
                    }
                    return null;
                case TokKind.Filter:
                    mapped = true;
                    if (!(current is JArray fa)) return null;
                    var res = new JArray();
                    foreach (var item in fa)
                        if (Matches(item, t)) res.Add(item);
                    return res;
            }
            return null;
        }

        private static bool Matches(object item, Tok t)
        {
            var v = Select(item, t.FilterPath);
            string want = t.FilterValue;
            bool numeric = Json.IsNumber(v) && double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out double wn);
            double have = Json.ToDouble(v);
            double target = Json.ToDouble(want);
            switch (t.FilterOp)
            {
                case "=":
                case "==":
                    if (numeric) return Math.Abs(have - target) < 1e-9;
                    if (v is bool b) return b.ToString().Equals(want, StringComparison.OrdinalIgnoreCase);
                    return v != null && string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), want, StringComparison.OrdinalIgnoreCase);
                case "!=":
                    if (numeric) return Math.Abs(have - target) >= 1e-9;
                    return v == null || !string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), want, StringComparison.OrdinalIgnoreCase);
                case "<": return numeric && have < target;
                case ">": return numeric && have > target;
                case "<=": return numeric && have <= target;
                case ">=": return numeric && have >= target;
                case "~": return v != null && Convert.ToString(v, CultureInfo.InvariantCulture).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        private static List<Tok> Tokenize(string path)
        {
            var list = new List<Tok>();
            int i = 0;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.') { i++; continue; }
                if (c == '[')
                {
                    int end = path.IndexOf(']', i);
                    if (end < 0) throw new ArgumentException("Unclosed '[' in path");
                    string inner = path.Substring(i + 1, end - i - 1).Trim();
                    i = end + 1;
                    if (inner == "*") { list.Add(new Tok { Kind = TokKind.Wildcard }); continue; }
                    if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    {
                        list.Add(new Tok { Kind = TokKind.Index, Index = idx });
                        continue;
                    }
                    if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\''))
                    {
                        list.Add(new Tok { Kind = TokKind.Member, Text = inner.Substring(1, inner.Length - 2) });
                        continue;
                    }
                    list.Add(ParseFilter(inner));
                    continue;
                }
                var sb = new StringBuilder();
                while (i < path.Length && path[i] != '.' && path[i] != '[') sb.Append(path[i++]);
                list.Add(new Tok { Kind = TokKind.Member, Text = sb.ToString().Trim() });
            }
            return list;
        }

        private static Tok ParseFilter(string inner)
        {
            string s = inner.TrimStart('?').Trim().Trim('(', ')');
            string[] ops = { "<=", ">=", "!=", "==", "=", "<", ">", "~" };
            foreach (var op in ops)
            {
                int k = s.IndexOf(op, StringComparison.Ordinal);
                if (k > 0)
                {
                    return new Tok
                    {
                        Kind = TokKind.Filter,
                        FilterPath = s.Substring(0, k).Trim().TrimStart('@').TrimStart('.'),
                        FilterOp = op,
                        FilterValue = s.Substring(k + op.Length).Trim().Trim('"', '\'')
                    };
                }
            }
            throw new ArgumentException("Bad filter '[" + inner + "]'");
        }
    }
}
