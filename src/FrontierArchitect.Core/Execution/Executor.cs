using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;

namespace FrontierArchitect.Core.Execution
{
    public enum ExecMode { Native, Instant }
    public enum OrderProgress { Pending, Done, Failed }

    public sealed class LiveStatus
    {
        public double FoodMonths = double.NaN, FirewoodMonths = double.NaN;
        public string Season = "spring";
        public float SeasonProgress;
        public bool GroundFrozen;
        public bool RaidActive;
        /// <summary>Monotonic game day counter (for duration tracking).</summary>
        public double GameDay;
        public string DateText = "";
        public int IdleBuilders = -1;
        public Dictionary<string, double> Stock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Game-side operations the executor needs. Implemented in the mod against the game's own
    /// code paths; every op has a validator, a Native executor, an Instant executor and a
    /// completion test — an op missing any of the four is reported unavailable.
    /// </summary>
    public interface IGameExecutor
    {
        string OpUnavailableReason(string op, ExecMode mode);
        string ValidateLive(PlanAction a, ExecMode mode);
        bool Issue(PlanAction a, ExecMode mode, out string error);
        OrderProgress Check(PlanAction a, ExecMode mode, out string note);
        bool CanSave(out string reason);
        bool Save(string name, out string error);
        bool Load(string name, out string error);
        LiveStatus Status();
        /// <summary>Null when Execute is allowed (game build matches the mapped build).</summary>
        string ExecuteBlockedReason();
    }

    public sealed class GuardrailSettings
    {
        public bool SeasonAware = true;
        public bool StockPause = true;
        public double FoodMonths = 3, FirewoodMonths = 3;
        public bool AutoSave = true;
        public bool PauseOnRaid = true;
        public int MaxParallelOrders = 3;
    }

    public sealed class GuardrailVerdict
    {
        public bool CanStart = true;
        public bool MustPause;
        public readonly List<string> Reasons = new List<string>();
    }

    public static class Guardrails
    {
        public static GuardrailVerdict Evaluate(Phase p, LiveStatus live, GuardrailSettings g, bool starting)
        {
            var v = new GuardrailVerdict();
            if (g.SeasonAware && starting)
            {
                string slot = Seasons.Slot(live.Season, live.SeasonProgress);
                var window = Seasons.Normalise(p.SeasonWindow);
                if (window.Count > 0 && !window.Contains(slot))
                {
                    v.CanStart = false;
                    v.Reasons.Add("season window: may start in " + string.Join(", ", Seasons.Slots.Where(window.Contains)) + " (now " + slot.Replace('_', ' ') + ")");
                }
                bool ground = p.Actions.Any(a => a.Op == Ops.Flatten || a.Op == Ops.ClearArea || a.Op == Ops.FieldCreate || a.Op == Ops.FieldResize || a.Op == Ops.RoadAdd);
                if (ground && live.GroundFrozen) { v.CanStart = false; v.Reasons.Add("ground is frozen"); }
            }
            if (g.StockPause)
            {
                if (!double.IsNaN(live.FoodMonths) && live.FoodMonths < g.FoodMonths)
                {
                    v.CanStart = false; v.MustPause = true;
                    v.Reasons.Add("food at " + Math.Round(live.FoodMonths, 1) + " months (threshold " + g.FoodMonths + ")");
                }
                if (!double.IsNaN(live.FirewoodMonths) && live.FirewoodMonths < g.FirewoodMonths)
                {
                    v.CanStart = false; v.MustPause = true;
                    v.Reasons.Add("firewood at " + Math.Round(live.FirewoodMonths, 1) + " months (threshold " + g.FirewoodMonths + ")");
                }
            }
            if (g.PauseOnRaid && live.RaidActive) { v.CanStart = false; v.MustPause = true; v.Reasons.Add("raid in progress"); }
            return v;
        }
    }

    public sealed class PhaseCard
    {
        public Phase Phase;
        public readonly List<KeyValuePair<PlanAction, string>> Actions = new List<KeyValuePair<PlanAction, string>>(); // action, problem (null = ok)
        public readonly Dictionary<string, double[]> CostVsStock = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public GuardrailVerdict Guardrails;
        public int BuildersNeeded;
        public string SaveBlocked;
        public string ExecuteBlocked;
        public bool NeedsRepair => Actions.Any(a => a.Value != null && !a.Value.StartsWith("manual:"));
    }

    /// <summary>
    /// Runs an approved plan phase by phase (driven by Tick() on the Unity main thread).
    /// Invariants from the spec: nothing is issued without approval; a pre-phase save exists for
    /// every started phase; a paused phase issues no orders; stopping leaves the last completed phase.
    /// </summary>
    public sealed class PhaseRunner
    {
        private readonly IGameExecutor _game;
        private GuardrailSettings _g;
        private readonly Action<string> _log;
        private readonly List<PlanAction> _queue = new List<PlanAction>();
        private readonly List<PlanAction> _inFlight = new List<PlanAction>();
        private double _phaseStartDay;

        public ArchitectPlan Plan { get; private set; }
        public ExecMode Mode { get; private set; }
        public int Index { get; private set; } = -1;
        public string PauseReason { get; private set; }
        public string LastMessage { get; private set; } = "";
        public Phase Current => Index >= 0 && Index < Plan.Phases.Count ? Plan.Phases[Index] : null;
        public bool Running => Current != null && (Current.Status == PhaseStatus.InProgress || Current.Status == PhaseStatus.Paused);

        public event Action Changed;

        /// <summary>Applies new guardrail settings (thresholds edited in Settings) from the next tick.</summary>
        public void UpdateGuardrails(GuardrailSettings g) { if (g != null) _g = g; }

        public PhaseRunner(IGameExecutor game, GuardrailSettings g, Action<string> log)
        {
            _game = game; _g = g; _log = log ?? (_ => { });
        }

        public void Load(ArchitectPlan plan, ExecMode mode)
        {
            Plan = plan;
            Mode = mode;
            Index = plan.Phases.FindIndex(p => p.Status != PhaseStatus.Completed && p.Status != PhaseStatus.Skipped);
            var cur = Current;
            if (cur != null && (cur.Status == PhaseStatus.InProgress || cur.Status == PhaseStatus.Paused))
            {
                // resumed after a reload: rebuild the queue from the action statuses
                _queue.Clear(); _inFlight.Clear();
                foreach (var a in cur.Actions)
                {
                    if (a.Status == ActionStatus.Pending) _queue.Add(a);
                    else if (a.Status == ActionStatus.Issued) _inFlight.Add(a);
                }
                if (cur.Status == PhaseStatus.InProgress) { cur.Status = PhaseStatus.Paused; PauseReason = "game reloaded: press Resume to continue"; }
            }
            Changed?.Invoke();
        }

        public bool SetMode(ExecMode mode)
        {
            if (Running) { LastMessage = "The mode can only change at a phase boundary."; return false; }
            Mode = mode;
            Plan.Mode = mode == ExecMode.Instant ? "instant" : "native";
            Changed?.Invoke();
            return true;
        }

        /// <summary>Re-validates the next phase against the live village and builds its card.</summary>
        public PhaseCard Prepare()
        {
            var p = Current;
            if (p == null) return null;
            var card = new PhaseCard { Phase = p, ExecuteBlocked = _game.ExecuteBlockedReason() };
            var live = _game.Status();
            foreach (var a in p.Actions)
            {
                if (a.Status == ActionStatus.Rejected || a.Status == ActionStatus.Skipped) continue;
                string why = _game.OpUnavailableReason(a.Op, Mode);
                if (why != null) { card.Actions.Add(new KeyValuePair<PlanAction, string>(a, "manual: " + why)); continue; }
                card.Actions.Add(new KeyValuePair<PlanAction, string>(a, _game.ValidateLive(a, Mode)));
            }
            foreach (var kv in p.EstCost)
            {
                if (kv.Key == "road_tiles") continue;
                double have; live.Stock.TryGetValue(kv.Key, out have);
                card.CostVsStock[kv.Key] = new[] { Json.ToDouble(kv.Value), have };
            }
            card.BuildersNeeded = Math.Min(10, Math.Max(1, p.Actions.Count(a => Ops.Spatial.Contains(a.Op))));
            card.Guardrails = Guardrails.Evaluate(p, live, _g, true);
            string saveWhy;
            if (_g.AutoSave && !_game.CanSave(out saveWhy)) card.SaveBlocked = saveWhy;
            if (p.Status == PhaseStatus.NotStarted) p.Status = PhaseStatus.AwaitingApproval;
            return card;
        }

        public bool Approve(PhaseCard card, string settlementKey)
        {
            var p = Current;
            if (p == null || card == null || card.Phase != p) { LastMessage = "Nothing to approve."; return false; }
            if (card.ExecuteBlocked != null) { LastMessage = card.ExecuteBlocked; return false; }
            if (card.NeedsRepair) { LastMessage = "Some actions no longer fit the village; repair the phase first."; return false; }
            if (!card.Guardrails.CanStart) { LastMessage = "Cannot start: " + string.Join("; ", card.Guardrails.Reasons); return false; }
            if (_g.AutoSave)
            {
                if (card.SaveBlocked != null) { LastMessage = "Auto-save unavailable: " + card.SaveBlocked; return false; }
                string name = SaveName(settlementKey, p.N);
                string err;
                if (!_game.Save(name, out err)) { LastMessage = "Auto-save failed, phase not started: " + err; return false; }
                p.SaveName = name;
            }
            _queue.Clear(); _inFlight.Clear();
            foreach (var kv in card.Actions)
            {
                if (kv.Value != null) { kv.Key.Status = ActionStatus.Manual; kv.Key.StatusNote = kv.Value.Substring(Math.Min(kv.Value.Length, 8)); continue; }
                kv.Key.Status = ActionStatus.Pending;
                _queue.Add(kv.Key);
            }
            var live = _game.Status();
            _phaseStartDay = live.GameDay;
            p.StartedAtGameDate = live.DateText;
            p.Status = PhaseStatus.InProgress;
            PauseReason = null;
            LastMessage = "Phase " + p.N + " started.";
            _log(LastMessage);
            Changed?.Invoke();
            Tick();
            return true;
        }

        public static string SaveName(string settlementKey, int phase) =>
            "FA_" + Regex.Replace(settlementKey ?? "village", "[^A-Za-z0-9]", "") + "_phase" + phase;

        public void Tick()
        {
            var p = Current;
            if (p == null || p.Status != PhaseStatus.InProgress) return;
            var live = _game.Status();
            var g = Guardrails.Evaluate(p, live, _g, false);
            if (g.MustPause)
            {
                p.Status = PhaseStatus.Paused;
                PauseReason = string.Join("; ", g.Reasons);
                LastMessage = "Paused: " + PauseReason;
                _log(LastMessage);
                Changed?.Invoke();
                return;
            }
            // completion of issued orders
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                var a = _inFlight[i];
                string note;
                var st = _game.Check(a, Mode, out note);
                if (st == OrderProgress.Pending) continue;
                a.Status = st == OrderProgress.Done ? ActionStatus.Done : ActionStatus.Failed;
                a.StatusNote = note ?? "";
                _inFlight.RemoveAt(i);
                Changed?.Invoke();
            }
            // issue new orders (Instant: everything now; Native: a few at a time)
            int capacity = Mode == ExecMode.Instant ? int.MaxValue : Math.Max(1, _g.MaxParallelOrders);
            while (_queue.Count > 0 && _inFlight.Count < capacity)
            {
                var a = _queue[0];
                _queue.RemoveAt(0);
                string why = _game.ValidateLive(a, Mode);
                if (why != null) { a.Status = ActionStatus.Failed; a.StatusNote = why; Changed?.Invoke(); continue; }
                string err;
                if (_game.Issue(a, Mode, out err)) { a.Status = ActionStatus.Issued; _inFlight.Add(a); }
                else { a.Status = ActionStatus.Failed; a.StatusNote = err ?? "order refused"; }
                Changed?.Invoke();
            }
            if (_queue.Count == 0 && _inFlight.Count == 0) CompletePhase(live);
        }

        private void CompletePhase(LiveStatus live)
        {
            var p = Current;
            p.ActualDays = Math.Max(0, live.GameDay - _phaseStartDay);
            int done = p.Actions.Count(a => a.Status == ActionStatus.Done);
            int failed = p.Actions.Count(a => a.Status == ActionStatus.Failed);
            int manual = p.Actions.Count(a => a.Status == ActionStatus.Manual);
            p.Report = "Planned " + Math.Round(p.EstDays, 1) + " days, took " + Math.Round(p.ActualDays, 1) + ". " +
                       done + " done, " + failed + " failed, " + manual + " manual.";
            p.Status = failed > 0 && done == 0 ? PhaseStatus.Failed : PhaseStatus.Completed;
            LastMessage = "Phase " + p.N + " finished: " + p.Report;
            _log(LastMessage);
            Index = Plan.Phases.FindIndex(x => x.Status != PhaseStatus.Completed && x.Status != PhaseStatus.Skipped && x.Status != PhaseStatus.Failed);
            Changed?.Invoke();
        }

        public void Resume()
        {
            var p = Current;
            if (p == null || p.Status != PhaseStatus.Paused) return;
            var g = Guardrails.Evaluate(p, _game.Status(), _g, false);
            if (g.MustPause) { LastMessage = "Still blocked: " + string.Join("; ", g.Reasons); Changed?.Invoke(); return; }
            p.Status = PhaseStatus.InProgress;
            PauseReason = null;
            LastMessage = "Resumed.";
            Changed?.Invoke();
        }

        public void PauseByPlayer()
        {
            var p = Current;
            if (p == null || p.Status != PhaseStatus.InProgress) return;
            p.Status = PhaseStatus.Paused;
            PauseReason = "paused by player";
            Changed?.Invoke();
        }

        public void Skip()
        {
            var p = Current;
            if (p == null || Running) { LastMessage = "Finish or roll back the running phase first."; return; }
            p.Status = PhaseStatus.Skipped;
            foreach (var a in p.Actions) if (a.Status == ActionStatus.Pending) a.Status = ActionStatus.Skipped;
            Index = Plan.Phases.FindIndex(x => x.Status != PhaseStatus.Completed && x.Status != PhaseStatus.Skipped && x.Status != PhaseStatus.Failed);
            Changed?.Invoke();
        }

        /// <summary>Stop at the boundary: issues nothing more; the village keeps the last completed phase.</summary>
        public void Stop()
        {
            var p = Current;
            if (p != null && Running)
            {
                _queue.Clear();
                p.Status = PhaseStatus.Paused;
                PauseReason = "plan stopped: roll back this phase or let issued orders finish";
            }
            LastMessage = "Plan stopped.";
            Changed?.Invoke();
        }

        /// <summary>Reloads the pre-phase save. The caller persists the plan first (the game reloads).</summary>
        public bool Rollback(Phase phase, out string error)
        {
            error = null;
            if (phase == null || string.IsNullOrEmpty(phase.SaveName)) { error = "This phase has no pre-phase save."; return false; }
            foreach (var a in phase.Actions)
                if (a.Status != ActionStatus.Rejected) { a.Status = ActionStatus.Pending; a.StatusNote = ""; }
            phase.Status = PhaseStatus.RolledBack;
            _queue.Clear(); _inFlight.Clear();
            Changed?.Invoke();
            if (!_game.Load(phase.SaveName, out error)) return false;
            phase.Status = PhaseStatus.NotStarted;
            Index = Plan.Phases.IndexOf(phase);
            return true;
        }
    }

    /// <summary>Persists plan and phase status per settlement so a plan survives a reload.</summary>
    public sealed class PlanStore
    {
        private readonly string _dir;
        public PlanStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

        public static string Key(string settlement, string save)
        {
            string s = string.IsNullOrWhiteSpace(settlement) ? save : settlement;
            s = Regex.Replace(s ?? "village", "[^A-Za-z0-9_-]", "_");
            return s.Length == 0 ? "village" : s;
        }

        private string PathFor(string key) => Path.Combine(_dir, key + ".plan.json");

        public void Save(string key, ArchitectPlan plan)
        {
            var o = plan.ToJson();
            string path = PathFor(key);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(o, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public ArchitectPlan Load(string key)
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return null;
            try { return ArchitectPlan.FromJson(Json.ParseObject(File.ReadAllText(path))); }
            catch { return null; }
        }

        public void Archive(string key)
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return;
            string arch = Path.Combine(_dir, "archive");
            Directory.CreateDirectory(arch);
            File.Move(path, Path.Combine(arch, key + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".plan.json"));
        }
    }
}
