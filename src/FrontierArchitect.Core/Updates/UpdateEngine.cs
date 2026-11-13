using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Net;

namespace FrontierArchitect.Core.Updates
{
    /// <summary>
    /// Checks GitHub Releases for a newer version and installs it.
    ///
    /// Flow (run by the updater plugin before MelonLoader loads any mod):
    ///  1. Apply files staged by an earlier run (a file that was locked last time).
    ///  2. Ask GitHub for the latest release; read its manifest.json asset.
    ///  3. If the manifest version is newer than the installed mod, download every file,
    ///     check SHA-256 against the manifest, then swap them into place.
    ///  4. A file that cannot be replaced now is staged and applied on the next launch.
    /// </summary>
    public sealed class UpdateEngine
    {
        public const string ManifestAssetName = "manifest.json";

        private readonly IHttpTransport _http;
        private readonly Action<string> _log;
        private readonly string _gameRoot;
        private readonly string _stateDir;

        public UpdateEngine(IHttpTransport http, string gameRoot, string userDataDir, Action<string> log)
        {
            _http = http;
            _gameRoot = gameRoot;
            _stateDir = Path.Combine(Path.Combine(userDataDir, "FrontierArchitect"), "updates");
            _log = log ?? (_ => { });
        }

        public string StatusPath => Path.Combine(_stateDir, "status.json");
        public string StagedDir => Path.Combine(_stateDir, "staged");
        public string LastCheckPath => Path.Combine(_stateDir, "last_check.txt");

        // ----------------------------------------------------------------- staged files

        public int ApplyStaged()
        {
            string manifestPath = Path.Combine(StagedDir, ManifestAssetName);
            if (!File.Exists(manifestPath)) return 0;
            int applied = 0;
            try
            {
                var manifest = ReleaseManifest.FromJson(Json.ParseObject(File.ReadAllText(manifestPath)));
                bool allDone = true;
                foreach (var f in manifest.Files)
                {
                    string staged = Path.Combine(StagedDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(staged)) continue;
                    var bytes = File.ReadAllBytes(staged);
                    if (Hashing.Sha256Hex(bytes) != f.Sha256) { _log("Staged file " + f.Path + " is corrupt; discarding."); File.Delete(staged); continue; }
                    if (TryInstall(f.Path, bytes)) { File.Delete(staged); applied++; }
                    else allDone = false;
                }
                if (allDone) TryDeleteDirectory(StagedDir);
                if (applied > 0) _log("Applied " + applied + " staged file(s) from v" + manifest.Version + ".");
            }
            catch (Exception e)
            {
                _log("Could not apply staged update: " + e.Message);
            }
            return applied;
        }

        // ----------------------------------------------------------------- check + install

        public UpdateStatus CheckAndInstall(UpdaterSettings settings, SemVer installed, CancellationToken cancel)
        {
            var status = new UpdateStatus
            {
                InstalledVersion = installed?.ToString() ?? "",
                CheckedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            try
            {
                if (!settings.AutoUpdate)
                {
                    status.State = UpdateState.Disabled;
                    status.Message = "Automatic updates are off (updater.json: auto_update).";
                    return Save(status);
                }
                if (!DueForCheck(settings))
                {
                    var previous = UpdateStatus.Load(StatusPath);
                    previous.InstalledVersion = status.InstalledVersion;
                    return previous;
                }
                File.WriteAllText(EnsureDir(LastCheckPath), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                var release = FetchRelease(settings, cancel, out string error);
                if (release == null)
                {
                    status.State = UpdateState.Error;
                    status.Message = error;
                    return Save(status);
                }
                var manifestAsset = FindAsset(release, ManifestAssetName);
                if (manifestAsset == null)
                {
                    status.State = UpdateState.Error;
                    status.Message = "Release " + release.Str("tag_name") + " has no manifest.json.";
                    return Save(status);
                }
                var manifestBytes = Download(manifestAsset, settings, cancel, out error);
                if (manifestBytes == null)
                {
                    status.State = UpdateState.Error;
                    status.Message = "Could not download manifest.json: " + error;
                    return Save(status);
                }
                var manifest = ReleaseManifest.FromJson(Json.ParseObject(System.Text.Encoding.UTF8.GetString(manifestBytes)));
                manifest.Notes = string.IsNullOrEmpty(manifest.Notes) ? (release.Str("body", "") ?? "") : manifest.Notes;
                status.LatestVersion = manifest.Version;
                status.Notes = Truncate(manifest.Notes, 2000);

                SemVer latest;
                if (!SemVer.TryParse(manifest.Version, out latest))
                {
                    status.State = UpdateState.Error;
                    status.Message = "Manifest version is not valid: " + manifest.Version;
                    return Save(status);
                }
                if (installed != null && !latest.IsNewerThan(installed))
                {
                    status.State = UpdateState.UpToDate;
                    status.Message = installed.IsNewerThan(latest)
                        ? "Installed v" + installed + " is newer than the latest release v" + latest + "."
                        : "Up to date (v" + installed + ").";
                    return Save(status);
                }

                // Download everything first: nothing is replaced unless the whole release verified.
                var payload = new List<KeyValuePair<ManifestFile, byte[]>>();
                foreach (var f in manifest.Files)
                {
                    if (FileMatches(f)) continue;
                    var asset = FindAsset(release, f.Asset);
                    if (asset == null) throw new InvalidDataException("Release is missing asset " + f.Asset);
                    var bytes = Download(asset, settings, cancel, out error);
                    if (bytes == null) throw new IOException("Download of " + f.Asset + " failed: " + error);
                    string sha = Hashing.Sha256Hex(bytes);
                    if (sha != f.Sha256) throw new InvalidDataException("Checksum mismatch for " + f.Asset + " (expected " + f.Sha256 + ", got " + sha + ")");
                    payload.Add(new KeyValuePair<ManifestFile, byte[]>(f, bytes));
                }

                int installedCount = 0, stagedCount = 0;
                foreach (var kv in payload)
                {
                    if (TryInstall(kv.Key.Path, kv.Value)) installedCount++;
                    else { Stage(kv.Key, kv.Value); stagedCount++; }
                }
                if (stagedCount > 0)
                    File.WriteAllText(EnsureDir(Path.Combine(StagedDir, ManifestAssetName)), Json.Write(manifest.ToJson(), true));

                status.State = stagedCount > 0 ? UpdateState.Staged : UpdateState.Installed;
                status.Message = stagedCount > 0
                    ? "v" + latest + " downloaded; " + stagedCount + " file(s) finish installing on the next launch."
                    : "Updated to v" + latest + ".";
                _log(status.Message);
                return Save(status);
            }
            catch (OperationCanceledException)
            {
                status.State = UpdateState.Error;
                status.Message = "Update check timed out.";
                return Save(status);
            }
            catch (Exception e)
            {
                status.State = UpdateState.Error;
                status.Message = e.Message;
                _log("Update failed: " + e.Message);
                return Save(status);
            }
        }

        private bool DueForCheck(UpdaterSettings s)
        {
            if (s.CheckIntervalHours <= 0) return true;
            try
            {
                if (!File.Exists(LastCheckPath)) return true;
                var last = DateTime.Parse(File.ReadAllText(LastCheckPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                return (DateTime.UtcNow - last).TotalHours >= s.CheckIntervalHours;
            }
            catch { return true; }
        }

        private UpdateStatus Save(UpdateStatus s)
        {
            try { s.Save(StatusPath); } catch (Exception e) { _log("Could not write update status: " + e.Message); }
            return s;
        }

        // ----------------------------------------------------------------- GitHub

        private HttpRequest Api(string url, UpdaterSettings s)
        {
            var r = new HttpRequest { Url = url, TimeoutMs = s.TimeoutSeconds * 1000, ReadTimeoutMs = s.TimeoutSeconds * 1000 }
                .Header("User-Agent", "FrontierArchitect-Updater")
                .Header("Accept", "application/vnd.github+json")
                .Header("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(s.GitHubToken)) r.Header("Authorization", "Bearer " + s.GitHubToken);
            return r;
        }

        internal JObject FetchRelease(UpdaterSettings s, CancellationToken cancel, out string error)
        {
            error = null;
            string baseUrl = "https://api.github.com/repos/" + s.Repository;
            bool pre = string.Equals(s.Channel, "prerelease", StringComparison.OrdinalIgnoreCase);
            var resp = _http.Send(Api(baseUrl + (pre ? "/releases?per_page=10" : "/releases/latest"), s), null, cancel);
            if (resp.TransportError != null) { error = "GitHub unreachable: " + resp.TransportError; return null; }
            if (resp.Status == 404)
            {
                error = string.IsNullOrEmpty(s.GitHubToken)
                    ? "No release found. If the repository is private, add a read-only token to updater.json (github_token)."
                    : "No release found (or the token cannot read " + s.Repository + ").";
                return null;
            }
            if (resp.Status == 401 || resp.Status == 403) { error = "GitHub refused the request (" + resp.Status + "). Check github_token in updater.json."; return null; }
            if (!resp.Ok) { error = "GitHub answered " + resp.Status; return null; }
            var parsed = Json.Parse(resp.Body);
            if (!pre) return parsed as JObject;
            foreach (var r in (parsed as JArray).Objects())
                if (!r.Bool("draft")) return r;
            error = "No published release found.";
            return null;
        }

        private static JObject FindAsset(JObject release, string name)
        {
            foreach (var a in release.Arr("assets").Objects())
                if (string.Equals(a.Str("name"), name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        private byte[] Download(JObject asset, UpdaterSettings s, CancellationToken cancel, out string error)
        {
            error = null;
            HttpResponse resp;
            if (!string.IsNullOrEmpty(s.GitHubToken))
            {
                // Private repo: fetch through the API with the token, then follow the redirect to the
                // storage host WITHOUT the token.
                var req = Api(asset.Str("url"), s);
                req.Headers.RemoveAll(h => h.Key == "Accept");
                req.Header("Accept", "application/octet-stream");
                req.AllowRedirect = false;
                req.BinaryBody = true;
                resp = _http.Send(req, null, cancel);
                if (resp.Status >= 300 && resp.Status < 400 && resp.Header("Location") != null)
                {
                    var follow = new HttpRequest { Url = resp.Header("Location"), BinaryBody = true, TimeoutMs = s.TimeoutSeconds * 1000 * 3 }
                        .Header("User-Agent", "FrontierArchitect-Updater");
                    resp = _http.Send(follow, null, cancel);
                }
            }
            else
            {
                var req = new HttpRequest { Url = asset.Str("browser_download_url"), BinaryBody = true, TimeoutMs = s.TimeoutSeconds * 1000 * 3 }
                    .Header("User-Agent", "FrontierArchitect-Updater");
                resp = _http.Send(req, null, cancel);
            }
            if (resp.TransportError != null) { error = resp.TransportError; return null; }
            if (!resp.Ok) { error = "HTTP " + resp.Status; return null; }
            return resp.BodyBytes ?? System.Text.Encoding.UTF8.GetBytes(resp.Body ?? "");
        }

        // ----------------------------------------------------------------- files

        private string Target(string relPath) => Path.Combine(_gameRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        private bool FileMatches(ManifestFile f) => Hashing.Sha256File(Target(f.Path)) == f.Sha256;

        /// <summary>
        /// Replaces a file. A DLL that is already loaded may be locked on Windows; a locked file is
        /// renamed aside first (Windows allows renaming a file in use), and only if that fails is
        /// the update staged for the next launch.
        /// </summary>
        internal bool TryInstall(string relPath, byte[] bytes)
        {
            string target = Target(relPath);
            string tmp = target + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(tmp, bytes);
                if (!File.Exists(target)) { File.Move(tmp, target); return true; }
                try
                {
                    File.Copy(tmp, target, true);
                    File.Delete(tmp);
                    return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                string aside = target + ".old";
                try
                {
                    if (File.Exists(aside)) File.Delete(aside);
                }
                catch { aside = target + "." + DateTime.UtcNow.Ticks + ".old"; }
                File.Move(target, aside);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception e)
            {
                _log("Could not replace " + relPath + " now (" + e.Message + ").");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        private void Stage(ManifestFile f, byte[] bytes)
        {
            string staged = Path.Combine(StagedDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(EnsureDir(staged), bytes);
        }

        /// <summary>Deletes "*.old" files left by a rename-aside swap on a previous launch.</summary>
        public void CleanupOldFiles(params string[] relDirs)
        {
            foreach (var d in relDirs)
            {
                try
                {
                    string dir = Target(d);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "FrontierArchitect*.old")) { try { File.Delete(f); } catch { } }
                }
                catch { }
            }
        }

        private static string EnsureDir(string filePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            return filePath;
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static string Truncate(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
