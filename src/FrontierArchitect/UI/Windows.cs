using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrontierArchitect.Core;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Execution;
using FrontierArchitect.Core.Planning;
using FrontierArchitect.Core.Updates;
using UnityEngine;

namespace FrontierArchitect.UI
{
    /// <summary>All panels, drawn from MelonMod.OnGUI.</summary>
    internal static class Windows
    {
        public static Controller C;
        public static bool HudVisible = true;
        public static bool InGame;
        public static readonly ReportWindow Report = new ReportWindow();
        public static readonly ArchitectWindow Architect = new ArchitectWindow();
        public static readonly ExecuteWindow Execute = new ExecuteWindow();
        public static readonly SettingsWindow Settings = new SettingsWindow();
        public static readonly AgentWindow Agent = new AgentWindow();

        public static void ShowReport() { Report.Visible = true; }
        public static void ShowArchitect() { Architect.Visible = true; }
        public static void ShowExecute() { Execute.Visible = true; }

        public static bool MouseOverUi;
        private static bool _reportWasVisible;

        public static void Draw()
        {
            Styles.Ensure();
            float s = C.Config.UiScale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
            if (HudVisible) Hud();
            MouseOverUi = false;
            if (_reportWasVisible && !Report.Visible) C.ClearUndo(); // Apply can be undone until the panel closes
            _reportWasVisible = Report.Visible;
            Report.Draw(101); Architect.Draw(102); Execute.Draw(103); Settings.Draw(104); Agent.Draw(105);
            GUI.matrix = old;
        }

        private static void Hud()
        {
            float w = 440;
            var area = new Rect(Screen.width / C.Config.UiScale - w - 10, 60, w, 140);
            GUILayout.BeginArea(area);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            string spin = C.Busy ? " " + "|/-\\"[(int)(Time.realtimeSinceStartup * 6) % 4] : "";
            if (GUILayout.Button("Assistant" + (C.Busy && C.BusyWhat == "Assistant" ? spin : ""), Styles.Button))
            {
                if (Report.Visible) Report.Visible = false;
                else if (C.Report != null || C.Busy) Report.Visible = true;
                else C.StartAssistant();
            }
            if (GUILayout.Button("Architect" + (C.Busy && C.BusyWhat == "Architect" ? spin : ""), Styles.Button)) Architect.Visible = !Architect.Visible;
            if (GUILayout.Button("Execute" + (C.Runner.Running ? " (running)" : ""), Styles.Button)) Execute.Visible = !Execute.Visible;
            if (GUILayout.Button("Settings", Styles.Button)) Settings.Visible = !Settings.Visible;
            GUILayout.EndHorizontal();
            if (!InGame)
                GUILayout.Label("Frontier Architect v" + BuildInfo.Version + ": load a settlement to use the buttons.", Styles.Box);
            if (C.Busy)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(C.BusyWhat + ": " + (C.Progress?.Line ?? "starting…"), Styles.Small);
                if (GUILayout.Button("Cancel", GUILayout.Width(60))) C.Cancel();
                GUILayout.EndHorizontal();
            }
            if (C.Notice.Length > 0 && Time.realtimeSinceStartup < C.NoticeUntil) GUILayout.Label(C.Notice, Styles.Box);
            if (C.Update.State == UpdateState.Installed && C.Update.InstalledVersion != BuildInfo.Version)
                GUILayout.Label("Frontier Architect updated to v" + C.Update.LatestVersion + ".", Styles.Small);
            if (C.Update.State == UpdateState.Staged)
                GUILayout.Label("Update v" + C.Update.LatestVersion + " downloaded: restart the game to finish.", Styles.Important);
            GUILayout.EndArea();
        }

        public static string Pct(double v) => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    internal abstract class Window
    {
        public bool Visible;
        protected Rect Pos;
        protected Vector2 Scroll;
        protected abstract string Title { get; }
        protected abstract void Body();
        protected Controller C => Windows.C;

        protected Window(Rect initial) { Pos = initial; }

        public void Draw(int id)
        {
            if (!Visible) return;
            Pos = GUILayout.Window(id, Pos, _ => Frame(), Title, GUILayout.MinWidth(Pos.width), GUILayout.MinHeight(120));
        }

        private void Frame()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("x", GUILayout.Width(24))) Visible = false;
            GUILayout.EndHorizontal();
            try { Body(); }
            catch (Exception e) { GUILayout.Label("UI error: " + e.Message, Styles.Bad); }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
    }

    // ====================================================================== Assistant

    internal sealed class ReportWindow : Window
    {
        public ReportWindow() : base(new Rect(20, 80, 560, 600)) { }
        protected override string Title => "Assistant — village report";

        protected override void Body()
        {
            var r = C.Report;
            GUILayout.BeginHorizontal();
            GUI.enabled = !C.Busy;
            if (GUILayout.Button(r == null ? "Analyse village" : "Re-run analysis")) { C.ClearUndo(); C.StartAssistant(); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (r == null) { GUILayout.Label(C.Busy ? "Analysing…" : "No report yet.", Styles.Wrap); return; }
            if (!string.IsNullOrEmpty(r.Summary)) GUILayout.Label(r.Summary, Styles.Wrap);
            GUILayout.Label("Model " + r.Model + " · agent " + r.AgentHash, Styles.Small);
            Scroll = GUILayout.BeginScrollView(Scroll, GUILayout.Height(470));
            foreach (var f in r.Findings)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                var style = f.Severity == Severity.Critical ? Styles.Critical : f.Severity == Severity.Important ? Styles.Important : Styles.Optimisation;
                GUILayout.Label(f.Severity.ToString().ToUpperInvariant() + " · " + f.Domain + " — " + f.Title, style);
                foreach (var e in f.Evidence) GUILayout.Label("• " + e, Styles.Small);
                GUILayout.Label(f.Recommendation, Styles.Wrap);
                GUILayout.BeginHorizontal();
                switch (f.Kind)
                {
                    case FindingActionKind.Apply:
                        {
                            string blocked = C.ApplyBlocked(f);
                            if (f.Applied)
                            {
                                GUILayout.Label("Applied: " + string.Join("; ", f.BeforeAfter.ToArray()), Styles.Good);
                                if (C.CanUndo(f) && GUILayout.Button("Undo", GUILayout.Width(60))) C.UndoFinding(f);
                            }
                            else if (blocked == null) { if (GUILayout.Button("Apply", GUILayout.Width(80))) C.ApplyFinding(f); }
                            else GUILayout.Label("Manual — " + blocked, Styles.Small);
                            break;
                        }
                    case FindingActionKind.Architect:
                        if (GUILayout.Button("Send to Architect", GUILayout.Width(140))) C.SendToArchitect(f);
                        if (!string.IsNullOrEmpty(f.ManualReason)) GUILayout.Label(f.ManualReason, Styles.Small);
                        break;
                    default:
                        GUILayout.Label("Manual" + (string.IsNullOrEmpty(f.ManualReason) ? "" : " — " + f.ManualReason), Styles.Small);
                        break;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            GUILayout.Label("Applied changes can be undone until this panel is closed.", Styles.Small);
        }
    }

    // ====================================================================== Architect

    internal sealed class ArchitectWindow : Window
    {
        public ArchitectWindow() : base(new Rect(600, 80, 620, 680)) { }
        protected override string Title => "Architect — redesign scenario";
        public string SeedRequest = "";
        private string _note = "";
        private readonly HashSet<int> _open = new HashSet<int> { 1 };
        public bool ShowOverlay = true;
        public int OverlayPhase; // 0 = all

        protected override void Body()
        {
            var req = C.LastRequest;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ambition " + req.Ambition + " — " + Core.Agents.AgentDefinition.AmbitionNames[Mathf.Clamp(req.Ambition, 1, 5)], GUILayout.Width(190));
            req.Ambition = Mathf.RoundToInt(GUILayout.HorizontalSlider(req.Ambition, 1, 5, GUILayout.Width(200)));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Agent...", GUILayout.Width(80))) Windows.Agent.Visible = !Windows.Agent.Visible;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode", GUILayout.Width(50));
            if (GUILayout.Toggle(req.Mode == "native", "Native (cost, builders, time)", GUILayout.Width(220))) req.Mode = "native";
            if (GUILayout.Toggle(req.Mode == "instant", "Instant (sandbox)", GUILayout.Width(150))) req.Mode = "instant";
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Note", GUILayout.Width(50));
            _note = GUILayout.TextField(_note ?? "", 300);
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(SeedRequest))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("From Assistant: " + SeedRequest, Styles.Small);
                if (GUILayout.Button("x", GUILayout.Width(22))) SeedRequest = "";
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUI.enabled = !C.Busy;
            if (GUILayout.Button(C.Plan == null ? "Generate scenario" : "Regenerate", Styles.Button))
                C.StartArchitect(new ArchitectRequest { Ambition = req.Ambition, Mode = req.Mode, PlayerNote = _note, SeedRequest = SeedRequest });
            GUI.enabled = true;
            GUILayout.Label("Locked: " + C.Locked.Count + "  (hover a building and press " + C.Config.LockKey + ")", Styles.Small);
            GUILayout.EndHorizontal();
            if (C.AgentErrors.Count > 0) GUILayout.Label("Agent files: " + string.Join("; ", C.AgentErrors.ToArray()), Styles.Bad);

            var plan = C.Plan;
            if (plan == null) { GUILayout.Label(C.Busy && C.BusyWhat == "Architect" ? "Designing… " + (C.Progress?.LastThinking ?? "") : "No scenario yet.", Styles.Wrap); return; }

            GUILayout.Space(4);
            GUILayout.Label(plan.Summary, Styles.Wrap);
            GUILayout.Label("Ambition " + plan.Ambition + " · " + plan.Mode + " · profile " + plan.Profile + " · agent " + plan.AgentHash + " (v" + plan.AgentVersion + ") · " + plan.Model, Styles.Small);
            ScoreTable(plan);
            GUILayout.BeginHorizontal();
            ShowOverlay = GUILayout.Toggle(ShowOverlay, "Overlay", GUILayout.Width(80));
            GUILayout.Label("Phase", GUILayout.Width(44));
            if (GUILayout.Button(OverlayPhase == 0 ? "all" : OverlayPhase.ToString(), GUILayout.Width(44))) OverlayPhase = (OverlayPhase + 1) % (plan.Phases.Count + 1);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Send to Execute (" + plan.Mode + ")", Styles.Button))
                C.SendToExecute(plan.Mode == "instant" ? ExecMode.Instant : ExecMode.Native);
            if (GUILayout.Button("Discard", GUILayout.Width(70))) C.DiscardPlan();
            GUILayout.EndHorizontal();
            if (C.PlanValidation != null && !C.PlanValidation.Ok)
                GUILayout.Label("After your changes: " + string.Join(" | ", C.PlanValidation.Errors.Take(4).ToArray()), Styles.Bad);

            Scroll = GUILayout.BeginScrollView(Scroll, GUILayout.Height(380));
            foreach (var p in plan.Phases)
            {
                var old = GUI.color;
                GUI.color = Styles.Phase(p.N);
                GUILayout.BeginVertical(GUI.skin.box);
                GUI.color = old;
                GUILayout.BeginHorizontal();
                bool open = _open.Contains(p.N);
                if (GUILayout.Button(open ? "-" : "+", GUILayout.Width(24))) { if (open) _open.Remove(p.N); else _open.Add(p.N); }
                GUILayout.Label("Phase " + p.N + " — " + p.Name + "  [" + p.Status + "]", Styles.Bold);
                GUILayout.EndHorizontal();
                GUILayout.Label(p.Goal + "  · " + Math.Round(p.EstDays) + " days · cost " + CostText(p.EstCost) + " · window " + string.Join(", ", p.SeasonWindow.ToArray()), Styles.Small);
                if (open)
                    foreach (var a in p.Actions)
                    {
                        GUILayout.BeginHorizontal();
                        bool keep = a.Status != ActionStatus.Rejected;
                        bool now = GUILayout.Toggle(keep, "", GUILayout.Width(18));
                        if (now != keep) C.ToggleReject(a);
                        GUILayout.Label("<b>" + a.Op + "</b> " + a.Target + " " + ParamText(a) + " — " + a.Rationale + " (" + a.Objective + ")"
                            + (string.IsNullOrEmpty(a.StatusNote) ? "" : "  <color=#ff7766>" + a.StatusNote + "</color>"), Styles.Small);
                        if (a.Target != null && a.Target.Contains("@") && GUILayout.Button(C.Locked.Contains(a.Target) ? "unlock" : "lock", GUILayout.Width(56))) C.ToggleLock(a.Target);
                        GUILayout.EndHorizontal();
                    }
                GUILayout.EndVertical();
            }
            if (plan.Warnings.Count > 0)
            {
                GUILayout.Label("Notes from validation", Styles.Bold);
                foreach (var w in plan.Warnings.Take(12)) GUILayout.Label("• " + w, Styles.Small);
            }
            GUILayout.EndScrollView();
        }

        private static void ScoreTable(ArchitectPlan plan)
        {
            string[] names = { "Farming", "Logistics", "Residential", "Missing bldgs", "Defence", "Roads" };
            GUILayout.BeginHorizontal(GUI.skin.box);
            for (int i = 0; i < 6; i++)
            {
                string key = "O" + (i + 1);
                double b = plan.ScoreBefore.Num(key), a = plan.ScoreAfter.Num(key);
                GUILayout.BeginVertical(GUILayout.Width(92));
                GUILayout.Label(key + " " + names[i], Styles.Small);
                GUILayout.Label(Math.Round(b) + " → " + Math.Round(a), a >= b ? Styles.Good : Styles.Bad);
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            double wb = plan.ScoreBefore.Num("weighted"), wa = plan.ScoreAfter.Num("weighted");
            GUILayout.Label("Weighted " + Math.Round(wb, 1) + " → " + Math.Round(wa, 1) + " (" + (wa - wb >= 0 ? "+" : "") + Math.Round(wa - wb, 1) + ")", wa >= wb ? Styles.Good : Styles.Bad);
        }

        public static string CostText(JObject cost)
        {
            var parts = cost.Where(kv => kv.Key != "road_tiles").Take(5).Select(kv => kv.Key + " " + Math.Round(Json.ToDouble(kv.Value))).ToArray();
            return parts.Length == 0 ? "—" : string.Join(", ", parts);
        }

        public static string ParamText(PlanAction a)
        {
            if (a.Params.ContainsKey("x")) return "→ [" + a.X + "," + a.Z + "] r" + a.Rot;
            Core.Model.Rect r;
            if (a.TryRect(out r)) return r.ToString();
            if (a.Params.ContainsKey("count")) return "= " + a.Params.Int("count");
            if (a.Params.ContainsKey("limit")) return "= " + a.Params.Int("limit");
            var t = a.Tiles();
            if (t.Count > 0) return t.Count + " tiles";
            if (a.Params.ContainsKey("rotation")) return string.Join(" / ", a.Rotation().Select(y => string.Join("+", y.ToArray())).ToArray());
            return "";
        }
    }

    // ====================================================================== Execute

    internal sealed class ExecuteWindow : Window
    {
        public ExecuteWindow() : base(new Rect(600, 120, 600, 620)) { }
        protected override string Title => "Execute — phased programme";

        protected override void Body()
        {
            var r = C.Runner;
            if (C.Plan == null || r.Plan == null) { GUILayout.Label("Send a scenario from the Architect panel first.", Styles.Wrap); return; }
            var blocked = C.Executor.ExecuteBlockedReason();
            if (blocked != null) GUILayout.Label(blocked, Styles.Bad);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode", GUILayout.Width(44));
            GUI.enabled = !r.Running;
            if (GUILayout.Toggle(r.Mode == ExecMode.Native, "Native", GUILayout.Width(80)) && r.Mode != ExecMode.Native) { r.SetMode(ExecMode.Native); C.PrepareCard(); }
            if (GUILayout.Toggle(r.Mode == ExecMode.Instant, "Instant", GUILayout.Width(80)) && r.Mode != ExecMode.Instant) { r.SetMode(ExecMode.Instant); C.PrepareCard(); }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label(r.LastMessage, Styles.Small);
            GUILayout.EndHorizontal();

            Scroll = GUILayout.BeginScrollView(Scroll, GUILayout.Height(470));
            var cur = r.Current;
            if (cur == null) GUILayout.Label("All phases are finished or skipped.", Styles.Good);
            else if (r.Running) Running(cur);
            else Card(cur);

            GUILayout.Space(6);
            GUILayout.Label("Phases", Styles.Bold);
            foreach (var p in C.Plan.Phases)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.N + ". " + p.Name + " — " + p.Status + (string.IsNullOrEmpty(p.Report) ? "" : " · " + p.Report), Styles.Small);
                if ((p.Status == PhaseStatus.Completed || p.Status == PhaseStatus.Failed || p.Status == PhaseStatus.Paused || p.Status == PhaseStatus.InProgress) && !string.IsNullOrEmpty(p.SaveName))
                    if (GUILayout.Button("Roll back", GUILayout.Width(80))) C.Rollback(p);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void Card(Phase p)
        {
            if (C.Card == null || C.Card.Phase != p) { if (GUILayout.Button("Prepare phase " + p.N)) C.PrepareCard(); return; }
            var card = C.Card;
            GUILayout.Label("Phase " + p.N + " — " + p.Name, Styles.Header);
            GUILayout.Label(p.Goal, Styles.Wrap);
            GUILayout.Label("Duration ≈ " + Math.Round(p.EstDays) + " game days · builders ≈ " + card.BuildersNeeded + " · may start in: " + string.Join(", ", p.SeasonWindow.ToArray()), Styles.Small);
            if (p.Offline.Count > 0) GUILayout.Label("Offline during the phase: " + string.Join(", ", p.Offline.ToArray()), Styles.Small);
            if (card.CostVsStock.Count > 0)
                GUILayout.Label("Cost vs stock: " + string.Join(", ", card.CostVsStock.Select(kv => kv.Key + " " + Math.Round(kv.Value[0]) + "/" + Math.Round(kv.Value[1])).ToArray()),
                    card.CostVsStock.Any(kv => kv.Value[0] > kv.Value[1]) && r().Mode == ExecMode.Native ? Styles.Bad : Styles.Small);
            foreach (var kv in card.Actions)
            {
                string state = kv.Value == null ? "<color=#77ee77>ready</color>" : kv.Value.StartsWith("manual:") ? "<color=#ffcc55>" + kv.Value + "</color>" : "<color=#ff7766>" + kv.Value + "</color>";
                GUILayout.Label("• <b>" + kv.Key.Op + "</b> " + kv.Key.Target + " " + ArchitectWindow.ParamText(kv.Key) + " — " + kv.Key.Rationale + "  " + state, Styles.Small);
            }
            foreach (var reason in card.Guardrails.Reasons) GUILayout.Label("Waiting: " + reason, Styles.Important);
            if (card.SaveBlocked != null) GUILayout.Label("Auto-save: " + card.SaveBlocked, Styles.Bad);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Approve phase " + p.N, Styles.Button)) C.Approve();
            if (card.NeedsRepair && GUILayout.Button("Repair phase", GUILayout.Width(110))) C.RepairCurrentPhase();
            if (GUILayout.Button("Skip", GUILayout.Width(60))) { r().Skip(); C.Card = null; C.SavePlan(); }
            if (GUILayout.Button("Re-check", GUILayout.Width(80))) C.PrepareCard();
            GUILayout.EndHorizontal();
            GUILayout.Label("Manual actions are shown but not issued: do them by hand, or map their op (Settings > Diagnostics).", Styles.Small);
        }

        private PhaseRunner r() => C.Runner;

        private void Running(Phase p)
        {
            GUILayout.Label("Phase " + p.N + " — " + p.Name + " — " + p.Status, Styles.Header);
            if (r().PauseReason != null) GUILayout.Label("Paused: " + r().PauseReason, Styles.Important);
            foreach (var a in p.Actions)
                GUILayout.Label("• " + a.Status + " — " + a.Op + " " + a.Target + (string.IsNullOrEmpty(a.StatusNote) ? "" : " (" + a.StatusNote + ")"), a.Status == ActionStatus.Failed ? Styles.Bad : Styles.Small);
            GUILayout.BeginHorizontal();
            if (p.Status == PhaseStatus.Paused) { if (GUILayout.Button("Resume", Styles.Button)) r().Resume(); }
            else if (GUILayout.Button("Pause", GUILayout.Width(80))) r().PauseByPlayer();
            if (GUILayout.Button("Stop plan", GUILayout.Width(90))) r().Stop();
            GUILayout.EndHorizontal();
        }
    }

    // ====================================================================== Settings

    internal sealed class SettingsWindow : Window
    {
        public SettingsWindow() : base(new Rect(240, 100, 520, 640)) { }
        protected override string Title => "Frontier Architect — settings";
        private string _key, _model, _food, _wood, _token;
        private UpdaterSettings _upd;
        private string _probeResult = "";

        protected override void Body()
        {
            var cfg = C.Config;
            if (_key == null)
            {
                _key = cfg.ApiKey; _model = cfg.ModelOverride; _food = cfg.FoodMonths.ToString(CultureInfo.InvariantCulture);
                _wood = cfg.FirewoodMonths.ToString(CultureInfo.InvariantCulture);
                _upd = UpdaterSettings.Load(Paths.UpdaterConfig, BuildInfo.Repository);
                _token = _upd.GitHubToken;
            }
            Scroll = GUILayout.BeginScrollView(Scroll, GUILayout.Height(560));
            GUILayout.Label("Claude", Styles.Header);
            GUILayout.Label("Anthropic API key (stored only in UserData/FrontierArchitect/config.json)", Styles.Small);
            _key = GUILayout.PasswordField(_key ?? "", '•', 200);
            GUILayout.Label("Model (empty = most capable model your key can use)", Styles.Small);
            _model = GUILayout.TextField(_model ?? "", 80);

            GUILayout.Space(6);
            GUILayout.Label("Guardrails", Styles.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Food months", GUILayout.Width(110)); _food = GUILayout.TextField(_food, 6, GUILayout.Width(50));
            GUILayout.Label("Firewood months", GUILayout.Width(120)); _wood = GUILayout.TextField(_wood, 6, GUILayout.Width(50));
            GUILayout.EndHorizontal();
            cfg.SeasonAware = GUILayout.Toggle(cfg.SeasonAware, "Season-aware scheduling");
            cfg.StockPause = GUILayout.Toggle(cfg.StockPause, "Pause when food or firewood is below the threshold");
            cfg.AutoSave = GUILayout.Toggle(cfg.AutoSave, "Auto-save before each phase (required for rollback)");
            cfg.PauseOnRaid = GUILayout.Toggle(cfg.PauseOnRaid, "Pause during raids");
            cfg.AllowExperimentalOps = GUILayout.Toggle(cfg.AllowExperimentalOps, "Allow experimental ops (mapped but not yet proven on this game build)");

            GUILayout.Space(6);
            GUILayout.Label("Updates", Styles.Header);
            _upd.AutoUpdate = GUILayout.Toggle(_upd.AutoUpdate, "Install new versions automatically at game start");
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_upd.Channel != "prerelease", "Stable", GUILayout.Width(80))) _upd.Channel = "stable";
            if (GUILayout.Toggle(_upd.Channel == "prerelease", "Pre-releases", GUILayout.Width(110))) _upd.Channel = "prerelease";
            GUILayout.EndHorizontal();
            GUILayout.Label("GitHub token (only while the repository is private; read-only)", Styles.Small);
            _token = GUILayout.PasswordField(_token ?? "", '•', 200);
            var u = C.Update;
            GUILayout.Label("Installed v" + BuildInfo.Version + " · " + u.State + (string.IsNullOrEmpty(u.LatestVersion) ? "" : " · latest v" + u.LatestVersion) +
                            (string.IsNullOrEmpty(u.Message) ? "" : " — " + u.Message), Styles.Small);

            GUILayout.Space(6);
            GUILayout.Label("Diagnostics (milestone M0 mapping)", Styles.Header);
            GUILayout.Label("Game " + Application.version + " · mapped " + (string.IsNullOrEmpty(C.Map.MappedGameVersion) ? "—" : C.Map.MappedGameVersion) + " · " + C.SyncSummary, Styles.Small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Run probe")) { try { _probeResult = Game.Probe.Run(C.Access); } catch (Exception e) { _probeResult = "Probe failed: " + e.Message; } }
            if (GUILayout.Button("Open folder")) Application.OpenURL("file:///" + Paths.Root.Replace('\\', '/'));
            if (GUILayout.Button("Reload game map")) { C.Map = Game.GameMap.Load(); _probeResult = "gamemap.json reloaded (restart for a full refresh)."; }
            GUILayout.EndHorizontal();
            if (_probeResult.Length > 0) GUILayout.Label(_probeResult, Styles.Small);
            foreach (var op in Ops.All)
            {
                string why = C.Executor.OpUnavailableReason(op, ExecMode.Native);
                GUILayout.Label(op + ": " + C.Map.Status(op) + (why == null ? " — available" : " — " + why), why == null ? Styles.Good : Styles.Small);
            }
            GUILayout.BeginHorizontal();
            GUI.enabled = !C.Busy;
            if (GUILayout.Button("Ask Claude to propose mappings")) C.ProposeGameMap();
            GUI.enabled = System.IO.File.Exists(C.ProposalPath);
            if (GUILayout.Button("Apply proposal")) C.ApplyGameMapProposal();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Test on a building (then hover it and press " + C.Config.LockKey + "):", Styles.Small);
            if (GUILayout.Button(C.ArmedTest == Ops.SetWorkers ? "armed" : "set_workers", GUILayout.Width(90))) C.ArmedTest = Ops.SetWorkers;
            if (GUILayout.Button(C.ArmedTest == Ops.SetPaused ? "armed" : "set_paused", GUILayout.Width(90))) C.ArmedTest = Ops.SetPaused;
            GUILayout.EndHorizontal();
            GUILayout.Label("Once every op you need is marked proven, mark this build as mapped to enable Execute:", Styles.Small);
            if (GUILayout.Button("Mark game " + Application.version + " as mapped"))
            {
                var local = new JObject();
                try { if (System.IO.File.Exists(Paths.GameMapLocal)) local = Json.ParseObject(System.IO.File.ReadAllText(Paths.GameMapLocal)); } catch { }
                local.Set("mapped_game_version", Application.version);
                System.IO.File.WriteAllText(Paths.GameMapLocal, Json.Write(local, true));
                C.Map = Game.GameMap.Load();
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", Styles.Button))
            {
                cfg.ApiKey = (_key ?? "").Trim();
                cfg.ModelOverride = (_model ?? "").Trim();
                double d;
                if (double.TryParse(_food, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0) cfg.FoodMonths = d;
                if (double.TryParse(_wood, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0) cfg.FirewoodMonths = d;
                cfg.Save();
                _upd.GitHubToken = (_token ?? "").Trim();
                _upd.Save(Paths.UpdaterConfig);
                C.Runner.UpdateGuardrails(C.Guardrails());
                C.Toast("Settings saved.");
            }
            GUILayout.EndHorizontal();
        }
    }

    // ====================================================================== Agent settings

    internal sealed class AgentWindow : Window
    {
        public AgentWindow() : base(new Rect(60, 60, 760, 700)) { }
        protected override string Title => "Architect agent — settings and prompt";
        private string _file = "system.md";
        private string _text;
        private string _status = "";
        private string _profileName = "";
        private readonly Dictionary<string, float> _w = new Dictionary<string, float>();
        private float _detour = 1.3f, _minGain = 2f, _turns = 120, _minutes = 10;
        private string _model = "", _effort = "high";
        private string _loadedHash;

        protected override void Body()
        {
            var loader = C.ArchitectAgent;
            var agent = loader.Current;
            if (agent != null && _loadedHash != agent.Hash + agent.Profile)
            {
                _loadedHash = agent.Hash + agent.Profile;
                var raw = Core.Yaml.MiniYaml.ParseObject(loader.ReadFile("agent.yaml"));
                foreach (var k in Core.Agents.AgentDefinition.ObjectiveKeys)
                {
                    var key = (raw.Obj("objectives") ?? new JObject()).Keys.FirstOrDefault(x => x.StartsWith(k));
                    _w[k] = (float)(key == null ? 1.0 : raw.Obj("objectives").Num(key, 1));
                }
                _detour = (float)agent.Definition.DetourLimit; _minGain = (float)agent.Definition.MinGain;
                _turns = agent.Definition.MaxTurns; _minutes = (float)agent.Definition.MaxMinutes;
                _model = agent.Definition.ModelId; _effort = agent.Definition.Effort;
            }
            GUILayout.Label(agent == null ? "No valid agent loaded." : "Active: profile " + agent.Profile + " · hash " + agent.Hash + " · v" + agent.Definition.Version, Styles.Small);
            foreach (var e in C.AgentErrors) GUILayout.Label(e, Styles.Bad);

            // profiles
            GUILayout.BeginHorizontal();
            GUILayout.Label("Profile", GUILayout.Width(50));
            foreach (var p in loader.Profiles())
                if (GUILayout.Toggle(C.Config.ArchitectProfile == p, p, GUILayout.ExpandWidth(false)) && C.Config.ArchitectProfile != p)
                { C.Config.ArchitectProfile = p; C.Config.Save(); C.ReloadAgents(); }
            GUILayout.EndHorizontal();

            // weights and limits
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(360));
            string[] names = { "O1 farming", "O2 logistics", "O3 residential", "O4 missing", "O5 defence", "O6 roads" };
            for (int i = 0; i < 6; i++)
            {
                string k = "O" + (i + 1);
                GUILayout.BeginHorizontal();
                GUILayout.Label(names[i] + " " + _w[k].ToString("0.0", CultureInfo.InvariantCulture), GUILayout.Width(140));
                _w[k] = Mathf.Round(GUILayout.HorizontalSlider(_w[k], 0, 3, GUILayout.Width(200)) * 10) / 10f;
                GUILayout.EndHorizontal();
            }
            Slider("Detour limit", ref _detour, 1, 3, 0.05f);
            Slider("Min gain", ref _minGain, 0, 10, 0.5f);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.Label("Model id (most-capable = best available)", Styles.Small);
            _model = GUILayout.TextField(_model, 60);
            GUILayout.Label("Thinking effort", Styles.Small);
            GUILayout.BeginHorizontal();
            foreach (var e in Core.Agents.AgentDefinition.Efforts) if (GUILayout.Toggle(_effort == e, e, GUILayout.ExpandWidth(false))) _effort = e;
            GUILayout.EndHorizontal();
            Slider("Max turns", ref _turns, 5, 300, 5);
            Slider("Max minutes", ref _minutes, 1, 60, 1);
            GUILayout.BeginHorizontal();
            _profileName = GUILayout.TextField(_profileName, 40, GUILayout.Width(150));
            if (GUILayout.Button("Save as profile"))
            {
                try
                {
                    var objectives = new JObject();
                    foreach (var kv in _w.OrderBy(k => k.Key)) objectives.Set(kv.Key + "_weight", Math.Round(kv.Value, 2));
                    var o = new JObject()
                        .Set("model", new JObject().Set("id", _model).Set("effort", _effort))
                        .Set("loop", new JObject().Set("max_turns", (int)_turns).Set("max_minutes", Math.Round(_minutes)))
                        .Set("objectives", objectives)
                        .Set("roads", new JObject().Set("detour_limit", Math.Round(_detour, 2)))
                        .Set("planning", new JObject().Set("min_gain", Math.Round(_minGain, 2)));
                    string name = string.IsNullOrWhiteSpace(_profileName) ? "custom" : _profileName.Trim();
                    loader.SaveProfile(name, o);
                    C.Config.ArchitectProfile = name; C.Config.Save(); C.ReloadAgents();
                    _status = "Profile '" + name + "' saved and active.";
                }
                catch (Exception e) { _status = e.Message; }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            // file editor
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            foreach (var f in loader.EditableFiles())
                if (GUILayout.Toggle(_file == f, f, GUILayout.ExpandWidth(false)) && _file != f) { _file = f; _text = null; }
            GUILayout.EndHorizontal();
            if (_text == null) _text = loader.ReadFile(_file);
            Scroll = GUILayout.BeginScrollView(Scroll, GUILayout.Height(300));
            _text = GUILayout.TextArea(_text, Styles.Mono, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save file", Styles.Button))
            {
                loader.WriteFile(_file, _text);
                C.ReloadAgents();
                _status = C.AgentErrors.Count == 0 ? "Saved; the next run uses it (hash " + loader.Current?.Hash + ")." : "Saved, but it failed validation: the previous version stays active.";
            }
            if (GUILayout.Button("Revert")) _text = loader.ReadFile(_file);
            if (GUILayout.Button("Open in system editor")) Application.OpenURL("file:///" + System.IO.Path.Combine(loader.Root, _file).Replace('\\', '/'));
            if (GUILayout.Button("Reload from disk")) { C.ReloadAgents(); _text = loader.ReadFile(_file); _status = "Reloaded."; }
            GUILayout.EndHorizontal();
            if (_status.Length > 0) GUILayout.Label(_status, Styles.Small);
        }

        private static void Slider(string label, ref float v, float min, float max, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + " " + v.ToString("0.##", CultureInfo.InvariantCulture), GUILayout.Width(140));
            v = Mathf.Round(GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(180)) / step) * step;
            GUILayout.EndHorizontal();
        }
    }
}
