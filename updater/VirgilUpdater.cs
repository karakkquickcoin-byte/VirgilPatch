// VirgilPatch Updater - keeps Epsilon patch folders in sync with the VirgilPatch GitHub repository
// (every patch folder published there installs as Patches\<folder name>).
//
// Only files that are new or different are downloaded (each verified against its git blob SHA-1 from index.json);
// each patch.json is written last, files the previous version installed and the new one dropped are removed (a patch
// that is no longer published disappears completely). Every install and update downloads only the individual
// files it needs; the full repository archive is never downloaded.
//
// Build: powershell -File build.ps1 -CompileOnly (.NET Framework 4.8, no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Chronicles of Lordaeron")]
[assembly: System.Reflection.AssemblyDescription("Keeps the VirgilPatch Epsilon patch folders up to date from github.com/karakkquickcoin-byte/VirgilPatch")]
[assembly: System.Reflection.AssemblyCompany("VirgilPatch")]
[assembly: System.Reflection.AssemblyProduct("Chronicles of Lordaeron")]
[assembly: System.Reflection.AssemblyCopyright("VirgilPatch contributors")]
[assembly: System.Reflection.AssemblyVersion("1.4.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.3.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.4.3")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace VirgilPatch
{
    static class Config
    {
        public const string Owner = "karakkquickcoin-byte";
        public const string Repo = "VirgilPatch";
        public const string Branch = "main";
        public const string UserAgent = "VirgilPatchUpdater/1.4.3";
        public static string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirgilPatch");
        public static bool StartAmbienceMuted;
    }

    // ------------------------------------------------------------------ where the files come from
    interface ISource
    {
        string ResolveCommit();
        byte[] Get(string commit, string repoPath);                                  // small text files
        void Download(string commit, string repoPath, string dest, long maxBytes, Action<long> progress);
        string Describe();
    }

    class GitHubSource : ISource
    {
        public string Describe() { return "github.com/" + Config.Owner + "/" + Config.Repo; }

        static HttpWebRequest Req(string url, string accept)
        {
            var uri = new Uri(url, UriKind.Absolute);
            string prefix;
            switch (uri.Host.ToLowerInvariant())
            {
                case "api.github.com": prefix = "/repos/" + Config.Owner + "/" + Config.Repo + "/"; break;
                case "raw.githubusercontent.com":
                    prefix = "/" + Config.Owner + "/" + Config.Repo + "/"; break;
                case "github.com": prefix = "/" + Config.Owner + "/" + Config.Repo + ".git/"; break;
                default: throw new InvalidDataException("Unexpected download host.");
            }
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Downloads must use the configured GitHub repository over HTTPS.");
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = Config.UserAgent;
            r.AllowAutoRedirect = false;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 120000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (accept != null) r.Accept = accept;
            return r;
        }

        static HttpWebResponse Response(string url, string accept)
        {
            var response = (HttpWebResponse)Req(url, accept).GetResponse();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new InvalidDataException("Unexpected HTTP response; redirects are not accepted.");
            }
            return response;
        }

        static byte[] ReadLimited(Stream stream, long limit)
        {
            using (var output = new MemoryStream())
            {
                CopyLimited(stream, output, limit, null);
                return output.ToArray();
            }
        }

        internal static void CopyLimited(Stream input, Stream output, long limit, Action<long> progress)
        {
            var buffer = new byte[1 << 20];
            long count = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (n > limit - count) throw new InvalidDataException("Download exceeds its declared size limit.");
                output.Write(buffer, 0, n);
                count += n;
                if (progress != null) progress(n);
            }
        }

        static string Text(string url, string accept)
        {
            using (var resp = Response(url, accept))
            using (var stream = resp.GetResponseStream())
                return Encoding.UTF8.GetString(ReadLimited(stream, 4 * 1024 * 1024));
        }

        public string ResolveCommit()
        {
            try
            {
                var sha = Text("https://api.github.com/repos/" + Config.Owner + "/" + Config.Repo + "/commits/" + Config.Branch,
                               "application/vnd.github.sha").Trim();
                if (Regex.IsMatch(sha, "\\A[0-9a-f]{40}\\z")) return sha;
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
            using (var resp = Response(RawUrl(commit, repoPath), null))
            using (var s = resp.GetResponseStream())
                return ReadLimited(s, repoPath == ProgramRelease.ManifestPath ? 65536 : repoPath == "index.json" ? Safety.MaxIndexBytes : Safety.MaxMetadataBytes);
        }

        public void Download(string commit, string repoPath, string dest, long maxBytes, Action<long> progress)
        {
            Fetch(RawUrl(commit, repoPath), dest, maxBytes, progress);
        }

        public static void Fetch(string url, string dest, long maxBytes, Action<long> progress)
        {
            using (var resp = Response(url, null))
            using (var s = resp.GetResponseStream())
            using (var f = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                CopyLimited(s, f, maxBytes, progress);
            }
        }

    }

    // a local copy of the repository - used to test the updater without touching GitHub
    class FolderSource : ISource
    {
        readonly string root;
        public FolderSource(string root) { this.root = Path.GetFullPath(root); Safety.NoLinks(this.root); }
        public string Describe() { return root; }
        public string ResolveCommit() { return "local"; }
        string P(string repoPath) { return Safety.Under(root, repoPath); }
        public byte[] Get(string commit, string repoPath)
        {
            var path = P(repoPath);
            if (new FileInfo(path).Length > (repoPath == "index.json" ? Safety.MaxIndexBytes : Safety.MaxMetadataBytes))
                throw new InvalidDataException("Metadata exceeds size limit.");
            return File.ReadAllBytes(path);
        }
        public void Download(string commit, string repoPath, string dest, long maxBytes, Action<long> progress)
        {
            using (var input = File.OpenRead(P(repoPath)))
            using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                GitHubSource.CopyLimited(input, output, maxBytes, null);
            if (progress != null) progress(new FileInfo(dest).Length);
        }
    }

    // Treat both remote manifests and local state as untrusted input.
    static class Safety
    {
        internal const long MaxIndexBytes = 64L * 1024 * 1024;
        internal const long MaxMetadataBytes = 16L * 1024 * 1024;
        internal const long MaxAssetBytes = 2L * 1024 * 1024 * 1024;
        static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".adt", ".anim", ".blp", ".bone", ".db2", ".m2", ".ogg", ".phys", ".skel", ".skin", ".wdl", ".wdt", ".wmo" };

        internal static bool IsSha(string value)
        { return value != null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z"); }

        internal static void Relative(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 220 || value.IndexOf('\\') >= 0 || value.Any(c => char.IsControl(c)) || Path.IsPathRooted(value))
                throw new InvalidDataException("Unsafe relative path.");
            foreach (var segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".") || segment.EndsWith(" ") ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(c => char.IsControl(c)) ||
                    segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(segment.Split('.')[0], @"\A(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9Â¹Â²Â³]|LPT[1-9Â¹Â²Â³])\z", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Unsafe Windows path component.");
            }
        }

        internal static void Patch(string patch)
        {
            Relative(patch);
            if (patch.IndexOf('/') >= 0) throw new InvalidDataException("Patch must be one folder name.");
        }

        internal static void Asset(string name, bool metadata)
        {
            Relative(name);
            if (!(metadata && name == "patch.json") && !Extensions.Contains(Path.GetExtension(name)))
                throw new InvalidDataException("Only supported game asset types may be installed.");
        }

        internal static void Key(string key)
        {
            Relative(key);
            var slash = key.IndexOf('/');
            if (slash < 1) throw new InvalidDataException("Invalid installed file key.");
            Patch(key.Substring(0, slash));
            Asset(key.Substring(slash + 1), true);
        }

        internal static void NoLinks(string path)
        {
            var full = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(full))
            {
                uint attributes = GetFileAttributes(full);
                if (attributes == uint.MaxValue)
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (error != 2 && error != 3)
                        throw new IOException("Cannot validate path: " + new System.ComponentModel.Win32Exception(error).Message);
                }
                else if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symbolic links and junctions are not supported: " + full);
                full = Path.GetDirectoryName(full);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetFileAttributes(string path);

        internal static string Under(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Missing root folder.");
            Relative(relative);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes root folder.");
            NoLinks(path);
            return path;
        }

        internal static void DeleteTemp(string path)
        {
            try { NoLinks(path); if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void PruneEmpty(string dir)
        {
            NoLinks(dir);
            if (!Directory.Exists(dir)) return;
            foreach (var child in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) PruneEmpty(child);
            }
            NoLinks(dir);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }

        internal static void PatchMetadata(byte[] bytes, string patch, Dictionary<string, FileInfoEntry> expected)
        {
            if (bytes.LongLength > MaxMetadataBytes) throw new InvalidDataException("Patch metadata exceeds size limit.");
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
            var js = new JavaScriptSerializer { MaxJsonLength = (int)MaxMetadataBytes };
            var metadata = js.Deserialize<Dictionary<string, object>>(text);
            var entries = metadata["files"] as System.Collections.IEnumerable;
            if (entries == null || metadata["files"] is string) throw new InvalidDataException("Invalid patch file list.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries)
            {
                var file = item as Dictionary<string, object>;
                if (file == null) throw new InvalidDataException("Invalid patch entry.");
                var name = file["file"] as string;
                Asset(name, false);
                FileInfoEntry match;
                if (!seen.Add(name) || !expected.TryGetValue(patch + "/" + name, out match) || match.Name == "patch.json" ||
                    Convert.ToUInt32(file["id"]) != match.Id)
                    throw new InvalidDataException("patch.json does not match the verified manifest.");
            }
            if (seen.Count != expected.Values.Count(f => f.Patch == patch && f.Name != "patch.json"))
                throw new InvalidDataException("patch.json is missing manifest entries.");
        }
    }

    // ------------------------------------------------------------------ the update itself
    class FileInfoEntry
    {
        public string Patch;    // patch folder name = Patches\<Patch>
        public string Name;     // path inside the patch folder, forward slashes
        public string RepoPath; // path inside the repository
        public long Size;
        public string Sha;
        public uint Id;
        public string Key { get { return Patch + "/" + Name; } }
    }

    class Plan
    {
        public string Commit;
        public string Published;
        public List<string> Patches = new List<string>();
        public Dictionary<string, FileInfoEntry> Files = new Dictionary<string, FileInfoEntry>(StringComparer.OrdinalIgnoreCase);
        public List<FileInfoEntry> Fetch = new List<FileInfoEntry>();
        public List<string> Remove = new List<string>();                   // installed keys "<patch>/<file>"
        public List<FileInfoEntry> PatchJsons = new List<FileInfoEntry>(); // changed patch.json files, written last
        public long FetchBytes { get { return Fetch.Sum(f => f.Size); } }
        public long TotalBytes;
        public int TotalFiles;
        public Dictionary<string, string> Installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool UpToDate { get { return Fetch.Count == 0 && Remove.Count == 0 && PatchJsons.Count == 0; } }
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
                Safety.NoLinks(FilePath);
                if (new FileInfo(FilePath).Length > Safety.MaxIndexBytes) throw new InvalidDataException("State exceeds size limit.");
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
            Safety.NoLinks(FilePath);
            Directory.CreateDirectory(Config.StateDir);
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, js.Serialize(new Dictionary<string, object> {
                { "patches_dir", patches_dir }, { "commit", commit }, { "published", published },
                { "hashes", hashes }, { "installed", installed } }));
            Safety.NoLinks(FilePath);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
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

        static FileInfoEntry Entry(string patch, string repoDir, string name, object o)
        {
            Safety.Patch(patch);
            Safety.Asset(name, true);
            Safety.Relative(repoDir);
            var d = (Dictionary<string, object>)o;
            var size = Convert.ToInt64(d["size"]);
            var sha = d["sha"] as string;
            if (size < 0 || size > (name == "patch.json" ? Safety.MaxMetadataBytes : Safety.MaxAssetBytes) || !Safety.IsSha(sha))
                throw new InvalidDataException("Invalid size or checksum in manifest.");
            return new FileInfoEntry { Patch = patch, Name = name, RepoPath = repoDir + "/" + name,
                                       Size = size, Sha = sha, Id = name == "patch.json" ? 0 : Convert.ToUInt32(d["id"]) };
        }

        public string Local(string key)
        {
            Safety.Key(key);
            return Safety.Under(state.patches_dir, key);
        }

        public string Local(FileInfoEntry f) { return Local(f.Key); }

        public Plan Check()
        {
            if (string.IsNullOrWhiteSpace(state.patches_dir)) throw new InvalidDataException("Choose a Patches folder first.");
            state.patches_dir = Path.GetFullPath(state.patches_dir);
            Safety.NoLinks(state.patches_dir);
            Log("Looking for the latest version on " + src.Describe() + " ...");
            var p = new Plan { Commit = src.ResolveCommit() };
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(src.Get(p.Commit, "index.json")));
            p.Published = index.ContainsKey("published") ? Convert.ToString(index["published"]) : "";
            // format 2: {"patches": {name: {repo_dir, patch_json, files}}}; format 1: one patch at the top level
            var patches = new List<KeyValuePair<string, Dictionary<string, object>>>();
            if (index.ContainsKey("patches"))
                foreach (var kv in (Dictionary<string, object>)index["patches"])
                    patches.Add(new KeyValuePair<string, Dictionary<string, object>>(kv.Key, (Dictionary<string, object>)kv.Value));
            else
                patches.Add(new KeyValuePair<string, Dictionary<string, object>>((string)index["patch_folder"], index));
            var all = new List<FileInfoEntry>();
            var jsons = new List<FileInfoEntry>();
            foreach (var kv in patches)
            {
                Safety.Patch(kv.Key);
                if (p.Patches.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Duplicate patch folder.");
                var d = kv.Value;
                var repoDir = d.ContainsKey("repo_dir") ? (string)d["repo_dir"] : "patches/" + kv.Key;
                if (repoDir != "patches/" + kv.Key && !(patches.Count == 1 && !index.ContainsKey("patches") && repoDir == "patch"))
                    throw new InvalidDataException("Unexpected repository path for patch.");
                p.Patches.Add(kv.Key);
                jsons.Add(Entry(kv.Key, repoDir, "patch.json", d["patch_json"]));
                all.AddRange(((Dictionary<string, object>)d["files"]).Select(f => Entry(kv.Key, repoDir, f.Key, f.Value)));
            }
            foreach (var entry in all.Concat(jsons))
            {
                // Validate the complete manifest, including links, before writing any state or files.
                Local(entry);
                if (p.Files.ContainsKey(entry.Key)) throw new InvalidDataException("Duplicate destination in manifest.");
                p.Files.Add(entry.Key, entry);
            }
            foreach (var key in p.Files.Keys)
                for (int slash = key.LastIndexOf('/'); slash > 0; slash = key.LastIndexOf('/', slash - 1))
                    if (p.Files.ContainsKey(key.Substring(0, slash)))
                        throw new InvalidDataException("Manifest file is also used as a directory.");
            p.TotalFiles = all.Count;
            p.TotalBytes = all.Sum(f => f.Size);
            Log(string.Format("Latest version: {0} ({1}): {2} - {3} files, {4:0.00} GB", p.Commit.Substring(0, Math.Min(7, p.Commit.Length)),
                              p.Published, string.Join(", ", p.Patches), p.TotalFiles, p.TotalBytes / 1e9));
            Log("Checking your files in " + state.patches_dir + " ...");
            long done = 0, total = p.TotalBytes;
            foreach (var f in all)
            {
                var path = Local(f);
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != f.Size || CachedSha(path) != f.Sha) p.Fetch.Add(f);
                p.Installed[f.Key] = f.Sha;
                done += f.Size;
                Progress(done, total);
            }
            foreach (var j in jsons)
            {
                var path = Local(j);
                if (!File.Exists(path) || CachedSha(path) != j.Sha) p.PatchJsons.Add(j);
                p.Installed[j.Key] = j.Sha;
            }
            // files an earlier update installed that the new version no longer has (left alone if you changed them),
            // including whole patches that are no longer published
            foreach (var kv in state.installed)
                if (!p.Installed.ContainsKey(kv.Key) && kv.Key.IndexOf('/') > 0)
                {
                    // Old state is untrusted too; never use it to delete outside the install root.
                    string path;
                    try { path = Local(kv.Key); }
                    catch (InvalidDataException) { Log("Ignored unsafe entry in previous state."); continue; }
                    if (Safety.IsSha(kv.Value) && File.Exists(path) && BlobSha(path) == kv.Value) p.Remove.Add(kv.Key);
                }
            state.Save();
            return p;
        }

        void Place(string tmp, string dest, FileInfoEntry entry)
        {
            Safety.NoLinks(tmp);
            Safety.NoLinks(dest);
            if (new FileInfo(tmp).Length != entry.Size) throw new InvalidDataException("Downloaded size does not match manifest.");
            var sha = entry.Sha;
            var got = BlobSha(tmp);
            if (got != sha) { File.Delete(tmp); throw new InvalidDataException("Checksum mismatch for " + Path.GetFileName(dest) + ". Update stopped; the existing file was preserved."); }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            Safety.NoLinks(dest);
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
            var fi = new FileInfo(dest);
            state.hashes[dest] = new object[] { fi.Length, fi.LastWriteTimeUtc.Ticks.ToString(), sha };
        }

        public void Apply(Plan p, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            // Validate plans again at use time, including after a folder became a junction.
            foreach (var entry in p.Files.Values) Local(entry);
            long total = p.FetchBytes, done = 0;
            var pending = new List<FileInfoEntry>(p.Fetch);
            if (pending.Count > 0)
            {
                Log(string.Format("Downloading {0} files ({1:0.0} MB) ...", pending.Count, pending.Sum(f => f.Size) / 1e6));
                var errors = new List<string>();
                var opts = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel };
                Parallel.ForEach(pending, opts, f =>
                {
                    var dest = Local(f);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".vtmp";
                    for (int attempt = 1; ; attempt++)
                    {
                        long got = 0;
                        try
                        {
                            cancel.ThrowIfCancellationRequested();
                            src.Download(p.Commit, f.RepoPath, tmp, f.Size, n => { cancel.ThrowIfCancellationRequested(); got += n; Progress(Interlocked.Add(ref done, n), total); });
                            cancel.ThrowIfCancellationRequested();
                            lock (state) Place(tmp, Local(f), f);
                            break;
                        }
                        catch (Exception ex)
                        {
                            Safety.DeleteTemp(tmp);
                            if (ex is OperationCanceledException) throw;
                            Interlocked.Add(ref done, -got);
                            var we = ex as WebException;
                            var code = we != null && we.Response is HttpWebResponse ? (int)((HttpWebResponse)we.Response).StatusCode : 0;
                            if (attempt >= 4 || ex is InvalidDataException || ex is IOException && !(ex is WebException))
                            {
                                lock (errors) errors.Add(f.Key + ": " + ex.Message);
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
            // each patch.json last: until now the game still sees the previous file lists
            foreach (var j in p.PatchJsons)
            {
                cancel.ThrowIfCancellationRequested();
                var dest = Local(j);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".vtmp";
                try
                {
                    var bytes = src.Get(p.Commit, j.RepoPath);
                    Safety.PatchMetadata(bytes, j.Patch, p.Files);
                    using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        output.Write(bytes, 0, bytes.Length);
                    Place(tmp, Local(j), j);
                }
                finally { Safety.DeleteTemp(tmp); }
            }
            var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in p.Remove)
            {
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    var path = Local(key);
                    string previous;
                    if (!state.installed.TryGetValue(key, out previous) || !Safety.IsSha(previous) ||
                        !File.Exists(path) || BlobSha(path) != previous)
                    { Log("kept changed file " + key); continue; }
                    Safety.NoLinks(path);
                    File.Delete(path);
                    Log("removed " + key);
                    touchedDirs.Add(Safety.Under(state.patches_dir, key.Substring(0, key.IndexOf('/'))));
                }
                catch (IOException ex) { Log("could not remove " + key + ": " + ex.Message); }
            }
            foreach (var dir in touchedDirs)                 // a patch that is no longer published: drop its empty folders
                try
                {
                    Safety.PruneEmpty(dir);
                }
                catch (IOException) { }
            state.commit = p.Commit;
            state.published = p.Published;
            state.installed = p.Installed;
            state.Save();
            Log("Done - you have the version from " + p.Published + ".");
        }


    }

    // ------------------------------------------------------------------ window
    partial class MainForm : LauncherWindow
    {
        readonly State state = State.Load();
        readonly ISource source;
        readonly TextBox dirBox = new TextBox { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        readonly Button browse = new LauncherButton { Text = "Browse...", Anchor = AnchorStyles.Right | AnchorStyles.Top };
        readonly Label status = new Label { AutoSize = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        readonly Button updateBtn = new LauncherButton { Text = "Update", Primary = true };
        readonly LauncherProgress bar = new LauncherProgress();
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                                             Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
                                             Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        Plan plan;
        CancellationTokenSource cts;

        readonly string confirmationDirectory;
        readonly bool skipProgramCheck;
        readonly string[] programArguments;

        public MainForm(ISource source, string confirmationDirectory = null, bool skipProgramCheck = false, string[] programArguments = null)
        {
            this.source = source;
            this.confirmationDirectory = confirmationDirectory;
            this.skipProgramCheck = skipProgramCheck;
            this.programArguments = programArguments ?? new string[0];
            BuildLauncher();
            browse.Click += delegate { PickFolder(); };
            updateBtn.Click += delegate { RunUpdate(); };
            FormClosing += (s, e) => { if (cts != null) cts.Cancel(); };
            if (string.IsNullOrEmpty(state.patches_dir) || !Directory.Exists(state.patches_dir)) state.patches_dir = Detect();
            dirBox.Text = state.patches_dir ?? "";
            status.Text = string.IsNullOrEmpty(state.patches_dir) ? "Choose your Patches folder, then click Update." :
                "Ready. Your patches change only when you click Update.";
            Shown += delegate {
                if (this.confirmationDirectory != null) SelfUpdate.ConfirmStartup(this.confirmationDirectory);
                StartAmbience();
            };
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
                RememberFolder(p);
                return true;
            }
        }

        void RememberFolder(string path)
        {
            state.patches_dir = path;
            dirBox.Text = path;
            state.Save();
            plan = null;
            updateBtn.Enabled = true;
            status.Text = "Folder selected. Click Update when you are ready.";
        }

        void Say(string s)
        {
            if (InvokeRequired) { Post(delegate { Say(s); }); return; }
            log.AppendText(s + "\r\n");
        }

        long lastTick;
        void Prog(long done, long total)
        {
            var now = Environment.TickCount;
            if (done < total && now - Interlocked.Read(ref lastTick) < 100) return;
            Interlocked.Exchange(ref lastTick, now);
            Post(delegate { bar.Value = total <= 0 ? 0 : (int)Math.Min(1000, done * 1000 / total); });
        }

        void Busy(bool on)
        {
            isBusy = on;
            browse.Enabled = !on;
            updateBtn.Enabled = !on;
            updateBtn.Text = on ? "Please wait..." : "Update";
        }

        Updater MakeUpdater()
        {
            return new Updater(source, state) { Log = Say, Progress = Prog };
        }

        void RunCheck(bool applyAfterCheck = false)
        {
            if (source is GitHubSource && !skipProgramCheck) RunProgramCheck(true, applyAfterCheck);
            else RunPatchCheck(applyAfterCheck);
        }

        void RunProgramCheck(bool chooseFolder, bool applyAfterCheck = false)
        {
            Busy(true);
            plan = null;
            status.Text = "Checking for an updater upgrade ...";
            Task.Factory.StartNew(() => {
                var directory = SelfUpdate.Prepare(source, SelfUpdate.Here, programArguments, Say);
                if (directory == null) return false;
                Say("Program upgrade verified. Restarting the updater ...");
                return SelfUpdate.StartHelper(directory);
            }).ContinueWith(t => Post(() => {
                if (t.IsFaulted) Say("Program upgrade unavailable: " + t.Exception.GetBaseException().Message + " Continuing with the current updater.");
                else if (!t.IsCanceled && t.Result) { Close(); return; }
                Busy(false);
                if (chooseFolder || !string.IsNullOrEmpty(state.patches_dir)) RunPatchCheck(applyAfterCheck);
                else status.Text = "Choose your Epsilon Patches folder to check for patch updates.";
            }));
        }

        void RunPatchCheck(bool applyAfterCheck = false)
        {
            if (string.IsNullOrEmpty(state.patches_dir) && !PickFolder()) return;
            Busy(true);
            plan = null;
            status.Text = "Checking your installed patch files...";
            var u = MakeUpdater();
            Task.Factory.StartNew(() => u.Check()).ContinueWith(t => Post(() =>
            {
                if (t.IsFaulted) { Say("Error: " + t.Exception.GetBaseException().Message); status.Text = "Update unavailable. Open Details for the error, then try again."; }
                else
                {
                    plan = t.Result;
                    if (plan.UpToDate) { Say("You're up to date."); status.Text = "Up to date: " + plan.Published; }
                    else
                    {
                        var msg = string.Format("Update available ({0}): {1} files to download ({2:0.0} MB){3}{4}", plan.Published,
                                                plan.Fetch.Count, plan.FetchBytes / 1e6,
                                                plan.Remove.Count > 0 ? ", " + plan.Remove.Count + " old files to remove" : "",
                                                plan.Fetch.Count == 0 && plan.PatchJsons.Count > 0 ? ", file list changed" : "");
                        Say(msg);
                        status.Text = msg;
                    }
                }
                bar.Value = plan != null && plan.UpToDate ? 1000 : 0;
                Busy(false);
                if (applyAfterCheck && plan != null && !plan.UpToDate) ApplyUpdate();
            }));
        }

        void RunUpdate()
        {
            if (isBusy) return;
            if (string.IsNullOrEmpty(state.patches_dir) && !PickFolder()) return;
            RunCheck(true);
        }

        void ApplyUpdate()
        {
            if (isBusy || plan == null || plan.UpToDate) return;
            if (Process.GetProcessesByName("Wow").Length > 0 &&
                MessageBox.Show(this, "WoW is running. Close it first so the patch files aren't in use.\r\n\r\nUpdate anyway?", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            Busy(true);
            updateBtn.Text = "Updating...";
            status.Text = "Updating your patch files. Please keep the launcher open.";
            var u = MakeUpdater();
            var p = plan;
            cts = new CancellationTokenSource();
            var tok = cts.Token;
            Task.Factory.StartNew(() => u.Apply(p, tok)).ContinueWith(t => Post(() =>
            {
                if (t.IsFaulted) { Say("Error: " + t.Exception.GetBaseException().Message); status.Text = "Update unavailable. Open Details for the error, then try again."; }
                else if (t.IsCanceled) { Say("Update cancelled."); status.Text = "Update cancelled. Click Update to try again."; }
                else status.Text = "Up to date: " + p.Published;
                plan = null;
                cts.Dispose(); cts = null;
                bar.Value = t.IsFaulted || t.IsCanceled ? 0 : 1000;
                Busy(false);
            }));
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;     // TLS 1.2
            ServicePointManager.CheckCertificateRevocationList = true;
            ServicePointManager.DefaultConnectionLimit = 8;
            if (args.Length == 2 && args[0] == "--apply-self-update") return SelfUpdate.RunHelper(args[1]);
            ISource src = new GitHubSource();
            string patches = null, logFile = null, confirmationDirectory = null;
            bool cli = false, skipProgramCheck = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--source" && i + 1 < args.Length) src = new FolderSource(args[++i]);
                else if (args[i] == "--patches" && i + 1 < args.Length) patches = args[++i];
                else if (args[i] == "--state" && i + 1 < args.Length) Config.StateDir = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) logFile = args[++i];
                else if (args[i] == "--cli") cli = true;
                else if (args[i] == "--mute") Config.StartAmbienceMuted = true;
                else if (args[i] == "--confirm-self-update" && i + 1 < args.Length) confirmationDirectory = args[++i];
                else if (args[i] == "--skip-self-update-once") skipProgramCheck = true;
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
                                      plan.FetchBytes, plan.Remove.Count, plan.PatchJsons.Count));
                    if (!plan.UpToDate) u.Apply(plan, CancellationToken.None);
                    return 0;
                }
                catch (Exception ex) { say("ERROR " + ex); return 1; }
                finally { if (w != null) w.Dispose(); }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(src, confirmationDirectory, skipProgramCheck, SelfUpdate.UserArguments(args)));
            return 0;
        }
    }
}
