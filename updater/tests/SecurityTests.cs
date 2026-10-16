using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VirgilPatch;

class MemorySource : ISource
{
    public Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
    public Action OnDownload;
    public List<string> Downloads = new List<string>();
    public List<string> Reads = new List<string>();
    public string ResolveCommit() { return "local"; }
    public byte[] Get(string commit, string path) { Reads.Add(path); return Data[path]; }
    public void Download(string commit, string path, string dest, long maxBytes, Action<long> progress)
    {
        lock (Downloads) Downloads.Add(path);
        if (OnDownload != null) OnDownload();
        using (var input = new MemoryStream(Data[path]))
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
            GitHubSource.CopyLimited(input, output, maxBytes, progress);
    }
    public string Describe() { return "isolated test"; }
}

class SecurityTests
{
    static string root;
    static int passed;
    static JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static byte[] Bytes(string text) { return Encoding.UTF8.GetBytes(text); }
    static string Hash(byte[] data)
    {
        using (var sha = SHA1.Create())
            return BitConverter.ToString(sha.ComputeHash(Bytes("blob " + data.Length + "\0").Concat(data).ToArray())).Replace("-", "").ToLowerInvariant();
    }
    static Dictionary<string, object> Info(byte[] bytes, uint id)
    { return new Dictionary<string, object> { {"size", bytes.Length}, {"sha", Hash(bytes)}, {"id", id} }; }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action action)
    { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (ex is InvalidDataException || ex is AggregateException || ex is OperationCanceledException ||
                ex.GetType() == typeof(Exception) && ex.Message.Contains("files failed")) return;
            throw;
        }
        throw new Exception("Unsafe input was accepted.");
    }
    static State Fresh(string name)
    {
        var area = Path.Combine(root, name);
        Directory.CreateDirectory(area);
        Config.StateDir = Path.Combine(area, "state");
        var state = new State { patches_dir = Path.Combine(area, "Patches") };
        Directory.CreateDirectory(state.patches_dir);
        return state;
    }
    static MemorySource Source(string patch = "TestPatch", string name = "sub/a.blp", string repoDir = null, byte[] content = null)
    {
        content = content ?? Bytes("asset content");
        var source = new MemorySource();
        var metadata = Bytes(js.Serialize(new { name = patch, version = "1", url = (string)null,
            files = new[] { new { file = name, id = 123 } } }));
        var dir = repoDir ?? "patches/" + patch;
        source.Data[dir + "/" + name] = content;
        source.Data[dir + "/patch.json"] = metadata;
        source.Data["index.json"] = Bytes(js.Serialize(new { format = 2, published = "test", patches =
            new Dictionary<string, object> { { patch, new { repo_dir = dir, patch_json = Info(metadata, 0), files =
                new Dictionary<string, object> { { name, Info(content, 123) } } } } } }));
        return source;
    }
    static MemorySource ManyFiles(int count, int size = 16)
    {
        var source = new MemorySource();
        var files = new Dictionary<string, object>();
        var entries = new List<object>();
        for (int i = 0; i < count; i++)
        {
            var name = "file" + i.ToString("D4") + ".ogg";
            var bytes = Enumerable.Repeat((byte)(i % 251), size).ToArray();
            source.Data["patches/TestPatch/" + name] = bytes;
            files.Add(name, Info(bytes, (uint)(1000 + i)));
            entries.Add(new { file = name, id = 1000 + i });
        }
        var metadata = Bytes(js.Serialize(new { name = "TestPatch", version = "1", files = entries }));
        source.Data["patches/TestPatch/patch.json"] = metadata;
        source.Data["index.json"] = Bytes(js.Serialize(new { format = 2, published = "test", patches =
            new Dictionary<string, object> { { "TestPatch", new { repo_dir = "patches/TestPatch", patch_json = Info(metadata, 0), files = files } } } }));
        return source;
    }
    static void Seed(Updater updater, MemorySource source, string name)
    {
        var dest = updater.Local("TestPatch/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        File.WriteAllBytes(dest, source.Data["patches/TestPatch/" + name]);
    }
    static Updater U(MemorySource source, State state) { return new Updater(source, state); }
    static void InvalidManifest(string label, string patch, string name, string repoDir = null)
    {
        Test(label, delegate {
            var state = Fresh(label);
            Reject(() => U(Source(patch, name, repoDir), state).Check());
            Assert(!File.Exists(Path.Combine(Config.StateDir, "state.json")), "State written before validation.");
        });
    }
    static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            Test("install-nested-assets-and-metadata", delegate {
                var state = Fresh("install"); var source = Source(); var u = U(source, state); var p = u.Check();
                Assert(p.Fetch.Count == 1 && p.PatchJsons.Count == 1, "Incorrect plan.");
                u.Apply(p, CancellationToken.None);
                Assert(File.ReadAllText(u.Local("TestPatch/sub/a.blp")) == "asset content", "Asset missing.");
                Assert(u.Check().UpToDate, "Second check should be current.");
                Assert(State.Load().installed.Count == 2, "State roundtrip failed.");
            });
            foreach (var name in new[] { "../escape.blp", "sub/../../escape.blp", "/root.blp", "C:/escape.blp", "C:escape.blp",
                "sub\\escape.blp", "x.blp:stream", "nul.blp", "COM1.blp", "lpt¹.blp", "sub./a.blp", "sub /a.blp", "a//b.blp",
                ".git/a.blp", "bad\nname.blp", "evil.exe", "evil.dll", "evil.ps1", "evil.bat", "evil.lnk", "payload.zip" })
                InvalidManifest("reject-file-" + passed, "TestPatch", name);
            foreach (var patch in new[] { "..", "../outside", "C:/outside", "nul", "TestPatch.", "one/two" })
                InvalidManifest("reject-patch-" + passed, patch, "a.blp");
            InvalidManifest("reject-repo-outside-patches", "TestPatch", "a.blp", "updater");
            Test("reject-case-colliding-destinations", delegate {
                var state = Fresh("duplicate"); var source = Source(name: "a.blp");
                var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(source.Data["index.json"]));
                var patch = (Dictionary<string, object>)((Dictionary<string, object>)index["patches"])["TestPatch"];
                ((Dictionary<string, object>)patch["files"]).Add("A.blp", Info(Bytes("different"), 124));
                source.Data["index.json"] = Bytes(js.Serialize(index));
                Reject(() => U(source, state).Check());
            });
            Test("reject-file-also-used-as-directory", delegate {
                var state = Fresh("prefix"); var source = Source(name: "a.blp");
                var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(source.Data["index.json"]));
                var patch = (Dictionary<string, object>)((Dictionary<string, object>)index["patches"])["TestPatch"];
                var files = (Dictionary<string, object>)patch["files"];
                files.Add("a.blp-other.blp", Info(Bytes("other"), 124));
                files.Add("a.blp/inside.blp", Info(Bytes("nested"), 125));
                source.Data["index.json"] = Bytes(js.Serialize(index));
                Reject(() => U(source, state).Check());
            });
            Test("ignore-traversal-in-old-state", delegate {
                var state = Fresh("old-state"); var outside = Path.Combine(root, "old-state", "outside.blp");
                File.WriteAllText(outside, "sentinel"); state.installed["../outside.blp"] = Updater.BlobSha(outside);
                var u = U(Source(), state); var p = u.Check(); Assert(p.Remove.Count == 0, "Traversal scheduled for deletion.");
                u.Apply(p, CancellationToken.None); Assert(File.ReadAllText(outside) == "sentinel", "Sentinel changed.");
            });
            Test("checksum-failure-preserves-existing-file", delegate {
                var state = Fresh("checksum"); var source = Source(name: "a.blp"); var u = U(source, state);
                var dest = u.Local("TestPatch/a.blp"); Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.WriteAllText(dest, "original");
                var p = u.Check(); source.Data["patches/TestPatch/a.blp"] = Bytes("wrong content");
                Reject(() => u.Apply(p, CancellationToken.None));
                Assert(File.ReadAllText(dest) == "original", "Existing file was destroyed.");
                Assert(!Directory.GetFiles(state.patches_dir, "*.vtmp", SearchOption.AllDirectories).Any(), "Temporary file leaked.");
            });
            Test("oversized-download-stops-before-write", delegate {
                var state = Fresh("oversized"); var source = Source(name: "a.blp"); var u = U(source, state); var p = u.Check();
                source.Data["patches/TestPatch/a.blp"] = new byte[100];
                Reject(() => u.Apply(p, CancellationToken.None)); Assert(!File.Exists(u.Local("TestPatch/a.blp")), "Oversized asset installed.");
            });
            Test("unsafe-patch-metadata-is-not-installed", delegate {
                var state = Fresh("metadata"); var source = Source(name: "a.blp"); var u = U(source, state); var p = u.Check();
                source.Data["patches/TestPatch/patch.json"] = Bytes("{\"files\":[{\"file\":\"../../evil.exe\",\"id\":123}]}");
                Reject(() => u.Apply(p, CancellationToken.None)); Assert(!File.Exists(u.Local("TestPatch/patch.json")), "Unsafe metadata installed.");
            });
            Test("patch-metadata-must-match-manifest", delegate {
                var state = Fresh("metadata-mismatch"); var source = Source(); var u = U(source, state); var p = u.Check();
                Reject(() => Safety.PatchMetadata(Bytes("{\"files\":[{\"file\":\"sub/a.blp\",\"id\":999}]}"), "TestPatch", p.Files));
                Reject(() => Safety.PatchMetadata(Bytes("{\"files\":[]}"), "TestPatch", p.Files));
            });
            Test("recheck-deletion-after-plan", delegate {
                var state = Fresh("remove-changed"); var source = Source(name: "a.blp"); var u = U(source, state); u.Apply(u.Check(), CancellationToken.None);
                var next = Source(name: "b.blp"); u = U(next, state); var p = u.Check(); Assert(p.Remove.Contains("TestPatch/a.blp"), "No removal planned.");
                File.WriteAllText(u.Local("TestPatch/a.blp"), "user edit after check"); u.Apply(p, CancellationToken.None);
                Assert(File.ReadAllText(u.Local("TestPatch/a.blp")) == "user edit after check", "User edit deleted.");
            });
            Test("remove-unchanged-previous-files", delegate {
                var state = Fresh("remove"); var source = Source(name: "a.blp"); var u = U(source, state); u.Apply(u.Check(), CancellationToken.None);
                u = U(Source(name: "b.blp"), state); u.Apply(u.Check(), CancellationToken.None);
                Assert(!File.Exists(u.Local("TestPatch/a.blp")) && File.Exists(u.Local("TestPatch/b.blp")), "Removal did not work.");
            });
            Test("cancel-before-update-preserves-files", delegate {
                var state = Fresh("cancel"); var u = U(Source(), state); var p = u.Check();
                Reject(() => u.Apply(p, new CancellationToken(true))); Assert(!Directory.GetDirectories(state.patches_dir).Any(), "Cancelled update wrote assets.");
            });
            Test("cancel-during-download-cleans-temporary-file", delegate {
                var state = Fresh("cancel-download"); var source = Source(); var u = U(source, state); var p = u.Check();
                var cancel = new CancellationTokenSource(); source.OnDownload = () => cancel.Cancel();
                Reject(() => u.Apply(p, cancel.Token));
                Assert(!Directory.GetFiles(state.patches_dir, "*.vtmp", SearchOption.AllDirectories).Any(), "Cancelled download leaked temp file.");
            });
            Test("network-host-and-https-policy", delegate {
                var request = typeof(GitHubSource).GetMethod("Req", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var url in new[] { "http://raw.githubusercontent.com/karakkquickcoin-byte/VirgilPatch/main/a", "https://evil.invalid/a", "https://codeload.github.com/karakkquickcoin-byte/VirgilPatch/zip/main",
                    "https://raw.githubusercontent.com/another/repo/main/a", "https://raw.githubusercontent.com:444/karakkquickcoin-byte/VirgilPatch/main/a" })
                {
                    try { request.Invoke(null, new object[] { url, null }); throw new Exception("Unsafe URL accepted."); }
                    catch (TargetInvocationException ex) { Assert(ex.InnerException is InvalidDataException, "Unexpected failure."); }
                }
                var good = (System.Net.HttpWebRequest)request.Invoke(null, new object[] { GitHubSource.RawUrl(new string('a',40), "index.json"), null });
                Assert(!good.AllowAutoRedirect, "Redirect policy not enforced.");
            });
            Test("large-update-downloads-only-402-missing-files", delegate {
                Assert(typeof(ISource).GetMethod("ArchiveUrl") == null, "Archive source API still exists.");
                var state = Fresh("large-update"); var source = ManyFiles(404); var u = U(source, state);
                Seed(u, source, "file0402.ogg"); Seed(u, source, "file0403.ogg");
                var existing = u.Local("TestPatch/file0403.ogg"); var stamp = File.GetLastWriteTimeUtc(existing);
                var p = u.Check(); Assert(p.Fetch.Count == 402, "Large update plan incorrect.");
                u.Apply(p, CancellationToken.None);
                Assert(source.Downloads.Count == 402 && source.Downloads.Distinct().Count() == 402, "Wrong number of individual downloads.");
                Assert(!source.Downloads.Contains("patches/TestPatch/file0402.ogg") && !source.Downloads.Contains("patches/TestPatch/file0403.ogg"), "Unchanged assets downloaded.");
                Assert(File.GetLastWriteTimeUtc(existing) == stamp, "Unchanged asset rewritten.");
                Assert(u.Check().UpToDate, "Large update did not finish correctly.");
            });
            Test("update-over-40-percent-downloads-only-needed-assets", delegate {
                var state = Fresh("majority-update"); var source = ManyFiles(2, 32); var u = U(source, state);
                Seed(u, source, "file0001.ogg");
                var p = u.Check(); Assert(p.FetchBytes > 0.4 * p.TotalBytes, "Fixture did not exceed old size threshold.");
                u.Apply(p, CancellationToken.None);
                Assert(source.Downloads.SequenceEqual(new[] { "patches/TestPatch/file0000.ogg" }), "Unchanged asset downloaded in majority update.");
            });
            Test("no-op-update-downloads-no-assets-or-patch-metadata", delegate {
                var state = Fresh("no-op"); var source = ManyFiles(3); var u = U(source, state);
                u.Apply(u.Check(), CancellationToken.None); source.Downloads.Clear(); source.Reads.Clear();
                var p = u.Check(); Assert(p.UpToDate, "Unchanged install not current.");
                u.Apply(p, CancellationToken.None);
                Assert(source.Downloads.Count == 0 && source.Reads.SequenceEqual(new[] { "index.json" }), "No-op fetched an unnecessary file.");
            });
            Test("missing-and-corrupt-assets-only-are-downloaded", delegate {
                var state = Fresh("repair"); var source = ManyFiles(3); var u = U(source, state);
                Seed(u, source, "file0000.ogg"); Seed(u, source, "file0001.ogg");
                File.WriteAllBytes(u.Local("TestPatch/file0001.ogg"), new byte[16]);
                var p = u.Check(); Assert(p.Fetch.Count == 2, "Repair plan incorrect.");
                u.Apply(p, CancellationToken.None);
                Assert(new HashSet<string>(source.Downloads).SetEquals(new[] { "patches/TestPatch/file0001.ogg", "patches/TestPatch/file0002.ogg" }), "Repair redownloaded a valid asset.");
            });
            Test("lost-state-does-not-redownload-matching-assets", delegate {
                var state = Fresh("lost-state"); var source = ManyFiles(3); var u = U(source, state);
                u.Apply(u.Check(), CancellationToken.None); source.Downloads.Clear();
                var fresh = new State { patches_dir = state.patches_dir };
                var next = U(source, fresh).Check();
                Assert(next.UpToDate && source.Downloads.Count == 0, "Existing assets require a state cache to be reused.");
            });
            if (args.Length > 1)
                Test("production-index-and-all-patch-metadata-compatible", delegate {
                    // Validate the entire schema and metadata without probing 69,000 nonexistent fixture assets.
                    var source = new FolderSource(args[1]);
                    var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(source.Get("local", "index.json")));
                    var patches = (Dictionary<string, object>)index["patches"];
                    var expected = new Dictionary<string, FileInfoEntry>(StringComparer.OrdinalIgnoreCase);
                    var entryMethod = typeof(Updater).GetMethod("Entry", BindingFlags.Static | BindingFlags.NonPublic);
                    int count = 0;
                    foreach (var patch in patches)
                    {
                        var data = (Dictionary<string, object>)patch.Value;
                        var repoDir = (string)data["repo_dir"];
                        Assert(repoDir == "patches/" + patch.Key, "Unexpected production repo path.");
                        var files = (Dictionary<string, object>)data["files"];
                        foreach (var file in files.Concat(new[] { new KeyValuePair<string, object>("patch.json", data["patch_json"]) }))
                        {
                            var entry = (FileInfoEntry)entryMethod.Invoke(null, new object[] { patch.Key, repoDir, file.Key, file.Value });
                            Safety.Key(entry.Key);
                            expected.Add(entry.Key, entry);
                            // Confirm the existing installation root can represent the path on .NET Framework.
                            Path.GetFullPath(Path.Combine(@"D:\D-Fix\Epsilon\Epsilon\_retail_\Patches", entry.Key.Replace('/', '\\')));
                            if (file.Key != "patch.json") count++;
                        }
                    }
                    foreach (var patch in patches)
                        Safety.PatchMetadata(source.Get("local", "patches/" + patch.Key + "/patch.json"), patch.Key, expected);
                    Console.WriteLine("Validated production files: " + count + "; patches: " + patches.Count);
                });
            if (args.Length > 2)
            {
                Test("reject-directory-junction-destination", () => Reject(() => Safety.Under(args[2], "jump/a.blp")));
                Test("reject-junction-as-install-root", () => Reject(() => Safety.Under(Path.Combine(args[2], "jump"), "a.blp")));
                Test("ignore-junction-in-previous-removal-state", delegate {
                    var state = Fresh("junction-state"); state.patches_dir = args[2];
                    state.installed["jump/a.blp"] = Hash(Bytes("sentinel"));
                    var plan = U(Source(), state).Check(); Assert(plan.Remove.Count == 0, "Junction scheduled for removal.");
                });
            }
            Console.WriteLine("TOTAL " + passed + " passed");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
