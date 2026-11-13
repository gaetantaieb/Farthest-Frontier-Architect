using System.Collections.Generic;
using System.IO;
using System.Linq;
using FrontierArchitect.Core.Agents;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Planning;
using Xunit;

namespace FrontierArchitect.Tests
{
    public class AgentLoaderTests
    {
        private static string CopyAgents()
        {
            string root = Temp.Dir();
            Temp.CopyDir(Village.AgentsDir, root);
            return root;
        }

        [Fact]
        public void LoadsShippedAgentsWithoutErrors()
        {
            string root = CopyAgents();
            foreach (var name in new[] { "architect", "assistant" })
            {
                var r = new AgentLoader(root, name).Load("default");
                Assert.Empty(r.Errors);
                Assert.NotNull(r.Agent);
                Assert.Equal(12, r.Agent.Hash.Length);
                Assert.Empty(r.Agent.Warnings);
            }
            var arch = new AgentLoader(root, "architect").Load("default").Agent;
            Assert.Equal("high", arch.Definition.Effort);
            Assert.Equal(5, arch.Definition.Ambition[1].MaxRelocations);
            Assert.Equal(-1, arch.Definition.Ambition[5].MaxRelocations);
            Assert.Contains(Ops.Flatten, arch.Definition.Ambition[4].Allow);
            Assert.DoesNotContain(Ops.Flatten, arch.Definition.Ambition[3].Allow);
            Assert.Contains("town_center", arch.Definition.Ambition[5].Movable);
            Assert.Contains("wall", arch.Definition.Ambition[5].Movable);
            Assert.Equal(6, arch.Definition.Weights.Values.Sum(), 6); // normalised
            Assert.Contains("coordinates.md", arch.Knowledge.Keys);
        }

        [Fact]
        public void InvalidEditKeepsPreviousVersionAndVersionsAreKept()
        {
            string root = CopyAgents();
            var loader = new AgentLoader(root, "architect");
            var good = loader.Load("default").Agent;
            Assert.True(Directory.GetDirectories(loader.VersionsDir).Length == 1);

            loader.WriteFile("agent.yaml", loader.ReadFile("agent.yaml").Replace("detour_limit: 1.3", "detour_limit: 0.2"));
            var bad = loader.Load("default");
            Assert.NotEmpty(bad.Errors);
            Assert.True(bad.UsedPrevious);
            Assert.Equal(good.Hash, bad.Agent.Hash);

            loader.WriteFile("agent.yaml", loader.ReadFile("agent.yaml").Replace("detour_limit: 0.2", "detour_limit: 1.5"));
            var fixedAgent = loader.Load("default");
            Assert.Empty(fixedAgent.Errors);
            Assert.NotEqual(good.Hash, fixedAgent.Agent.Hash);
            Assert.Equal(1.5, fixedAgent.Agent.Definition.DetourLimit);
            Assert.Equal(2, Directory.GetDirectories(loader.VersionsDir).Length);
        }

        [Fact]
        public void ProfilesOverrideOnlyTheirKeys()
        {
            string root = CopyAgents();
            var loader = new AgentLoader(root, "architect");
            var def = loader.Load("default").Agent.Definition;
            var food = loader.Load("food-first").Agent;
            Assert.Equal("food-first", food.Profile);
            Assert.True(food.Definition.Weights["O1"] > def.Weights["O1"]);
            Assert.Equal(def.DetourLimit, food.Definition.DetourLimit);
            loader.SaveProfile("mine", new JObject().Set("roads", new JObject().Set("detour_limit", 1.8)));
            Assert.Contains("mine", loader.Profiles());
            Assert.Equal(1.8, loader.Load("mine").Agent.Definition.DetourLimit);
        }

        [Fact]
        public void TaskMustCarryTheSnapshot()
        {
            string root = CopyAgents();
            var loader = new AgentLoader(root, "assistant");
            loader.WriteFile("task.md", "Analyse the village.");
            var r = loader.Load("default");
            Assert.Contains(r.Errors, e => e.Contains("{{snapshot_json}}"));
        }
    }

    public class DefaultsSyncTests
    {
        [Fact]
        public void NeverOverwritesPlayerEdits()
        {
            string root = Temp.Dir();
            var v1 = new Dictionary<string, string> { { "architect/system.md", "v1 prompt" }, { "architect/task.md", "v1 task" } };
            var r1 = DefaultsSync.Sync(v1, root);
            Assert.Equal(2, r1.Installed.Count);

            File.WriteAllText(Path.Combine(root, "architect", "task.md"), "my own task"); // player edit
            var v2 = new Dictionary<string, string> { { "architect/system.md", "v2 prompt" }, { "architect/task.md", "v2 task" } };
            var r2 = DefaultsSync.Sync(v2, root);
            Assert.Equal(new[] { "architect/system.md" }, r2.Updated.ToArray());
            Assert.Equal(new[] { "architect/task.md" }, r2.KeptPlayerEdits.ToArray());
            Assert.Equal("v2 prompt", File.ReadAllText(Path.Combine(root, "architect", "system.md")));
            Assert.Equal("my own task", File.ReadAllText(Path.Combine(root, "architect", "task.md")));
            Assert.Equal("v2 task", File.ReadAllText(Path.Combine(root, "architect", "task.md.new-default")));
        }
    }
}
