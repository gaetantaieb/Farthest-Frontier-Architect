using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Planning;

namespace FrontierArchitect.Core.Agents
{
    public sealed class AmbitionLevel
    {
        public int Level;
        public string Name = "";
        public int MaxRelocations = -1;
        /// <summary>Loop limits for this level (0 = the loop section's values); bigger redesigns need more turns.</summary>
        public int MaxTurns;
        public double MaxMinutes;
        public HashSet<string> Allow = new HashSet<string>();
        public HashSet<string> Movable = new HashSet<string>();
    }

    /// <summary>Typed view of agent.yaml (+ the active profile's overrides), schema-checked on load.</summary>
    public sealed class AgentDefinition
    {
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };
        public static readonly string[] ObjectiveKeys = { "O1", "O2", "O3", "O4", "O5", "O6" };
        public static readonly string[] AmbitionNames = { "", "Touch-up", "Tidy", "Rework", "Redistrict", "Rebuild" };

        public string Name = "architect";
        public string Version = "0.0.0";
        public string ModelId = "most-capable";
        public string Effort = "high";
        public int MaxOutputTokens = 64000;
        public int MaxTurns = 120;
        public double MaxMinutes = 10;
        public double StopIfGainBelow = 0.5;
        public int GainWindowTurns = 10;
        public Dictionary<string, double> Weights = new Dictionary<string, double>();
        public bool ConnectivityRequired = true;
        public double DetourLimit = 1.3;
        public List<string> KeyTrips = new List<string>();
        public bool ReuseExistingSegments = true;
        public double RoadTileWeight = 100;
        public Dictionary<int, AmbitionLevel> Ambition = new Dictionary<int, AmbitionLevel>();
        public bool ReserveNextTier = true;
        public double OfflineCap = 0.5;
        public double MinGain = 2;
        public int PhaseMaxActions = 12;
        public double MaxSlopeDeg = 12;
        public double RelocationCostFactor = 0.5;
        public bool BuildReplacementFirst = true;
        public List<string> EnabledTools = new List<string>();
        public Dictionary<string, string> ToolDescriptions = new Dictionary<string, string>();
        public HashSet<string> DisabledOps = new HashSet<string>();
        public string OutputSchema = "architect_plan_v1";
        public string Rationale = "short_line";
        public string Language = "en";
        public JObject Raw = new JObject();

        public static AgentDefinition FromYaml(JObject o, List<string> errors, List<string> warnings)
        {
            var d = new AgentDefinition { Raw = o };
            var known = new[] { "agent", "version", "model", "loop", "objectives", "roads", "ambition_levels", "planning", "tools", "output", "ops" };
            foreach (var k in o.Keys) if (!known.Contains(k)) warnings.Add("agent.yaml: unknown key '" + k + "' is ignored");

            d.Name = o.Str("agent", "architect");
            d.Version = o.Str("version", "0.0.0");

            var model = o.Obj("model") ?? new JObject();
            d.ModelId = model.Str("id", "most-capable");
            d.Effort = (model.Str("effort", "high") ?? "high").ToLowerInvariant();
            if (!Efforts.Contains(d.Effort)) errors.Add("model.effort must be one of " + string.Join(", ", Efforts));
            d.MaxOutputTokens = model.Int("max_output_tokens", 64000);
            if (d.MaxOutputTokens < 1024 || d.MaxOutputTokens > 128000) errors.Add("model.max_output_tokens must be 1024..128000");
            if (model.ContainsKey("thinking_budget_tokens"))
                warnings.Add("model.thinking_budget_tokens is ignored: current Claude models use adaptive thinking; set model.effort instead");

            var loop = o.Obj("loop") ?? new JObject();
            d.MaxTurns = loop.Int("max_turns", 120);
            d.MaxMinutes = loop.Num("max_minutes", 10);
            d.StopIfGainBelow = loop.Num("stop_if_gain_below", 0.5);
            d.GainWindowTurns = loop.Int("gain_window_turns", 10);
            if (d.MaxTurns < 1 || d.MaxTurns > 500) errors.Add("loop.max_turns must be 1..500");
            if (d.MaxMinutes <= 0 || d.MaxMinutes > 120) errors.Add("loop.max_minutes must be > 0 and <= 120");

            var obj = o.Obj("objectives") ?? new JObject();
            foreach (var kv in obj)
            {
                var m = Regex.Match(kv.Key, "^(O[1-6])");
                if (!m.Success) { warnings.Add("objectives: '" + kv.Key + "' is not O1..O6"); continue; }
                if (!Json.IsNumber(kv.Value) || Json.ToDouble(kv.Value) < 0) { errors.Add("objectives." + kv.Key + " must be a number >= 0"); continue; }
                d.Weights[m.Groups[1].Value] = Json.ToDouble(kv.Value);
            }
            foreach (var key in ObjectiveKeys) if (!d.Weights.ContainsKey(key)) d.Weights[key] = 1.0;
            double sum = d.Weights.Values.Sum();
            if (sum <= 0) errors.Add("objectives: at least one weight must be > 0");
            else foreach (var key in ObjectiveKeys) d.Weights[key] = d.Weights[key] / sum * ObjectiveKeys.Length; // normalised at load

            var roads = o.Obj("roads") ?? new JObject();
            d.ConnectivityRequired = (roads.Str("connectivity", "required") ?? "required") == "required";
            d.DetourLimit = roads.Num("detour_limit", 1.3);
            if (d.DetourLimit < 1.0 || d.DetourLimit > 5) errors.Add("roads.detour_limit must be between 1.0 and 5");
            d.KeyTrips = roads.Arr("key_trips")?.Strings() ?? new List<string> { "house_market", "house_well", "producer_storage", "storage_market", "field_barn" };
            d.ReuseExistingSegments = roads.Bool("reuse_existing_segments", true);
            d.RoadTileWeight = roads.Num("tile_weight", 100);

            var amb = o.Obj("ambition_levels") ?? new JObject();
            var prev = new HashSet<string>();
            bool needsLevels = d.Name == "architect";
            for (int level = 1; level <= 5; level++)
            {
                var lv = amb.Obj(level.ToString());
                if (lv == null)
                {
                    if (needsLevels) errors.Add("ambition_levels." + level + " is missing");
                    d.Ambition[level] = new AmbitionLevel { Level = level, Name = AmbitionNames[level], Allow = new HashSet<string>(Ops.All) };
                    continue;
                }
                var al = new AmbitionLevel { Level = level, Name = lv.Str("name", AmbitionNames[level]) };
                object mr = lv["max_relocations"];
                al.MaxRelocations = mr is string s && s == "all" ? -1 : (int)Json.ToLong(mr, -1);
                al.MaxTurns = lv.Int("max_turns", 0);
                al.MaxMinutes = lv.Num("max_minutes", 0);
                if (al.MaxTurns < 0 || al.MaxTurns > 500) errors.Add("ambition_levels." + level + ".max_turns must be 0..500");
                if (al.MaxMinutes < 0 || al.MaxMinutes > 120) errors.Add("ambition_levels." + level + ".max_minutes must be 0..120");
                var allow = new HashSet<string>(prev);
                foreach (var item in lv.Arr("allow").Strings())
                {
                    if (item == "all") { foreach (var op in Ops.All) allow.Add(op); continue; }
                    string op2 = item.TrimStart('+');
                    if (!Ops.IsKnown(op2)) { errors.Add("ambition_levels." + level + ".allow: unknown op '" + op2 + "'"); continue; }
                    allow.Add(op2); // "+op" adds to the previous level; a bare op is listed explicitly
                }
                al.Allow = allow;
                al.Movable = new HashSet<string>(lv.Arr("movable").Strings().Select(m => m.TrimEnd('s') == "wall" ? "wall" : m.TrimEnd('s') == "gate" ? "gate" : m));
                prev = allow;
                d.Ambition[level] = al;
            }

            var planning = o.Obj("planning") ?? new JObject();
            d.ReserveNextTier = planning.Bool("reserve_next_tier", true);
            d.OfflineCap = planning.Num("offline_cap", 0.5);
            if (d.OfflineCap <= 0 || d.OfflineCap > 1) errors.Add("planning.offline_cap must be in (0, 1]");
            d.MinGain = planning.Num("min_gain", 2);
            d.PhaseMaxActions = planning.Int("phase_max_actions", 12);
            if (d.PhaseMaxActions < 1 || d.PhaseMaxActions > 100) errors.Add("planning.phase_max_actions must be 1..100");
            d.MaxSlopeDeg = planning.Num("max_slope_deg", 12);
            d.RelocationCostFactor = planning.Num("relocation_cost_factor", 0.5);
            d.BuildReplacementFirst = planning.Bool("build_replacement_first", true);

            var tools = o.Obj("tools") ?? new JObject();
            d.EnabledTools = tools.Arr("enabled")?.Strings() ?? new List<string>();
            foreach (var kv in tools.Obj("descriptions") ?? new JObject())
                if (kv.Value is string desc) d.ToolDescriptions[kv.Key] = desc;

            var ops = o.Obj("ops") ?? new JObject();
            foreach (var op in ops.Arr("disabled").Strings())
            {
                if (!Ops.IsKnown(op)) errors.Add("ops.disabled: unknown op '" + op + "'");
                else d.DisabledOps.Add(op);
            }

            var output = o.Obj("output") ?? new JObject();
            d.OutputSchema = output.Str("schema", d.Name == "assistant" ? "assistant_report_v1" : "architect_plan_v1");
            d.Rationale = output.Str("rationale", "short_line");
            d.Language = output.Str("language", "en");
            return d;
        }

        public RunSettings ToRunSettings(int ambition, string mode, IEnumerable<string> locked)
        {
            ambition = Math.Max(1, Math.Min(5, ambition));
            AmbitionLevel lv;
            if (!Ambition.TryGetValue(ambition, out lv)) lv = new AmbitionLevel { Level = ambition, Allow = new HashSet<string>(Ops.All) };
            var allowed = new HashSet<string>(lv.Allow);
            allowed.ExceptWith(DisabledOps);
            return new RunSettings
            {
                Ambition = ambition,
                Mode = mode,
                LockedIds = new HashSet<string>(locked ?? Enumerable.Empty<string>()),
                AllowedOps = allowed,
                MaxRelocations = lv.MaxRelocations,
                Movable = new HashSet<string>(lv.Movable),
                MaxSlopeDeg = (float)MaxSlopeDeg,
                ReserveNextTier = ReserveNextTier,
                OfflineCap = OfflineCap,
                MinGain = MinGain,
                PhaseMaxActions = PhaseMaxActions,
                DetourLimit = DetourLimit,
                ConnectivityRequired = ConnectivityRequired,
                KeyTrips = new List<string>(KeyTrips),
                ReuseExistingRoads = ReuseExistingSegments,
                RoadTileWeight = RoadTileWeight,
                RelocationCostFactor = RelocationCostFactor,
                BuildReplacementFirst = BuildReplacementFirst,
                Weights = new Dictionary<string, double>(Weights),
            };
        }
    }
}
