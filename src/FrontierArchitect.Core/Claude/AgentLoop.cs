using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Claude
{
    public sealed class ToolSpec
    {
        public string Name;
        public string Description;
        public JObject InputSchema;

        public JObject ToApi(bool eagerStreaming)
        {
            var o = new JObject().Set("name", Name).Set("description", Description).Set("input_schema", InputSchema);
            // Streaming requests with client tools: stream tool input as it is generated. The loop
            // then validates every input itself (see ToolValidation) before running anything.
            if (eagerStreaming) o.Set("eager_input_streaming", true);
            return o;
        }
    }

    public sealed class ToolOutcome
    {
        public string Content;
        public bool IsError;
        /// <summary>True when this call delivered the final answer (submit_plan / submit_report accepted).</summary>
        public bool Finished;
        public object Payload;

        public static ToolOutcome Ok(object json) => new ToolOutcome { Content = json is string s ? s : Json.Write(json) };
        public static ToolOutcome Error(string message) => new ToolOutcome { Content = Json.Write(new JObject().Set("error", message)), IsError = true };
    }

    /// <summary>What the loop needs from the side that owns the tools (Assistant or Architect).</summary>
    public interface IToolHost
    {
        IList<ToolSpec> Tools { get; }
        ToolOutcome Execute(string name, JObject input, CancellationToken cancel);
        /// <summary>Called after each turn; return a short instruction to append (e.g. "gains have stalled, phase and submit now"), or null.</summary>
        string WrapUpHint(AgentProgress progress);
        /// <summary>The model ended its turn without the submit tool. Try to accept a JSON answer written as text.</summary>
        bool TryAcceptText(string text, out ToolOutcome outcome);
        string MissingSubmitMessage { get; }
    }

    public sealed class AgentRunConfig
    {
        public List<string> ModelCandidates = new List<string> { ModelResolver.SafeDefault };
        public int MaxOutputTokens = 64000;
        public string Effort = "high";
        public int MaxTurns = 60;
        public double MaxMinutes = 10;
        /// <summary>Stable system text (knowledge pack). Gets the explicit cache breakpoint.</summary>
        public string StaticSystem = "";
        /// <summary>Per-run system text (rendered system.md).</summary>
        public string RunSystem = "";
        public string UserMessage = "";
        public List<string> Betas = new List<string>();
        /// <summary>Optional: records every turn and tool call (run log).</summary>
        public IRunRecorder Recorder;
    }

    public sealed class AgentProgress
    {
        public int Turn;
        public int MaxTurns;
        public TimeSpan Elapsed;
        public double MaxMinutes;
        public string Model;
        public string Status = "";
        public string LastThinking = "";
        public string LastTool = "";
        public long InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens;

        public string Line =>
            "turn " + Turn + "/" + MaxTurns + " · " + (int)Elapsed.TotalMinutes + "m" + Elapsed.Seconds.ToString("00") + "s" +
            (string.IsNullOrEmpty(LastTool) ? "" : " · " + LastTool) +
            (string.IsNullOrEmpty(Status) ? "" : " · " + Status);
    }

    public sealed class AgentResult
    {
        public bool Success;
        public object Payload;
        public string Error;
        public string Model;
        public int Turns;
        public AgentProgress Progress;
        public string FinalText = "";
    }

    /// <summary>
    /// Manual tool-use loop over the streaming Messages API. The conversation is append-only
    /// (assistant content is echoed back unchanged), which keeps both prompt caching and the
    /// thinking-block history check intact.
    /// </summary>
    public sealed class AgentLoop
    {
        private readonly ClaudeClient _client;

        public AgentLoop(ClaudeClient client) { _client = client; }

        public AgentResult Run(AgentRunConfig cfg, IToolHost host, Action<AgentProgress> onProgress, CancellationToken cancel)
        {
            var progress = new AgentProgress { MaxTurns = cfg.MaxTurns, MaxMinutes = cfg.MaxMinutes };
            var result = new AgentResult { Progress = progress };
            var sw = Stopwatch.StartNew();
            var messages = new JArray();
            messages.Add(new JObject().Set("role", "user").Set("content", new JArray
            {
                new JObject().Set("type", "text").Set("text", cfg.UserMessage)
            }));

            var tools = new JArray();
            foreach (var t in host.Tools) tools.Add(t.ToApi(true));
            var rec = cfg.Recorder;
            try { rec?.Start(cfg, host.Tools); } catch { rec = null; }

            int modelIndex = 0;
            string model = cfg.ModelCandidates[0];
            int maxTokens = cfg.MaxOutputTokens;
            bool repairSent = false;
            bool wrapUpSent = false;
            int turn = 0;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (turn >= cfg.MaxTurns + 2)
                {
                    result.Error = "Stopped after " + cfg.MaxTurns + " turns without a final answer.";
                    break;
                }
                if (sw.Elapsed.TotalMinutes > cfg.MaxMinutes * 1.25)
                {
                    result.Error = "Stopped after " + cfg.MaxMinutes + " minutes without a final answer.";
                    break;
                }
                turn++;
                progress.Turn = turn;
                progress.Model = model;
                progress.Elapsed = sw.Elapsed;
                progress.Status = "thinking";
                onProgress?.Invoke(progress);

                var body = BuildBody(cfg, model, maxTokens, tools, messages);
                var betas = new List<string>(cfg.Betas);
                if (ModelResolver.SupportsServerFallback(model) && !betas.Contains("server-side-fallback-2026-07-01"))
                    betas.Add("server-side-fallback-2026-07-01");

                MessageResult msg;
                var turnClock = Stopwatch.StartNew();
                try
                {
                    msg = _client.Stream(body, betas, (type, ev) => OnStreamEvent(type, ev, progress, onProgress), cancel);
                }
                catch (ClaudeException e) when (turn == 1 && modelIndex + 1 < cfg.ModelCandidates.Count &&
                                                (e.Kind == ClaudeErrorKind.NotFound || e.Kind == ClaudeErrorKind.Permission ||
                                                 (e.Kind == ClaudeErrorKind.InvalidRequest && e.Message.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    // e.g. a model this organisation may not use: fall back to the next candidate.
                    modelIndex++;
                    Safe(() => rec?.Note(turn, "model_fallback", cfg.ModelCandidates[modelIndex - 1] + " unavailable (" + e.Message + "); trying " + cfg.ModelCandidates[modelIndex]));
                    model = cfg.ModelCandidates[modelIndex];
                    turn = 0;
                    continue;
                }
                AddUsage(progress, msg.Usage);
                Safe(() => rec?.Turn(turn, msg, turnClock.Elapsed.TotalSeconds));
                result.Model = msg.Model ?? model;

                if (msg.StopReason == "refusal")
                {
                    string cat = msg.StopDetails?.Str("category");
                    result.Error = "Claude declined this request" + (cat != null ? " (" + cat + ")" : "") + ".";
                    break;
                }
                if (msg.StopReason == "max_tokens" && maxTokens < 128000)
                {
                    // Output was cut off (a tool input may be truncated): re-issue the same turn with more room.
                    maxTokens = Math.Min(128000, maxTokens * 2);
                    Safe(() => rec?.Note(turn, "max_tokens", "output cut off; retrying the turn with max_tokens " + maxTokens));
                    turn--;
                    continue;
                }

                var echo = ClaudeClient.EchoContent(msg.Content);
                messages.Add(new JObject().Set("role", "assistant").Set("content", echo));
                result.FinalText = msg.Text;

                var toolUses = new List<JObject>();
                foreach (var b in echo.Objects()) if (b.Str("type") == "tool_use") toolUses.Add(b);

                if (msg.StopReason == "pause_turn")
                {
                    turn--;
                    continue;
                }

                if (toolUses.Count == 0)
                {
                    ToolOutcome textOutcome;
                    if (host.TryAcceptText(msg.Text, out textOutcome) && textOutcome.Finished)
                    {
                        result.Success = true;
                        result.Payload = textOutcome.Payload;
                        break;
                    }
                    if (repairSent)
                    {
                        result.Error = "Claude finished without submitting a valid answer." +
                                       (textOutcome != null && textOutcome.IsError ? " Last problem: " + textOutcome.Content : "");
                        break;
                    }
                    repairSent = true;
                    string repair = host.MissingSubmitMessage + (textOutcome != null && textOutcome.IsError ? "\nProblems found: " + textOutcome.Content : "");
                    Safe(() => rec?.Note(turn, "repair_prompt", repair));
                    messages.Add(new JObject().Set("role", "user").Set("content", new JArray { new JObject().Set("type", "text").Set("text", repair) }));
                    continue;
                }

                var results = new JArray();
                bool finished = false;
                foreach (var tu in toolUses)
                {
                    cancel.ThrowIfCancellationRequested();
                    string id = tu.Str("id");
                    string name = tu.Str("name");
                    progress.LastTool = name;
                    progress.Status = "running " + name;
                    progress.Elapsed = sw.Elapsed;
                    onProgress?.Invoke(progress);
                    ToolOutcome outcome;
                    string raw;
                    var toolClock = Stopwatch.StartNew();
                    if (msg.StopReason == "max_tokens")
                        outcome = ToolOutcome.Error("your output was cut off at max_tokens before this call finished; send a shorter call");
                    else if (msg.InvalidToolInputs.TryGetValue(id, out raw))
                        outcome = new ToolOutcome { IsError = true, Content = Json.Write(new JObject().Set("INVALID_JSON", raw)) };
                    else
                    {
                        try { outcome = host.Execute(name, tu.Obj("input") ?? new JObject(), cancel); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { outcome = ToolOutcome.Error(name + " failed: " + e.Message); }
                    }
                    var tr = new JObject().Set("type", "tool_result").Set("tool_use_id", id).Set("content", outcome.Content ?? "");
                    if (outcome.IsError) tr.Set("is_error", true);
                    results.Add(tr);
                    var o2 = outcome;
                    Safe(() => rec?.Tool(turn, name, tu.Obj("input"), o2, toolClock.Elapsed.TotalSeconds));
                    if (outcome.Finished && !outcome.IsError)
                    {
                        finished = true;
                        result.Payload = outcome.Payload;
                    }
                }
                if (finished)
                {
                    result.Success = true;
                    break;
                }

                // All tool results go back in ONE user message; a wrap-up note may follow them.
                progress.Elapsed = sw.Elapsed;
                string hint = host.WrapUpHint(progress);
                bool limitsNear = turn >= cfg.MaxTurns - 2 || sw.Elapsed.TotalMinutes >= cfg.MaxMinutes * 0.85;
                if (limitsNear && !wrapUpSent)
                {
                    hint = (hint == null ? "" : hint + "\n") +
                           "Turn or time limit is close (" + turn + "/" + cfg.MaxTurns + " turns, " +
                           (int)sw.Elapsed.TotalMinutes + "/" + cfg.MaxMinutes + " min). Finish now: phase what you have and submit it.";
                    wrapUpSent = true;
                }
                if (!string.IsNullOrEmpty(hint))
                {
                    results.Add(new JObject().Set("type", "text").Set("text", hint));
                    Safe(() => rec?.Note(turn, "wrap_up_hint", hint));
                }
                messages.Add(new JObject().Set("role", "user").Set("content", results));
            }

            result.Turns = turn;
            progress.Elapsed = sw.Elapsed;
            progress.Status = result.Success ? "done" : "stopped";
            onProgress?.Invoke(progress);
            return result;
        }

        private static void Safe(Action a) { try { a(); } catch { } }

        internal static JObject BuildBody(AgentRunConfig cfg, string model, int maxTokens, JArray tools, JArray messages)
        {
            var system = new JArray();
            if (!string.IsNullOrEmpty(cfg.StaticSystem))
                system.Add(new JObject().Set("type", "text").Set("text", cfg.StaticSystem)
                    .Set("cache_control", new JObject().Set("type", "ephemeral")));
            if (!string.IsNullOrEmpty(cfg.RunSystem))
                system.Add(new JObject().Set("type", "text").Set("text", cfg.RunSystem));

            var body = new JObject()
                .Set("model", model)
                .Set("max_tokens", maxTokens)
                // Automatic caching for the growing conversation tail.
                .Set("cache_control", new JObject().Set("type", "ephemeral"))
                .Set("thinking", new JObject().Set("type", "adaptive").Set("display", "summarized"))
                .Set("output_config", new JObject().Set("effort", string.IsNullOrEmpty(cfg.Effort) ? "high" : cfg.Effort))
                .Set("system", system)
                .Set("messages", messages);
            if (tools.Count > 0) body.Set("tools", tools).Set("tool_choice", new JObject().Set("type", "auto"));
            if (ModelResolver.SupportsServerFallback(model)) body.Set("fallbacks", "default");
            return body;
        }

        private static void OnStreamEvent(string type, JObject ev, AgentProgress p, Action<AgentProgress> onProgress)
        {
            if (type == "content_block_delta")
            {
                var d = ev.Obj("delta");
                string dt = d?.Str("type");
                if (dt == "thinking_delta")
                {
                    p.LastThinking = Tail(p.LastThinking + d.Str("thinking", ""), 400);
                    p.Status = "thinking";
                }
                else if (dt == "text_delta") p.Status = "writing";
                else if (dt == "input_json_delta") p.Status = "preparing tool call";
            }
            else if (type == "content_block_start")
            {
                var b = ev.Obj("content_block");
                if (b?.Str("type") == "tool_use") { p.LastTool = b.Str("name"); p.Status = "calling " + b.Str("name"); }
            }
            else return;
            onProgress?.Invoke(p);
        }

        private static string Tail(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);

        private static void AddUsage(AgentProgress p, JObject u)
        {
            if (u == null) return;
            p.InputTokens += u.Long("input_tokens");
            p.OutputTokens += u.Long("output_tokens");
            p.CacheReadTokens += u.Long("cache_read_input_tokens");
            p.CacheWriteTokens += u.Long("cache_creation_input_tokens");
        }
    }

    /// <summary>Validation of tool inputs against the subset of JSON Schema used by our tools.</summary>
    public static class ToolValidation
    {
        /// <summary>
        /// Repairs common shape slips before validation: JSON sent as a string ("[1,2]" for an
        /// array, "{...}" for an object), numbers and booleans sent as text. Returns the repaired value.
        /// </summary>
        public static object Coerce(JObject schema, object value)
        {
            if (schema == null || value == null) return value;
            string type = schema.Str("type");
            if ((type == "array" || type == "object") && value is string text)
            {
                string t = text.Trim();
                if ((type == "array" && t.StartsWith("[")) || (type == "object" && t.StartsWith("{")))
                {
                    try { value = Json.Parse(t); } catch { return value; }
                }
            }
            switch (type)
            {
                case "object":
                    {
                        var o = value as JObject;
                        var props = schema.Obj("properties");
                        if (o == null || props == null) return value;
                        foreach (var key in new List<string>(o.Keys))
                        {
                            var ps = props.Obj(key);
                            if (ps != null) o[key] = Coerce(ps, o[key]);
                        }
                        return o;
                    }
                case "array":
                    {
                        var a = value as JArray;
                        var items = schema.Obj("items");
                        if (a == null || items == null) return value;
                        for (int i = 0; i < a.Count; i++) a[i] = Coerce(items, a[i]);
                        return a;
                    }
                case "integer":
                case "number":
                    if (value is string ns && double.TryParse(ns.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return type == "integer" && Math.Abs(d - Math.Round(d)) < 1e-9 ? (object)(long)Math.Round(d) : d;
                    return value;
                case "boolean":
                    if (value is string bs)
                    {
                        if (string.Equals(bs.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(bs.Trim(), "false", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return value;
            }
            return value;
        }

        public static string Validate(JObject schema, object value, string path = "input")
        {
            if (schema == null) return null;
            string type = schema.Str("type");
            switch (type)
            {
                case "object":
                    {
                        var o = value as JObject;
                        if (o == null) return path + " must be an object";
                        foreach (var req in schema.Arr("required").Strings())
                            if (!o.ContainsKey(req) || o[req] == null) return path + "." + req + " is required";
                        var props = schema.Obj("properties");
                        if (props != null)
                            foreach (var kv in o)
                            {
                                var ps = props.Obj(kv.Key);
                                if (ps == null) continue;
                                var err = Validate(ps, kv.Value, path + "." + kv.Key);
                                if (err != null) return err;
                            }
                        return null;
                    }
                case "array":
                    {
                        var a = value as JArray;
                        if (a == null) return path + " must be an array";
                        if (schema.ContainsKey("minItems") && a.Count < schema.Int("minItems")) return path + " needs at least " + schema.Int("minItems") + " items";
                        if (schema.ContainsKey("maxItems") && a.Count > schema.Int("maxItems")) return path + " allows at most " + schema.Int("maxItems") + " items";
                        var items = schema.Obj("items");
                        if (items != null)
                            for (int i = 0; i < a.Count; i++)
                            {
                                var err = Validate(items, a[i], path + "[" + i + "]");
                                if (err != null) return err;
                            }
                        return null;
                    }
                case "string":
                    if (!(value is string s)) return path + " must be a string";
                    var en = schema.Arr("enum");
                    if (en != null && !en.Strings().Contains(s)) return path + " must be one of " + string.Join(", ", en.Strings());
                    return null;
                case "integer":
                    if (!Json.IsNumber(value) || Math.Abs(Json.ToDouble(value) - Math.Round(Json.ToDouble(value))) > 1e-9) return path + " must be an integer";
                    return RangeCheck(schema, Json.ToDouble(value), path);
                case "number":
                    if (!Json.IsNumber(value)) return path + " must be a number";
                    return RangeCheck(schema, Json.ToDouble(value), path);
                case "boolean":
                    return value is bool ? null : path + " must be true or false";
            }
            return null;
        }

        private static string RangeCheck(JObject schema, double v, string path)
        {
            if (schema.ContainsKey("minimum") && v < schema.Num("minimum")) return path + " must be >= " + schema.Num("minimum");
            if (schema.ContainsKey("maximum") && v > schema.Num("maximum")) return path + " must be <= " + schema.Num("maximum");
            return null;
        }
    }
}
