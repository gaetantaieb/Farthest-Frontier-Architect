using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Claude
{
    /// <summary>Receives every step of an agent run, for later study.</summary>
    public interface IRunRecorder
    {
        void Start(AgentRunConfig cfg, IList<ToolSpec> tools);
        void Turn(int turn, MessageResult msg, double seconds);
        void Tool(int turn, string name, JObject input, ToolOutcome outcome, double seconds);
        void Note(int turn, string kind, string text);
        void End(AgentResult result, object payload, string error);
    }

    /// <summary>
    /// One JSON-lines file per run in UserData/FrontierArchitect/runs/: run settings, the prompts
    /// (system, task with the snapshot), then per turn Claude's reasoning summary, text, tool calls
    /// and the mod's answers, then the outcome. Written line by line so an interrupted run still
    /// leaves a readable log. Nothing in it comes from HTTP headers, so the API key never appears.
    /// The API returns a summary of Claude's reasoning, not the raw chain of thought; that summary
    /// is what the log keeps.
    /// </summary>
    public sealed class RunLog : IRunRecorder
    {
        public string PathOnDisk { get; }
        private readonly string _agent;
        private readonly JObject _meta;
        private readonly object _lock = new object();
        private DateTime _started;

        public RunLog(string dir, string agent, string settlement, JObject meta)
        {
            Directory.CreateDirectory(dir);
            _agent = agent;
            _meta = meta ?? new JObject();
            string safe = Regex.Replace(settlement ?? "village", "[^A-Za-z0-9_-]", "_");
            if (safe.Length == 0) safe = "village";
            PathOnDisk = System.IO.Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + agent + "_" + safe + ".jsonl");
        }

        private void Write(JObject o)
        {
            o.Set("t", Math.Round((DateTime.UtcNow - _started).TotalSeconds, 2));
            lock (_lock)
            {
                try { File.AppendAllText(PathOnDisk, Json.Write(o) + "\n", Encoding.UTF8); } catch { }
            }
        }

        public void Start(AgentRunConfig cfg, IList<ToolSpec> tools)
        {
            _started = DateTime.UtcNow;
            Write(new JObject().Set("event", "start").Set("agent", _agent)
                .Set("started_utc", _started.ToString("o", CultureInfo.InvariantCulture))
                .Set("meta", _meta)
                .Set("models", cfg.ModelCandidates.ToJArray()).Set("effort", cfg.Effort)
                .Set("max_turns", cfg.MaxTurns).Set("max_minutes", cfg.MaxMinutes)
                .Set("tools", tools.Select(t => t.Name).ToJArray())
                .Set("system_static", cfg.StaticSystem).Set("system_run", cfg.RunSystem)
                .Set("task", cfg.UserMessage));
        }

        public void Turn(int turn, MessageResult msg, double seconds)
        {
            var blocks = new JArray();
            foreach (var b in msg.Content.Objects())
            {
                string type = b.Str("type");
                var o = new JObject().Set("type", type);
                switch (type)
                {
                    case "thinking": o.Set("summary", b.Str("thinking", "")); break; // signature omitted
                    case "redacted_thinking": break;
                    case "text": o.Set("text", b.Str("text", "")); break;
                    case "tool_use": o.Set("id", b.Str("id")).Set("name", b.Str("name")).Set("input", b.Obj("input")); break;
                    default: o.Set("block", b); break;
                }
                blocks.Add(o);
            }
            Write(new JObject().Set("event", "turn").Set("turn", turn).Set("model", msg.Model)
                .Set("stop_reason", msg.StopReason).Set("seconds", Math.Round(seconds, 1))
                .Set("usage", msg.Usage).Set("content", blocks));
        }

        public void Tool(int turn, string name, JObject input, ToolOutcome outcome, double seconds)
        {
            object result = outcome.Content;
            object parsed; string err;
            if (outcome.Content != null && outcome.Content.Length < 200000 && Json.TryParse(outcome.Content, out parsed, out err)) result = parsed;
            Write(new JObject().Set("event", "tool").Set("turn", turn).Set("name", name).Set("input", input)
                .Set("is_error", outcome.IsError).Set("finished", outcome.Finished)
                .Set("ms", Math.Round(seconds * 1000)).Set("result", result));
        }

        public void Note(int turn, string kind, string text) =>
            Write(new JObject().Set("event", "note").Set("turn", turn).Set("kind", kind).Set("text", text));

        public void End(AgentResult result, object payload, string error)
        {
            var p = result?.Progress;
            Write(new JObject().Set("event", "end").Set("success", result != null && result.Success && error == null)
                .Set("error", error ?? result?.Error).Set("turns", result?.Turns ?? 0).Set("model", result?.Model)
                .Set("seconds", Math.Round((DateTime.UtcNow - _started).TotalSeconds, 1))
                .Set("usage", new JObject().Set("input_tokens", p?.InputTokens ?? 0).Set("output_tokens", p?.OutputTokens ?? 0)
                    .Set("cache_read_tokens", p?.CacheReadTokens ?? 0).Set("cache_write_tokens", p?.CacheWriteTokens ?? 0))
                .Set("payload", payload));
        }

        /// <summary>Keeps the newest <paramref name="keep"/> logs.</summary>
        public static void Prune(string dir, int keep)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*.jsonl").OrderByDescending(x => x, StringComparer.Ordinal).Skip(Math.Max(1, keep)))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    /// <summary>A parsed run log, for the in-game viewer.</summary>
    public sealed class RunLogView
    {
        public string Path;
        public JObject Start;
        public JObject End;
        public readonly List<JObject> Events = new List<JObject>();

        public string Agent => Start?.Str("agent") ?? "?";
        public string When => Start?.Str("started_utc") ?? "";
        public bool Finished => End != null;

        public static RunLogView Read(string path, bool full)
        {
            var v = new RunLogView { Path = path };
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.Trim().Length == 0) continue;
                    JObject o;
                    try { o = Json.ParseObject(line); } catch { continue; }
                    string e = o.Str("event");
                    if (e == "start") v.Start = o;
                    else if (e == "end") v.End = o;
                    else if (full) v.Events.Add(o);
                }
            }
            catch { }
            return v;
        }

        public static List<RunLogView> List(string dir, int max = 50)
        {
            var list = new List<RunLogView>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl").OrderByDescending(x => x, StringComparer.Ordinal).Take(max))
                list.Add(Read(f, false));
            return list;
        }
    }
}
