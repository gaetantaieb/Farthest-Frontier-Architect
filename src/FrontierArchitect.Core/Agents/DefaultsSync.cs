using System;
using System.Collections.Generic;
using System.IO;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Updates;

namespace FrontierArchitect.Core.Agents
{
    public sealed class SyncReport
    {
        public readonly List<string> Installed = new List<string>();
        public readonly List<string> Updated = new List<string>();
        public readonly List<string> KeptPlayerEdits = new List<string>();

        public string Summary()
        {
            var parts = new List<string>();
            if (Installed.Count > 0) parts.Add(Installed.Count + " installed");
            if (Updated.Count > 0) parts.Add(Updated.Count + " updated");
            if (KeptPlayerEdits.Count > 0) parts.Add(KeptPlayerEdits.Count + " kept (your edits; new default saved as *.new-default)");
            return parts.Count == 0 ? "agent files up to date" : "agent files: " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Copies the default agent files shipped inside the mod DLL into UserData, without ever
    /// overwriting a file the player edited. A ledger records the hash of every default it wrote:
    /// a file still equal to that hash is untouched by the player and may be replaced by a newer
    /// default; an edited file is kept and the new default is written beside it as *.new-default.
    /// </summary>
    public static class DefaultsSync
    {
        public const string LedgerName = ".shipped.json";

        public static SyncReport Sync(IDictionary<string, string> defaults, string root)
        {
            var report = new SyncReport();
            Directory.CreateDirectory(root);
            string ledgerPath = Path.Combine(root, LedgerName);
            var ledger = new JObject();
            try { if (File.Exists(ledgerPath)) ledger = Json.ParseObject(File.ReadAllText(ledgerPath)); } catch { ledger = new JObject(); }

            foreach (var kv in defaults)
            {
                string rel = kv.Key.Replace('\\', '/');
                if (rel.Contains("..")) continue;
                string target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                string content = kv.Value.Replace("\r\n", "\n");
                string newHash = Hashing.Sha256Hex(content);
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                if (!File.Exists(target))
                {
                    File.WriteAllText(target, content);
                    ledger.Set(rel, newHash);
                    report.Installed.Add(rel);
                    continue;
                }
                string current = File.ReadAllText(target).Replace("\r\n", "\n");
                string currentHash = Hashing.Sha256Hex(current);
                if (currentHash == newHash) { ledger.Set(rel, newHash); continue; }
                string shipped = ledger.Str(rel);
                if (shipped != null && shipped == currentHash)
                {
                    File.WriteAllText(target, content); // player never touched it: take the new default
                    ledger.Set(rel, newHash);
                    report.Updated.Add(rel);
                }
                else
                {
                    string side = target + ".new-default";
                    if (!File.Exists(side) || Hashing.Sha256File(side) != newHash)
                    {
                        File.WriteAllText(side, content);
                        report.KeptPlayerEdits.Add(rel);
                    }
                }
            }
            File.WriteAllText(ledgerPath, Json.Write(ledger, true));
            return report;
        }
    }
}
