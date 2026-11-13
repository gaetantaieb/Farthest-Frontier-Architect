using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Execution;
using FrontierArchitect.Core.Net;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Updates;
using Xunit;

namespace FrontierArchitect.Tests
{
    internal sealed class FakeGame : IGameExecutor
    {
        public LiveStatus Live = new LiveStatus { FoodMonths = 8, FirewoodMonths = 8, Season = "spring", SeasonProgress = 0.2f };
        public readonly List<string> Issued = new List<string>();
        public readonly List<string> Saves = new List<string>();
        public bool SaveWorks = true;
        public HashSet<string> Done = new HashSet<string>();
        public string Unavailable;

        public string OpUnavailableReason(string op, ExecMode mode) => op == Unavailable ? "unmapped" : null;
        public string ValidateLive(PlanAction a, ExecMode mode) => null;
        public bool Issue(PlanAction a, ExecMode mode, out string error) { error = null; Issued.Add(a.Id); return true; }
        public OrderProgress Check(PlanAction a, ExecMode mode, out string note) { note = null; return Done.Contains(a.Id) ? OrderProgress.Done : OrderProgress.Pending; }
        public bool CanSave(out string reason) { reason = SaveWorks ? null : "unmapped"; return SaveWorks; }
        public bool Save(string name, out string error) { error = null; Saves.Add(name); return true; }
        public bool Load(string name, out string error) { error = null; return true; }
        public LiveStatus Status() => Live;
        public string ExecuteBlockedReason() => null;
    }

    public class PhaseRunnerTests
    {
        private static ArchitectPlan Plan()
        {
            var p1 = new Phase { N = 1, Name = "Settings", SeasonWindow = new List<string> { "spring", "summer" } };
            p1.Actions.Add(new PlanAction { Id = "a1", Op = Ops.SetWorkers, Target = "x", Params = new JObject().Set("count", 1) });
            p1.Actions.Add(new PlanAction { Id = "a2", Op = Ops.SetPaused, Target = "y", Params = new JObject().Set("paused", false) });
            var p2 = new Phase { N = 2, Name = "Build", SeasonWindow = new List<string> { "summer" } };
            p2.Actions.Add(new PlanAction { Id = "b1", Op = Ops.Build, Target = "House", Params = new JObject().Set("x", 1).Set("z", 1) });
            return new ArchitectPlan { Phases = new List<Phase> { p1, p2 } };
        }

        [Fact]
        public void ApprovedPhaseSavesIssuesAndCompletes()
        {
            var game = new FakeGame();
            var r = new PhaseRunner(game, new GuardrailSettings(), null);
            r.Load(Plan(), ExecMode.Native);
            var card = r.Prepare();
            Assert.Empty(game.Issued); // nothing before approval
            Assert.True(r.Approve(card, "Testford"));
            Assert.Equal(new[] { "FA_Testford_phase1" }, game.Saves.ToArray());
            Assert.Equal(new[] { "a1", "a2" }, game.Issued.ToArray());
            game.Done.Add("a1"); game.Done.Add("a2");
            r.Tick();
            Assert.Equal(PhaseStatus.Completed, r.Plan.Phases[0].Status);
            Assert.Equal(1, r.Index);
        }

        [Fact]
        public void SeasonWindowAndMissingSaveBlockApproval()
        {
            var game = new FakeGame();
            var r = new PhaseRunner(game, new GuardrailSettings(), null);
            var plan = Plan();
            plan.Phases[0].Status = PhaseStatus.Completed;
            r.Load(plan, ExecMode.Native);
            var card = r.Prepare(); // phase 2 only runs in summer
            Assert.False(r.Approve(card, "t"));
            Assert.Contains("season", r.LastMessage);
            game.Live.Season = "summer";
            game.SaveWorks = false;
            card = r.Prepare();
            Assert.False(r.Approve(card, "t"));
            Assert.Contains("Auto-save", r.LastMessage);
            Assert.Empty(game.Issued);
        }

        [Fact]
        public void LowStockPausesAndStopsNewOrders()
        {
            var game = new FakeGame();
            var r = new PhaseRunner(game, new GuardrailSettings { MaxParallelOrders = 1 }, null);
            r.Load(Plan(), ExecMode.Native);
            Assert.True(r.Approve(r.Prepare(), "t"));
            Assert.Equal(new[] { "a1" }, game.Issued.ToArray());
            game.Live.FirewoodMonths = 1;
            game.Done.Add("a1");
            r.Tick();
            Assert.Equal(PhaseStatus.Paused, r.Current.Status);
            Assert.Equal(new[] { "a1" }, game.Issued.ToArray());
            game.Live.FirewoodMonths = 6;
            r.Resume();
            r.Tick();
            Assert.Equal(new[] { "a1", "a2" }, game.Issued.ToArray());
        }

        [Fact]
        public void UnavailableOpsBecomeManual()
        {
            var game = new FakeGame { Unavailable = Ops.SetPaused };
            var r = new PhaseRunner(game, new GuardrailSettings(), null);
            r.Load(Plan(), ExecMode.Native);
            var card = r.Prepare();
            Assert.False(card.NeedsRepair);
            Assert.True(r.Approve(card, "t"));
            Assert.Equal(ActionStatus.Manual, r.Plan.Phases[0].Actions[1].Status);
            Assert.Equal(new[] { "a1" }, game.Issued.ToArray());
        }

        [Fact]
        public void PlanStoreRoundTrip()
        {
            var store = new PlanStore(Temp.Dir());
            var plan = Plan();
            plan.Phases[0].Status = PhaseStatus.Completed;
            plan.AgentHash = "abc123def456";
            store.Save("Testford", plan);
            var back = store.Load("Testford");
            Assert.Equal(PhaseStatus.Completed, back.Phases[0].Status);
            Assert.Equal("abc123def456", back.AgentHash);
            Assert.Equal(3, back.AllActions().Count());
        }
    }

    public class UpdateTests
    {
        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        private sealed class Release
        {
            public string Version;
            public byte[] Mod = Bytes("mod-v2"), Updater = Bytes("updater-v2");
            public bool CorruptMod;

            public HttpResponse Handle(HttpRequest r)
            {
                if (r.Url.EndsWith("/releases/latest"))
                {
                    var assets = new JArray();
                    foreach (var n in new[] { "manifest.json", "FrontierArchitect.dll", "FrontierArchitect.Updater.dll" })
                        assets.Add(new JObject().Set("name", n).Set("url", "https://api.github.com/assets/" + n).Set("browser_download_url", "https://github.com/dl/" + n));
                    return new HttpResponse { Status = 200, Body = Json.Write(new JObject().Set("tag_name", "v" + Version).Set("assets", assets)) };
                }
                if (r.Url.EndsWith("manifest.json"))
                {
                    var m = new ReleaseManifest { Version = Version, Tag = "v" + Version };
                    m.Files.Add(new ManifestFile { Path = "Mods/FrontierArchitect.dll", Asset = "FrontierArchitect.dll", Sha256 = Hashing.Sha256Hex(Mod), Size = Mod.Length });
                    m.Files.Add(new ManifestFile { Path = "Plugins/FrontierArchitect.Updater.dll", Asset = "FrontierArchitect.Updater.dll", Sha256 = Hashing.Sha256Hex(Updater), Size = Updater.Length });
                    return new HttpResponse { Status = 200, BodyBytes = Bytes(Json.Write(m.ToJson())) };
                }
                if (r.Url.EndsWith("FrontierArchitect.dll")) return new HttpResponse { Status = 200, BodyBytes = CorruptMod ? Bytes("tampered") : Mod };
                if (r.Url.EndsWith("FrontierArchitect.Updater.dll")) return new HttpResponse { Status = 200, BodyBytes = Updater };
                return new HttpResponse { Status = 404 };
            }
        }

        private static (string root, string user) Game()
        {
            string root = Temp.Dir();
            Directory.CreateDirectory(Path.Combine(root, "Mods"));
            Directory.CreateDirectory(Path.Combine(root, "Plugins"));
            File.WriteAllText(Path.Combine(root, "Mods", "FrontierArchitect.dll"), "mod-v1");
            File.WriteAllText(Path.Combine(root, "Plugins", "FrontierArchitect.Updater.dll"), "updater-v1");
            string user = Path.Combine(root, "UserData");
            Directory.CreateDirectory(user);
            return (root, user);
        }

        [Fact]
        public void InstallsNewerReleaseAfterVerifyingChecksums()
        {
            var (root, user) = Game();
            var rel = new Release { Version = "0.2.0" };
            var http = new FakeHttp { Router = rel.Handle };
            var engine = new UpdateEngine(http, root, user, null);
            var status = engine.CheckAndInstall(new UpdaterSettings { Repository = "o/r" }, SemVer.Parse("0.1.0"), CancellationToken.None);
            Assert.Equal(UpdateState.Installed, status.State);
            Assert.Equal("mod-v2", File.ReadAllText(Path.Combine(root, "Mods", "FrontierArchitect.dll")));
            Assert.Equal("updater-v2", File.ReadAllText(Path.Combine(root, "Plugins", "FrontierArchitect.Updater.dll")));
            Assert.True(File.Exists(Path.Combine(user, "FrontierArchitect", "updates", "status.json")));
            Assert.DoesNotContain(http.Requests, r => r.Headers.Any(h => h.Key == "Authorization"));
        }

        [Fact]
        public void TamperedDownloadChangesNothing()
        {
            var (root, user) = Game();
            var rel = new Release { Version = "0.2.0", CorruptMod = true };
            var engine = new UpdateEngine(new FakeHttp { Router = rel.Handle }, root, user, null);
            var status = engine.CheckAndInstall(new UpdaterSettings { Repository = "o/r" }, SemVer.Parse("0.1.0"), CancellationToken.None);
            Assert.Equal(UpdateState.Error, status.State);
            Assert.Contains("Checksum", status.Message);
            Assert.Equal("mod-v1", File.ReadAllText(Path.Combine(root, "Mods", "FrontierArchitect.dll")));
            Assert.Equal("updater-v1", File.ReadAllText(Path.Combine(root, "Plugins", "FrontierArchitect.Updater.dll")));
        }

        [Fact]
        public void SameOrOlderReleaseIsLeftAlone()
        {
            var (root, user) = Game();
            var engine = new UpdateEngine(new FakeHttp { Router = new Release { Version = "0.1.0" }.Handle }, root, user, null);
            var status = engine.CheckAndInstall(new UpdaterSettings { Repository = "o/r" }, SemVer.Parse("0.1.0"), CancellationToken.None);
            Assert.Equal(UpdateState.UpToDate, status.State);
            Assert.Equal("mod-v1", File.ReadAllText(Path.Combine(root, "Mods", "FrontierArchitect.dll")));
        }

        [Fact]
        public void PrivateRepositoryUsesTokenOnlyOnGitHubApi()
        {
            var (root, user) = Game();
            var rel = new Release { Version = "0.3.0" };
            var http = new FakeHttp
            {
                Router = r =>
                {
                    bool auth = r.Headers.Any(h => h.Key == "Authorization" && h.Value == "Bearer tok");
                    if (r.Url.StartsWith("https://api.github.com/assets/"))
                    {
                        Assert.True(auth);
                        Assert.Contains(r.Headers, h => h.Key == "Accept" && h.Value == "application/octet-stream");
                        var resp = new HttpResponse { Status = 302 };
                        resp.Headers["Location"] = "https://objects.example/" + r.Url.Substring(r.Url.LastIndexOf('/') + 1);
                        return resp;
                    }
                    if (r.Url.StartsWith("https://objects.example/")) Assert.False(auth);
                    if (r.Url.Contains("/repos/") && !auth) return new HttpResponse { Status = 404 };
                    return rel.Handle(r);
                }
            };
            var engine = new UpdateEngine(http, root, user, null);
            var noToken = engine.CheckAndInstall(new UpdaterSettings { Repository = "o/r" }, SemVer.Parse("0.1.0"), CancellationToken.None);
            Assert.Equal(UpdateState.Error, noToken.State);
            Assert.Contains("private", noToken.Message);
            var withToken = engine.CheckAndInstall(new UpdaterSettings { Repository = "o/r", GitHubToken = "tok" }, SemVer.Parse("0.1.0"), CancellationToken.None);
            Assert.Equal(UpdateState.Installed, withToken.State);
        }

        [Fact]
        public void StagedFilesApplyOnNextLaunch()
        {
            var (root, user) = Game();
            var engine = new UpdateEngine(new FakeHttp(), root, user, null);
            var bytes = Bytes("mod-v9");
            var m = new ReleaseManifest { Version = "0.9.0" };
            m.Files.Add(new ManifestFile { Path = "Mods/FrontierArchitect.dll", Asset = "FrontierArchitect.dll", Sha256 = Hashing.Sha256Hex(bytes) });
            Directory.CreateDirectory(Path.Combine(engine.StagedDir, "Mods"));
            File.WriteAllBytes(Path.Combine(engine.StagedDir, "Mods", "FrontierArchitect.dll"), bytes);
            File.WriteAllText(Path.Combine(engine.StagedDir, "manifest.json"), Json.Write(m.ToJson()));
            Assert.Equal(1, engine.ApplyStaged());
            Assert.Equal("mod-v9", File.ReadAllText(Path.Combine(root, "Mods", "FrontierArchitect.dll")));
            Assert.False(Directory.Exists(engine.StagedDir));
        }

        [Fact]
        public void ManifestRejectsUnsafePaths()
        {
            var o = new JObject().Set("version", "1.0.0").Set("files", new JArray { new JObject().Set("path", "../evil.dll").Set("asset", "x").Set("sha256", new string('a', 64)) });
            Assert.Throws<InvalidDataException>(() => ReleaseManifest.FromJson(o));
        }

        [Fact]
        public void SemVerOrdering()
        {
            Assert.True(SemVer.Parse("v1.2.0").IsNewerThan(SemVer.Parse("1.1.9")));
            Assert.True(SemVer.Parse("1.2.0").IsNewerThan(SemVer.Parse("1.2.0-beta.2")));
            Assert.True(SemVer.Parse("1.2.0-beta.10").IsNewerThan(SemVer.Parse("1.2.0-beta.2")));
            Assert.False(SemVer.Parse("0.1.0").IsNewerThan(SemVer.Parse("0.1.0")));
        }
    }
}
