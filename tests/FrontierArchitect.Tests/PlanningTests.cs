using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Roads;
using FrontierArchitect.Core.Scoring;
using FrontierArchitect.Core.Tools;
using Xunit;

namespace FrontierArchitect.Tests
{
    internal static class A
    {
        public static PlanAction Act(string id, string op, string target, JObject p = null, string obj = "O2") =>
            new PlanAction { Id = id, Op = op, Target = target, Params = p ?? new JObject(), Rationale = "test", Objective = obj };

        public static JObject At(int x, int z, int rot = 0) => new JObject().Set("x", x).Set("z", z).Set("rot", rot);

        public static RunSettings Settings(int ambition = 3, string mode = "instant")
        {
            var s = new RunSettings { Ambition = ambition, Mode = mode };
            return s;
        }
    }

    public class DraftTests
    {
        [Fact]
        public void PlacementRules()
        {
            var snap = Village.Build();
            var d = new DraftLayout(snap);
            var s = A.Settings();
            Assert.True(d.Check(A.Act("a", Ops.Build, "House", A.At(40, 10)), s).Valid);
            Assert.Contains("Water", string.Join(" ", d.Check(A.Act("a", Ops.Build, "House", A.At(3, 30)), s).Reasons));
            Assert.Contains("overlaps", string.Join(" ", d.Check(A.Act("a", Ops.Build, "House", A.At(28, 28)), s).Reasons));
            Assert.False(d.Check(A.Act("a", Ops.Build, "House", A.At(58, 30)), s).Valid); // off the map
            Assert.False(d.Check(A.Act("a", Ops.Build, "NotUnlockedThing", A.At(40, 10)), s).Valid);

            s.LockedIds.Add("House@20,20");
            Assert.Contains("locked", string.Join(" ", d.Check(A.Act("a", Ops.Relocate, "House@20,20", A.At(40, 10)), s).Reasons));
            Assert.Contains("ambition 5", string.Join(" ", d.Check(A.Act("a", Ops.Relocate, "TownCenter@27,27", A.At(40, 40)), s).Reasons));

            s.AllowedOps.Remove(Ops.Flatten);
            Assert.Contains("not allowed", string.Join(" ", d.Check(A.Act("a", Ops.Flatten, null, new JObject().Set("rect", new JArray { 10L, 10L, 3L, 3L })), s).Reasons));
        }

        [Fact]
        public void BuildingOnARoadRemovesThatRoad()
        {
            var snap = Village.Build();
            var d = new DraftLayout(snap);
            var road = snap.RoadCells().First(c => d.OwnerAt(c) == null && c.X > 8 && c.X < 50 && c.Z > 8 && c.Z < 50);
            var r = d.Apply(A.Act("a", Ops.Build, "Well", A.At(road.X, road.Z)), A.Settings());
            if (r.Valid)
            {
                Assert.NotEmpty(r.RoadTilesRemoved);
                Assert.DoesNotContain(road, d.Roads);
            }
        }

        [Fact]
        public void FieldsAreFiveToTwelveTiles()
        {
            var d = new DraftLayout(Village.Build());
            var s = A.Settings();
            Assert.False(d.Check(A.Act("f", Ops.FieldCreate, null, new JObject().Set("rect", new JArray { 40L, 10L, 4L, 8L })), s).Valid);
            var ok = d.Check(A.Act("f", Ops.FieldCreate, null, new JObject().Set("rect", new JArray { 42L, 8L, 8L, 8L })
                .Set("rotation", new JArray { new JArray { "Beans" }, new JArray { "Wheat" }, new JArray { "Clover" } })), s);
            Assert.True(ok.Valid, string.Join(";", ok.Reasons));
            var badCrop = d.Check(A.Act("f", Ops.FieldCreate, null, new JObject().Set("rect", new JArray { 42L, 8L, 8L, 8L })
                .Set("rotation", new JArray { new JArray { "Tomatoes" }, new JArray { "Wheat" }, new JArray { "Clover" } })), s);
            Assert.Contains("unknown crop", string.Join(" ", badCrop.Reasons));
        }

        [Fact]
        public void RelocationLimitAndUndo()
        {
            var d = new DraftLayout(Village.Build());
            var s = A.Settings(1);
            s.AllowedOps = new HashSet<string> { Ops.Relocate };
            s.MaxRelocations = 1;
            Assert.True(d.Apply(A.Act("a", Ops.Relocate, "House@20,20", A.At(40, 10)), s).Valid);
            Assert.Contains("relocation limit", string.Join(" ", d.Check(A.Act("b", Ops.Relocate, "House@36,20", A.At(45, 10)), s).Reasons));
            Assert.Equal(40, d.Buildings["House@20,20"].X);
            d.Undo(s);
            Assert.Equal(20, d.Buildings["House@20,20"].X);
            Assert.Equal(0, d.RelocationCount);
        }

        [Fact]
        public void FixedSitesStayOnTheirResource()
        {
            var d = new DraftLayout(Village.Build());
            var s = A.Settings();
            Assert.Contains("clay", string.Join(" ", d.Check(A.Act("a", Ops.Build, "ClayPit", A.At(40, 30)), s).Reasons));
            Assert.True(d.Check(A.Act("a", Ops.Build, "ClayPit", A.At(48, 10)), s).Valid);
        }
    }

    public class RoadTests
    {
        [Fact]
        public void NetworkConnectsEveryBuilding()
        {
            var snap = Village.Build(connected: false);
            var d = new DraftLayout(snap);
            var p = new RoadPlanner(d, A.Settings());
            Assert.True(p.CheckConnectivity().Connectivity < 1);
            var net = p.Design();
            Assert.Empty(net.Unreachable);
            Assert.Equal(1.0, net.Connectivity);
            Assert.Equal(1.0, p.CheckConnectivity(net.Tiles).Connectivity);
            Assert.True(net.Tiles.All(c => d.OwnerAt(c) == null && snap.Grid.IsLand(c)));
        }

        [Fact]
        public void ExistingRoadsAreReused()
        {
            var snap = Village.Build(connected: true);
            var net = new RoadPlanner(new DraftLayout(snap), A.Settings()).Design(null, false);
            Assert.True(net.ReusedTiles > net.NewTiles);
        }

        [Fact]
        public void RouteRoadAvoidsWaterAndBuildings()
        {
            var d = new DraftLayout(Village.Build(connected: false));
            var path = new RoadPlanner(d, A.Settings()).RouteRoad(new Cell(8, 30), new Cell(50, 30));
            Assert.NotNull(path);
            Assert.All(path, c => Assert.True(d.Grid.IsLand(c) && d.OwnerAt(c) == null));
        }
    }

    public class ScoringTests
    {
        [Fact]
        public void ScoresAreDeterministicAndRewardCloserStorage()
        {
            var snap = Village.Build();
            var s = A.Settings();
            var before = new Scorer(s).Score(new DraftLayout(snap));
            Assert.Equal(before.Weighted, new Scorer(s).Score(new DraftLayout(snap)).Weighted);
            var d = new DraftLayout(snap);
            Assert.True(d.Apply(A.Act("a", Ops.Relocate, "Storehouse@10,45", A.At(16, 16)), s).Valid);
            var after = new Scorer(s).Score(d);
            Assert.True(after.Objectives["O2"] > before.Objectives["O2"]);
        }

        [Fact]
        public void LegumeRotationHoldsFertilityBetterThanWheatMonoculture()
        {
            var k = Village.Knowledge();
            var f = new FieldInfo { Id = "f", Rect = new Rect(0, 0, 8, 8), Fertility = 0.7f };
            var mono = RotationEvaluator.Evaluate(f, new List<List<string>> { new List<string> { "Wheat" }, new List<string> { "Wheat" }, new List<string> { "Wheat" } }, k);
            var good = RotationEvaluator.Evaluate(f, new List<List<string>> { new List<string> { "Beans" }, new List<string> { "Wheat" }, new List<string> { "Clover" } }, k);
            Assert.True(good.Years.Last().FertilityEnd > mono.Years.Last().FertilityEnd);
            Assert.True(mono.Years.Last().DiseaseRisk > good.Years.Last().DiseaseRisk);
            Assert.True(good.Score > mono.Score);
        }
    }

    public class PlanValidatorTests
    {
        private static ArchitectPlan Plan(params Phase[] phases) => new ArchitectPlan { Summary = "t", Phases = phases.ToList() };

        private static Phase P(int n, string name, params PlanAction[] actions) =>
            new Phase { N = n, Name = name, Actions = actions.ToList(), SeasonWindow = new List<string> { "spring", "summer", "early_autumn" } };

        [Fact]
        public void ValidPlanPasses()
        {
            var snap = Village.Build();
            var plan = Plan(
                P(1, "Settings", A.Act("a1", Ops.SetRotation, "field@44,40", new JObject().Set("rotation", new JArray { new JArray { "Beans" }, new JArray { "Wheat" }, new JArray { "Clover" } }), "O1")),
                P(2, "Storage", A.Act("a2", Ops.Relocate, "Storehouse@10,45", A.At(16, 16))));
            var v = new PlanValidator(A.Settings()).Validate(plan, snap);
            // the relocated storehouse needs a road to its new entrance
            var extra = new RoadPlanner(v.FinalDraft, A.Settings()).Design(null, false);
            if (v.Errors.Any(e => e.Contains("connect")))
            {
                var fresh = extra.Tiles.Where(c => !v.FinalDraft.Roads.Contains(c)).Select(c => (object)c.ToJson()).ToList();
                plan.Phases[1].Actions.Add(A.Act("a3", Ops.RoadAdd, null, new JObject().Set("tiles", new JArray(fresh)), "O6"));
                v = new PlanValidator(A.Settings()).Validate(plan, snap);
            }
            Assert.True(v.Ok, string.Join(" | ", v.Errors));
            Assert.True(plan.ScoreAfter.Num("O1") >= plan.ScoreBefore.Num("O1"));
        }

        [Fact]
        public void PhaseOrderIsEnforced()
        {
            var snap = Village.Build();
            var plan = Plan(
                P(1, "Houses", A.Act("a1", Ops.Relocate, "House@36,20", A.At(40, 10), "O3")),
                P(2, "Storage", A.Act("a2", Ops.Relocate, "Storehouse@10,45", A.At(16, 16))));
            var v = new PlanValidator(A.Settings()).Validate(plan, snap);
            Assert.Contains(v.Errors, e => e.Contains("standard order"));
        }

        [Fact]
        public void LastTierOneBuildingNeedsItsReplacementFirst()
        {
            var snap = Village.Build();
            var plan = Plan(P(1, "Clean-up", A.Act("a1", Ops.Demolish, "Well@30,20", null, "O3")));
            var v = new PlanValidator(A.Settings()).Validate(plan, snap);
            Assert.Contains(v.Errors, e => e.Contains("replacement"));
        }

        [Fact]
        public void OfflineCapLimitsTierOneMoves()
        {
            var snap = Village.Build();
            var plan = Plan(P(1, "Houses",
                A.Act("a1", Ops.Relocate, "House@20,20", A.At(40, 8), "O3"),
                A.Act("a2", Ops.Relocate, "House@20,36", A.At(45, 8), "O3"),
                A.Act("a3", Ops.Relocate, "House@36,20", A.At(50, 8), "O3")));
            var v = new PlanValidator(A.Settings()).Validate(plan, snap);
            Assert.Contains(v.Errors, e => e.Contains("offline"));
        }

        [Fact]
        public void SeasonRulesForFieldsAndFrozenGround()
        {
            var snap = Village.Build();
            var plan = Plan(new Phase
            {
                N = 1, Name = "Farming", SeasonWindow = new List<string> { "summer", "winter" },
                Actions = new List<PlanAction> { A.Act("a1", Ops.FieldRemove, "field@44,40", new JObject().Set("after_harvest", true), "O1") }
            });
            var v = new PlanValidator(A.Settings()).Validate(plan, snap);
            Assert.Contains(v.Errors, e => e.Contains("summer"));

            var plan2 = Plan(new Phase { N = 1, Name = "Prepare", Actions = new List<PlanAction> { A.Act("a1", Ops.ClearArea, null, new JObject().Set("rect", new JArray { 40L, 8L, 4L, 4L }), "O2") } });
            new PlanValidator(A.Settings()).Validate(plan2, snap);
            Assert.DoesNotContain("winter", plan2.Phases[0].SeasonWindow); // filled by the mod
        }

        [Fact]
        public void NativeModeChecksStocks()
        {
            var snap = Village.Build();
            snap.Metrics.Obj("winter").Set("firewood", 10); // almost out
            var plan = Plan(P(1, "Production", A.Act("a1", Ops.Relocate, "FirewoodSplitter@12,12", A.At(14, 20), "O2")));
            plan.Phases[0].EstDays = 60;
            var v = new PlanValidator(A.Settings(3, "native")).Validate(plan, snap);
            Assert.Contains(v.Errors, e => e.Contains("firewood"));
        }
    }

    public class ToolServerTests
    {
        private static ArchitectToolHost Host(VillageSnapshot snap = null) =>
            new ArchitectToolHost(snap ?? Village.Build(), A.Settings(), null, null, 3, 3);

        [Fact]
        public void RegionFindSitesAndApply()
        {
            var h = Host();
            var region = Json.ParseObject(h.Execute("get_region", new JObject().Set("x", 0L).Set("z", 25L).Set("w", 10L).Set("h", 2L).Set("layers", new JArray { "terrain" }), CancellationToken.None).Content);
            Assert.Equal("#.~~~~....", region.Arr("terrain")[0]);

            var sites = Json.ParseObject(h.Execute("find_sites", new JObject().Set("building_type", "Market").Set("near", "TownCenter@27,27").Set("limit", 3L), CancellationToken.None).Content);
            var first = sites.Arr("sites").Objects().First();
            var apply = h.Execute("apply_to_draft", new JObject().Set("action", new JObject().Set("op", "build").Set("target", "Market").Set("params", A.At(first.Int("x"), first.Int("z"), first.Int("rot")))), CancellationToken.None);
            Assert.False(apply.IsError, apply.Content);
            Assert.StartsWith("new:Market:", Json.ParseObject(apply.Content).Str("new_id"));
            var undo = h.Execute("undo_draft", new JObject(), CancellationToken.None);
            Assert.False(undo.IsError);
        }

        [Fact]
        public void ToolInputsAreValidated()
        {
            var r = Host().Execute("get_region", new JObject().Set("x", "a"), CancellationToken.None);
            Assert.True(r.IsError);
        }

        [Fact]
        public void SubmitPlanRejectsThenAccepts()
        {
            var h = Host();
            var bad = new JObject().Set("scenario", new JObject().Set("summary", "s")).Set("phases", new JArray
            {
                new JObject().Set("n", 1).Set("name", "x").Set("actions", new JArray { new JObject().Set("op", "build").Set("target", "House").Set("params", A.At(3, 30)) })
            });
            var r1 = h.Execute("submit_plan", bad, CancellationToken.None);
            Assert.True(r1.IsError);
            Assert.False(r1.Finished);

            var good = new JObject().Set("scenario", new JObject().Set("summary", "s")).Set("phases", new JArray
            {
                new JObject().Set("n", 1).Set("name", "Settings").Set("actions", new JArray
                {
                    new JObject().Set("id", "a1").Set("op", "set_rotation").Set("target", "field@44,40").Set("objective", "O1").Set("rationale", "Beans restore fertility")
                        .Set("params", new JObject().Set("rotation", new JArray { new JArray { "Beans" }, new JArray { "Wheat" }, new JArray { "Clover" } }))
                })
            });
            var r2 = h.Execute("submit_plan", good, CancellationToken.None);
            Assert.False(r2.IsError, r2.Content);
            Assert.True(r2.Finished);
            Assert.NotNull(h.AcceptedPlan);
        }

        [Fact]
        public void RouteNetworkReturnsRoadActions()
        {
            var h = Host(Village.Build(connected: false));
            var r = Json.ParseObject(h.Execute("route_network", new JObject().Set("apply", true), CancellationToken.None).Content);
            Assert.Equal(1.0, r.Num("connectivity"));
            Assert.Contains(r.Arr("actions").Objects(), a => a.Str("op") == "road_add");
            Assert.Equal(1.0, new RoadPlanner(h.Draft, A.Settings()).CheckConnectivity().Connectivity);
        }
    }
}

namespace FrontierArchitect.Tests
{
    public class SnapshotShapeTests
    {
        [Fact]
        public void WallPiecesAreSummarisedAndDoorSideDoesNotMatter()
        {
            var snap = Village.Build();
            var k = snap.Knowledge;
            for (int i = 0; i < 30; i++) snap.Buildings.Add(Village.B(k, "WallPalisade", 8 + i, 55));
            var json = snap.ToJson();
            Assert.DoesNotContain(json.Arr("buildings").Objects(), b => b.Str("type") == "WallPalisade");
            Assert.Equal(30L, json.Obj("walls").Long("pieces"));

            // rotate every building so its guessed door faces away from the road: still connected
            foreach (var b in snap.Buildings) b.Rot = (b.Rot + 2) & 3;
            var conn = new FrontierArchitect.Core.Roads.RoadPlanner(new FrontierArchitect.Core.Layout.DraftLayout(snap), A.Settings()).CheckConnectivity();
            Assert.True(conn.Connectivity > 0.9, string.Join(",", conn.Unreachable));
        }
    }
}

namespace FrontierArchitect.Tests
{
    /// <summary>Fixes from the first live Architect run (v1.1.2a, 384x384 map).</summary>
    public class LiveRunRegressionTests
    {
        private static ArchitectToolHost Host(VillageSnapshot snap) =>
            new ArchitectToolHost(snap, A.Settings(), null, null, 3, 3);

        [Fact]
        public void CoordinatesSentAsTextAreAccepted()
        {
            Cell c;
            Assert.True(Cell.TryParse("[163,195]", out c));
            Assert.Equal(new Cell(163, 195), c);
            Assert.True(Cell.TryParse(" 12, 7 ", out c));
            Assert.False(Cell.TryParse("House@20,20", out c));
            Rect r;
            Assert.True(Rect.TryParse("[1,2,3,4]", out r));
            Assert.Equal(3, r.W);

            var h = Host(Village.Build());
            var road = h.Execute("route_road", new JObject().Set("from", "[30,40]").Set("to", "field:field@44,40"), CancellationToken.None);
            Assert.False(road.IsError, road.Content);
            var sites = Json.ParseObject(h.Execute("find_sites", new JObject().Set("building_type", "Well").Set("near", "[45,15]").Set("limit", "2"), CancellationToken.None).Content);
            var first = sites.Arr("sites").Objects().First();
            Assert.True(new Cell(first.Int("x"), first.Int("z")).DistanceTo(new Cell(45, 15)) < 8, first.ToString());
            Assert.Equal(2, sites.Arr("sites").Count);
        }

        [Fact]
        public void StringifiedJsonIsRepairedBeforeValidation()
        {
            var schema = Json.ParseObject("{\"type\":\"object\",\"properties\":{\"tiles\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"apply\":{\"type\":\"boolean\"},\"n\":{\"type\":\"integer\"}}}");
            var input = Json.ParseObject("{\"tiles\":\"[[1,2],[3,4]]\",\"apply\":\"true\",\"n\":\"5\"}");
            var fixedInput = (JObject)FrontierArchitect.Core.Claude.ToolValidation.Coerce(schema, input);
            Assert.Null(FrontierArchitect.Core.Claude.ToolValidation.Validate(schema, fixedInput));
            Assert.Equal(2, fixedInput.Arr("tiles").Count);
            Assert.True(fixedInput.Bool("apply"));
        }

        [Fact]
        public void GatesArePassableAndFieldsNeedNoRoad()
        {
            var snap = Village.Build();
            var k = snap.Knowledge;
            // fence ring two tiles around the house at 20,36 (footprint 20..22 x 36..38), gate on the south side
            var house = snap.Buildings.First(b => b.Id == "House@20,36");
            var keep = new HashSet<Cell>(snap.RoadCells());
            for (int x = 18; x <= 24; x++)
                for (int z = 34; z <= 40; z++)
                {
                    bool ring = x == 18 || x == 24 || z == 34 || z == 40;
                    if (!ring) continue;
                    var type = (x == 21 && z == 34) ? "WallFenceGate" : "WallFence";
                    snap.Buildings.Add(Village.B(k, type, x, z));
                    keep.Remove(new Cell(x, z));
                }
            // roads: inside the ring from the gate to the house, outside from the gate to the network
            for (int x = 19; x <= 23; x++) for (int z = 35; z <= 39; z++) keep.Remove(new Cell(x, z));
            keep.Add(new Cell(21, 35));
            snap.Roads.Clear();
            var planner0 = new RoadPlanner(new DraftLayout(snap), A.Settings());
            foreach (var c in planner0.RouteRoad(new Cell(21, 33), new Cell(29, 26)) ?? new List<Cell>()) keep.Add(c);
            snap.Roads.Add(new RoadInfo { Tiles = keep.ToList() });

            var planner = new RoadPlanner(new DraftLayout(snap), A.Settings());
            Assert.DoesNotContain(planner.Terminals(), t => t.Id.StartsWith("field:"));
            var conn = planner.CheckConnectivity();
            Assert.DoesNotContain(house.Id, conn.Unreachable);
        }

        [Fact]
        public void RouteNetworkGroupsTilesAndReportsTheScorersConnectivity()
        {
            var h = Host(Village.Build(connected: false));
            var r = Json.ParseObject(h.Execute("route_network", new JObject().Set("apply", true), CancellationToken.None).Content);
            int adds = r.Arr("actions").Objects().Count(a => a.Str("op") == "road_add");
            Assert.InRange(adds, 1, 3);
            var card = new Scorer(A.Settings()).Score(h.Draft);
            Assert.Equal(card.Roads.Connectivity, r.Num("connectivity"), 3);
            Assert.Empty(card.Violations);
        }

        [Fact]
        public void SnapshotSummarisesThousandsOfResourceNodes()
        {
            var snap = Village.Build();
            var rnd = new System.Random(3);
            for (int i = 0; i < 5000; i++)
                snap.Resources.Add(new ResourceNode { Type = i % 3 == 0 ? "forage" : "tree", Pos = new Cell(rnd.Next(1, 59), rnd.Next(1, 59)), Remaining = 30 });
            snap.Resources.Add(new ResourceNode { Type = "ore:iron", Pos = new Cell(5, 50), Remaining = 900 });
            var json = snap.ToJson();
            var res = json.Obj("resources");
            Assert.Equal(1L, res.Arr("deposits").Objects().Count(d => d.Str("type") == "ore:iron"));
            Assert.True(res.Obj("totals").Obj("tree").Long("nodes") > 3000);
            Assert.True(Json.Write(res).Length < 40000, "resources section is " + Json.Write(res).Length + " chars");
            // empty building fields are left out
            Assert.DoesNotContain(json.Arr("buildings").Objects(), b => b.ContainsKey("inputs") || b.ContainsKey("locked"));
        }
    }
}

namespace FrontierArchitect.Tests
{
    /// <summary>District-scale redesign (v0.5): zones, place_group with templates, auto-phases, continuing a plan.</summary>
    public class DistrictTests
    {
        private static ArchitectToolHost Host(VillageSnapshot snap = null, ArchitectPlan prior = null) =>
            new ArchitectToolHost(snap ?? Village.Build(), A.Settings(), null, null, 3, 3, 0.5, 10, prior);

        private static JObject Run(ArchitectToolHost h, string tool, string json)
        {
            var r = h.Execute(tool, Json.ParseObject(json), CancellationToken.None);
            Assert.False(r.IsError, tool + ": " + r.Content);
            return Json.ParseObject(r.Content);
        }

        private static readonly string Zones =
            "{\"zones\":[{\"name\":\"east_housing\",\"purpose\":\"housing\",\"rect\":[34,6,22,20]},{\"name\":\"west_ind\",\"purpose\":\"industry\",\"rect\":[8,6,16,16]}]}";

        [Fact]
        public void ZonesAreReportedAndAHousingBlockIsLaidOutInOneCall()
        {
            var h = Host();
            var z = Run(h, "set_zones", Zones);
            Assert.Equal(2, z.Arr("zones").Count);
            Assert.True(z.Arr("zones").Objects().First().Long("free_tiles") > 300);

            var g = Run(h, "place_group", "{\"zone\":\"east_housing\",\"items\":[{\"type\":\"Well\",\"count\":1},{\"type\":\"House\",\"count\":8}]}");
            Assert.Equal("housing_block", g.Str("template"));          // default template for a housing zone
            Assert.Equal(9, g.Arr("placed").Count);
            Assert.Empty(g.Arr("unplaced"));
            var placed = g.Arr("placed").Objects().ToList();
            Assert.Equal("hub", placed[0].Str("role"));                // the well goes first, at the centre
            var zone = new Rect(34, 6, 22, 20);
            var houses = h.Draft.LiveBuildings.Where(b => b.Id.StartsWith("new:House")).ToList();
            Assert.Equal(8, houses.Count);
            Assert.All(houses, b => Assert.True(zone.Contains(b.Center)));
            // spacing 1: no two new houses touch
            foreach (var a in houses)
                foreach (var b in houses)
                    if (a != b) Assert.False(new Rect(a.Footprint.X - 1, a.Footprint.Z - 1, a.Footprint.W + 2, a.Footprint.H + 2).Overlaps(b.Footprint), a.Id + " touches " + b.Id);
            var well = h.Draft.LiveBuildings.First(b => b.Id.StartsWith("new:Well"));
            Assert.All(houses, b => Assert.True(b.Center.DistanceTo(well.Center) <= System.Math.Max(8, well.ServiceRadius) + 1, b.Id));
        }

        [Fact]
        public void PlaceGroupRelocatesBuildingsAndKeepsThoseAlreadyWellPlaced()
        {
            var h = Host();
            Run(h, "set_zones", Zones);
            var g = Run(h, "place_group", "{\"zone\":\"east_housing\",\"items\":[{\"move_type\":\"House\"}]}");
            int moved = g.Arr("placed").Count, kept = g.Arr("kept_in_place").Count;
            Assert.Equal(3, moved + kept);
            Assert.Contains("House@36,20", g.Arr("kept_in_place").Strings().Concat(g.Arr("placed").Objects().Select(p => p.Str("target"))));
            Assert.Equal(moved, h.Draft.RelocationCount);
        }

        [Fact]
        public void UnknownZoneOrTemplateIsExplained()
        {
            var h = Host();
            var r = h.Execute("place_group", Json.ParseObject("{\"zone\":\"nowhere\",\"items\":[{\"type\":\"House\"}]}"), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("set_zones", r.Content);
            r = h.Execute("place_group", Json.ParseObject("{\"area\":[34,6,22,20],\"template\":\"castle\",\"items\":[{\"type\":\"House\"}]}"), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("housing_block", r.Content);
        }

        [Fact]
        public void AutoPhasesTurnTheDraftIntoAValidPlan()
        {
            var h = Host();
            Run(h, "set_zones", Zones);
            Run(h, "place_group", "{\"zone\":\"east_housing\",\"items\":[{\"type\":\"Well\"},{\"type\":\"House\",\"count\":14}]}");
            Run(h, "place_group", "{\"zone\":\"west_ind\",\"items\":[{\"type\":\"Storehouse\"},{\"type\":\"FirewoodSplitter\",\"count\":2}]}");
            Run(h, "route_network", "{\"apply\":true}");
            int applied = h.Draft.Applied.Count;
            var r = h.Execute("submit_plan", Json.ParseObject("{\"scenario\":{\"summary\":\"east housing block and west industry\"},\"auto_phases\":true}"), CancellationToken.None);
            Assert.False(r.IsError, r.Content);
            var plan = h.AcceptedPlan;
            Assert.Equal(applied, plan.AllActions().Count());
            Assert.All(plan.Phases, p => Assert.True(p.Actions.Count <= 12));
            Assert.Equal(2, plan.Zones.Count);
            var json = Json.ParseObject(Json.Write(plan.ToJson()));
            Assert.Equal(2, ArchitectPlan.FromJson(json).Zones.Count);   // zones survive the plan file
        }

        [Fact]
        public void PhasesCanReferToDraftActionsById()
        {
            var h = Host();
            var g = Run(h, "place_group", "{\"area\":[34,6,22,20],\"items\":[{\"type\":\"Well\"},{\"type\":\"House\",\"count\":2}]}");
            Assert.Equal("housing_block", g.Str("template"));             // inferred from the items
            var net = Run(h, "route_network", "{\"apply\":true,\"remove_unused\":false}");
            var ids = new JArray(g.Arr("placed").Objects().Select(p => (object)p.Str("id")));
            foreach (var a in h.Draft.Applied.Where(a => a.Op == "road_add")) ids.Insert(0, a.Id);
            var plan = new JObject().Set("scenario", new JObject().Set("summary", "s"))
                .Set("phases", new JArray { new JObject().Set("n", 1).Set("name", "Housing").Set("action_ids", ids) });
            var r = h.Execute("submit_plan", plan, CancellationToken.None);
            Assert.False(r.IsError, r.Content + " placed: " + g.ToString());
            Assert.Equal(ids.Count, h.AcceptedPlan.AllActions().Count());
            var bad = h.Execute("submit_plan", Json.ParseObject("{\"scenario\":{\"summary\":\"s\"},\"phases\":[{\"n\":1,\"name\":\"x\",\"action_ids\":[\"zz9\"]}]}"), CancellationToken.None);
            Assert.True(bad.IsError);
        }

        [Fact]
        public void ContinuingAPlanKeepsItsPhasesInFront()
        {
            var first = Host();
            Run(first, "set_zones", Zones);
            Run(first, "place_group", "{\"zone\":\"east_housing\",\"items\":[{\"type\":\"Well\"},{\"type\":\"House\",\"count\":4}]}");
            Run(first, "route_network", "{\"apply\":true,\"remove_unused\":false}");
            var r1 = first.Execute("submit_plan", Json.ParseObject("{\"scenario\":{\"summary\":\"one\"},\"auto_phases\":true}"), CancellationToken.None);
            Assert.False(r1.IsError, r1.Content);
            var prior = first.AcceptedPlan;

            var second = Host(null, prior);
            Assert.Equal(first.Draft.Applied.Count, second.PriorActionCount);
            Assert.True(second.Execute("undo_draft", new JObject(), CancellationToken.None).IsError);
            Assert.Equal(2, second.Zones.Count);
            Run(second, "place_group", "{\"zone\":\"west_ind\",\"items\":[{\"type\":\"Storehouse\"}]}");
            Run(second, "route_network", "{\"apply\":true,\"remove_unused\":false}");
            var r = second.Execute("submit_plan", Json.ParseObject("{\"scenario\":{\"summary\":\"two\"},\"auto_phases\":true}"), CancellationToken.None);
            Assert.False(r.IsError, r.Content);
            var plan = second.AcceptedPlan;
            Assert.Equal(second.Draft.Applied.Count, plan.AllActions().Count());
            Assert.Equal(prior.Phases[0].Actions[0].Id, plan.Phases[0].Actions[0].Id);
        }
    }
}
