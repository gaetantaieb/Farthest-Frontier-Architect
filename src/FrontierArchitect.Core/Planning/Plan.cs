using System;
using System.Collections.Generic;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;

namespace FrontierArchitect.Core.Planning
{
    /// <summary>The standard phase order (spec section 5). The mod enforces it on every plan.</summary>
    public enum Stage
    {
        Settings = 1,
        Prepare = 2,
        AddCapacity = 3,
        StorageLogistics = 4,
        ProductionChains = 5,
        Farming = 6,
        Residential = 7,
        CleanUp = 8
    }

    /// <summary>Action catalogue (spec section 7).</summary>
    public static class Ops
    {
        public const string Relocate = "relocate", Build = "build", Demolish = "demolish", ClearArea = "clear_area",
            Flatten = "flatten", RoadAdd = "road_add", RoadRemove = "road_remove", FieldCreate = "field_create",
            FieldResize = "field_resize", FieldRemove = "field_remove", SetRotation = "set_rotation",
            SetWorkCircle = "set_work_circle", SetWorkers = "set_workers", SetProductionLimit = "set_production_limit",
            SetProfessionTarget = "set_profession_target", SetPaused = "set_paused";

        public static readonly string[] All =
        {
            Relocate, Build, Demolish, ClearArea, Flatten, RoadAdd, RoadRemove, FieldCreate, FieldResize, FieldRemove,
            SetRotation, SetWorkCircle, SetWorkers, SetProductionLimit, SetProfessionTarget, SetPaused
        };

        /// <summary>Ops the Assistant may Apply directly (no placement decision).</summary>
        public static readonly string[] AssistantApply = { SetWorkers, SetProductionLimit, SetRotation, SetProfessionTarget, SetPaused, SetWorkCircle };

        public static readonly HashSet<string> Settings = new HashSet<string> { SetRotation, SetWorkCircle, SetWorkers, SetProductionLimit, SetProfessionTarget, SetPaused };
        public static readonly HashSet<string> Spatial = new HashSet<string> { Relocate, Build, Demolish, ClearArea, Flatten, RoadAdd, RoadRemove, FieldCreate, FieldResize, FieldRemove };

        public static bool IsKnown(string op) => Array.IndexOf(All, op) >= 0;

        /// <summary>Parameters each op needs (besides "target"), used for validation and the tool schema.</summary>
        public static string ParamHelp(string op)
        {
            switch (op)
            {
                case Relocate: return "target=building id; params {x, z, rot}";
                case Build: return "target=building type; params {x, z, rot}";
                case Demolish: return "target=building id";
                case ClearArea: return "params {rect:[x,z,w,h]}";
                case Flatten: return "params {rect:[x,z,w,h]}";
                case RoadAdd: return "params {road_type, tiles:[[x,z],...]}";
                case RoadRemove: return "params {tiles:[[x,z],...]}";
                case FieldCreate: return "params {rect:[x,z,w,h], rotation:[[y1 crops],[y2],[y3]]}";
                case FieldResize: return "target=field id; params {rect:[x,z,w,h]}";
                case FieldRemove: return "target=field id; params {after_harvest:true}";
                case SetRotation: return "target=field id; params {rotation:[[y1],[y2],[y3]]}";
                case SetWorkCircle: return "target=building id; params {x, z}";
                case SetWorkers: return "target=building id; params {count}";
                case SetProductionLimit: return "target=item; params {limit}";
                case SetProfessionTarget: return "target=profession; params {count}";
                case SetPaused: return "target=building id; params {paused:true|false}";
            }
            return "";
        }
    }

    public enum ActionStatus { Pending, Issued, Done, Failed, Rejected, Manual, Skipped }

    public sealed class PlanAction
    {
        public string Id;
        public string Op;
        public string Target;
        public JObject Params = new JObject();
        public string Rationale = "";
        public string Objective = "";
        public ActionStatus Status = ActionStatus.Pending;
        public string StatusNote = "";

        public int X => Params.Int("x");
        public int Z => Params.Int("z");
        public int Rot => Params.Int("rot") & 3;

        public bool TryRect(out Rect r) => Rect.TryParse(Params["rect"], out r);

        public List<Cell> Tiles()
        {
            var list = new List<Cell>();
            foreach (var t in Params.Arr("tiles") ?? new JArray())
                if (Cell.TryParse(t, out Cell c)) list.Add(c);
            return list;
        }

        public List<List<string>> Rotation()
        {
            var rot = new List<List<string>>();
            foreach (var y in Params.Arr("rotation") ?? new JArray())
                rot.Add(y is JArray ya ? ya.Strings() : new List<string> { Convert.ToString(y) });
            return rot;
        }

        public static PlanAction FromJson(JObject o)
        {
            var a = new PlanAction
            {
                Id = o.Str("id"),
                Op = o.Str("op"),
                Target = o.Str("target"),
                Params = o.Obj("params") ?? new JObject(),
                Rationale = o.Str("rationale", ""),
                Objective = o.Str("objective", ""),
            };
            ActionStatus st;
            if (o.Str("status") != null && Enum.TryParse(o.Str("status"), true, out st)) a.Status = st;
            a.StatusNote = o.Str("status_note", "");
            // tolerate params written at the top level
            foreach (var k in new[] { "x", "z", "rot", "rect", "tiles", "rotation", "count", "limit", "paused", "road_type", "after_harvest" })
                if (o.ContainsKey(k) && !a.Params.ContainsKey(k)) a.Params.Set(k, o[k]);
            return a;
        }

        public JObject ToJson() => new JObject()
            .Set("id", Id).Set("op", Op).Set("target", Target).Set("params", Params)
            .Set("rationale", Rationale).Set("objective", Objective)
            .Set("status", Status.ToString()).Set("status_note", StatusNote);

        public Stage Stage(KnowledgePack k, VillageSnapshot snap)
        {
            if (Ops.Settings.Contains(Op)) return Planning.Stage.Settings;
            switch (Op)
            {
                case Ops.ClearArea:
                case Ops.Flatten:
                case Ops.RoadAdd: return Planning.Stage.Prepare;
                case Ops.RoadRemove: return Planning.Stage.CleanUp;
                case Ops.FieldCreate:
                case Ops.FieldResize:
                case Ops.FieldRemove: return Planning.Stage.Farming;
            }
            string type = Op == Ops.Build ? Target : snap?.Building(Target)?.Type;
            string cat = k?.Spec(type)?.Category ?? "other";
            switch (cat)
            {
                case "storage": return Op == Ops.Build ? Planning.Stage.AddCapacity : Planning.Stage.StorageLogistics;
                case "production":
                case "resource":
                case "food":
                    return Op == Ops.Build ? Planning.Stage.AddCapacity : Planning.Stage.ProductionChains;
                case "farm": return Planning.Stage.Farming;
                case "housing":
                case "service":
                case "town_center":
                    return Planning.Stage.Residential;
                case "decoration": return Planning.Stage.CleanUp;
                case "defence":
                case "wall":
                case "gate":
                    return Op == Ops.Build ? Planning.Stage.AddCapacity : Planning.Stage.Residential;
            }
            if (Op == Ops.Demolish) return Planning.Stage.CleanUp;
            return Op == Ops.Build ? Planning.Stage.AddCapacity : Planning.Stage.ProductionChains;
        }
    }

    /// <summary>A district Claude assigns to one purpose (set_zones); kept with the plan and drawn by the overlay.</summary>
    public sealed class Zone
    {
        public static readonly string[] Purposes = { "housing", "farming", "industry", "storage", "services", "defence", "mixed", "reserve" };
        public string Name = "";
        public string Purpose = "mixed";
        public Rect Rect;

        /// <summary>Building categories that belong in a zone of this purpose.</summary>
        public static string[] Categories(string purpose)
        {
            switch (purpose)
            {
                case "housing": return new[] { "housing", "decoration", "service" };
                case "farming": return new[] { "farm", "food" };
                case "industry": return new[] { "production", "resource" };
                case "storage": return new[] { "storage" };
                case "services": return new[] { "service", "decoration", "town_center" };
                case "defence": return new[] { "defence", "wall", "gate" };
                case "reserve": return new string[0];
                default: return null; // mixed: anything
            }
        }

        public static Zone FromJson(JObject o)
        {
            Rect r;
            Rect.TryParse(o["rect"], out r);
            return new Zone { Name = o.Str("name", ""), Purpose = (o.Str("purpose", "mixed") ?? "mixed").ToLowerInvariant(), Rect = r };
        }

        public JObject ToJson() => new JObject().Set("name", Name).Set("purpose", Purpose).Set("rect", Rect.ToJson());
    }

    public enum PhaseStatus { NotStarted, AwaitingApproval, InProgress, Paused, Completed, Skipped, Failed, RolledBack }

    public sealed class Phase
    {
        public int N;
        public string Name = "";
        public string Goal = "";
        public List<string> SeasonWindow = new List<string>();
        public List<int> DependsOn = new List<int>();
        public List<PlanAction> Actions = new List<PlanAction>();
        public List<string> Offline = new List<string>();
        public JObject EstCost = new JObject();
        public double EstDays;
        public PhaseStatus Status = PhaseStatus.NotStarted;
        public string SaveName = "";
        public string Report = "";
        public string StartedAtGameDate = "";
        public double ActualDays;

        public static Phase FromJson(JObject o)
        {
            var p = new Phase
            {
                N = o.Int("n"),
                Name = o.Str("name", ""),
                Goal = o.Str("goal", ""),
                SeasonWindow = o.Arr("season_window").Strings(),
                Offline = o.Arr("offline").Strings(),
                EstCost = o.Obj("est_cost") ?? new JObject(),
                EstDays = o.Num("est_days"),
                SaveName = o.Str("save_name", ""),
                Report = o.Str("report", ""),
                StartedAtGameDate = o.Str("started_at", ""),
                ActualDays = o.Num("actual_days"),
            };
            foreach (var d in o.Arr("depends_on") ?? new JArray()) p.DependsOn.Add((int)Json.ToLong(d));
            foreach (var a in o.Arr("actions").Objects()) p.Actions.Add(PlanAction.FromJson(a));
            PhaseStatus st;
            if (o.Str("status") != null && Enum.TryParse(o.Str("status"), true, out st)) p.Status = st;
            return p;
        }

        public JObject ToJson()
        {
            var acts = new JArray();
            foreach (var a in Actions) acts.Add(a.ToJson());
            var deps = new JArray(); foreach (var d in DependsOn) deps.Add((long)d);
            return new JObject()
                .Set("n", N).Set("name", Name).Set("goal", Goal)
                .Set("season_window", SeasonWindow.ToJArray()).Set("depends_on", deps)
                .Set("actions", acts).Set("offline", Offline.ToJArray())
                .Set("est_cost", EstCost).Set("est_days", EstDays)
                .Set("status", Status.ToString()).Set("save_name", SaveName).Set("report", Report)
                .Set("started_at", StartedAtGameDate).Set("actual_days", ActualDays);
        }
    }

    public sealed class ArchitectPlan
    {
        public int Ambition = 3;
        public string Summary = "";
        public JObject ScoreBefore = new JObject();
        public JObject ScoreAfter = new JObject();
        public List<Phase> Phases = new List<Phase>();

        // traceability (spec: every plan records the profile and agent hash)
        public string Profile = "";
        public string AgentHash = "";
        public string AgentVersion = "";
        public string Model = "";
        public string CreatedAtUtc = "";
        public string Mode = "native";
        public string SettlementKey = "";
        public string GameVersion = "";
        public List<string> LockedIds = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<Zone> Zones = new List<Zone>();

        public IEnumerable<PlanAction> AllActions()
        {
            foreach (var p in Phases) foreach (var a in p.Actions) yield return a;
        }

        public static ArchitectPlan FromJson(JObject o)
        {
            var s = o.Obj("scenario") ?? new JObject();
            var plan = new ArchitectPlan
            {
                Ambition = s.Int("ambition", 3),
                Summary = s.Str("summary", ""),
                ScoreBefore = s.Obj("score_before") ?? new JObject(),
                ScoreAfter = s.Obj("score_after") ?? new JObject(),
            };
            foreach (var p in o.Arr("phases").Objects()) plan.Phases.Add(Phase.FromJson(p));
            foreach (var z in o.Arr("zones").Objects()) plan.Zones.Add(Zone.FromJson(z));
            var meta = o.Obj("meta");
            if (meta != null)
            {
                plan.Profile = meta.Str("profile", "");
                plan.AgentHash = meta.Str("agent_hash", "");
                plan.AgentVersion = meta.Str("agent_version", "");
                plan.Model = meta.Str("model", "");
                plan.CreatedAtUtc = meta.Str("created_at_utc", "");
                plan.Mode = meta.Str("mode", "native");
                plan.SettlementKey = meta.Str("settlement", "");
                plan.GameVersion = meta.Str("game_version", "");
                plan.LockedIds = meta.Arr("locked").Strings();
                plan.Warnings = meta.Arr("warnings").Strings();
            }
            return plan;
        }

        public JObject ToJson()
        {
            var phases = new JArray();
            foreach (var p in Phases) phases.Add(p.ToJson());
            var zones = new JArray();
            foreach (var z in Zones) zones.Add(z.ToJson());
            return new JObject()
                .Set("zones", zones)
                .Set("scenario", new JObject().Set("ambition", Ambition).Set("summary", Summary)
                    .Set("score_before", ScoreBefore).Set("score_after", ScoreAfter))
                .Set("phases", phases)
                .Set("meta", new JObject()
                    .Set("profile", Profile).Set("agent_hash", AgentHash).Set("agent_version", AgentVersion)
                    .Set("model", Model).Set("created_at_utc", CreatedAtUtc).Set("mode", Mode)
                    .Set("settlement", SettlementKey).Set("game_version", GameVersion)
                    .Set("locked", LockedIds.ToJArray()).Set("warnings", Warnings.ToJArray()));
        }
    }

    // ------------------------------------------------------------------ Assistant report

    public enum Severity { Critical, Important, Optimisation }
    public enum FindingActionKind { Apply, Architect, Manual }

    public sealed class Finding
    {
        public string Id;
        public Severity Severity = Severity.Important;
        public string Domain = "";
        public string Title = "";
        public List<string> Evidence = new List<string>();
        public string Recommendation = "";
        public FindingActionKind Kind = FindingActionKind.Manual;
        public List<PlanAction> Ops = new List<PlanAction>();
        public string ManualReason = "";

        // Apply bookkeeping for undo while the report panel is open
        public bool Applied;
        public List<string> BeforeAfter = new List<string>();

        public static Finding FromJson(JObject o)
        {
            var f = new Finding
            {
                Id = o.Str("id"),
                Domain = o.Str("domain", ""),
                Title = o.Str("title", ""),
                Evidence = o.Arr("evidence").Strings(),
                Recommendation = o.Str("recommendation", ""),
            };
            switch ((o.Str("severity") ?? "").ToLowerInvariant())
            {
                case "critical": f.Severity = Severity.Critical; break;
                case "optimisation":
                case "optimization": f.Severity = Severity.Optimisation; break;
                default: f.Severity = Severity.Important; break;
            }
            var act = o.Obj("action") ?? new JObject();
            switch ((act.Str("kind") ?? "manual").ToLowerInvariant())
            {
                case "apply": f.Kind = FindingActionKind.Apply; break;
                case "architect": f.Kind = FindingActionKind.Architect; break;
                default: f.Kind = FindingActionKind.Manual; break;
            }
            int n = 0;
            foreach (var op in act.Arr("ops").Objects())
            {
                var a = PlanAction.FromJson(op);
                if (string.IsNullOrEmpty(a.Id)) a.Id = (f.Id ?? "f") + "." + (++n);
                f.Ops.Add(a);
            }
            return f;
        }

        public JObject ToJson()
        {
            var ops = new JArray(); foreach (var a in Ops) ops.Add(a.ToJson());
            return new JObject()
                .Set("id", Id).Set("severity", Severity.ToString().ToLowerInvariant()).Set("domain", Domain)
                .Set("title", Title).Set("evidence", Evidence.ToJArray()).Set("recommendation", Recommendation)
                .Set("action", new JObject().Set("kind", Kind.ToString().ToLowerInvariant()).Set("ops", ops))
                .Set("manual_reason", ManualReason);
        }
    }

    public sealed class AssistantReport
    {
        public List<Finding> Findings = new List<Finding>();
        public string Summary = "";
        public string Model = "";
        public string AgentHash = "";
        public string CreatedAtUtc = "";

        public static AssistantReport FromJson(JObject o)
        {
            var r = new AssistantReport { Summary = o.Str("summary", "") };
            foreach (var f in o.Arr("findings").Objects()) r.Findings.Add(Finding.FromJson(f));
            r.Findings.Sort((a, b) => a.Severity.CompareTo(b.Severity));
            return r;
        }

        public JObject ToJson()
        {
            var f = new JArray(); foreach (var x in Findings) f.Add(x.ToJson());
            return new JObject().Set("summary", Summary).Set("findings", f).Set("model", Model)
                .Set("agent_hash", AgentHash).Set("created_at_utc", CreatedAtUtc);
        }
    }
}
