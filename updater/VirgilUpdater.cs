// VirgilPatch Updater - keeps an Epsilon patch folder in sync with the VirgilPatch GitHub repository.
//
// Only files that are new or different are downloaded (each verified against its git blob SHA-1 from index.json);
// patch.json is written last, files the previous version installed and the new one dropped are removed.
// A fresh install (or a very large update) downloads the repository archive once instead of thousands of files.
//
// Build (no SDK needed, .NET Framework 4.8 ships with Windows 10/11):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:VirgilUpdater.exe
//     /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
//     /win32icon:virgil.ico VirgilUpdater.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("VirgilPatch Updater")]
[assembly: System.Reflection.AssemblyProduct("VirgilPatch")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace VirgilPatch
{
    static class Config
    {
        public const string Owner = "karakkquickcoin-byte";
        public const string Repo = "VirgilPatch";
        public const string Branch = "main";
        public const string UserAgent = "VirgilPatchUpdater/1.0";
        public static string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirgilPatch");
    }

    // ------------------------------------------------------------------ where the files come from
    interface ISource
    {
        string ResolveCommit();
        byte[] Get(string commit, string repoPath);                                  // small text files
        void Download(string commit, string repoPath, string dest, Action<long> progress);
        string ArchiveUrl(string commit);                                            // null = no archive
        string Describe();
    }

    class GitHubSource : ISource
    {
        public string Describe() { return "github.com/" + Config.Owner + "/" + Config.Repo; }

        static HttpWebRequest Req(string url, string accept)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = Config.UserAgent;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 120000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (accept != null) r.Accept = accept;
            return r;
        }

        static string Text(string url, string accept)
        {
            using (var resp = (HttpWebResponse)Req(url, accept).GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }

        public string ResolveCommit()
        {
            try
            {
                var sha = Text("https://api.github.com/repos/" + Config.Owner + "/" + Config.Repo + "/commits/" + Config.Branch,
                               "application/vnd.github.sha").Trim();
                if (Regex.IsMatch(sha, "^[0-9a-f]{40}$")) return sha;
            }
            catch (WebException) { }                      // API rate limit etc. -> ask git's smart-http endpoint instead
            var refs = Text("https://github.com/" + Config.Owner + "/" + Config.Repo + ".git/info/refs?service=git-upload-pack", null);
            var m = Regex.Match(refs, "([0-9a-f]{40}) refs/heads/" + Regex.Escape(Config.Branch));
            if (!m.Success) throw new Exception("could not find the latest version on GitHub");
            return m.Groups[1].Value;
        }

        public static string RawUrl(string commit, string repoPath)
        {
            var parts = repoPath.Split('/').Select(Uri.EscapeDataString);
            return "https://raw.githubusercontent.com/" + Config.Owner + "/" + Config.Repo + "/" + commit + "/" + string.Join("/", parts);
        }

        public byte[] Get(string commit, string repoPath)
        {
            using (var resp = (HttpWebResponse)Req(RawUrl(commit, repoPath), null).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public void Download(string commit, string repoPath, string dest, Action<long> progress)
        {
            Fetch(RawUrl(commit, repoPath), dest, progress);
        }

        public static void Fetch(string url, string dest, Action<long> progress)
        {
            using (var resp = (HttpWebResponse)Req(url, null).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    if (progress != null) progress(n);
                }
            }
        }

        public string ArchiveUrl(string commit)
        {
            return "https://codeload.github.com/" + Config.Owner + "/" + Config.Repo + "/zip/" + commit;
        }
    }

    // a local copy of the repository - used to test the updater without touching GitHub
    class FolderSource : ISource
    {
        readonly string root;
        public FolderSource(string root) { this.root = root; }
        public string Describe() { return root; }
        public string ResolveCommit() { return "local"; }
        string P(string repoPath) { return Path.Combine(root, repoPath.Replace('/', '\\')); }
        public byte[] Get(string commit, string repoPath) { return File.ReadAllBytes(P(repoPath)); }
        public void Download(string commit, string repoPath, string dest, Action<long> progress)
        {
            File.Copy(P(repoPath), dest, true);
            if (progress != null) progress(new FileInfo(dest).Length);
        }
        public string ArchiveUrl(string commit) { return null; }
    }

    // ------------------------------------------------------------------ the update itself
    class FileInfoEntry
    {
        public string Name;     // path inside the patch folder, forward slashes
        public long Size;
        public string Sha;
    }

    class Plan
    {
        public string Commit;
        public string PatchFolder;
        public string RepoDir;
        public string Published;
        public List<FileInfoEntry> Fetch = new List<FileInfoEntry>();
        public List<string> Remove = new List<string>();
        public FileInfoEntry PatchJson;
        public bool PatchJsonChanged;
        public long FetchBytes { get { return Fetch.Sum(f => f.Size); } }
        public long TotalBytes;
        public int TotalFiles;
        public Dictionary<string, string> Installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool UpToDate { get { return Fetch.Count == 0 && Remove.Count == 0 && !PatchJsonChanged; } }
    }

    class State
    {
        public string patches_dir;
        public string commit;
        public string published;
        public Dictionary<string, object[]> hashes = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string FilePath { get { return Path.Combine(Config.StateDir, "state.json"); } }

        public static State Load()
        {
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath));
                var s = new State();
                object v;
                if (d.TryGetValue("patches_dir", out v)) s.patches_dir = v as string;
                if (d.TryGetValue("commit", out v)) s.commit = v as string;
                if (d.TryGetValue("published", out v)) s.published = v as string;
                if (d.TryGetValue("hashes", out v) && v is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)v)
                    {
                        var arr = kv.Value as object[];
                        if (arr == null && kv.Value is System.Collections.ArrayList) arr = ((System.Collections.ArrayList)kv.Value).ToArray();
                        if (arr != null && arr.Length == 3) s.hashes[kv.Key] = arr;
                    }
                if (d.TryGetValue("installed", out v) && v is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)v) s.installed[kv.Key] = kv.Value as string;
                return s;
            }
            catch { return new State(); }
        }

        public void Save()
        {
            Directory.CreateDirectory(Config.StateDir);
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, js.Serialize(new Dictionary<string, object> {
                { "patches_dir", patches_dir }, { "commit", commit }, { "published", published },
                { "hashes", hashes }, { "installed", installed } }));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
    }

    class Updater
    {
        public Action<string> Log = delegate { };
        public Action<long, long> Progress = delegate { };       // done, total
        readonly ISource src;
        readonly State state;
        public Updater(ISource src, State state) { this.src = src; this.state = state; }

        public static string BlobSha(string path)
        {
            using (var sha = SHA1.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            {
                var head = Encoding.ASCII.GetBytes("blob " + f.Length + "\0");
                sha.TransformBlock(head, 0, head.Length, null, 0);
                var buf = new byte[1 << 20];
                int n;
                while ((n = f.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        string CachedSha(string path)
        {
            var fi = new FileInfo(path);
            object[] hit;
            if (state.hashes.TryGetValue(path, out hit) && Convert.ToInt64(hit[0]) == fi.Length &&
                Convert.ToString(hit[1]) == fi.LastWriteTimeUtc.Ticks.ToString())
                return (string)hit[2];
            var s = BlobSha(path);
            state.hashes[path] = new object[] { fi.Length, fi.LastWriteTimeUtc.Ticks.ToString(), s };
            return s;
        }

        static FileInfoEntry Entry(string name, object o)
        {
            var d = (Dictionary<string, object>)o;
            return new FileInfoEntry { Name = name, Size = Convert.ToInt64(d["size"]), Sha = (string)d["sha"] };
        }

        public string Local(Plan p, string name)
        {
            return Path.Combine(state.patches_dir, p.PatchFolder, name.Replace('/', '\\'));
        }

        public Plan Check()
        {
            Log("Looking for the latest version on " + src.Describe() + " ...");
            var p = new Plan { Commit = src.ResolveCommit() };
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(src.Get(p.Commit, "index.json")));
            p.PatchFolder = (string)index["patch_folder"];
            p.RepoDir = index.ContainsKey("repo_dir") ? (string)index["repo_dir"] : "patch";
            p.Published = index.ContainsKey("published") ? Convert.ToString(index["published"]) : "";
            p.PatchJson = Entry("patch.json", index["patch_json"]);
            var files = ((Dictionary<string, object>)index["files"]).Select(kv => Entry(kv.Key, kv.Value)).ToList();
            p.TotalFiles = files.Count;
            p.TotalBytes = files.Sum(f => f.Size);
            Log(string.Format("Latest version: {0} ({1}), {2} files, {3:0.00} GB", p.Commit.Substring(0, Math.Min(7, p.Commit.Length)),
                              p.Published, p.TotalFiles, p.TotalBytes / 1e9));
            Log("Checking your files in " + Path.Combine(state.patches_dir, p.PatchFolder) + " ...");
            long done = 0, total = p.TotalBytes;
            foreach (var f in files)
            {
                var path = Local(p, f.Name);
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != f.Size || CachedSha(path) != f.Sha) p.Fetch.Add(f);
                p.Installed[f.Name] = f.Sha;
                done += f.Size;
                Progress(done, total);
            }
            var pj = Local(p, "patch.json");
            p.PatchJsonChanged = !File.Exists(pj) || CachedSha(pj) != p.PatchJson.Sha;
            p.Installed["patch.json"] = p.PatchJson.Sha;
            // files an earlier update installed that the new version no longer has (left alone if you changed them)
            foreach (var kv in state.installed)
                if (!p.Installed.ContainsKey(kv.Key))
                {
                    var path = Local(p, kv.Key);
                    if (File.Exists(path) && CachedSha(path) == kv.Value) p.Remove.Add(kv.Key);
                }
            state.Save();
            return p;
        }

        void Place(string tmp, string dest, string sha)
        {
            var got = BlobSha(tmp);
            if (got != sha) { File.Delete(tmp); throw new Exception("checksum mismatch for " + Path.GetFileName(dest) + " (the patch may be mid-publish; try again in a few minutes)"); }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            var fi = new FileInfo(dest);
            state.hashes[dest] = new object[] { fi.Length, fi.LastWriteTimeUtc.Ticks.ToString(), sha };
        }

        public void Apply(Plan p, CancellationToken cancel)
        {
            var target = Path.Combine(state.patches_dir, p.PatchFolder);
            Directory.CreateDirectory(target);
            long total = p.FetchBytes, done = 0;
            var pending = new List<FileInfoEntry>(p.Fetch);
            var archive = src.ArchiveUrl(p.Commit);
            if (archive != null && pending.Count > 0 && (pending.Count > 400 || total > 0.4 * p.TotalBytes))
            {
                try { pending = FromArchive(p, archive, pending, cancel); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log("Archive download failed (" + ex.Message + "), downloading file by file instead."); }
                done = total - pending.Sum(f => f.Size);
            }
            if (pending.Count > 0)
            {
                Log(string.Format("Downloading {0} files ({1:0.0} MB) ...", pending.Count, pending.Sum(f => f.Size) / 1e6));
                var errors = new List<string>();
                var opts = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel };
                Parallel.ForEach(pending, opts, f =>
                {
                    var dest = Local(p, f.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    var tmp = dest + ".vtmp";
                    for (int attempt = 1; ; attempt++)
                    {
                        long got = 0;
                        try
                        {
                            src.Download(p.Commit, p.RepoDir + "/" + f.Name, tmp, n => { got += n; Progress(Interlocked.Add(ref done, n), total); });
                            lock (state) Place(tmp, dest, f.Sha);
                            break;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Add(ref done, -got);
                            var we = ex as WebException;
                            var code = we != null && we.Response is HttpWebResponse ? (int)((HttpWebResponse)we.Response).StatusCode : 0;
                            if (attempt >= 4 || ex is IOException && !(ex is WebException))
                            {
                                lock (errors) errors.Add(f.Name + ": " + ex.Message);
                                break;
                            }
                            Thread.Sleep(code == 429 || code == 403 ? 30000 * attempt : 2000 * attempt);
                        }
                    }
                });
                if (errors.Count > 0)
                {
                    state.Save();
                    throw new Exception(errors.Count + " files failed, e.g.\r\n  " + string.Join("\r\n  ", errors.Take(5)) +
                                        "\r\n(If a file is in use, close WoW and run the update again - finished files are kept.)");
                }
            }
            // patch.json last: until now the game still sees the previous file list
            if (p.PatchJsonChanged)
            {
                var dest = Local(p, "patch.json");
                var tmp = dest + ".vtmp";
                File.WriteAllBytes(tmp, src.Get(p.Commit, p.RepoDir + "/patch.json"));
                Place(tmp, dest, p.PatchJson.Sha);
            }
            foreach (var name in p.Remove)
            {
                try { File.Delete(Local(p, name)); Log("removed " + name); }
                catch (IOException ex) { Log("could not remove " + name + ": " + ex.Message); }
            }
            state.commit = p.Commit;
            state.published = p.Published;
            state.installed = p.Installed;
            state.Save();
            Log("Done - you have the version from " + p.Published + ".");
        }

        List<FileInfoEntry> FromArchive(Plan p, string url, List<FileInfoEntry> pending, CancellationToken cancel)
        {
            var zipPath = Path.Combine(state.patches_dir, "VirgilPatch_download.zip.part");
            Log(string.Format("Downloading the full patch archive (about {0:0.00} GB, one download) ...", p.TotalBytes / 1e9));
            long got = 0;
            GitHubSource.Fetch(url, zipPath, n =>
            {
                cancel.ThrowIfCancellationRequested();
                got += n;
                Progress(Math.Min(got, p.TotalBytes), p.TotalBytes);
            });
            var left = new List<FileInfoEntry>();
            try
            {
                using (var z = ZipFile.OpenRead(zipPath))
                {
                    var top = z.Entries.Count > 0 ? z.Entries[0].FullName.Split('/')[0] + "/" : "";
                    var prefix = top + p.RepoDir + "/";
                    var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in z.Entries)
                        if (e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) map[e.FullName.Substring(prefix.Length)] = e;
                    Log("Unpacking " + pending.Count + " files ...");
                    long done = 0, total = pending.Sum(f => f.Size);
                    foreach (var f in pending)
                    {
                        cancel.ThrowIfCancellationRequested();
                        ZipArchiveEntry e;
                        if (!map.TryGetValue(f.Name, out e)) { left.Add(f); continue; }
                        var dest = Local(p, f.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        var tmp = dest + ".vtmp";
                        e.ExtractToFile(tmp, true);
                        try { Place(tmp, dest, f.Sha); }
                        catch (Exception) { left.Add(f); }
                        done += f.Size;
                        Progress(done, total);
                    }
                }
            }
            finally { try { File.Delete(zipPath); } catch { } }
            state.Save();
            return left;
        }
    }

    // ------------------------------------------------------------------ window
    class MainForm : Form
    {
        readonly State state = State.Load();
        readonly ISource source;
        readonly TextBox dirBox = new TextBox { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        readonly Button browse = new Button { Text = "Browse...", Anchor = AnchorStyles.Right | AnchorStyles.Top };
        readonly Label status = new Label { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        readonly Button checkBtn = new Button { Text = "Check for updates", Width = 140 };
        readonly Button updateBtn = new Button { Text = "Update", Width = 140, Enabled = false };
        readonly ProgressBar bar = new ProgressBar { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Maximum = 1000 };
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                                             Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
                                             Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        Plan plan;
        CancellationTokenSource cts;

        public MainForm(ISource source)
        {
            this.source = source;
            Text = "VirgilPatch Updater";
            ClientSize = new Size(640, 420);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var lbl = new Label { Text = "Epsilon Patches folder:", AutoSize = true, Location = new Point(12, 15) };
            dirBox.SetBounds(150, 12, 390, 23);
            browse.SetBounds(548, 11, 80, 25);
            status.SetBounds(12, 44, 616, 36);
            checkBtn.Location = new Point(12, 84);
            updateBtn.Location = new Point(160, 84);
            bar.SetBounds(310, 86, 318, 22);
            log.SetBounds(12, 118, 616, 290);
            Controls.AddRange(new Control[] { lbl, dirBox, browse, status, checkBtn, updateBtn, bar, log });
            browse.Click += delegate { PickFolder(); };
            checkBtn.Click += delegate { RunCheck(); };
            updateBtn.Click += delegate { RunUpdate(); };
            FormClosing += (s, e) => { if (cts != null) cts.Cancel(); };
            if (string.IsNullOrEmpty(state.patches_dir) || !Directory.Exists(state.patches_dir)) state.patches_dir = Detect();
            dirBox.Text = state.patches_dir ?? "";
            status.Text = state.commit == null ? "Not installed by this updater yet." :
                "Installed version: " + state.published + " (" + state.commit.Substring(0, Math.Min(7, state.commit.Length)) + ")";
            Shown += delegate { if (!string.IsNullOrEmpty(state.patches_dir)) RunCheck(); };
        }

        static string Detect()
        {
            var here = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (var c in new[] { here, Path.Combine(here, "Patches"), Path.Combine(here, "_retail_", "Patches"),
                                      Path.Combine(here, "Epsilon", "_retail_", "Patches") })
                if (Directory.Exists(c) && Path.GetFileName(c).Equals("Patches", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(Path.GetDirectoryName(c), "Wow.exe")))
                    return c;
            return null;
        }

        bool PickFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Select your Epsilon _retail_\\Patches folder", ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(state.patches_dir)) d.SelectedPath = state.patches_dir;
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                var p = d.SelectedPath;
                if (!Path.GetFileName(p).Equals("Patches", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(p, "Patches")))
                    p = Path.Combine(p, "Patches");
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(p) ?? p, "Wow.exe")) &&
                    MessageBox.Show(this, "This doesn't look like Epsilon's _retail_\\Patches folder (no Wow.exe next to it).\r\nUse it anyway?",
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
                state.patches_dir = p;
                dirBox.Text = p;
                state.Save();
                plan = null;
                updateBtn.Enabled = false;
                return true;
            }
        }

        void Say(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Say), s); return; }
            log.AppendText(s + "\r\n");
        }

        long lastTick;
        void Prog(long done, long total)
        {
            var now = Environment.TickCount;
            if (done < total && now - Interlocked.Read(ref lastTick) < 100) return;
            Interlocked.Exchange(ref lastTick, now);
            BeginInvoke(new Action(() => bar.Value = total <= 0 ? 0 : (int)Math.Min(1000, done * 1000 / total)));
        }

        void Busy(bool on)
        {
            checkBtn.Enabled = !on;
            browse.Enabled = !on;
            updateBtn.Enabled = !on && plan != null && !plan.UpToDate;
        }

        Updater MakeUpdater()
        {
            return new Updater(source, state) { Log = Say, Progress = Prog };
        }

        void RunCheck()
        {
            if (string.IsNullOrEmpty(state.patches_dir) && !PickFolder()) return;
            Busy(true);
            plan = null;
            var u = MakeUpdater();
            Task.Factory.StartNew(() => u.Check()).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) Say("Error: " + t.Exception.GetBaseException().Message);
                else
                {
                    plan = t.Result;
                    if (plan.UpToDate) { Say("You're up to date."); status.Text = "Up to date: " + plan.Published; }
                    else
                    {
                        var msg = string.Format("Update available ({0}): {1} files to download ({2:0.0} MB){3}{4}", plan.Published,
                                                plan.Fetch.Count, plan.FetchBytes / 1e6,
                                                plan.Remove.Count > 0 ? ", " + plan.Remove.Count + " old files to remove" : "",
                                                plan.Fetch.Count == 0 && plan.PatchJsonChanged ? ", file list changed" : "");
                        Say(msg);
                        status.Text = msg;
                    }
                }
                bar.Value = 0;
                Busy(false);
            })));
        }

        void RunUpdate()
        {
            if (plan == null) return;
            if (Process.GetProcessesByName("Wow").Length > 0 &&
                MessageBox.Show(this, "WoW is running. Close it first so the patch files aren't in use.\r\n\r\nUpdate anyway?", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            Busy(true);
            var u = MakeUpdater();
            var p = plan;
            cts = new CancellationTokenSource();
            var tok = cts.Token;
            Task.Factory.StartNew(() => u.Apply(p, tok)).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) Say("Error: " + t.Exception.GetBaseException().Message);
                else status.Text = "Up to date: " + p.Published;
                plan = null;
                bar.Value = t.IsFaulted ? 0 : 1000;
                Busy(false);
            })));
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;     // TLS 1.2
            ServicePointManager.DefaultConnectionLimit = 8;
            ISource src = new GitHubSource();
            string patches = null, logFile = null;
            bool cli = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--source" && i + 1 < args.Length) src = new FolderSource(args[++i]);
                else if (args[i] == "--patches" && i + 1 < args.Length) patches = args[++i];
                else if (args[i] == "--state" && i + 1 < args.Length) Config.StateDir = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) logFile = args[++i];
                else if (args[i] == "--cli") cli = true;
            }
            if (cli)
            {
                // headless: check + update, log to a file (used for testing)
                var w = logFile != null ? new StreamWriter(logFile, false, Encoding.UTF8) { AutoFlush = true } : null;
                Action<string> say = s => { if (w != null) lock (w) w.WriteLine(s); };
                try
                {
                    var st = State.Load();
                    if (patches != null) st.patches_dir = patches;
                    var u = new Updater(src, st) { Log = say };
                    var plan = u.Check();
                    say(string.Format("plan: fetch {0} files / {1} bytes, remove {2}, patch.json changed {3}", plan.Fetch.Count,
                                      plan.FetchBytes, plan.Remove.Count, plan.PatchJsonChanged));
                    if (!plan.UpToDate) u.Apply(plan, CancellationToken.None);
                    return 0;
                }
                catch (Exception ex) { say("ERROR " + ex); return 1; }
                finally { if (w != null) w.Dispose(); }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(src));
            return 0;
        }
    }
}
