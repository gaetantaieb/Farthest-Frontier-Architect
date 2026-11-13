using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Tools
{
    /// <summary>Tiny builder for the JSON Schemas of tool inputs.</summary>
    internal static class S
    {
        public static JObject Obj(JObject props, params string[] required)
        {
            var o = new JObject().Set("type", "object").Set("properties", props ?? new JObject());
            if (required.Length > 0) o.Set("required", new JArray(required));
            return o;
        }

        public static JObject Props(params object[] kv)
        {
            var o = new JObject();
            for (int i = 0; i + 1 < kv.Length; i += 2) o.Set((string)kv[i], kv[i + 1]);
            return o;
        }

        public static JObject Str(string desc) => new JObject().Set("type", "string").Set("description", desc);
        public static JObject Int(string desc) => new JObject().Set("type", "integer").Set("description", desc);
        public static JObject Num(string desc) => new JObject().Set("type", "number").Set("description", desc);
        public static JObject Bool(string desc) => new JObject().Set("type", "boolean").Set("description", desc);
        public static JObject Arr(JObject items, string desc) => new JObject().Set("type", "array").Set("items", items).Set("description", desc);
        public static JObject AnyObj(string desc) => new JObject().Set("type", "object").Set("description", desc);

        public static JObject Enum(string desc, params string[] values) =>
            new JObject().Set("type", "string").Set("description", desc).Set("enum", new JArray(values));

        public static JObject Point(string desc) =>
            new JObject().Set("type", "array").Set("items", new JObject().Set("type", "integer")).Set("minItems", 2).Set("maxItems", 2).Set("description", desc);

        public static JObject Action() => Obj(Props(
                "id", Str("Your id for this action, e.g. a12"),
                "op", Enum("Action op", Planning.Ops.All),
                "target", Str("Building id, building type (build), field id, item or profession, depending on op"),
                "params", AnyObj("Op parameters: x, z, rot (0-3), rect [x,z,w,h], tiles [[x,z],...], rotation [[y1],[y2],[y3]], count, limit, paused, road_type, after_harvest"),
                "rationale", Str("One line, under 15 words, stating the gain"),
                "objective", Enum("Objective served", "O1", "O2", "O3", "O4", "O5", "O6")),
            "op");
    }
}
