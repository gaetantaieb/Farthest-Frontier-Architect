using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FrontierArchitect.Core.Net;
using FrontierArchitect.Core.Updates;
using MelonLoader;
using MelonLoader.Utils;

namespace FrontierArchitect.Updater
{
    /// <summary>
    /// Keeps Frontier Architect up to date. MelonLoader loads plugins before mods, so at
    /// OnPreModsLoaded the mod DLL is not loaded yet and can be replaced: a new release published on
    /// GitHub is installed on the very launch that finds it.
    /// Settings: UserData/FrontierArchitect/updater.json. Status: UserData/FrontierArchitect/updates/status.json.
    /// </summary>
    public sealed class UpdaterPlugin : MelonPlugin
    {
        private const string ModFile = "Mods/FrontierArchitect.dll";

        public override void OnPreModsLoaded()
        {
            try { Run(); }
            catch (Exception e) { LoggerInstance.Error("Frontier Architect updater failed: " + e.Message); }
        }

        private void Run()
        {
            string gameRoot = MelonEnvironment.GameRootDirectory;
            string userData = MelonEnvironment.UserDataDirectory;
            var engine = new UpdateEngine(new WebRequestTransport(), gameRoot, userData, m => LoggerInstance.Msg(m));
            engine.CleanupOldFiles("Mods", "Plugins");
            engine.ApplyStaged();

            var settings = UpdaterSettings.Load(Path.Combine(Path.Combine(userData, "FrontierArchitect"), "updater.json"), BuildInfo.Repository);
            var installed = InstalledVersion(Path.Combine(gameRoot, ModFile.Replace('/', Path.DirectorySeparatorChar)));
            if (installed == null)
                LoggerInstance.Warning("Mods/FrontierArchitect.dll not found; the updater will install the latest release.");

            // Bounded wait: never hold up the game's start for long.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds * 3))))
            {
                var status = engine.CheckAndInstall(settings, installed, cts.Token);
                LoggerInstance.Msg("Frontier Architect " + (installed != null ? "v" + installed : "(not installed)") + ": " + status.Message);
            }
        }

        /// <summary>Reads the mod's version without loading it (so the file stays replaceable).</summary>
        private static SemVer InstalledVersion(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var v = AssemblyName.GetAssemblyName(path).Version;
                return new SemVer { Major = v.Major, Minor = v.Minor, Patch = Math.Max(0, v.Build) };
            }
            catch { return new SemVer(); }
        }
    }
}
