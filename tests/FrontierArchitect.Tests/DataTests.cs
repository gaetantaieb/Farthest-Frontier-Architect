using System.IO;
using System.Linq;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Yaml;
using Xunit;

namespace FrontierArchitect.Tests
{
    public class JsonTests
    {
        [Fact]
        public void RoundTripKeepsOrderAndTypes()
        {
            string src = "{\"b\":1,\"a\":[true,null,2.5,\"x\\n\\u00e9\"],\"c\":{\"d\":-3}}";
            var o = Json.ParseObject(src);
            Assert.Equal(new[] { "b", "a", "c" }, o.Keys.ToArray());
            Assert.Equal(1L, o["b"]);
            Assert.Equal(2.5, (double)o.Arr("a")[2]);
            Assert.Equal("x\né", o.Arr("a")[3]);
            Assert.Equal(src.Replace("\\u00e9", "é"), Json.Write(o));
        }

        [Fact]
        public void ExtractsJsonFromProse()
        {
            var o = Json.ExtractFirstObject("Here is the plan: {\"phases\": [{\"n\": 1, \"name\": \"a } b\"}]} Thanks.");
            Assert.NotNull(o);
            Assert.Equal("a } b", o.Arr("phases").Objects().First().Str("name"));
        }

        [Fact]
        public void PathQueriesFilterAndMap()
        {
            var o = Json.ParseObject("{\"buildings\":[{\"id\":\"A\",\"type\":\"House\",\"w\":{\"n\":1}},{\"id\":\"B\",\"type\":\"Well\",\"w\":{\"n\":3}},{\"id\":\"C\",\"type\":\"House\",\"w\":{\"n\":0}}]}");
            Assert.Equal(2L, JsonPath.Select(o, "buildings[type=House].length"));
            Assert.Equal(new[] { "A", "B", "C" }, ((JArray)JsonPath.Select(o, "buildings[*].id")).Strings());
            Assert.Equal(new[] { "B" }, ((JArray)JsonPath.Select(o, "buildings[w.n>1].id")).Strings());
            Assert.Equal("C", ((JObject)JsonPath.Select(o, "buildings[-1]")).Str("id"));
        }
    }

    public class YamlTests
    {
        [Fact]
        public void ParsesShippedArchitectAgent()
        {
            var o = MiniYaml.ParseObject(File.ReadAllText(Path.Combine(Village.AgentsDir, "architect", "agent.yaml")));
            Assert.Equal("architect", o.Str("agent"));
            Assert.Equal(1.3, o.Obj("roads").Num("detour_limit"));
            Assert.Equal(16, o.Obj("tools").Arr("enabled").Count); // flow list spanning two lines
            var l5 = o.Obj("ambition_levels").Obj("5");
            Assert.Equal("all", l5["max_relocations"]);
            Assert.Equal(new[] { "town_center", "walls", "gates" }, l5.Arr("movable").Strings());
            Assert.Equal(new[] { "+build", "+road_add", "+road_remove" }, o.Obj("ambition_levels").Obj("2").Arr("allow").Strings());
        }

        [Fact]
        public void ParsesSharedKnowledge()
        {
            var b = MiniYaml.ParseObject(File.ReadAllText(Path.Combine(Village.AgentsDir, "shared", "buildings.yaml")));
            Assert.Contains(b.Arr("rules").Objects(), r => r.Str("match") == "TownCent(er|re)|TownHall");
            Assert.True(b.Obj("assumptions").Arr("food_items").Count > 10);
            var c = MiniYaml.ParseObject(File.ReadAllText(Path.Combine(Village.AgentsDir, "shared", "crops.yaml")));
            Assert.Equal(-0.10, c.Obj("crops").Obj("Wheat").Num("fertility_delta"), 3);
            Assert.False(c.Obj("crops").Obj("Clover").Bool("needs_harvest", true));
        }

        [Fact]
        public void WriteThenParseRoundTrips()
        {
            var o = new JObject().Set("model", new JObject().Set("id", "most-capable").Set("effort", "high"))
                .Set("objectives", new JObject().Set("O1_weight", 1.5).Set("O2_weight", 0.0))
                .Set("note", "a: b # not a comment").Set("list", new JArray { "x", "y, z" })
                .Set("text", "line1\nline2\n");
            var back = MiniYaml.ParseObject(MiniYaml.Write(o));
            Assert.Equal("high", back.Obj("model").Str("effort"));
            Assert.Equal(1.5, back.Obj("objectives").Num("O1_weight"));
            Assert.Equal("a: b # not a comment", back.Str("note"));
            Assert.Equal(new[] { "x", "y, z" }, back.Arr("list").Strings());
            Assert.Equal("line1\nline2\n", back.Str("text"));
        }

        [Fact]
        public void BlockSequencesOfMaps()
        {
            var o = MiniYaml.ParseObject("rules:\n  - match: A\n    category: x\n  - { match: B, category: y }\nempty:\n");
            Assert.Equal(new[] { "A", "B" }, o.Arr("rules").Objects().Select(r => r.Str("match")).ToArray());
            Assert.Equal("x", o.Arr("rules").Objects().First().Str("category"));
            Assert.Null(o["empty"]);
        }
    }

    public class TemplateTests
    {
        [Fact]
        public void RendersKnownVariablesAndReportsUnknown()
        {
            var t = "Ambition {{ambition}} {{ ambition_name }} {{unknown_var}}";
            Assert.Equal("Ambition 3 Rework {{unknown_var}}", Template.Render(t, new System.Collections.Generic.Dictionary<string, string> { { "ambition", "3" }, { "ambition_name", "Rework" } }));
            Assert.Equal(new[] { "unknown_var" }, Template.UnknownVariables(t).ToArray());
        }
    }
}
