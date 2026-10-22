using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace VirgilPatch
{
    // Deliberately structured: raw logs, exception messages, usernames and environment values never enter the report.
    sealed class PatchDiagnostics
    {
        sealed class Event { public string Operation, Patch, From, To, Error; }
        readonly object gate = new object();
        readonly Queue<Event> events = new Queue<Event>();
        string commit;
        public void Catalog(Plan plan) { lock (gate) commit = Regex.IsMatch(plan.Commit ?? "", @"\A[0-9a-f]{7,40}\z") ? plan.Commit : "<local or unavailable>"; }
        public static string ErrorCode(Exception error)
        {
            if (error == null) return "none";
            string category = error is OperationCanceledException ? "cancelled" : error is UnauthorizedAccessException ? "access denied" : error is FileNotFoundException || error is DirectoryNotFoundException ? "file or directory missing" : error is InvalidDataException ? "invalid data or checksum" : error is IOException ? "I/O error" : error is System.Net.WebException ? "network error" : "operation failed";
            return category + " (HRESULT 0x" + error.HResult.ToString("X8") + ")";
        }
        public void Record(string operation, string patch = null, string from = null, string to = null, Exception error = null)
        {
            // Operation is a closed set of application-defined labels, never external text.
            if (!new[] { "detected folder", "planned rename", "rename started", "renamed", "archive duplicate started", "duplicate archived", "patch complete", "patch failed", "check failed", "update failed", "check cancelled", "update cancelled" }.Contains(operation)) operation = "operation";
            lock (gate) { if (events.Count >= 256) events.Dequeue(); events.Enqueue(new Event { Operation = operation, Patch = patch, From = from, To = to, Error = error == null ? null : ErrorCode(error) }); }
        }
        internal sealed class Redactor
        {
            readonly Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public static bool KnownPatch(string name) { return name != null && !name.Any(char.IsControl) && Regex.IsMatch(name, @"\A(?:!Chronicles-TexturePatch|!Chronicles-Kalimdor|Chronicles-Classic|Chronicles-Northrend|azeroth)(?:\s*\([0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(?:\.[0-9]{1,9})?\))?\z", RegexOptions.IgnoreCase); }
            string Part(string text)
            {
                if (KnownPatch(text) || new[] { "Epsilon", "_retail_", "Patches", "Wow.exe", "VirgilPatch-Migration-Backups" }.Contains(text, StringComparer.OrdinalIgnoreCase)) return text;
                string token; if (!parts.TryGetValue(text, out token)) parts[text] = token = "<folder-" + (parts.Count + 1) + ">"; return token;
            }
            public string Name(string text) { return Part(text ?? ""); }
            public string Path(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return "<not selected>";
                try {
                    string full = System.IO.Path.GetFullPath(value), root = System.IO.Path.GetPathRoot(full), token;
                    if (!roots.TryGetValue(root, out token)) roots[root] = token = "<location-" + (roots.Count + 1) + ">";
                    return token + "\\" + string.Join("\\", full.Substring(root.Length).Split(new[] {'\\','/'}, StringSplitOptions.RemoveEmptyEntries).Select(Part));
                } catch { return "<invalid path>"; }
            }
        }
        static string Version(object value) { string text = value as string; return Regex.IsMatch(text ?? "", @"\A[0-9]{1,9}(?:\.[0-9]{1,9}){0,3}\z") ? text : "<nonstandard or unknown>"; }
        public string Build(State state, Plan plan, bool includeRunningGame = true)
        {
            Event[] history; string revision; lock (gate) { history = events.ToArray(); revision = commit; }
            var redactor = new Redactor(); var text = new StringBuilder();
            text.AppendLine("Virgil updater diagnostics / format 1").AppendLine("Updater: " + SelfUpdate.CurrentVersion.ToString(3));
            text.AppendLine("Privacy: personal path components and drive/network names are replaced by report-local tokens. Tokens match only within this report. Raw logs and error messages are excluded.");
            text.AppendLine("Catalog commit: " + (revision ?? "<not checked this session>"));
            text.AppendLine("Selected Patches root: " + redactor.Path(state.patches_dir));
            if (plan != null) {
                text.AppendLine("Last checked root: " + redactor.Path(plan.TargetRoot));
                text.AppendLine("Plan: " + plan.Fetch.Count + " downloads; " + plan.PatchJsons.Count + " metadata updates; " + plan.Migrations.Count + " folder reconciliations; up to date = " + plan.UpToDate);
                foreach (string patch in plan.Patches) { string dest; text.AppendLine("Selected patch: " + redactor.Name(patch)); if (plan.Destinations.TryGetValue(patch,out dest)) text.AppendLine("  Destination: " + redactor.Path(dest));
                    FolderMigration m; if (plan.Migrations.TryGetValue(patch,out m)) foreach(string source in m.Sources) text.AppendLine("  Reconcile: " + redactor.Path(source) + " -> " + redactor.Path(m.Target)); }
            }
            if (!string.IsNullOrWhiteSpace(state.patches_dir)) {
                try {
                    Safety.NoLinks(state.patches_dir);
                    text.AppendLine("Root exists: " + Directory.Exists(state.patches_dir));
                    var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(state.patches_dir));
                    text.AppendLine("Wow.exe beside Patches: " + File.Exists(System.IO.Path.Combine(parent ?? state.patches_dir, "Wow.exe")));
                    text.AppendLine("Detected direct child folders (no asset scan):");
                    int count=0;
                    foreach (string dir in Directory.EnumerateDirectories(state.patches_dir)) {
                        if (++count > 256) { text.AppendLine("  <folder list truncated at 256>"); break; }
                        string name=System.IO.Path.GetFileName(dir); text.Append("  " + redactor.Path(dir));
                        try {
                            Safety.NoLinks(dir);
                            if (Redactor.KnownPatch(name)) {
                                string meta=Safety.Under(dir,"patch.json");
                                text.Append("; patch.json=" + File.Exists(meta));
                                if(File.Exists(meta) && new FileInfo(meta).Length<=Safety.MaxMetadataBytes) {
                                    var data=StrictJson.Read(File.ReadAllBytes(meta),(int)Safety.MaxMetadataBytes);object value;
                                    if(data.TryGetValue("version",out value))text.Append("; version="+Version(value));
                                    if(data.TryGetValue("name",out value) && value is string)text.Append("; display name="+redactor.Name((string)value));
                                }
                            } else text.Append("; unrelated or unrecognized name (redacted)");
                        } catch(Exception ex) { text.Append("; inspect="+ErrorCode(ex)); }
                        text.AppendLine();
                        if (Redactor.KnownPatch(name) && (name.StartsWith("!Chronicles-TexturePatch",StringComparison.OrdinalIgnoreCase) || name.StartsWith("Chronicles-Classic",StringComparison.OrdinalIgnoreCase)))
                            foreach(string asset in new[]{"arakkoa-lightforged/male/arakkoa_lightforged_male.m2","dbfilesclient/chrmodel.db2","dbfilesclient/chrcustomizationoption.db2"}) {
                                try { string file=Safety.Under(dir,asset); text.AppendLine("    "+asset+": "+(File.Exists(file)?"present ("+new FileInfo(file).Length+" bytes)":"missing")); } catch(Exception ex){text.AppendLine("    "+asset+": "+ErrorCode(ex));}
                            }
                    }
                    text.AppendLine("Nearby installation candidates (existence checks only):");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string ancestor=parent;
                    for(int level=0;level<3 && !string.IsNullOrEmpty(ancestor);level++,ancestor=System.IO.Path.GetDirectoryName(ancestor))
                        foreach(string suffix in new[]{@"_retail_\Patches",@"Epsilon\_retail_\Patches",@"Epsilon\Epsilon\_retail_\Patches"}) {
                            string candidate=System.IO.Path.Combine(ancestor,suffix); if(!seen.Add(candidate))continue;
                            try { Safety.NoLinks(candidate); if(Directory.Exists(candidate))text.AppendLine("  "+redactor.Path(candidate)+(string.Equals(System.IO.Path.GetFullPath(candidate),System.IO.Path.GetFullPath(state.patches_dir),StringComparison.OrdinalIgnoreCase)?" [selected]":" [other]")); } catch(Exception ex) { text.AppendLine("  "+redactor.Path(candidate)+"; inspect="+ErrorCode(ex)); }
                        }
                } catch(Exception ex) { text.AppendLine("Folder inspection: "+ErrorCode(ex)); }
            }
            if(includeRunningGame) {
                text.AppendLine("Running WoW locations (if accessible):");
                foreach(var process in Process.GetProcessesByName("Wow").Take(8))using(process)try{text.AppendLine("  "+redactor.Path(process.MainModule.FileName));}catch(Exception ex){text.AppendLine("  "+ErrorCode(ex));}
            }
            text.AppendLine("Session events (oldest first; maximum 256):");
            foreach(var e in history) {
                text.Append("  "+e.Operation);if(e.Patch!=null)text.Append("; patch="+redactor.Name(e.Patch));
                if(e.From!=null)text.Append("; from="+redactor.Path(e.From));if(e.To!=null)text.Append("; to="+redactor.Path(e.To));
                if(e.Error!=null)text.Append("; error="+e.Error);text.AppendLine();
            }
            text.AppendLine("Copying diagnostics does not download, repair, rename or enable game patches.");
            return text.ToString();
        }
    }
}
