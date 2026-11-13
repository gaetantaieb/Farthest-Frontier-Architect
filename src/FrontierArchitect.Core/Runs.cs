using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Claude;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Tools;

namespace FrontierArchitect.Core
{
    public sealed class RunContext
    {
        public ClaudeClient Client;
        /// <summary>Models available to the key (from /v1/models), or null when unknown.</summary>
        public IList<string> AvailableModels;
        /// <summary>Player override from Settings; empty = use the agent file.</summary>
        public string ModelOverride = "";
        public double FoodMonths = 3, FirewoodMonths = 3;
        /// <summary>Why an op cannot run in this game version (null = available).</summary>
        public Func<string, string> OpUnavailable = _ => null;
        public string ModVersion = "";
        /// <summary>Creates a run log for (agent, settlement, meta); null = no logging.</summary>
        public Func<string, string, JObject, IRunRecorder> NewRecorder;
    }

    public sealed class ArchitectRequest
    {
        public int Ambition = 3;
        public string Mode = "native";
        public List<string> Locked = new List<string>();
        public string PlayerNote = "";
        public string SeedRequest = "";
        /// <summary>Continue this plan: its unfinished actions start on the draft and its phases stay in front.</summary>
        public ArchitectPlan ContinueFrom;
    }

    public sealed class ArchitectOutcome
    {
        public ArchitectPlan Plan;
        public PlanValidation Validation;
        public AgentResult Agent;
        public string Error;
    }

    public sealed class AssistantOutcome
    {
        public AssistantReport Report;
        public AgentResult Agent;
        public string Error;
    }

    /// <summary>Builds prompts from the agent files and runs the Assistant / Architect agents.</summary>
    public static class Runs
    {
        /// <summary>Runs the loop with an optional run log; the log's end line carries the outcome.</summary>
        private static AgentResult RunLogged(RunContext ctx, string agentName, VillageSnapshot snap, JObject meta, AgentRunConfig cfg,
            IToolHost host, Action<AgentProgress> progress, CancellationToken cancel, Func<AgentResult, object> payloadJson)
        {
            IRunRecorder rec = null;
            try { rec = ctx.NewRecorder?.Invoke(agentName, snap.Meta.SettlementName, meta); } catch { }
            cfg.Recorder = rec;
            AgentResult res;
            try { res = new AgentLoop(ctx.Client).Run(cfg, host, progress, cancel); }
            catch (Exception e)
            {
                try { rec?.End(null, null, e.Message); } catch { }
                throw;
            }
            try { rec?.End(res, res.Success ? payloadJson(res) : null, res.Error); } catch { }
            return res;
        }

        private static JObject Meta(LoadedAgent agent, VillageSnapshot snap, RunContext ctx) => new JObject()
            .Set("mod_version", ctx.ModVersion).Set("game_version", snap.Meta.GameVersion)
            .Set("settlement", snap.Meta.SettlementName).Set("year", snap.Meta.Year).Set("season", snap.Meta.Season)
            .Set("profile", agent.Profile).Set("agent_hash", agent.Hash).Set("agent_version", agent.Definition.Version)
            .Set("data_gaps", snap.Gaps.ToJArray());

        public static AgentRunConfig BaseConfig(LoadedAgent agent, RunContext ctx, string staticSystem, string runSystem, string user)
        {
            var d = agent.Definition;
            string configured = string.IsNullOrWhiteSpace(ctx.ModelOverride) ? d.ModelId : ctx.ModelOverride;
            return new AgentRunConfig
            {
                ModelCandidates = ModelResolver.Candidates(configured, ctx.AvailableModels),
                Effort = d.Effort,
                MaxOutputTokens = d.MaxOutputTokens,
                MaxTurns = d.MaxTurns,
                MaxMinutes = d.MaxMinutes,
                StaticSystem = staticSystem,
                RunSystem = runSystem,
                UserMessage = user,
            };
        }

        public static string StaticContext(LoadedAgent agent, VillageSnapshot snap) =>
            snap.Knowledge.ToPromptText() + "\n# Agent knowledge\n\n" + agent.KnowledgeText();

        private static Dictionary<string, string> CommonVars(VillageSnapshot snap, RunContext ctx)
        {
            return new Dictionary<string, string>
            {
                { "season", snap.Meta.Season.Replace('_', ' ') },
                { "year", snap.Meta.Year.ToString(CultureInfo.InvariantCulture) },
                { "town_tier", snap.Meta.TownTier.ToString(CultureInfo.InvariantCulture) },
                { "settlement", snap.Meta.SettlementName },
                { "food_threshold", ctx.FoodMonths.ToString(CultureInfo.InvariantCulture) },
                { "firewood_threshold", ctx.FirewoodMonths.ToString(CultureInfo.InvariantCulture) },
            };
        }

        // ------------------------------------------------------------------ Assistant

        public static AssistantOutcome Assistant(LoadedAgent agent, VillageSnapshot snap, RunContext ctx, Action<AgentProgress> progress, CancellationToken cancel)
        {
            var vars = CommonVars(snap, ctx);
            vars["snapshot_json"] = Json.Write(snap.ToJson(false));
            vars["player_note"] = "";
            var host = new AssistantToolHost(snap, ctx.OpUnavailable, agent.Definition.ToolDescriptions);
            var cfg = BaseConfig(agent, ctx, StaticContext(agent, snap), Template.Render(agent.SystemTemplate, vars), Template.Render(agent.TaskTemplate, vars));
            var res = RunLogged(ctx, "assistant", snap, Meta(agent, snap, ctx), cfg, host, progress, cancel, r => ((AssistantReport)r.Payload).ToJson());
            var outcome = new AssistantOutcome { Agent = res };
            if (!res.Success) { outcome.Error = res.Error; return outcome; }
            outcome.Report = (AssistantReport)res.Payload;
            outcome.Report.Model = res.Model;
            outcome.Report.AgentHash = agent.Hash;
            outcome.Report.CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return outcome;
        }

        // ------------------------------------------------------------------ Architect

        public static RunSettings Settings(LoadedAgent agent, ArchitectRequest req, RunContext ctx)
        {
            var s = agent.Definition.ToRunSettings(req.Ambition, req.Mode, req.Locked);
            // ops the game cannot execute yet stay proposable (Architect is proposal-only); Execute marks them Manual.
            return s;
        }

        public static Dictionary<string, string> ArchitectVars(LoadedAgent agent, VillageSnapshot snap, ArchitectRequest req, RunSettings s, RunContext ctx)
        {
            var d = agent.Definition;
            var vars = CommonVars(snap, ctx);
            vars["ambition"] = req.Ambition.ToString(CultureInfo.InvariantCulture);
            vars["ambition_name"] = d.Ambition.ContainsKey(req.Ambition) ? d.Ambition[req.Ambition].Name : AgentDefinition.AmbitionNames[Math.Max(1, Math.Min(5, req.Ambition))];
            vars["mode"] = req.Mode;
            vars["locked_ids"] = req.Locked.Count == 0 ? "none" : string.Join(", ", req.Locked);
            vars["player_note"] = string.IsNullOrWhiteSpace(req.PlayerNote) ? "none" : req.PlayerNote.Trim();
            vars["seed_request"] = string.IsNullOrWhiteSpace(req.SeedRequest) ? "none" : req.SeedRequest.Trim();
            vars["max_relocations"] = s.MaxRelocations < 0 ? "unlimited" : s.MaxRelocations.ToString(CultureInfo.InvariantCulture);
            vars["offline_cap"] = Math.Round(s.OfflineCap * 100) + "%";
            vars["min_gain"] = s.MinGain.ToString(CultureInfo.InvariantCulture);
            vars["detour_limit"] = s.DetourLimit.ToString(CultureInfo.InvariantCulture);
            vars["phase_max_actions"] = s.PhaseMaxActions.ToString(CultureInfo.InvariantCulture);
            vars["allowed_ops"] = string.Join(", ", Ops.All.Where(s.AllowedOps.Contains));
            vars["weights"] = string.Join(", ", s.Weights.OrderBy(k => k.Key).Select(k => k.Key + "=" + Math.Round(k.Value, 2).ToString(CultureInfo.InvariantCulture)));
            vars["snapshot_json"] = Json.Write(snap.ToJson(true));
            return vars;
        }

        public static ArchitectOutcome Architect(LoadedAgent agent, VillageSnapshot snap, ArchitectRequest req, RunContext ctx, Action<AgentProgress> progress, CancellationToken cancel)
        {
            var d = agent.Definition;
            var s = Settings(agent, req, ctx);
            var vars = ArchitectVars(agent, snap, req, s, ctx);
            var host = new ArchitectToolHost(snap, s, d.EnabledTools.Count > 0 ? d.EnabledTools : null, d.ToolDescriptions,
                ctx.FoodMonths, ctx.FirewoodMonths, d.StopIfGainBelow, d.GainWindowTurns, req.ContinueFrom);
            string user = Template.Render(agent.TaskTemplate, vars);
            if (req.ContinueFrom != null) user = ContinueNote(host, req.ContinueFrom) + "\n\n" + user;
            var cfg = BaseConfig(agent, ctx, StaticContext(agent, snap), Template.Render(agent.SystemTemplate, vars), user);
            AmbitionLevel lv;
            if (d.Ambition.TryGetValue(s.Ambition, out lv))
            {
                if (lv.MaxTurns > 0) cfg.MaxTurns = lv.MaxTurns;
                if (lv.MaxMinutes > 0) cfg.MaxMinutes = lv.MaxMinutes;
            }
            var meta = Meta(agent, snap, ctx).Set("ambition", req.Ambition).Set("mode", req.Mode)
                .Set("continues_plan", req.ContinueFrom != null).Set("max_turns", cfg.MaxTurns).Set("max_minutes", cfg.MaxMinutes)
                .Set("locked", req.Locked.ToJArray()).Set("player_note", req.PlayerNote).Set("seed_request", req.SeedRequest);
            var res = RunLogged(ctx, "architect", snap, meta, cfg, host, progress, cancel, r => PlanJson(r));
            var outcome = new ArchitectOutcome { Agent = res };
            if (!res.Success) { outcome.Error = res.Error; return outcome; }
            var kv = (KeyValuePair<ArchitectPlan, PlanValidation>)res.Payload;
            outcome.Plan = kv.Key;
            outcome.Validation = kv.Value;
            Stamp(outcome.Plan, agent, snap, req, res.Model, ctx);
            return outcome;
        }

        /// <summary>What Claude is told when it continues an earlier plan.</summary>
        private static string ContinueNote(ArchitectToolHost host, ArchitectPlan prior)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("## Continue the previous plan\n");
            sb.Append("Previous scenario: ").Append(prior.Summary).Append('\n');
            sb.Append("Its ").Append(host.PriorActionCount).Append(" unfinished actions (").Append(host.PriorPhaseCount)
              .Append(" phases) are already applied to your draft; score_draft starts from there. Do not repeat them, and do not undo them.\n");
            if (prior.Zones.Count > 0)
                sb.Append("Its zones (already set; change them with set_zones if needed): ").Append(Json.Write(new JArray(prior.Zones.Select(z => (object)z.ToJson())))).Append('\n');
            var touched = prior.AllActions().Where(a => a.Target != null && (a.Op == Ops.Relocate || a.Op == Ops.Demolish)).Select(a => a.Target).Distinct().Take(80).ToList();
            if (touched.Count > 0) sb.Append("Buildings it already moves or removes: ").Append(string.Join(", ", touched)).Append('\n');
            foreach (var n in host.PriorNotes.Take(10)) sb.Append("- note: ").Append(n).Append('\n');
            sb.Append("Work on the districts it did not reach. Submit only your new phases (or auto_phases: true); the mod keeps the previous phases in front of them.");
            return sb.ToString();
        }

        private static object PlanJson(AgentResult r)
        {
            var kv = (KeyValuePair<ArchitectPlan, PlanValidation>)r.Payload;
            return new JObject().Set("plan", kv.Key.ToJson()).Set("validation", kv.Value.ToJson());
        }

        public static void Stamp(ArchitectPlan plan, LoadedAgent agent, VillageSnapshot snap, ArchitectRequest req, string model, RunContext ctx)
        {
            plan.Profile = agent.Profile;
            plan.AgentHash = agent.Hash;
            plan.AgentVersion = agent.Definition.Version;
            plan.Model = model;
            plan.Mode = req.Mode;
            plan.Ambition = req.Ambition;
            plan.CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            plan.GameVersion = snap.Meta.GameVersion;
            plan.LockedIds = new List<string>(req.Locked);
        }

        /// <summary>
        /// Re-checks a displayed plan after the player rejected actions or changed locks: the plan
        /// is replayed without the rejected actions and dependants that break are reported.
        /// </summary>
        public static PlanValidation Recheck(ArchitectPlan plan, VillageSnapshot snap, LoadedAgent agent, ArchitectRequest req, RunContext ctx)
        {
            var s = Settings(agent, req, ctx);
            var copy = ArchitectPlan.FromJson(plan.ToJson());
            foreach (var p in copy.Phases) p.Actions.RemoveAll(a => a.Status == ActionStatus.Rejected);
            var v = new PlanValidator(s, ctx.FoodMonths, ctx.FirewoodMonths).Validate(copy, snap, false);
            // carry per-action errors back onto the displayed plan
            foreach (var a in plan.AllActions())
            {
                if (a.Status == ActionStatus.Rejected) continue;
                List<string> errs;
                a.StatusNote = v.ActionErrors.TryGetValue(a.Id, out errs) ? "breaks: " + string.Join("; ", errs) : "";
            }
            plan.ScoreAfter = v.After?.ObjectivesJson() ?? plan.ScoreAfter;
            return v;
        }

        /// <summary>
        /// Asks Claude to repair the remaining phases (from <paramref name="fromIndex"/>) against the
        /// live village, after a phase failed re-validation. Completed phases are untouched.
        /// </summary>
        public static ArchitectOutcome RepairFrom(LoadedAgent agent, VillageSnapshot liveSnap, ArchitectPlan plan, int fromIndex, IList<string> problems,
            ArchitectRequest req, RunContext ctx, Action<AgentProgress> progress, CancellationToken cancel)
        {
            var d = agent.Definition;
            var s = Settings(agent, req, ctx);
            var vars = ArchitectVars(agent, liveSnap, req, s, ctx);
            var remaining = new ArchitectPlan { Ambition = plan.Ambition, Summary = plan.Summary };
            for (int i = fromIndex; i < plan.Phases.Count; i++) remaining.Phases.Add(Phase.FromJson(plan.Phases[i].ToJson()));
            string user =
                "Repair this plan. Phases before " + (fromIndex + 1) + " are already done and are reflected in the snapshot below.\n" +
                "Phase " + (fromIndex + 1) + " failed re-validation against the live village:\n- " + string.Join("\n- ", problems) + "\n\n" +
                "Change only what is needed, keep the rest. Submit the remaining phases with submit_plan (renumber them from 1).\n\n" +
                "Remaining phases:\n" + Json.Write(remaining.ToJson()) + "\n\nSnapshot follows.\n" + vars["snapshot_json"];
            var host = new ArchitectToolHost(liveSnap, s, d.EnabledTools.Count > 0 ? d.EnabledTools : null, d.ToolDescriptions,
                ctx.FoodMonths, ctx.FirewoodMonths, d.StopIfGainBelow, d.GainWindowTurns);
            var cfg = BaseConfig(agent, ctx, StaticContext(agent, liveSnap), Template.Render(agent.SystemTemplate, vars), user);
            cfg.MaxTurns = Math.Min(cfg.MaxTurns, 40);
            var meta = Meta(agent, liveSnap, ctx).Set("repair_from_phase", fromIndex + 1).Set("problems", problems.ToJArray());
            var res = RunLogged(ctx, "architect-repair", liveSnap, meta, cfg, host, progress, cancel, r => PlanJson(r));
            var outcome = new ArchitectOutcome { Agent = res };
            if (!res.Success) { outcome.Error = res.Error; return outcome; }
            var kv = (KeyValuePair<ArchitectPlan, PlanValidation>)res.Payload;
            var repaired = kv.Key;
            var merged = ArchitectPlan.FromJson(plan.ToJson());
            merged.Phases.RemoveRange(fromIndex, merged.Phases.Count - fromIndex);
            foreach (var p in repaired.Phases)
            {
                p.N = merged.Phases.Count + 1;
                p.Status = PhaseStatus.NotStarted;
                merged.Phases.Add(p);
            }
            merged.ScoreAfter = repaired.ScoreAfter;
            merged.Warnings.Add("phases " + (fromIndex + 1) + "+ repaired on " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
            outcome.Plan = merged;
            outcome.Validation = kv.Value;
            return outcome;
        }
    }
}
