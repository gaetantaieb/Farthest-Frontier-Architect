using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Updates;
using FrontierArchitect.Core.Yaml;

namespace FrontierArchitect.Core.Agents
{
    /// <summary>A loaded, validated agent: definition + prompt files + knowledge + traceability hash.</summary>
    public sealed class LoadedAgent
    {
        public AgentDefinition Definition;
        public string SystemTemplate = "";
        public string TaskTemplate = "";
        public readonly SortedDictionary<string, string> Knowledge = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string Profile = "default";
        public string Hash = "";
        public DateTime LoadedAtUtc;
        public readonly List<string> Warnings = new List<string>();

        public string KnowledgeText()
        {
            var sb = new StringBuilder();
            foreach (var kv in Knowledge) sb.Append("## ").Append(kv.Key).Append("\n\n").Append(kv.Value.Trim()).Append("\n\n");
            return sb.ToString();
        }
    }

    public sealed class AgentLoadResult
    {
        public LoadedAgent Agent;      // the agent to use (new one, or the previous good one)
        public bool UsedPrevious;      // true when the files on disk failed validation
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Reads UserData/FrontierArchitect/agents/&lt;name&gt;/ at the start of every run, so edits apply to
    /// the next run without restarting. A file set that fails validation never replaces the working
    /// version; every newly accepted version is copied to versions/.
    /// </summary>
    public sealed class AgentLoader
    {
        public readonly string Root;
        public readonly string Name;
        private LoadedAgent _lastGood;
        private readonly bool _keepVersions;

        public AgentLoader(string agentsRoot, string name) : this(agentsRoot, name, true) { }

        private AgentLoader(string agentsRoot, string name, bool keepVersions)
        {
            Root = Path.Combine(agentsRoot, name);
            Name = name;
            _keepVersions = keepVersions;
        }

        public string AgentYamlPath => Path.Combine(Root, "agent.yaml");
        public string ProfilesDir => Path.Combine(Root, "profiles");
        public string VersionsDir => Path.Combine(Root, "versions");
        public string KnowledgeDir => Path.Combine(Root, "knowledge");
        public LoadedAgent Current => _lastGood;

        public List<string> Profiles()
        {
            var list = new List<string> { "default" };
            if (Directory.Exists(ProfilesDir))
                foreach (var f in Directory.GetFiles(ProfilesDir, "*.yaml").OrderBy(x => x, StringComparer.Ordinal))
                    list.Add(Path.GetFileNameWithoutExtension(f));
            return list;
        }

        /// <summary>Files the in-game editor offers (relative paths).</summary>
        public List<string> EditableFiles()
        {
            var list = new List<string> { "agent.yaml", "system.md", "task.md" };
            if (File.Exists(Path.Combine(Root, "tools.md"))) list.Add("tools.md");
            if (Directory.Exists(KnowledgeDir))
                foreach (var f in Directory.GetFiles(KnowledgeDir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
                    list.Add("knowledge/" + Path.GetFileName(f));
            if (Directory.Exists(ProfilesDir))
                foreach (var f in Directory.GetFiles(ProfilesDir, "*.yaml").OrderBy(x => x, StringComparer.Ordinal))
                    list.Add("profiles/" + Path.GetFileName(f));
            return list;
        }

        public string ReadFile(string rel)
        {
            string p = Resolve(rel);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }

        public void WriteFile(string rel, string content)
        {
            string p = Resolve(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content.Replace("\r\n", "\n"));
        }

        private string Resolve(string rel)
        {
            if (rel.Contains("..") || Path.IsPathRooted(rel)) throw new ArgumentException("bad path");
            return Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public AgentLoadResult Load(string profile)
        {
            var result = new AgentLoadResult();
            var agent = new LoadedAgent { Profile = string.IsNullOrEmpty(profile) ? "default" : profile, LoadedAtUtc = DateTime.UtcNow };
            var errors = result.Errors;
            var hashInput = new StringBuilder();
            try
            {
                if (!File.Exists(AgentYamlPath)) errors.Add("agent.yaml not found in " + Root);
                else
                {
                    string yaml = File.ReadAllText(AgentYamlPath);
                    hashInput.Append("agent.yaml\n").Append(yaml).Append('\n');
                    JObject doc = null;
                    try { doc = MiniYaml.ParseObject(yaml); }
                    catch (Exception e) { errors.Add("agent.yaml: " + e.Message); }

                    if (doc != null && agent.Profile != "default")
                    {
                        string pf = Path.Combine(ProfilesDir, agent.Profile + ".yaml");
                        if (!File.Exists(pf)) errors.Add("profile '" + agent.Profile + "' not found");
                        else
                        {
                            string ptext = File.ReadAllText(pf);
                            hashInput.Append("profiles/").Append(agent.Profile).Append(".yaml\n").Append(ptext).Append('\n');
                            try { DeepMerge(doc, MiniYaml.ParseObject(ptext)); }
                            catch (Exception e) { errors.Add("profiles/" + agent.Profile + ".yaml: " + e.Message); }
                        }
                    }
                    if (doc != null)
                    {
                        var def = AgentDefinition.FromYaml(doc, errors, agent.Warnings);
                        agent.Definition = def;
                    }
                }

                agent.SystemTemplate = ReadRequired("system.md", errors, hashInput);
                agent.TaskTemplate = ReadRequired("task.md", errors, hashInput);
                if (agent.TaskTemplate.Length > 0 && agent.TaskTemplate.IndexOf("{{snapshot_json}}", StringComparison.Ordinal) < 0)
                    errors.Add("task.md must contain {{snapshot_json}}");
                if (Directory.Exists(KnowledgeDir))
                    foreach (var f in Directory.GetFiles(KnowledgeDir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
                    {
                        string text = File.ReadAllText(f);
                        agent.Knowledge[Path.GetFileName(f)] = text;
                        hashInput.Append("knowledge/").Append(Path.GetFileName(f)).Append('\n').Append(text).Append('\n');
                    }
                foreach (var w in Template.UnknownVariables(agent.SystemTemplate + "\n" + agent.TaskTemplate))
                    agent.Warnings.Add("unknown template variable {{" + w + "}}");
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
            }

            if (errors.Count > 0)
            {
                result.Agent = _lastGood ?? LatestValidVersion(profile);
                result.UsedPrevious = result.Agent != null;
                return result;
            }

            agent.Hash = Hashing.Sha256Hex(hashInput.ToString()).Substring(0, 12);
            if (_keepVersions && (_lastGood == null || _lastGood.Hash != agent.Hash)) SnapshotVersion(agent);
            _lastGood = agent;
            result.Agent = agent;
            return result;
        }

        private string ReadRequired(string rel, List<string> errors, StringBuilder hash)
        {
            string p = Path.Combine(Root, rel);
            if (!File.Exists(p)) { errors.Add(rel + " not found"); return ""; }
            string t = File.ReadAllText(p);
            if (t.Trim().Length == 0) errors.Add(rel + " is empty");
            hash.Append(rel).Append('\n').Append(t).Append('\n');
            return t;
        }

        /// <summary>versions/&lt;utc&gt;_&lt;hash&gt;/ keeps a copy of every accepted version.</summary>
        private void SnapshotVersion(LoadedAgent a)
        {
            try
            {
                string dir = Path.Combine(VersionsDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + a.Hash);
                if (Directory.GetDirectories(VersionsDir.EnsureDir(), "*_" + a.Hash).Length > 0) return;
                Directory.CreateDirectory(dir);
                foreach (var rel in EditableFiles())
                {
                    string src = Resolve(rel);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
                File.WriteAllText(Path.Combine(dir, "profile.txt"), a.Profile);
            }
            catch { /* history is best effort */ }
        }

        /// <summary>At start-up with broken files: load the newest version that still validates.</summary>
        private LoadedAgent LatestValidVersion(string profile)
        {
            try
            {
                if (!Directory.Exists(VersionsDir)) return null;
                foreach (var dir in Directory.GetDirectories(VersionsDir).OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    var loader = new AgentLoader(Path.GetDirectoryName(dir), Path.GetFileName(dir), false);
                    var r = loader.Load(profile);
                    if (r.Errors.Count == 0 && r.Agent != null) return r.Agent;
                }
            }
            catch { }
            return null;
        }

        public void SaveProfile(string name, JObject overrides)
        {
            if (!Regex.IsMatch(name ?? "", "^[A-Za-z0-9 _-]{1,40}$")) throw new ArgumentException("Profile names use letters, digits, space, - and _");
            Directory.CreateDirectory(ProfilesDir);
            File.WriteAllText(Path.Combine(ProfilesDir, name + ".yaml"),
                "# Profile '" + name + "': overrides of agent.yaml (only the keys listed here change).\n" + MiniYaml.Write(overrides));
        }

        public static void DeepMerge(JObject target, JObject overrides)
        {
            foreach (var kv in overrides)
            {
                if (kv.Value is JObject o && target[kv.Key] is JObject t) DeepMerge(t, o);
                else target.Set(kv.Key, Json.DeepClone(kv.Value));
            }
        }
    }

    internal static class PathExt
    {
        public static string EnsureDir(this string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>{{variable}} substitution for system.md and task.md.</summary>
    public static class Template
    {
        public static readonly HashSet<string> KnownVariables = new HashSet<string>
        {
            "ambition", "ambition_name", "mode", "season", "year", "locked_ids", "player_note", "snapshot_json",
            "max_relocations", "offline_cap", "min_gain", "town_tier", "food_threshold", "firewood_threshold",
            "detour_limit", "phase_max_actions", "allowed_ops", "weights", "settlement", "seed_request"
        };

        private static readonly Regex Var = new Regex(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}");

        public static string Render(string template, IDictionary<string, string> vars)
        {
            return Var.Replace(template ?? "", m =>
            {
                string v;
                return vars.TryGetValue(m.Groups[1].Value, out v) ? v ?? "" : m.Value;
            });
        }

        public static IEnumerable<string> UnknownVariables(string template)
        {
            foreach (Match m in Var.Matches(template ?? ""))
                if (!KnownVariables.Contains(m.Groups[1].Value)) yield return m.Groups[1].Value;
        }
    }
}
