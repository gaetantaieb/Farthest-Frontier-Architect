using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FrontierArchitect.Core.Data;
using MelonLoader;
using MelonLoader.Utils;

namespace FrontierArchitect
{
    /// <summary>Folder layout under the game's Mono build.</summary>
    internal static class Paths
    {
        public static string GameRoot => MelonEnvironment.GameRootDirectory;
        public static string UserData => MelonEnvironment.UserDataDirectory;
        public static string Root => Path.Combine(UserData, "FrontierArchitect");
        public static string Agents => Path.Combine(Root, "agents");
        public static string Plans => Path.Combine(Root, "plans");
        public static string Reports => Path.Combine(Root, "reports");
        public static string Probe => Path.Combine(Root, "probe");
        public static string Logs => Path.Combine(Root, "logs");
        public static string Config => Path.Combine(Root, "config.json");
        public static string UpdaterConfig => Path.Combine(Root, "updater.json");
        public static string UpdateStatus => Path.Combine(Path.Combine(Root, "updates"), "status.json");
        public static string GameMap => Path.Combine(Root, "gamemap.json");
        public static string GameMapLocal => Path.Combine(Root, "gamemap.local.json");

        public static void Ensure()
        {
            foreach (var d in new[] { Root, Agents, Plans, Reports, Probe, Logs }) Directory.CreateDirectory(d);
        }
    }

    internal static class Log
    {
        public static void Info(string m) => MelonLogger.Msg(m);
        public static void Warn(string m) => MelonLogger.Warning(m);
        public static void Error(string m) => MelonLogger.Error(m);
    }

    /// <summary>
    /// UserData/FrontierArchitect/config.json. The API key lives only here: never in a save file,
    /// a plan, a report or a log line.
    /// </summary>
    internal sealed class ModConfig
    {
        public string ApiKey = "";
        public string ModelOverride = "";
        public double FoodMonths = 3, FirewoodMonths = 3;
        public bool SeasonAware = true, StockPause = true, AutoSave = true, PauseOnRaid = true;
        public int MaxParallelOrders = 3;
        public bool AllowExperimentalOps = false;
        public string Language = "en";
        public string ArchitectProfile = "default";
        public string AssistantProfile = "default";
        public int DefaultAmbition = 2;
        public string DefaultMode = "native";
        public string ToggleHudKey = "F8", ToggleOverlayKey = "F9", LockKey = "L";
        public float UiScale = 1f;

        public static ModConfig Load()
        {
            var c = new ModConfig();
            try
            {
                if (File.Exists(Paths.Config))
                {
                    var o = Json.ParseObject(File.ReadAllText(Paths.Config));
                    c.ApiKey = o.Str("anthropic_api_key", "");
                    c.ModelOverride = o.Str("model", "");
                    var t = o.Obj("thresholds");
                    c.FoodMonths = t.Num("food_months", 3);
                    c.FirewoodMonths = t.Num("firewood_months", 3);
                    var g = o.Obj("guardrails");
                    c.SeasonAware = g.Bool("season_aware", true);
                    c.StockPause = g.Bool("stock_pause", true);
                    c.AutoSave = g.Bool("auto_save", true);
                    c.PauseOnRaid = g.Bool("pause_on_raid", true);
                    c.MaxParallelOrders = Math.Max(1, g.Int("max_parallel_orders", 3));
                    c.AllowExperimentalOps = o.Bool("allow_experimental_ops", false);
                    c.Language = o.Str("language", "en");
                    c.ArchitectProfile = o.Str("architect_profile", "default");
                    c.AssistantProfile = o.Str("assistant_profile", "default");
                    c.DefaultAmbition = Math.Max(1, Math.Min(5, o.Int("default_ambition", 2)));
                    c.DefaultMode = o.Str("default_mode", "native");
                    var k = o.Obj("hotkeys");
                    c.ToggleHudKey = k.Str("toggle_hud", "F8");
                    c.ToggleOverlayKey = k.Str("toggle_overlay", "F9");
                    c.LockKey = k.Str("lock_under_cursor", "L");
                    c.UiScale = (float)Math.Max(0.6, Math.Min(2.5, o.Num("ui_scale", 1)));
                    return c;
                }
            }
            catch (Exception e) { Log.Warn("config.json could not be read (" + e.Message + "); using defaults."); }
            c.Save();
            return c;
        }

        public void Save()
        {
            var o = new JObject()
                .Set("anthropic_api_key", ApiKey)
                .Set("model", ModelOverride)
                .Set("thresholds", new JObject().Set("food_months", FoodMonths).Set("firewood_months", FirewoodMonths))
                .Set("guardrails", new JObject().Set("season_aware", SeasonAware).Set("stock_pause", StockPause)
                    .Set("auto_save", AutoSave).Set("pause_on_raid", PauseOnRaid).Set("max_parallel_orders", MaxParallelOrders))
                .Set("allow_experimental_ops", AllowExperimentalOps)
                .Set("language", Language)
                .Set("architect_profile", ArchitectProfile)
                .Set("assistant_profile", AssistantProfile)
                .Set("default_ambition", DefaultAmbition)
                .Set("default_mode", DefaultMode)
                .Set("hotkeys", new JObject().Set("toggle_hud", ToggleHudKey).Set("toggle_overlay", ToggleOverlayKey).Set("lock_under_cursor", LockKey))
                .Set("ui_scale", UiScale)
                .Set("_help", "anthropic_api_key: your own key from console.anthropic.com (stays on this computer). model: empty = the agent file's choice (most capable available).");
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(Paths.Config, Json.Write(o, true));
        }
    }

    /// <summary>Runs callbacks on the Unity main thread (all game reads and writes happen there).</summary>
    internal static class MainThread
    {
        private static readonly Queue<Action> _queue = new Queue<Action>();

        public static void Post(Action a)
        {
            lock (_queue) _queue.Enqueue(a);
        }

        public static void Drain()
        {
            while (true)
            {
                Action a;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    a = _queue.Dequeue();
                }
                try { a(); }
                catch (Exception e) { Log.Error("main-thread task failed: " + e); }
            }
        }
    }

    /// <summary>Default files shipped inside the DLL (agents/*, gamemap.json).</summary>
    internal static class EmbeddedDefaults
    {
        public static Dictionary<string, string> Read(string prefix)
        {
            var result = new Dictionary<string, string>();
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                using (var s = asm.GetManifestResourceStream(name))
                using (var r = new StreamReader(s)) result[name.Substring(prefix.Length)] = r.ReadToEnd();
            }
            return result;
        }
    }
}
