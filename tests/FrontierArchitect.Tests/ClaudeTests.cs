using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FrontierArchitect.Core;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Claude;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Net;
using Xunit;

namespace FrontierArchitect.Tests
{
    public class StreamTests
    {
        [Fact]
        public void RebuildsThinkingAndToolUse()
        {
            var acc = new StreamAccumulator();
            foreach (var e in Sse.ToolUse("toolu_1", "query", new JObject().Set("path", "snapshot.metrics")))
                foreach (var l in e.Split('\n')) acc.FeedLine(l);
            acc.Finish();
            Assert.True(acc.Completed);
            var r = acc.Result;
            Assert.Equal("tool_use", r.StopReason);
            Assert.Equal("thinking", ((JObject)r.Content[0]).Str("type"));
            Assert.Equal("sig123", ((JObject)r.Content[0]).Str("signature"));
            var tu = (JObject)r.Content[1];
            Assert.Equal("snapshot.metrics", tu.Obj("input").Str("path"));
            Assert.Equal(50L, r.Usage["output_tokens"]);
        }

        [Fact]
        public void FlagsInvalidToolJson()
        {
            var acc = new StreamAccumulator();
            var lines = new[]
            {
                "event: content_block_start", "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"query\",\"input\":{}}}", "",
                "event: content_block_delta", "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\": \\\"unterminated\"}}", "",
                "event: content_block_stop", "data: {\"type\":\"content_block_stop\",\"index\":0}", "",
                "event: message_stop", "data: {\"type\":\"message_stop\"}", ""
            };
            foreach (var l in lines) acc.FeedLine(l);
            Assert.True(acc.Result.InvalidToolInputs.ContainsKey("t1"));
        }

        [Fact]
        public void EchoDropsThinkingAndToolUseBeforeFallbackBoundary()
        {
            var content = new JArray
            {
                new JObject().Set("type", "thinking").Set("thinking", "").Set("signature", "a"),
                new JObject().Set("type", "text").Set("text", "partial"),
                new JObject().Set("type", "tool_use").Set("id", "x").Set("name", "q").Set("input", new JObject()),
                new JObject().Set("type", "fallback").Set("from", new JObject().Set("model", "m1")).Set("to", new JObject().Set("model", "m2")),
                new JObject().Set("type", "thinking").Set("thinking", "").Set("signature", "b"),
                new JObject().Set("type", "tool_use").Set("id", "y").Set("name", "q").Set("input", new JObject()),
            };
            var echo = ClaudeClient.EchoContent(content);
            Assert.Equal(new[] { "text", "fallback", "thinking", "tool_use" }, echo.Objects().Select(b => b.Str("type")).ToArray());
            Assert.Equal("y", echo.Objects().Last().Str("id"));
        }
    }

    public class ClientTests
    {
        [Fact]
        public void RetriesOverloadedThenSucceeds()
        {
            var http = new FakeHttp();
            http.EnqueueStatus(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}");
            http.EnqueueSse(Sse.Text("hello"));
            var client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "sk-test" }, (t, c) => { });
            var msg = client.Stream(new JObject().Set("model", "claude-opus-5-5").Set("max_tokens", 10).Set("messages", new JArray()), null, null, CancellationToken.None);
            Assert.Equal("hello", msg.Text);
            Assert.Equal(2, http.Requests.Count);
            var h = http.Requests[1].Headers.ToDictionary(k => k.Key, k => k.Value);
            Assert.Equal("2026-10-21", h["anthropic-version"]);
            Assert.Equal("sk-test", h["x-api-key"]);
        }

        [Fact]
        public void AuthErrorsAreNotRetried()
        {
            var http = new FakeHttp();
            http.EnqueueStatus(401, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            var client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "bad" }, (t, c) => { });
            var e = Assert.Throws<ClaudeException>(() => client.Stream(new JObject(), null, null, CancellationToken.None));
            Assert.Equal(ClaudeErrorKind.Auth, e.Kind);
            Assert.Single(http.Requests);
            Assert.DoesNotContain("bad", e.PlayerMessage);
        }

        [Fact]
        public void MostCapableResolvesFromAvailableModels()
        {
            Assert.Equal(new[] { "claude-fable-5-2", "claude-opus-5-5" }, ModelResolver.Candidates("most-capable", new List<string> { "claude-opus-5-5", "claude-fable-5-2", "claude-haiku-5-5" }).ToArray());
            Assert.Equal(new[] { "claude-opus-5-5" }, ModelResolver.Candidates("most-capable", null).ToArray());
            Assert.Equal(new[] { "claude-sonnet-5-5" }, ModelResolver.Candidates("claude-sonnet-5-5", null).ToArray());
        }
    }

    public class AgentLoopTests
    {
        private static LoadedAgent Assistant()
        {
            string root = Temp.Dir();
            Temp.CopyDir(Village.AgentsDir, root);
            return new AgentLoader(root, "assistant").Load("default").Agent;
        }

        [Fact]
        public void AssistantRunQueriesThenSubmits()
        {
            var http = new FakeHttp();
            http.EnqueueSse(Sse.ToolUse("toolu_1", "query", new JObject().Set("path", "snapshot.metrics.winter")));
            var report = new JObject().Set("summary", "Fine.").Set("findings", new JArray
            {
                new JObject().Set("id", "f1").Set("severity", "important").Set("domain", "production").Set("title", "Splitter understaffed")
                    .Set("evidence", new JArray { "firewood 300, burn 6 per month" }).Set("recommendation", "Add a worker")
                    .Set("action", new JObject().Set("kind", "apply").Set("ops", new JArray { new JObject().Set("op", "set_workers").Set("target", "FirewoodSplitter@12,12").Set("params", new JObject().Set("count", 2)) })),
                new JObject().Set("id", "f2").Set("severity", "critical").Set("domain", "storage").Set("title", "Storehouse far away")
                    .Set("evidence", new JArray { "storehouse 25 tiles from houses" }).Set("recommendation", "Move it")
                    .Set("action", new JObject().Set("kind", "apply").Set("ops", new JArray { new JObject().Set("op", "relocate").Set("target", "Storehouse@10,45") })),
            });
            http.EnqueueSse(Sse.ToolUse("toolu_2", "submit_report", report));
            var ctx = new RunContext { Client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "k" }, (t, c) => { }), OpUnavailable = op => op == "set_workers" ? null : "unmapped" };
            var snap = Village.Build();
            var outcome = Runs.Assistant(Assistant(), snap, ctx, null, CancellationToken.None);

            Assert.Null(outcome.Error);
            Assert.Equal(2, outcome.Report.Findings.Count);
            Assert.Equal("f2", outcome.Report.Findings[0].Id); // critical first
            Assert.Equal(Core.Planning.FindingActionKind.Architect, outcome.Report.Findings[0].Kind); // spatial op -> Architect
            Assert.Equal(Core.Planning.FindingActionKind.Apply, outcome.Report.Findings[1].Kind);     // valid settings op, available

            // request shape
            var body = Json.ParseObject(http.Requests[0].Body);
            Assert.Equal("adaptive", body.Obj("thinking").Str("type"));
            Assert.Equal("ephemeral", body.Obj("cache_control").Str("type"));
            Assert.False(body.ContainsKey("temperature"));
            Assert.Equal("ephemeral", body.Arr("system").Objects().First().Obj("cache_control").Str("type"));
            Assert.True(body.Arr("tools").Objects().All(t => t.Bool("eager_input_streaming")));
            Assert.Equal("default", body.Str("fallbacks"));
            Assert.Contains(http.Requests[0].Headers, h => h.Key == "anthropic-beta" && h.Value.Contains("server-side-fallback-2026-07-01"));

            // the second request echoes the assistant turn unchanged and returns one tool_result
            var second = Json.ParseObject(http.Requests[1].Body);
            var msgs = second.Arr("messages").Objects().ToList();
            Assert.Equal(3, msgs.Count);
            Assert.Equal("sig123", msgs[1].Arr("content").Objects().First().Str("signature"));
            var results = msgs[2].Arr("content").Objects().Where(b => b.Str("type") == "tool_result").ToList();
            Assert.Single(results);
            Assert.Equal("toolu_1", results[0].Str("tool_use_id"));
        }

        [Fact]
        public void EndingWithoutSubmitGetsOneRepairPrompt()
        {
            var http = new FakeHttp();
            http.EnqueueSse(Sse.Text("I think the village is fine."));
            http.EnqueueSse(Sse.Text("Still nothing to submit."));
            var ctx = new RunContext { Client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "k" }, (t, c) => { }) };
            var outcome = Runs.Assistant(Assistant(), Village.Build(), ctx, null, CancellationToken.None);
            Assert.NotNull(outcome.Error);
            Assert.Equal(2, http.Requests.Count);
            var last = Json.ParseObject(http.Requests[1].Body).Arr("messages").Objects().Last();
            Assert.Contains("submit_report", last.Arr("content").Objects().First().Str("text"));
        }

        [Fact]
        public void RefusalStopsTheRun()
        {
            var http = new FakeHttp();
            http.EnqueueSse(Sse.Text("", "refusal"));
            var ctx = new RunContext { Client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "k" }, (t, c) => { }) };
            var outcome = Runs.Assistant(Assistant(), Village.Build(), ctx, null, CancellationToken.None);
            Assert.Contains("declined", outcome.Error);
        }
    }
}

namespace FrontierArchitect.Tests
{
    public class ArchitectRunTests
    {
        [Fact]
        public void ArchitectRunRendersPromptsAndReturnsValidatedPlan()
        {
            string root = Temp.Dir();
            Temp.CopyDir(Village.AgentsDir, root);
            var agent = new AgentLoader(root, "architect").Load("default").Agent;
            var http = new FakeHttp();
            http.EnqueueSse(Sse.ToolUse("toolu_1", "score_draft", new JObject()));
            var plan = new JObject().Set("scenario", new JObject().Set("ambition", 1).Set("summary", "Fix the rotation."))
                .Set("phases", new JArray
                {
                    new JObject().Set("n", 1).Set("name", "Settings").Set("goal", "Hold fertility").Set("actions", new JArray
                    {
                        new JObject().Set("id", "a1").Set("op", "set_rotation").Set("target", "field@44,40").Set("objective", "O1")
                            .Set("rationale", "Beans and clover restore fertility")
                            .Set("params", new JObject().Set("rotation", new JArray { new JArray { "Beans" }, new JArray { "Wheat" }, new JArray { "Clover" } }))
                    })
                });
            http.EnqueueSse(Sse.ToolUse("toolu_2", "submit_plan", plan));
            string logDir = Temp.Dir();
            var ctx = new RunContext
            {
                Client = new ClaudeClient(http, new ClaudeOptions { ApiKey = "sk-secret-key" }, (t, c) => { }),
                NewRecorder = (a, s, m) => new RunLog(logDir, a, s, m)
            };
            var req = new ArchitectRequest { Ambition = 1, Mode = "instant", PlayerNote = "prioritise brick production" };
            var outcome = Runs.Architect(agent, Village.Build(), req, ctx, null, CancellationToken.None);

            Assert.Null(outcome.Error);
            Assert.Equal(agent.Hash, outcome.Plan.AgentHash);
            Assert.Equal("default", outcome.Plan.Profile);
            Assert.True(outcome.Plan.ScoreAfter.Num("O1") > outcome.Plan.ScoreBefore.Num("O1"));
            var body = Json.ParseObject(http.Requests[0].Body);
            string system = string.Join("\n", body.Arr("system").Objects().Select(b => b.Str("text")));
            string user = body.Arr("messages").Objects().First().Arr("content").Objects().First().Str("text");
            Assert.DoesNotContain("{{", system);
            Assert.DoesNotContain("{{", user);
            Assert.Contains("prioritise brick production", user);
            Assert.Contains("Touch-up", user);
            Assert.Contains("\"buildings\"", user);
            Assert.Contains("Game knowledge pack", body.Arr("system").Objects().First().Str("text")); // cached block first
            Assert.Equal(16, body.Arr("tools").Count);

            // run log: start, 2 turns with reasoning summaries, 2 tool results, end with the plan; no API key
            var logFile = System.IO.Directory.GetFiles(logDir, "*.jsonl").Single();
            Assert.DoesNotContain("sk-secret-key", System.IO.File.ReadAllText(logFile));
            var view = RunLogView.Read(logFile, true);
            Assert.Equal("architect", view.Agent);
            Assert.Equal(1, view.Start.Obj("meta").Int("ambition"));
            Assert.Contains("prioritise brick production", view.Start.Str("task"));
            Assert.Equal(2, view.Events.Count(e => e.Str("event") == "turn"));
            Assert.Equal("I should look.", view.Events.First(e => e.Str("event") == "turn").Arr("content").Objects().First().Str("summary"));
            Assert.Equal(new[] { "score_draft", "submit_plan" }, view.Events.Where(e => e.Str("event") == "tool").Select(e => e.Str("name")).ToArray());
            Assert.True(view.End.Bool("success"));
            Assert.NotNull(view.End.Obj("payload").Obj("plan"));
        }
    }
}
