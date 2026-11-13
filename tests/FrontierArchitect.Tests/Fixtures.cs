using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Net;
using FrontierArchitect.Core.Yaml;

namespace FrontierArchitect.Tests
{
    /// <summary>A small synthetic village on a 60x60 map: river on the west, Town Center in the middle.</summary>
    internal static class Village
    {
        public static string Repo => Path.GetDirectoryName(typeof(Village).Assembly.Location);
        public static string AgentsDir => Path.Combine(Repo, "agents");

        public static KnowledgePack Knowledge()
        {
            var k = new KnowledgePack { GameVersion = "test" };
            k.LoadRules(MiniYaml.ParseObject(File.ReadAllText(Path.Combine(AgentsDir, "shared", "buildings.yaml"))));
            k.LoadCrops(MiniYaml.ParseObject(File.ReadAllText(Path.Combine(AgentsDir, "shared", "crops.yaml"))));
            k.LoadDistricts(MiniYaml.ParseObject(File.ReadAllText(Path.Combine(AgentsDir, "shared", "districts.yaml"))));
            void Spec(string type, int w, int h, int tier, params (string, double)[] cost)
            {
                var s = new BuildingSpec { Type = type, W = w, H = h, Tier = tier };
                foreach (var c in cost) s.Cost[c.Item1] = c.Item2;
                k.Classify(s);
                k.Buildings[type] = s;
            }
            Spec("TownCenter", 6, 6, 1);
            Spec("House", 3, 3, 1, ("Logs", 10));
            Spec("Storehouse", 4, 4, 1, ("Logs", 20));
            Spec("Well", 2, 2, 1, ("Stone", 5));
            Spec("Market", 5, 5, 2, ("Logs", 30));
            Spec("FirewoodSplitter", 3, 3, 1, ("Logs", 10));
            Spec("Farmhouse", 3, 3, 1, ("Logs", 15));
            Spec("ClayPit", 3, 3, 2);
            Spec("Watchtower", 2, 2, 2, ("Stone", 20));
            return k;
        }

        public static BuildingInfo B(KnowledgePack k, string type, int x, int z, int rot = 0)
        {
            var s = k.Spec(type);
            var b = new BuildingInfo { Type = type, X = x, Z = z, Rot = rot, W = s.W, H = s.H };
            b.Id = type + "@" + x + "," + z;
            k.ApplyTo(b);
            if (b.Category == "housing") b.Extra.Set("capacity", 4);
            return b;
        }

        public static VillageSnapshot Build(bool connected = true)
        {
            var k = Knowledge();
            var grid = new TileGrid(60, 60);
            for (int z = 0; z < 60; z++)
                for (int x = 0; x < 60; x++)
                {
                    int i = grid.Idx(x, z);
                    bool edge = x == 0 || z == 0 || x == 59 || z == 59;
                    grid.Terrain[i] = edge ? Terrain.Edge : (x >= 2 && x <= 5 ? Terrain.Water : Terrain.Land);
                    grid.Fertility[i] = x > 40 ? 0.85f : 0.4f;
                    grid.Clay[i] = 0.5f;
                    grid.Slope[i] = 2;
                }
            var snap = new VillageSnapshot { Grid = grid, Knowledge = k };
            snap.Meta.Season = "spring";
            snap.Meta.Year = 3;
            snap.Meta.TownTier = 2;
            snap.Meta.SettlementName = "Testford";
            snap.Buildings.Add(B(k, "TownCenter", 27, 27));
            snap.Buildings.Add(B(k, "House", 20, 20));
            snap.Buildings.Add(B(k, "House", 20, 36));
            snap.Buildings.Add(B(k, "House", 36, 20));
            snap.Buildings.Add(B(k, "Storehouse", 10, 45));
            snap.Buildings.Add(B(k, "Well", 30, 20));
            snap.Buildings.Add(B(k, "FirewoodSplitter", 12, 12));
            snap.Fields.Add(new FieldInfo
            {
                Id = "field@44,40", Rect = new Rect(44, 40, 8, 8), Fertility = 0.85f,
                Rotation = new List<List<string>> { new List<string> { "Wheat" }, new List<string> { "Wheat" }, new List<string> { "Wheat" } }
            });
            snap.Resources.Add(new ResourceNode { Type = "clay", Pos = new Cell(50, 12), Remaining = 500 });
            snap.Resources.Add(new ResourceNode { Type = "tree", Pos = new Cell(14, 10), Remaining = 40 });
            snap.Stock["Logs"] = 200; snap.Stock["Stone"] = 80; snap.Stock["Firewood"] = 300; snap.Stock["Bread"] = 400;
            snap.Unlocked = k.Buildings.Keys.ToList();
            snap.Metrics = new JObject()
                .Set("population", new JObject().Set("count", 12).Set("housing_capacity", 12))
                .Set("food", new JObject().Set("stock_total", 400).Set("consumption_per_month", 12).Set("production_per_month", 14))
                .Set("winter", new JObject().Set("firewood", 300).Set("firewood_burn_per_month", 6).Set("firewood_production_per_month", 8))
                .Set("storage", new JObject().Set("fill_ratio", 0.5));
            if (connected)
            {
                // a road loop through every entrance
                var road = new RoadInfo { Type = "dirt" };
                var cells = new HashSet<Cell>();
                var planner = new Core.Roads.RoadPlanner(new Core.Layout.DraftLayout(snap), new Core.Layout.RunSettings());
                var net = planner.Design(null, false);
                foreach (var c in net.Tiles) cells.Add(c);
                road.Tiles = cells.ToList();
                snap.Roads.Add(road);
            }
            return snap;
        }
    }

    /// <summary>Scripted HTTP transport: returns queued responses, records requests.</summary>
    internal sealed class FakeHttp : IHttpTransport
    {
        public readonly List<HttpRequest> Requests = new List<HttpRequest>();
        public readonly Queue<Func<HttpRequest, Action<string>, HttpResponse>> Script = new Queue<Func<HttpRequest, Action<string>, HttpResponse>>();
        public Func<HttpRequest, HttpResponse> Router;

        public HttpResponse Send(HttpRequest request, Action<string> onLine, CancellationToken cancel)
        {
            Requests.Add(request);
            if (Router != null) return Router(request);
            if (Script.Count == 0) throw new InvalidOperationException("no scripted response for " + request.Url);
            return Script.Dequeue()(request, onLine);
        }

        public void EnqueueSse(params string[] events)
        {
            Script.Enqueue((req, onLine) =>
            {
                foreach (var e in events)
                    foreach (var line in e.Split('\n')) onLine(line);
                return new HttpResponse { Status = 200 };
            });
        }

        public void EnqueueStatus(int status, string body)
        {
            Script.Enqueue((req, onLine) => new HttpResponse { Status = status, Body = body });
        }
    }

    /// <summary>Builds Messages API SSE streams.</summary>
    internal static class Sse
    {
        private static string Ev(string type, JObject data) => "event: " + type + "\ndata: " + Json.Write(data.Set("type", type)) + "\n";

        public static string[] ToolUse(string id, string name, JObject input, string thinking = "I should look.", string stop = "tool_use")
        {
            string json = Json.Write(input);
            var parts = new List<string>
            {
                Ev("message_start", new JObject().Set("message", new JObject().Set("id", "msg_1").Set("model", "claude-opus-5-5").Set("usage", new JObject().Set("input_tokens", 100)))),
                Ev("content_block_start", new JObject().Set("index", 0).Set("content_block", new JObject().Set("type", "thinking").Set("thinking", "").Set("signature", ""))),
                Ev("content_block_delta", new JObject().Set("index", 0).Set("delta", new JObject().Set("type", "thinking_delta").Set("thinking", thinking))),
                Ev("content_block_delta", new JObject().Set("index", 0).Set("delta", new JObject().Set("type", "signature_delta").Set("signature", "sig123"))),
                Ev("content_block_stop", new JObject().Set("index", 0)),
                Ev("content_block_start", new JObject().Set("index", 1).Set("content_block", new JObject().Set("type", "tool_use").Set("id", id).Set("name", name).Set("input", new JObject()))),
            };
            int half = json.Length / 2;
            parts.Add(Ev("content_block_delta", new JObject().Set("index", 1).Set("delta", new JObject().Set("type", "input_json_delta").Set("partial_json", json.Substring(0, half)))));
            parts.Add(Ev("content_block_delta", new JObject().Set("index", 1).Set("delta", new JObject().Set("type", "input_json_delta").Set("partial_json", json.Substring(half)))));
            parts.Add(Ev("content_block_stop", new JObject().Set("index", 1)));
            parts.Add(Ev("message_delta", new JObject().Set("delta", new JObject().Set("stop_reason", stop)).Set("usage", new JObject().Set("output_tokens", 50))));
            parts.Add(Ev("message_stop", new JObject()));
            return parts.ToArray();
        }

        public static string[] Text(string text, string stop = "end_turn") => new[]
        {
            Ev("message_start", new JObject().Set("message", new JObject().Set("id", "msg_2").Set("model", "claude-opus-5-5").Set("usage", new JObject().Set("input_tokens", 10)))),
            Ev("content_block_start", new JObject().Set("index", 0).Set("content_block", new JObject().Set("type", "text").Set("text", ""))),
            Ev("content_block_delta", new JObject().Set("index", 0).Set("delta", new JObject().Set("type", "text_delta").Set("text", text))),
            Ev("content_block_stop", new JObject().Set("index", 0)),
            Ev("message_delta", new JObject().Set("delta", new JObject().Set("stop_reason", stop)).Set("usage", new JObject().Set("output_tokens", 5))),
            Ev("message_stop", new JObject()),
        };
    }

    internal static class Temp
    {
        public static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "fa-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void CopyDir(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.Replace(from, to)));
                File.Copy(f, f.Replace(from, to), true);
            }
        }
    }
}
