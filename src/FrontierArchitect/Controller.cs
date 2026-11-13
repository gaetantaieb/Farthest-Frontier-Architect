using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FrontierArchitect.Core;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Claude;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Execution;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Net;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Updates;
using FrontierArchitect.Game;
using UnityEngine;

namespace FrontierArchitect
{
    /// <summary>
    /// Mod state and orchestration. Game objects are touched only on the main thread; Claude runs
    /// on a background thread over the immutable snapshot.
    /// </summary>
    internal sealed class Controller
    {
        public ModConfig Config;
        public GameMap Map;
        public GameAccess Access;
        public SnapshotBuilder Snapshots;
        public GameExecutor Executor;
        public AgentLoader ArchitectAgent, AssistantAgent;
        public PlanStore Plans;
        public PhaseRunner Runner;
        public UpdateStatus Update = new UpdateStatus();
        public string SyncSummary = "";

        // last results
        public VillageSnapshot LastSnapshot;
        public AssistantReport Report;
        public ArchitectPlan Plan;
        public PlanValidation PlanValidation;
        public PhaseCard Card;
        public readonly HashSet<string> Locked = new HashSet<string>();
        public ArchitectRequest LastRequest = new ArchitectRequest();
        public readonly List<string> AgentErrors = new List<string>();

        // background run state (read from OnGUI)
        public volatile bool Busy;
        public volatile string BusyWhat = "";
        public AgentProgress Progress;
        public string Notice = "";
        public float NoticeUntil;
        private CancellationTokenSource _cancel;
        private IList<string> _models;
        private float _nextTick;

        public string SettlementKey => PlanStore.Key(LastSnapshot?.Meta.SettlementName ?? Access?.SettlementName(), "village");

        public void Init()
        {
            Paths.Ensure();
            Config = ModConfig.Load();
            var defaults = EmbeddedDefaults.Read("defaults/agents/");
            var report = DefaultsSync.Sync(defaults, Paths.Agents);
            var mapDefault = EmbeddedDefaults.Read("defaults/").Where(kv => kv.Key == "gamemap.json").ToDictionary(kv => kv.Key, kv => kv.Value);
            var mapReport = DefaultsSync.Sync(mapDefault, Paths.Root);
            SyncSummary = report.Summary() + (mapReport.KeptPlayerEdits.Count > 0 ? "; gamemap.json kept (your edits)" : "");
            Log.Info(SyncSummary);
            Map = GameMap.Load();
            Access = new GameAccess(Map);
            Snapshots = new SnapshotBuilder(Access, BuildInfo.Version);
            Executor = new GameExecutor(Access, Config);
            ArchitectAgent = new AgentLoader(Paths.Agents, "architect");
            AssistantAgent = new AgentLoader(Paths.Agents, "assistant");
            Plans = new PlanStore(Paths.Plans);
            Runner = new PhaseRunner(Executor, Guardrails(), m => { Log.Info(m); Toast(m); });
            Runner.Changed += () => { if (Plan != null) SavePlan(); };
            Update = UpdateStatus.Load(Paths.UpdateStatus);
            LastRequest.Ambition = Config.DefaultAmbition;
            LastRequest.Mode = Config.DefaultMode;
            ReloadAgents();
        }

        public GuardrailSettings Guardrails() => new GuardrailSettings
        {
            SeasonAware = Config.SeasonAware, StockPause = Config.StockPause, FoodMonths = Config.FoodMonths,
            FirewoodMonths = Config.FirewoodMonths, AutoSave = Config.AutoSave, PauseOnRaid = Config.PauseOnRaid,
            MaxParallelOrders = Config.MaxParallelOrders
        };

        public void ReloadAgents()
        {
            AgentErrors.Clear();
            var a = ArchitectAgent.Load(Config.ArchitectProfile);
            foreach (var e in a.Errors) AgentErrors.Add("architect: " + e);
            var b = AssistantAgent.Load(Config.AssistantProfile);
            foreach (var e in b.Errors) AgentErrors.Add("assistant: " + e);
            if (a.Agent != null) foreach (var w in a.Agent.Warnings) Log.Warn("architect: " + w);
        }

        public void Toast(string m, float seconds = 6)
        {
            Notice = m;
            NoticeUntil = Time.realtimeSinceStartup + seconds;
        }

        /// <summary>Called when the game scene is ready: restore a plan saved for this settlement.</summary>
        public void OnGameSceneReady()
        {
            Access.ResetCache();
            try
            {
                LastSnapshot = Snapshots.Build();
                Executor.Grid = LastSnapshot.Grid;
                Executor.Knowledge = LastSnapshot.Knowledge;
            }
            catch (Exception e) { Log.Warn("initial snapshot failed: " + e.Message); }
            var saved = Plans.Load(SettlementKey);
            if (saved != null)
            {
                Plan = saved;
                Locked.Clear();
                foreach (var id in saved.LockedIds) Locked.Add(id);
                Runner.Load(saved, saved.Mode == "instant" ? ExecMode.Instant : ExecMode.Native);
                Toast("Frontier Architect: plan restored for " + SettlementKey + ".");
            }
        }

        public void Tick()
        {
            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + 2f;
            try { Runner.Tick(); } catch (Exception e) { Log.Error("executor tick: " + e); }
        }

        // ------------------------------------------------------------------ runs

        private ClaudeClient Client() => new ClaudeClient(new WebRequestTransport(), new ClaudeOptions
        {
            ApiKey = Config.ApiKey, UserAgent = "FrontierArchitect/" + BuildInfo.Version
        });

        private RunContext Context() => new RunContext
        {
            Client = Client(),
            AvailableModels = _models,
            ModelOverride = Config.ModelOverride,
            FoodMonths = Config.FoodMonths,
            FirewoodMonths = Config.FirewoodMonths,
            OpUnavailable = op => Executor.OpUnavailableReason(op, ExecMode.Instant),
            ModVersion = BuildInfo.Version,
        };

        public bool CanRun(out string why)
        {
            why = null;
            if (Busy) { why = "A run is already in progress."; return false; }
            if (string.IsNullOrWhiteSpace(Config.ApiKey)) { why = "Add your Anthropic API key in Settings first."; return false; }
            if (!Access.InGame) { why = "Load a settlement first."; return false; }
            return true;
        }

        private void Background(string what, Action<CancellationToken> work)
        {
            Busy = true;
            BusyWhat = what;
            Progress = null;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            var t = new Thread(() =>
            {
                try
                {
                    if (_models == null)
                    {
                        try { _models = Client().ListModels(token); }
                        catch (ClaudeException e) when (e.Kind == ClaudeErrorKind.Auth) { throw; }
                        catch { _models = null; }
                    }
                    work(token);
                }
                catch (ClaudeException e) { MainThread.Post(() => Toast(e.PlayerMessage, 10)); }
                catch (OperationCanceledException) { MainThread.Post(() => Toast("Cancelled.")); }
                catch (Exception e)
                {
                    MainThread.Post(() => { Toast(what + " failed: " + e.Message, 10); Log.Error(e.ToString()); });
                }
                finally { MainThread.Post(() => { Busy = false; BusyWhat = ""; }); }
            }) { IsBackground = true, Name = "FrontierArchitect " + what };
            t.Start();
        }

        public void Cancel() { try { _cancel?.Cancel(); } catch { } }

        private VillageSnapshot FreshSnapshot()
        {
            var snap = Snapshots.Build();
            foreach (var b in snap.Buildings) if (Locked.Contains(b.Id)) b.Locked = true;
            LastSnapshot = snap;
            Executor.Grid = snap.Grid;
            Executor.Knowledge = snap.Knowledge;
            return snap;
        }

        public void StartAssistant()
        {
            string why;
            if (!CanRun(out why)) { Toast(why); return; }
            ReloadAgents();
            var agent = AssistantAgent.Current;
            if (agent == null) { Toast("Assistant agent files are invalid: " + string.Join("; ", AgentErrors)); return; }
            var snap = FreshSnapshot();
            var ctx = Context();
            Background("Assistant", token =>
            {
                var outcome = Runs.Assistant(agent, snap, ctx, p => Progress = p, token);
                MainThread.Post(() =>
                {
                    if (outcome.Report == null) { Toast("Assistant: " + outcome.Error, 10); return; }
                    Report = outcome.Report;
                    SaveJson(Path.Combine(Paths.Reports, SettlementKey + ".report.json"), Report.ToJson());
                    UI.Windows.ShowReport();
                });
            });
        }

        public void StartArchitect(ArchitectRequest req)
        {
            string why;
            if (!CanRun(out why)) { Toast(why); return; }
            if (Runner.Running) { Toast("A plan is being executed; stop it before generating a new one."); return; }
            ReloadAgents();
            var agent = ArchitectAgent.Current;
            if (agent == null) { Toast("Architect agent files are invalid: " + string.Join("; ", AgentErrors)); return; }
            req.Locked = Locked.ToList();
            LastRequest = req;
            var snap = FreshSnapshot();
            var ctx = Context();
            Background("Architect", token =>
            {
                var outcome = Runs.Architect(agent, snap, req, ctx, p => Progress = p, token);
                MainThread.Post(() =>
                {
                    if (outcome.Plan == null) { Toast("Architect: " + outcome.Error, 12); return; }
                    Plan = outcome.Plan;
                    Plan.SettlementKey = SettlementKey;
                    PlanValidation = outcome.Validation;
                    Card = null;
                    SavePlan();
                    UI.Windows.ShowArchitect();
                });
            });
        }

        public void SavePlan()
        {
            if (Plan == null) return;
            Plan.LockedIds = Locked.ToList();
            try { Plans.Save(SettlementKey, Plan); } catch (Exception e) { Log.Warn("plan save: " + e.Message); }
        }

        private static void SaveJson(string path, JObject o)
        {
            try { File.WriteAllText(path, Json.Write(o, true)); } catch { }
        }

        // ------------------------------------------------------------------ Assistant apply

        public string ApplyBlocked(Finding f)
        {
            if (f.Kind != FindingActionKind.Apply) return f.ManualReason;
            foreach (var a in f.Ops)
            {
                var why = Executor.OpUnavailableReason(a.Op, ExecMode.Instant);
                if (why != null) return why;
            }
            return null;
        }

        public void ApplyFinding(Finding f)
        {
            if (f.Applied) return;
            var why = ApplyBlocked(f);
            if (why != null) { Toast(why); return; }
            f.BeforeAfter.Clear();
            var undo = new List<PlanAction>();
            foreach (var a in f.Ops)
            {
                var before = BeforeValue(a);
                string err;
                if (!Executor.Issue(a, ExecMode.Instant, out err)) { Toast("Apply failed: " + err, 10); break; }
                f.BeforeAfter.Add(a.Op + " " + a.Target + ": " + (before?.Item1 ?? "?") + " → " + AfterValue(a));
                if (before?.Item2 != null) undo.Add(before.Item2);
            }
            f.Applied = true;
            _undo[f] = undo;
        }

        private readonly Dictionary<Finding, List<PlanAction>> _undo = new Dictionary<Finding, List<PlanAction>>();

        public bool CanUndo(Finding f) => f.Applied && _undo.ContainsKey(f) && _undo[f].Count > 0;

        public void UndoFinding(Finding f)
        {
            List<PlanAction> list;
            if (!_undo.TryGetValue(f, out list)) return;
            foreach (var a in list)
            {
                string err;
                if (!Executor.Issue(a, ExecMode.Instant, out err)) Toast("Undo failed: " + err);
            }
            f.Applied = false;
            _undo.Remove(f);
        }

        public void ClearUndo() => _undo.Clear();

        private Tuple<string, PlanAction> BeforeValue(PlanAction a)
        {
            var b = LastSnapshot?.Building(a.Target);
            switch (a.Op)
            {
                case Ops.SetWorkers:
                    if (b == null) return null;
                    return Tuple.Create(b.WorkersAssigned.ToString(), new PlanAction { Id = a.Id + ".undo", Op = a.Op, Target = a.Target, Params = new JObject().Set("count", b.WorkersAssigned) });
                case Ops.SetPaused:
                    if (b == null) return null;
                    bool paused = b.Status == "paused";
                    return Tuple.Create(paused ? "paused" : "running", new PlanAction { Id = a.Id + ".undo", Op = a.Op, Target = a.Target, Params = new JObject().Set("paused", paused) });
                case Ops.SetRotation:
                    {
                        var f = LastSnapshot?.Field(a.Target);
                        if (f == null) return null;
                        var rot = new JArray(); foreach (var y in f.Rotation) rot.Add(y.ToJArray());
                        return Tuple.Create(string.Join(" / ", f.Rotation.Select(y => string.Join("+", y))), new PlanAction { Id = a.Id + ".undo", Op = a.Op, Target = a.Target, Params = new JObject().Set("rotation", rot) });
                    }
                case Ops.SetWorkCircle:
                    if (b?.WorkCircleCenter == null) return null;
                    return Tuple.Create(b.WorkCircleCenter.Value.ToString(), new PlanAction { Id = a.Id + ".undo", Op = a.Op, Target = a.Target, Params = new JObject().Set("x", b.WorkCircleCenter.Value.X).Set("z", b.WorkCircleCenter.Value.Z) });
            }
            return Tuple.Create<string, PlanAction>("?", null);
        }

        private static string AfterValue(PlanAction a)
        {
            if (a.Params.ContainsKey("count")) return a.Params.Int("count").ToString();
            if (a.Params.ContainsKey("limit")) return a.Params.Int("limit").ToString();
            if (a.Params.ContainsKey("paused")) return a.Params.Bool("paused") ? "paused" : "running";
            if (a.Params.ContainsKey("rotation")) return string.Join(" / ", a.Rotation().Select(y => string.Join("+", y)));
            if (a.Params.ContainsKey("x")) return "[" + a.X + "," + a.Z + "]";
            return "";
        }

        public void SendToArchitect(Finding f)
        {
            UI.Windows.ShowArchitect();
            UI.Windows.Architect.SeedRequest = f.Title + ": " + f.Recommendation;
        }

        // ------------------------------------------------------------------ Architect panel

        public void ToggleLock(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!Locked.Remove(id)) Locked.Add(id);
            if (Plan != null && LastSnapshot != null && ArchitectAgent.Current != null)
            {
                bool touched = Plan.AllActions().Any(a => a.Target == id && a.Status != ActionStatus.Rejected);
                if (touched)
                {
                    foreach (var a in Plan.AllActions().Where(a => a.Target == id)) a.Status = Locked.Contains(id) ? ActionStatus.Rejected : ActionStatus.Pending;
                    Recheck();
                }
            }
            SavePlan();
        }

        public void ToggleReject(PlanAction a)
        {
            a.Status = a.Status == ActionStatus.Rejected ? ActionStatus.Pending : ActionStatus.Rejected;
            Recheck();
            SavePlan();
        }

        public void Recheck()
        {
            if (Plan == null || LastSnapshot == null || ArchitectAgent.Current == null) return;
            var req = new ArchitectRequest { Ambition = Plan.Ambition, Mode = Plan.Mode, Locked = Locked.ToList() };
            PlanValidation = Runs.Recheck(Plan, LastSnapshot, ArchitectAgent.Current, req, Context());
        }

        public void LockUnderCursor()
        {
            Vector3 p;
            if (!GameAccess.CursorWorldPoint(out p) || LastSnapshot?.Grid == null) return;
            var cell = LastSnapshot.Grid.WorldToCell(p.x, p.z);
            var b = LastSnapshot.Buildings.FirstOrDefault(x => x.Footprint.Contains(cell));
            if (b == null) { Toast("No building under the cursor."); return; }
            ToggleLock(b.Id);
            Toast((Locked.Contains(b.Id) ? "Locked " : "Unlocked ") + b.Id);
        }

        // ------------------------------------------------------------------ Execute

        public void SendToExecute(ExecMode mode)
        {
            if (Plan == null) return;
            var blocked = Executor.ExecuteBlockedReason();
            if (blocked != null) Toast(blocked, 12);
            foreach (var p in Plan.Phases) foreach (var a in p.Actions) if (a.Status == ActionStatus.Rejected) a.Status = ActionStatus.Rejected;
            Runner.Load(Plan, mode);
            Plan.Mode = mode == ExecMode.Instant ? "instant" : "native";
            Card = Runner.Prepare();
            SavePlan();
            UI.Windows.ShowExecute();
        }

        public void PrepareCard() { Card = Runner.Prepare(); }

        public void Approve()
        {
            if (Card == null) PrepareCard();
            if (Runner.Approve(Card, SettlementKey)) Card = null;
            else Toast(Runner.LastMessage, 10);
            SavePlan();
        }

        public void Rollback(Phase p)
        {
            SavePlan();
            string err;
            if (!Runner.Rollback(p, out err)) Toast(err, 12);
            SavePlan();
        }

        public void RepairCurrentPhase()
        {
            string why;
            if (!CanRun(out why)) { Toast(why); return; }
            if (Card == null) PrepareCard();
            if (Card == null) return;
            var problems = Card.Actions.Where(kv => kv.Value != null && !kv.Value.StartsWith("manual:")).Select(kv => kv.Key.Id + ": " + kv.Value).ToList();
            if (problems.Count == 0) { Toast("Nothing to repair."); return; }
            var agent = ArchitectAgent.Current;
            var snap = FreshSnapshot();
            var ctx = Context();
            var plan = Plan;
            int from = Runner.Index;
            var req = new ArchitectRequest { Ambition = plan.Ambition, Mode = plan.Mode, Locked = Locked.ToList() };
            Background("Repair", token =>
            {
                var outcome = Runs.RepairFrom(agent, snap, plan, from, problems, req, ctx, p => Progress = p, token);
                MainThread.Post(() =>
                {
                    if (outcome.Plan == null) { Toast("Repair: " + outcome.Error, 12); return; }
                    Plan = outcome.Plan;
                    Runner.Load(Plan, Runner.Mode);
                    Card = Runner.Prepare();
                    SavePlan();
                    Toast("Phase repaired.");
                });
            });
        }

        // ------------------------------------------------------------------ M0 mapping helpers

        /// <summary>Op armed for an in-game test: the next lock-key press over a building tests it.</summary>
        public string ArmedTest;

        public void LockOrTestUnderCursor()
        {
            if (ArmedTest == null) { LockUnderCursor(); return; }
            Vector3 p;
            if (!GameAccess.CursorWorldPoint(out p) || LastSnapshot?.Grid == null) return;
            var cell = LastSnapshot.Grid.WorldToCell(p.x, p.z);
            var b = LastSnapshot.Buildings.FirstOrDefault(x => x.Footprint.Contains(cell));
            if (b == null) { Toast("No building under the cursor."); return; }
            string op = ArmedTest;
            ArmedTest = null;
            Toast(TestOp(op, b));
        }

        /// <summary>Runs an op on a real building and checks its completion test, then reverts it.</summary>
        private string TestOp(string op, BuildingInfo b)
        {
            var before = Config.AllowExperimentalOps;
            Config.AllowExperimentalOps = true; // a test may exercise experimental recipes
            try
            {
                PlanAction doIt, undo;
                if (op == Ops.SetPaused)
                {
                    bool paused = b.Status == "paused";
                    doIt = new PlanAction { Id = "test", Op = op, Target = b.Id, Params = new JObject().Set("paused", !paused) };
                    undo = new PlanAction { Id = "test.undo", Op = op, Target = b.Id, Params = new JObject().Set("paused", paused) };
                }
                else if (op == Ops.SetWorkers)
                {
                    int n = b.WorkersAssigned < Math.Max(1, b.WorkersMax) ? b.WorkersAssigned + 1 : Math.Max(0, b.WorkersAssigned - 1);
                    doIt = new PlanAction { Id = "test", Op = op, Target = b.Id, Params = new JObject().Set("count", n) };
                    undo = new PlanAction { Id = "test.undo", Op = op, Target = b.Id, Params = new JObject().Set("count", b.WorkersAssigned) };
                }
                else return "In-game tests cover set_workers and set_paused; test other ops on a throwaway save with Execute (Instant).";
                string err, note;
                if (!Executor.Issue(doIt, ExecMode.Instant, out err)) { Map.RecordLocal(op, OpStatus.Experimental, "test failed: " + err); return op + " test failed: " + err; }
                var st = Executor.Check(doIt, ExecMode.Instant, out note);
                Executor.Issue(undo, ExecMode.Instant, out err);
                if (st == OrderProgress.Done) { Map.RecordLocal(op, OpStatus.Proven, "tested on " + b.Id); return op + " works on this build: marked proven."; }
                Map.RecordLocal(op, OpStatus.Experimental, "verify did not confirm: " + note);
                return op + " ran but its completion test did not confirm it (" + (note ?? "value unchanged") + ").";
            }
            finally { Config.AllowExperimentalOps = before; }
        }

        public string ProposalPath => Path.Combine(Paths.Root, "gamemap.proposed.json");

        /// <summary>Sends the probe output to Claude and writes a proposed gamemap patch for review.</summary>
        public void ProposeGameMap()
        {
            string why;
            if (!CanRun(out why)) { Toast(why); return; }
            string cand = Path.Combine(Paths.Probe, "candidates.md"), rep = Path.Combine(Paths.Probe, "report.json");
            if (!File.Exists(cand) || !File.Exists(rep)) { Toast(Game.Probe.Run(Access)); }
            string system = File.Exists(Path.Combine(Path.Combine(Paths.Agents, "mapper"), "system.md"))
                ? File.ReadAllText(Path.Combine(Path.Combine(Paths.Agents, "mapper"), "system.md")) : "Return a JSON patch for gamemap.json.";
            string candidates = File.ReadAllText(cand);
            if (candidates.Length > 700000) candidates = candidates.Substring(0, 700000) + "\n… (truncated)";
            string user = "gamemap.json (current):\n" + Json.Write(Map.Doc, true) + "\n\nreport.json:\n" + File.ReadAllText(rep) + "\n\ncandidates.md:\n" + candidates;
            var models = _models;
            var ctxModel = Config.ModelOverride;
            Background("Mapper", token =>
            {
                var client = Client();
                var list = ModelResolver.Candidates(string.IsNullOrWhiteSpace(ctxModel) ? ModelResolver.MostCapable : ctxModel, _models ?? models);
                var body = new JObject()
                    .Set("model", list[0]).Set("max_tokens", 32000)
                    .Set("thinking", new JObject().Set("type", "adaptive"))
                    .Set("output_config", new JObject().Set("effort", "high"))
                    .Set("system", system)
                    .Set("messages", new JArray { new JObject().Set("role", "user").Set("content", user) });
                var betas = new List<string>();
                if (ModelResolver.SupportsServerFallback(list[0])) { body.Set("fallbacks", "default"); betas.Add("server-side-fallback-2026-07-01"); }
                var msg = client.Stream(body, betas, null, token);
                var patch = Json.ExtractFirstObject(msg.Text);
                MainThread.Post(() =>
                {
                    if (patch == null) { Toast("Claude did not return a JSON patch.", 10); return; }
                    File.WriteAllText(ProposalPath, Json.Write(patch, true));
                    Toast("Proposal written to gamemap.proposed.json: review it, then Apply proposal.", 12);
                });
            });
        }

        /// <summary>Merges the reviewed proposal into gamemap.local.json; nothing it contains counts as proven.</summary>
        public void ApplyGameMapProposal()
        {
            if (!File.Exists(ProposalPath)) { Toast("No proposal yet."); return; }
            try
            {
                var patch = Json.ParseObject(File.ReadAllText(ProposalPath));
                foreach (var kv in patch.Obj("ops") ?? new JObject())
                    if (kv.Value is JObject op && (op.Str("status") ?? "") == "proven") op.Set("status", "experimental");
                foreach (var kv in patch.Obj("procedures") ?? new JObject())
                    if (kv.Value is JObject pr && (pr.Str("status") ?? "") == "proven") pr.Set("status", "experimental");
                patch.Remove("mapped_game_version");
                var local = File.Exists(Paths.GameMapLocal) ? Json.ParseObject(File.ReadAllText(Paths.GameMapLocal)) : new JObject();
                AgentLoader.DeepMerge(local, patch);
                File.WriteAllText(Paths.GameMapLocal, Json.Write(local, true));
                Map = GameMap.Load();
                Access = new GameAccess(Map);
                Snapshots = new SnapshotBuilder(Access, BuildInfo.Version);
                var grid = Executor.Grid; var know = Executor.Knowledge;
                Executor = new GameExecutor(Access, Config) { Grid = grid, Knowledge = know };
                Runner = new PhaseRunner(Executor, Guardrails(), m => { Log.Info(m); Toast(m); });
                Runner.Changed += () => { if (Plan != null) SavePlan(); };
                if (Plan != null) Runner.Load(Plan, Plan.Mode == "instant" ? ExecMode.Instant : ExecMode.Native);
                Toast("Proposal merged into gamemap.local.json (ops are experimental until tested).");
            }
            catch (Exception e) { Toast("Could not apply the proposal: " + e.Message, 10); }
        }

        public void DiscardPlan()
        {
            if (Runner.Running) { Toast("Stop the running phase first."); return; }
            Plans.Archive(SettlementKey);
            Plan = null; PlanValidation = null; Card = null;
        }
    }
}
