using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Updates
{
    /// <summary>One file of a release, as listed in the release's manifest.json asset.</summary>
    public sealed class ManifestFile
    {
        /// <summary>Install path relative to the game's Mono folder, e.g. "Mods/FrontierArchitect.dll".</summary>
        public string Path;
        /// <summary>Name of the GitHub release asset that holds the file.</summary>
        public string Asset;
        public string Sha256;
        public long Size;
    }

    /// <summary>
    /// manifest.json, published with every release by the release workflow. The updater trusts a
    /// downloaded file only when its SHA-256 matches the manifest.
    /// </summary>
    public sealed class ReleaseManifest
    {
        public string Name = "FrontierArchitect";
        public string Version;
        public string Tag;
        public string Notes = "";
        public string MinMelonLoader = "0.6.0";
        public readonly List<ManifestFile> Files = new List<ManifestFile>();

        public static ReleaseManifest FromJson(JObject o)
        {
            var m = new ReleaseManifest
            {
                Name = o.Str("name", "FrontierArchitect"),
                Version = o.Str("version"),
                Tag = o.Str("tag"),
                Notes = o.Str("notes", ""),
                MinMelonLoader = o.Str("min_melonloader", "0.6.0"),
            };
            foreach (var f in o.Arr("files").Objects())
            {
                var mf = new ManifestFile { Path = f.Str("path"), Asset = f.Str("asset"), Sha256 = (f.Str("sha256") ?? "").ToLowerInvariant(), Size = f.Long("size") };
                if (string.IsNullOrEmpty(mf.Path) || mf.Path.Contains("..") || System.IO.Path.IsPathRooted(mf.Path))
                    throw new InvalidDataException("Unsafe path in manifest: " + mf.Path);
                if (mf.Sha256.Length != 64) throw new InvalidDataException("Missing sha256 for " + mf.Path);
                m.Files.Add(mf);
            }
            if (string.IsNullOrEmpty(m.Version)) throw new InvalidDataException("Manifest has no version");
            return m;
        }

        public JObject ToJson()
        {
            var files = new JArray();
            foreach (var f in Files)
                files.Add(new JObject().Set("path", f.Path).Set("asset", f.Asset).Set("sha256", f.Sha256).Set("size", f.Size));
            return new JObject()
                .Set("name", Name).Set("version", Version).Set("tag", Tag)
                .Set("min_melonloader", MinMelonLoader).Set("notes", Notes).Set("files", files);
        }
    }

    /// <summary>UserData/FrontierArchitect/updater.json</summary>
    public sealed class UpdaterSettings
    {
        public bool AutoUpdate = true;
        /// <summary>"stable" (latest release) or "prerelease" (newest release including prereleases).</summary>
        public string Channel = "stable";
        public string Repository;
        /// <summary>
        /// Optional read-only GitHub token. Needed only while the repository is private: GitHub
        /// answers anonymous requests for a private repository's releases with 404.
        /// </summary>
        public string GitHubToken = "";
        public int TimeoutSeconds = 20;
        /// <summary>Minimum hours between two checks. 0 = check on every launch.</summary>
        public double CheckIntervalHours = 0;

        public static UpdaterSettings Load(string path, string defaultRepository)
        {
            var s = new UpdaterSettings { Repository = defaultRepository };
            try
            {
                if (File.Exists(path))
                {
                    var o = Json.ParseObject(File.ReadAllText(path));
                    s.AutoUpdate = o.Bool("auto_update", true);
                    s.Channel = o.Str("channel", "stable");
                    s.Repository = string.IsNullOrWhiteSpace(o.Str("repository")) ? defaultRepository : o.Str("repository").Trim();
                    s.GitHubToken = (o.Str("github_token", "") ?? "").Trim();
                    s.TimeoutSeconds = Math.Max(3, o.Int("timeout_seconds", 20));
                    s.CheckIntervalHours = Math.Max(0, o.Num("check_interval_hours", 0));
                    return s;
                }
            }
            catch { /* a broken file falls back to defaults and is rewritten below */ }
            s.Save(path);
            return s;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var o = new JObject()
                .Set("auto_update", AutoUpdate)
                .Set("channel", Channel)
                .Set("repository", Repository)
                .Set("github_token", GitHubToken)
                .Set("timeout_seconds", TimeoutSeconds)
                .Set("check_interval_hours", CheckIntervalHours)
                .Set("_help", "auto_update: install new releases at game start. channel: stable|prerelease. github_token: only needed while the GitHub repository is private (fine-grained token, read-only 'Contents' on that one repository).");
            File.WriteAllText(path, Json.Write(o, true));
        }
    }

    public enum UpdateState { NotChecked, UpToDate, Installed, Staged, Disabled, Error }

    /// <summary>UserData/FrontierArchitect/updates/status.json — written by the updater, shown by the mod.</summary>
    public sealed class UpdateStatus
    {
        public UpdateState State = UpdateState.NotChecked;
        public string InstalledVersion = "";
        public string LatestVersion = "";
        public string Message = "";
        public string Notes = "";
        public string CheckedAtUtc = "";

        public JObject ToJson() => new JObject()
            .Set("state", State.ToString())
            .Set("installed_version", InstalledVersion)
            .Set("latest_version", LatestVersion)
            .Set("message", Message)
            .Set("notes", Notes)
            .Set("checked_at_utc", CheckedAtUtc);

        public static UpdateStatus Load(string path)
        {
            var s = new UpdateStatus();
            try
            {
                if (!File.Exists(path)) return s;
                var o = Json.ParseObject(File.ReadAllText(path));
                UpdateState st;
                if (Enum.TryParse(o.Str("state", "NotChecked"), out st)) s.State = st;
                s.InstalledVersion = o.Str("installed_version", "");
                s.LatestVersion = o.Str("latest_version", "");
                s.Message = o.Str("message", "");
                s.Notes = o.Str("notes", "");
                s.CheckedAtUtc = o.Str("checked_at_utc", "");
            }
            catch { }
            return s;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Write(ToJson(), true));
        }
    }

    public static class Hashing
    {
        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text ?? ""));

        public static string Sha256File(string path) => File.Exists(path) ? Sha256Hex(File.ReadAllBytes(path)) : null;
    }
}
