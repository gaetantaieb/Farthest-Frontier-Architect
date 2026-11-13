using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using FrontierArchitect.Core.Claude;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;

namespace FrontierArchitect.Core.Tools
{
    /// <summary>
    /// Tools for the Assistant report: <c>query</c> to read the snapshot and <c>submit_report</c>
    /// for the answer. The mod verifies every proposed action; any that fails is downgraded to
    /// Manual (it is never offered as Apply).
    /// </summary>
    public sealed class AssistantToolHost : IToolHost
    {
        private readonly VillageSnapshot _snap;
        private readonly Func<string, string> _opUnavailableReason;
        private readonly List<ToolSpec> _tools = new List<ToolSpec>();
        private readonly RunSettings _settings;
        private JObject _snapshotJson;
        private int _rejections;

        public AssistantReport Accepted { get; private set; }

        /// <param name="opUnavailableReason">Returns null when the op can be executed in this game version, else why not.</param>
        public AssistantToolHost(VillageSnapshot snapshot, Func<string, string> opUnavailableReason, IDictionary<string, string> descriptionOverrides = null)
        {
            _snap = snapshot;
            _opUnavailableReason = opUnavailableReason ?? (_ => null);
            _settings = new RunSettings { AllowedOps = new HashSet<string>(Ops.AssistantApply), Ambition = 1 };
            foreach (var t in Specs())
            {
                string d;
                if (descriptionOverrides != null && descriptionOverrides.TryGetValue(t.Name, out d) && !string.IsNullOrWhiteSpace(d)) t.Description = d.Trim();
                _tools.Add(t);
            }
        }

        public IList<ToolSpec> Tools => _tools;

        public string MissingSubmitMessage => "You ended without calling submit_report. Call submit_report now with your ranked findings.";

        public static List<ToolSpec> Specs()
        {
            var op = S.Obj(S.Props(
                "op", S.Str("one of " + string.Join(", ", Ops.AssistantApply) + " (spatial ops belong to kind architect)"),
                "target", S.Str("building id, field id, item or profession"),
                "params", S.AnyObj("count | limit | rotation [[y1],[y2],[y3]] | paused | x,z")), "op", "target");
            return new List<ToolSpec>
            {
                new ToolSpec { Name = "query", Description =
                    "Read any part of the village snapshot by path (root: snapshot). Examples: snapshot.metrics.food, " +
                    "snapshot.buildings[category=storage], snapshot.buildings[workers.assigned<1].id, snapshot.fields[*].rotation.",
                    InputSchema = S.Obj(S.Props("path", S.Str("path expression")), "path") },
                new ToolSpec { Name = "submit_report", Description =
                    "Submit the report: findings ranked by urgency. Every finding cites at least one number from the snapshot in evidence. " +
                    "action.kind is apply (only settings ops with no placement decision), architect (anything spatial: new, moved or resized buildings/fields, roads) or manual.",
                    InputSchema = S.Obj(S.Props(
                        "summary", S.Str("one or two sentences on the village's state"),
                        "findings", S.Arr(S.Obj(S.Props(
                            "id", S.Str("f1, f2, ..."),
                            "severity", S.Enum("critical = people die or leave within a year", "critical", "important", "optimisation"),
                            "domain", S.Enum("domain", "population", "food", "winter", "health", "economy", "production", "farming", "storage", "defence", "happiness"),
                            "title", S.Str("short title"),
                            "evidence", S.Arr(S.Str("a fact with its number, e.g. 'firewood 140, burn 60/month, winter in 2 months'"), "evidence"),
                            "recommendation", S.Str("one concrete step"),
                            "action", S.Obj(S.Props("kind", S.Enum("kind", "apply", "architect", "manual"), "ops", S.Arr(op, "ops for apply"),
                                "seed", S.Str("for architect: what the Architect should solve")), "kind")),
                            "id", "severity", "domain", "title", "evidence", "recommendation", "action"), "findings")),
                        "findings") },
            };
        }

        public ToolOutcome Execute(string name, JObject input, CancellationToken cancel)
        {
            var spec = _tools.FirstOrDefault(t => t.Name == name);
            if (spec == null) return ToolOutcome.Error("unknown tool " + name);
            input = ToolValidation.Coerce(spec.InputSchema, input) as JObject ?? input;
            var err = ToolValidation.Validate(spec.InputSchema, input);
            if (err != null) return ToolOutcome.Error(err);
            if (name == "query")
            {
                if (_snapshotJson == null) _snapshotJson = _snap.ToJson();
                object v;
                try { v = JsonPath.Select(new JObject().Set("snapshot", _snapshotJson), input.Str("path")); }
                catch (Exception e) { return ToolOutcome.Error("bad path: " + e.Message); }
                string text = Json.Write(new JObject().Set("path", input.Str("path")).Set("value", v));
                return ToolOutcome.Ok(text.Length > 40000 ? text.Substring(0, 40000) + "… (truncated)" : text);
            }
            return Submit(input);
        }

        public string WrapUpHint(AgentProgress progress) =>
            progress.Turn >= 4 ? "Enough reading: call submit_report now." : null;

        public bool TryAcceptText(string text, out ToolOutcome outcome)
        {
            outcome = null;
            var o = Json.ExtractFirstObject(text);
            if (o == null || o.Arr("findings") == null) return false;
            outcome = Submit(o);
            return true;
        }

        private static readonly Regex HasNumber = new Regex(@"\d");

        private ToolOutcome Submit(JObject input)
        {
            AssistantReport report;
            try { report = AssistantReport.FromJson(input); }
            catch (Exception e) { return ToolOutcome.Error("could not read the report: " + e.Message); }
            var problems = new List<string>();
            if (report.Findings.Count == 0) problems.Add("the report has no findings");
            if (report.Findings.Count > 30) problems.Add("too many findings (max 30); keep the most important");
            int idx = 0;
            foreach (var f in report.Findings)
            {
                idx++;
                if (string.IsNullOrEmpty(f.Id)) f.Id = "f" + idx;
                if (string.IsNullOrWhiteSpace(f.Title)) problems.Add(f.Id + ": missing title");
                if (!f.Evidence.Any(e => HasNumber.IsMatch(e))) problems.Add(f.Id + ": evidence must cite at least one number from the snapshot");
            }
            // One repair round for format problems; after that accept what is valid.
            if (problems.Count > 0 && _rejections < 1)
            {
                _rejections++;
                return new ToolOutcome { IsError = true, Content = Json.Write(new JObject().Set("accepted", false).Set("problems", problems.ToJArray())) };
            }
            report.Findings.RemoveAll(f => !f.Evidence.Any(e => HasNumber.IsMatch(e)));

            var draft = new DraftLayout(_snap);
            foreach (var f in report.Findings)
            {
                if (f.Kind != FindingActionKind.Apply) continue;
                if (f.Ops.Count == 0) { f.Kind = FindingActionKind.Manual; f.ManualReason = "no action was given"; continue; }
                foreach (var a in f.Ops)
                {
                    if (!Ops.AssistantApply.Contains(a.Op))
                    {
                        f.Kind = FindingActionKind.Architect;
                        f.ManualReason = a.Op + " needs a placement decision; send it to Architect";
                        break;
                    }
                    var r = draft.Check(a, _settings);
                    if (!r.Valid) { f.Kind = FindingActionKind.Manual; f.ManualReason = string.Join("; ", r.Reasons); break; }
                    string why = _opUnavailableReason(a.Op);
                    if (why != null) { f.Kind = FindingActionKind.Manual; f.ManualReason = why; break; }
                }
            }
            report.Findings.Sort((a, b) => a.Severity.CompareTo(b.Severity));
            Accepted = report;
            return new ToolOutcome
            {
                Finished = true,
                Payload = report,
                Content = Json.Write(new JObject().Set("accepted", true).Set("findings", report.Findings.Count))
            };
        }
    }
}
