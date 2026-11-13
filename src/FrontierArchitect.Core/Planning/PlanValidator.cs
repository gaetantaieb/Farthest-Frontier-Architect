using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Roads;
using FrontierArchitect.Core.Scoring;

namespace FrontierArchitect.Core.Planning
{
    public sealed class PlanValidation
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly Dictionary<string, List<string>> ActionErrors = new Dictionary<string, List<string>>();
        public readonly List<PhaseForecast> Forecast = new List<PhaseForecast>();
        public ScoreCard Before, After;
        public double FinalConnectivity = 1;
        public DraftLayout FinalDraft;

        public bool Ok => Errors.Count == 0;

        public JObject ToJson()
        {
            var ae = new JObject();
            foreach (var kv in ActionErrors) ae.Set(kv.Key, kv.Value.ToJArray());
            var fc = new JArray(); foreach (var f in Forecast) fc.Add(f.ToJson());
            return new JObject().Set("ok", Ok).Set("errors", Errors.Take(40).ToJArray()).Set("warnings", Warnings.Take(40).ToJArray())
                .Set("action_errors", ae).Set("forecast", fc)
                .Set("score_before", Before?.ObjectivesJson()).Set("score_after", After?.ObjectivesJson())
                .Set("final_connectivity", Math.Round(FinalConnectivity, 3));
        }
    }

    /// <summary>
    /// The only gate between Claude's plan and the player's screen: replays every action on a fresh
    /// draft and enforces the hard constraints and the phase rules of the spec.
    /// </summary>
    public sealed class PlanValidator
    {
        private readonly RunSettings _s;
        private readonly double _foodMonths, _woodMonths;

        public PlanValidator(RunSettings settings, double foodThresholdMonths = 3, double firewoodThresholdMonths = 3)
        {
            _s = settings; _foodMonths = foodThresholdMonths; _woodMonths = firewoodThresholdMonths;
        }

        public PlanValidation Validate(ArchitectPlan plan, VillageSnapshot snap, bool fillEstimates = true)
        {
            var v = new PlanValidation();
            var k = snap.Knowledge;
            if (plan.Phases.Count == 0) { v.Errors.Add("the plan has no phases"); return v; }

            // numbering and ids
            plan.Phases.Sort((a, b) => a.N.CompareTo(b.N));
            var seen = new HashSet<string>();
            int seq = 0;
            for (int i = 0; i < plan.Phases.Count; i++)
            {
                var p = plan.Phases[i];
                p.N = i + 1;
                if (p.Actions.Count == 0) v.Warnings.Add("phase " + p.N + " has no actions");
                if (p.Actions.Count > _s.PhaseMaxActions) v.Errors.Add("phase " + p.N + " has " + p.Actions.Count + " actions (max " + _s.PhaseMaxActions + "); split it");
                foreach (var a in p.Actions)
                {
                    seq++;
                    if (string.IsNullOrEmpty(a.Id) || !seen.Add(a.Id)) { a.Id = "a" + seq; seen.Add(a.Id); }
                    if (string.IsNullOrWhiteSpace(a.Rationale)) v.Warnings.Add(a.Id + " has no rationale");
                    else if (a.Rationale.Split(' ').Length > 20) v.Warnings.Add(a.Id + " rationale is longer than 15 words");
                    if (!new[] { "O1", "O2", "O3", "O4", "O5", "O6" }.Contains(a.Objective)) v.Warnings.Add(a.Id + " has no objective tag O1..O6");
                }
            }

            var draft = new DraftLayout(snap);
            var scorer = new Scorer(_s);
            v.Before = scorer.Score(draft);
            double startConnectivity = v.Before.Roads?.Connectivity ?? 1;
            Stage lastPrimary = Stage.Settings;
            int firstFieldCreatePhase = int.MaxValue;
            foreach (var p in plan.Phases)
                if (p.Actions.Any(a => a.Op == Ops.FieldCreate)) { firstFieldCreatePhase = Math.Min(firstFieldCreatePhase, p.N); }
            var builtTypesBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in plan.Phases)
            {
                // ---- stage order
                var stages = p.Actions.Where(a => !Ops.Settings.Contains(a.Op) && a.Op != Ops.RoadAdd && a.Op != Ops.RoadRemove)
                                      .Select(a => a.Stage(k, snap)).ToList();
                if (stages.Count > 0)
                {
                    var primary = stages.Min();
                    if (primary < lastPrimary)
                        v.Errors.Add("phase " + p.N + " (" + primary + ") comes after a " + lastPrimary + " phase; standard order is Settings, Prepare, Add capacity, Storage and logistics, Production chains, Farming, Residential and services, Clean-up");
                    lastPrimary = primary > lastPrimary ? primary : lastPrimary;
                }

                // ---- offline cap (tier 1 and 2 types only)
                var countByType = draft.LiveBuildings.GroupBy(b => b.Type).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var offlineByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in p.Actions.Where(a => a.Op == Ops.Relocate || a.Op == Ops.Demolish))
                {
                    BuildingInfo b;
                    if (a.Target == null || !draft.Buildings.TryGetValue(a.Target, out b)) continue;
                    if (!k.IsTierOneTwo(b.Type)) continue;
                    int c; offlineByType.TryGetValue(b.Type, out c); offlineByType[b.Type] = c + 1;
                }
                foreach (var kv in offlineByType)
                {
                    int total; countByType.TryGetValue(kv.Key, out total);
                    int allowed = Math.Max(1, (int)Math.Floor(_s.OfflineCap * total));
                    if (kv.Value > allowed)
                        v.Errors.Add("phase " + p.N + " takes " + kv.Value + " of " + total + " " + kv.Key + " offline (cap " + Math.Round(_s.OfflineCap * 100) + "% → max " + allowed + ")");
                }

                // ---- seasons
                var window = Seasons.Normalise(p.SeasonWindow);
                bool fieldWork = p.Actions.Any(a => a.Op == Ops.FieldRemove || a.Op == Ops.FieldResize);
                bool groundWork = p.Actions.Any(a => a.Op == Ops.Flatten || a.Op == Ops.ClearArea || a.Op == Ops.FieldCreate || a.Op == Ops.FieldResize || a.Op == Ops.RoadAdd);
                bool foodWoodMove = p.Actions.Any(a => (a.Op == Ops.Relocate || a.Op == Ops.Demolish) && a.Target != null &&
                    draft.Buildings.TryGetValue(a.Target, out BuildingInfo fb) && (k.Spec(fb.Type).ProducesFood || k.Spec(fb.Type).ProducesFirewood));
                var forbidden = new HashSet<string>();
                if (groundWork) forbidden.Add("winter");
                if (foodWoodMove) { forbidden.Add("late_autumn"); forbidden.Add("winter"); }
                if (fieldWork) { forbidden.Add("summer"); forbidden.Add("early_autumn"); } // crop in the ground
                if (window.Count == 0)
                {
                    foreach (var slot in Seasons.Slots) if (!forbidden.Contains(slot)) window.Add(slot);
                    if (forbidden.Count > 0) v.Warnings.Add("phase " + p.N + ": season window filled by the mod (" + string.Join(", ", window) + ")");
                    p.SeasonWindow = Seasons.Slots.Where(window.Contains).ToList();
                }
                foreach (var bad in forbidden.Where(window.Contains))
                    v.Errors.Add("phase " + p.N + " may not run in " + bad + (fieldWork ? " (crops in the ground)" : foodWoodMove ? " (food or firewood building moves)" : " (frozen ground)"));
                if (window.Count == 0 || window.All(forbidden.Contains))
                    v.Errors.Add("phase " + p.N + " has no season in which it may start");

                // ---- fields: new fields first, old fields after
                foreach (var a in p.Actions.Where(a => a.Op == Ops.FieldRemove))
                    if (firstFieldCreatePhase != int.MaxValue && p.N < firstFieldCreatePhase)
                        v.Errors.Add(a.Id + " removes a field before the plan's new fields exist (phase " + firstFieldCreatePhase + ")");

                // ---- replay actions on the draft
                var phaseCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                double days = 0;
                foreach (var a in p.Actions)
                {
                    if (a.Status == ActionStatus.Rejected) continue;
                    BuildingInfo demolishTarget = null;
                    if (a.Op == Ops.Demolish && a.Target != null) draft.Buildings.TryGetValue(a.Target, out demolishTarget);
                    var r = draft.Apply(a, _s);
                    if (!r.Valid)
                    {
                        v.ActionErrors[a.Id] = r.Reasons;
                        v.Errors.Add(a.Id + " (" + a.Op + " " + a.Target + "): " + string.Join("; ", r.Reasons));
                        continue;
                    }
                    foreach (var w in r.Warnings) v.Warnings.Add(a.Id + ": " + w);
                    foreach (var kv in r.Cost)
                    {
                        if (kv.Key == "build_days" || kv.Key == "labour_days") { days += kv.Value; continue; }
                        double c; phaseCost.TryGetValue(kv.Key, out c); phaseCost[kv.Key] = c + kv.Value;
                    }
                    if (a.Op == Ops.Build) builtTypesBefore.Add(a.Target);
                    if (demolishTarget != null && _s.BuildReplacementFirst && k.IsTierOneTwo(demolishTarget.Type))
                    {
                        bool otherLeft = draft.LiveBuildings.Any(b => string.Equals(b.Type, demolishTarget.Type, StringComparison.OrdinalIgnoreCase));
                        if (!otherLeft && !builtTypesBefore.Contains(demolishTarget.Type))
                            v.Errors.Add(a.Id + " demolishes the last " + demolishTarget.Type + "; build its replacement in an earlier phase first");
                    }
                }
                if (fillEstimates)
                {
                    if (p.EstCost.Count == 0) foreach (var kv in phaseCost) p.EstCost.Set(kv.Key, Math.Round(kv.Value, 1));
                    if (p.EstDays <= 0) p.EstDays = Math.Round(days, 1);
                    if (p.Offline.Count == 0)
                        p.Offline = p.Actions.Where(a => a.Op == Ops.Relocate || a.Op == Ops.Demolish).Select(a => a.Target).Where(t => t != null).ToList();
                }

                // ---- connectivity after the phase
                var conn = new RoadPlanner(draft, _s).CheckConnectivity();
                if (conn.Connectivity + 1e-9 < Math.Min(1.0, startConnectivity))
                    v.Errors.Add("after phase " + p.N + " road connectivity drops to " + Math.Round(conn.Connectivity * 100) + "% (" +
                                 string.Join(", ", conn.Unreachable.Take(6)) + (conn.Unreachable.Count > 6 ? ", …" : "") + ")");
                v.FinalConnectivity = conn.Connectivity;
            }

            if (v.FinalConnectivity < 0.999)
                v.Errors.Add("the final layout must connect 100% of buildings by road (now " + Math.Round(v.FinalConnectivity * 100) + "%); call route_network and add its road_add actions");

            // ---- Native mode: stocks through the plan
            if (string.Equals(_s.Mode, "native", StringComparison.OrdinalIgnoreCase))
            {
                v.Forecast.AddRange(StockForecast.Run(snap, plan.Phases, _foodMonths, _woodMonths));
                foreach (var f in v.Forecast)
                    foreach (var b in f.Breaches) v.Errors.Add("phase " + f.Phase + ": " + b);
            }

            v.After = scorer.Score(draft);
            v.FinalDraft = draft;
            double gain = v.After.Weighted - v.Before.Weighted;
            if (gain < _s.MinGain) v.Warnings.Add("weighted gain is " + Math.Round(gain, 2) + " points (minimum " + _s.MinGain + ")");
            plan.ScoreBefore = v.Before.ObjectivesJson();
            plan.ScoreAfter = v.After.ObjectivesJson();
            return v;
        }
    }
}
