using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VirgilPatch;

class RepairSource : ISource
{
    public Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
    public List<string> Downloads = new List<string>();
    public bool FailAll;
    public int TransientFailures;
    public string ResolveCommit() { return "local"; }
    public string Describe() { return "isolated recovery fixture"; }
    public byte[] Get(string commit, string path) { return Data[path]; }
    public void Download(string commit, string path, string dest, long max, Action<long> progress)
    {
        lock (Downloads) Downloads.Add(path);
        if (Interlocked.Decrement(ref TransientFailures) >= 0) throw new WebException("simulated temporary network failure", WebExceptionStatus.ConnectFailure);
        if (FailAll) throw new InvalidDataException("simulated invalid published payload");
        using (var input = new MemoryStream(Data[path]))
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write)) GitHubSource.CopyLimited(input, output, max, progress);
    }
    public static string Sha(byte[] bytes)
    {
        using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("blob " + bytes.Length + "\0").Concat(bytes).ToArray())).Replace("-", "").ToLowerInvariant();
    }
    public static object Info(byte[] bytes, int id = 0) { return new { size = bytes.Length, sha = Sha(bytes), id = id }; }
    public void Publish(Dictionary<string, Dictionary<string, byte[]>> patches)
    {
        var js = new JavaScriptSerializer(); var entries = new Dictionary<string, object>();
        foreach (var patch in patches)
        {
            int id = 1; var files = new Dictionary<string, object>(); var list = new List<object>();
            foreach (var file in patch.Value)
            {
                Data["patches/" + patch.Key + "/" + file.Key] = file.Value;
                files[file.Key] = Info(file.Value, id); list.Add(new { file = file.Key, id = id++ });
            }
            var metadata = Encoding.UTF8.GetBytes(js.Serialize(new { name = patch.Key, version = "1", files = list }));
            Data["patches/" + patch.Key + "/patch.json"] = metadata;
            entries[patch.Key] = new { repo_dir = "patches/" + patch.Key, files = files, patch_json = Info(metadata) };
        }
        Data["index.json"] = Encoding.UTF8.GetBytes(js.Serialize(new { format = 2, published = "repair", patches = entries }));
    }
}
class RepairTests
{
    static string root;
    static int passed;
    static byte[] Bytes(string text) { return Encoding.UTF8.GetBytes(text); }
    static Dictionary<string, byte[]> Files(params string[] names) { return names.ToDictionary(n => n, n => Bytes(n + " asset bytes")); }
    static State Fresh()
    {
        var area = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
        Config.StateDir = Path.Combine(area, "state");
        var state = new State { patches_dir = Path.Combine(area, "Patches") }; Directory.CreateDirectory(state.patches_dir); return state;
    }
    static RepairSource Source(bool two = true)
    {
        var source = new RepairSource(); var patches = new Dictionary<string, Dictionary<string, byte[]>> { { "Small", Files("a.ogg", "b.ogg") } };
        if (two) patches.Add("Classic", Files("c.ogg")); source.Publish(patches); return source;
    }
    static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
    static void Rejected(Action run) { try { run(); } catch (Exception) { return; } throw new Exception("Failure was not rejected."); }
    static void Apply(Updater updater, params string[] selection) { var plan = updater.Check(selection.Length == 0 ? null : selection); if (!plan.UpToDate) updater.Apply(plan, CancellationToken.None); }
    static void Complete(string patches, string patch)
    {
        var folder = Path.Combine(patches, patch); var metadata = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "patch.json")));
        Assert(!string.IsNullOrEmpty((string)metadata["name"]), "Patch name missing.");
        foreach (Dictionary<string, object> entry in (System.Collections.IEnumerable)metadata["files"])
            Assert(File.Exists(Path.Combine(folder, ((string)entry["file"]).Replace('/', Path.DirectorySeparatorChar))), "Advertised patch has a missing file.");
    }
    static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            Test("select-small-without-installing-classic", delegate {
                var state = Fresh(); var source = Source(); var u = new Updater(source, state); Apply(u, "Small");
                Assert(source.Downloads.Count == 2 && source.Downloads.All(p => p.StartsWith("patches/Small/")) && !Directory.Exists(Path.Combine(state.patches_dir, "Classic")), "Unselected Classic was downloaded."); Complete(state.patches_dir, "Small");
            });
            Test("partial-selection-preserves-unselected-state-and-files", delegate {
                var state = Fresh(); var source = Source(); var u = new Updater(source, state); Apply(u, "Classic");
                var path = Path.Combine(state.patches_dir, "Classic/c.ogg"); var stamp = File.GetLastWriteTimeUtc(path); var sha = state.installed["Classic/c.ogg"];
                Apply(u, "Small"); Assert(state.installed["Classic/c.ogg"] == sha && File.GetLastWriteTimeUtc(path) == stamp, "Unselected tracking/files changed.");
            });
            Test("retired-unselected-folder-is-never-deleted", delegate {
                var state = Fresh(); var source = Source(); var u = new Updater(source, state); Apply(u); source.Publish(new Dictionary<string, Dictionary<string, byte[]>> { { "Small", Files("a.ogg", "b.ogg") } });
                Apply(u, "Small"); Assert(File.Exists(Path.Combine(state.patches_dir, "Classic/patch.json")) && state.installed.ContainsKey("Classic/c.ogg"), "Retired unselected patch removed.");
            });
            Test("missing-patch-json-restored-with-zero-asset-downloads", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); source.Downloads.Clear(); File.Delete(Path.Combine(state.patches_dir, "Small/patch.json"));
                var p = u.Check(new[] { "Small" }); Assert(p.Fetch.Count == 0 && p.PatchJsons.Count == 1, "Metadata repair incorrectly requires asset downloads."); u.Apply(p, CancellationToken.None);
                Assert(source.Downloads.Count == 0, "Metadata-only repair downloaded assets."); Complete(state.patches_dir, "Small");
            });
            Test("metadata-cache-cannot-hide-same-size-corruption", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); var path = Path.Combine(state.patches_dir, "Small/patch.json");
                var stamp = File.GetLastWriteTimeUtc(path); var bad = File.ReadAllBytes(path); bad[0] = (byte)'x'; File.WriteAllBytes(path, bad); File.SetLastWriteTimeUtc(path, stamp);
                var p = u.Check(); Assert(p.PatchJsons.Count == 1, "Corrupt patch.json was skipped by cache."); u.Apply(p, CancellationToken.None); Complete(state.patches_dir, "Small");
            });
            Test("first-repair-discards-stale-asset-cache", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); var path = Path.Combine(state.patches_dir, "Small/a.ogg");
                var stamp = File.GetLastWriteTimeUtc(path); var bad = File.ReadAllBytes(path); bad[0] ^= 1; File.WriteAllBytes(path, bad); File.SetLastWriteTimeUtc(path, stamp); state.verified_patches.Clear();
                var p = u.Check(); Assert(p.Fetch.Count == 1, "Migration trusted a stale asset cache."); u.Apply(p, CancellationToken.None); Assert(Updater.BlobSha(path) == RepairSource.Sha(source.Data["patches/Small/a.ogg"]), "Asset was not repaired.");
            });
            Test("deep-repair-catches-preserved-mtime-damage", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); var path = Path.Combine(state.patches_dir, "Small/a.ogg");
                var stamp = File.GetLastWriteTimeUtc(path); var bad = File.ReadAllBytes(path); bad[0] ^= 1; File.WriteAllBytes(path, bad); File.SetLastWriteTimeUtc(path, stamp); u.VerifyExisting = true;
                Assert(u.Check().Fetch.Count == 1, "Explicit verification failed to detect damage.");
            });
            Test("lost-state-reuses-matching-assets", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); source.Downloads.Clear(); state.hashes.Clear(); state.installed.Clear(); state.verified_patches.Clear();
                Assert(u.Check().UpToDate && source.Downloads.Count == 0, "Losing state forced a redownload.");
            });
            Test("one-failed-patch-does-not-hide-complete-classic", delegate {
                var state = Fresh(); var source = Source(); var u = new Updater(source, state); var p = u.Check(); var correct = source.Data["patches/Small/a.ogg"]; source.Data["patches/Small/a.ogg"] = new byte[correct.Length + 1];
                Rejected(() => u.Apply(p, CancellationToken.None)); Complete(state.patches_dir, "Classic"); Assert(!File.Exists(Path.Combine(state.patches_dir, "Small/patch.json")) && p.CompletedPatches.Contains("Classic"), "Bad patch was advertised or Classic was hidden.");
                source.Data["patches/Small/a.ogg"] = correct; source.Downloads.Clear(); Apply(u); Complete(state.patches_dir, "Small"); Assert(source.Downloads.All(k => !k.StartsWith("patches/Classic/")), "Retry redownloaded completed Classic.");
            });
            Test("failed-new-asset-preserves-existing-json", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); var path = Path.Combine(state.patches_dir, "Small/patch.json"); var original = File.ReadAllText(path);
                source.Publish(new Dictionary<string, Dictionary<string, byte[]>> { { "Small", Files("a.ogg", "b.ogg", "new.ogg") } }); var p = u.Check(); source.FailAll = true;
                Rejected(() => u.Apply(p, CancellationToken.None)); Assert(File.ReadAllText(path) == original, "Failed patch changed its previous JSON.");
            });
            Test("bad-published-metadata-rejected-before-asset-transfer", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); var p = u.Check(); source.Data["patches/Small/patch.json"][0] = (byte)'x';
                Rejected(() => u.Apply(p, CancellationToken.None)); Assert(source.Downloads.Count == 0, "Asset transfer started before metadata verified.");
            });
            Test("invalid-publication-stops-after-inflight-files", delegate {
                var state = Fresh(); var source = new RepairSource(); source.Publish(new Dictionary<string, Dictionary<string, byte[]>> { { "Small", Files(Enumerable.Range(0, 500).Select(i => i + ".ogg").ToArray()) } }); source.FailAll = true;
                var u = new Updater(source, state); var p = u.Check(); Rejected(() => u.Apply(p, CancellationToken.None)); Assert(source.Downloads.Count <= 4, "Invalid publication attempted hundreds of doomed downloads.");
            });
            Test("malformed-hash-cache-is-a-cache-miss", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); Apply(u); var path = Path.Combine(state.patches_dir, "Small/a.ogg");
                state.hashes[path] = new object[] { "not-a-size", "bad", "bad" }; Assert(u.Check().UpToDate, "Malformed cache broke valid files."); state.hashes[path] = null; Assert(u.Check().UpToDate, "Null cache broke valid files.");
            });
            Test("empty-and-unknown-selections-rejected", delegate {
                var state = Fresh(); var u = new Updater(Source(), state); Rejected(() => u.Check(new string[0])); Rejected(() => u.Check(new[] { "not-published" })); Assert(!File.Exists(Path.Combine(Config.StateDir, "state.json")), "Invalid selection wrote state.");
            });
            Test("cancel-check-before-saving-state", delegate {
                var state = Fresh(); var source = Source(false); var u = new Updater(source, state); var cts = new CancellationTokenSource(); cts.Cancel();
                Rejected(() => u.Check(null, null, cts.Token)); Assert(!File.Exists(Path.Combine(Config.StateDir, "state.json")), "Cancelled check saved state.");
            });
            Test("progress-percent-eta-remain-bounded", delegate {
                var estimate = new TransferEstimate(); estimate.Reset("Downloading"); Assert(estimate.Format(0, 0).Contains("0%"), "Unknown progress is invalid.");
                Assert(estimate.Format(100, 100).Contains("100%") && estimate.Format(-1, 100).Contains("0%"), "Progress is out of range."); estimate.Reset("Complete"); Assert(estimate.Format(0, 0).Contains("100%"), "Completion percentage missing.");
            });
            Test("ambiguous-and-invalid-json-rejected", delegate {
                foreach (var raw in new[] { "{\"files\":[],\"files\":[]}", "{\"id\":1,\"\\u0069d\":2}", "{\"n\":NaN}", "null", "{\"x\":[}", "{x:1}", "{'x':1}", "{\"x\":1,}", "{\"x\":01}", "{\"x\":\"\\x41\"}" })
                    Rejected(() => StrictJson.Read(Encoding.UTF8.GetBytes(raw), 4096));
                Rejected(() => StrictJson.Read(new byte[] { 255 }, 4096));
                Rejected(() => StrictJson.Read(Encoding.UTF8.GetBytes(new string('[', 70) + new string(']', 70)), 4096));
            });
            Test("temporary-network-failure-retries-and-reuses-success", delegate {
                var state=Fresh();var source=new RepairSource();source.Publish(new Dictionary<string,Dictionary<string,byte[]>> {{"Small",Files("a.ogg")}});source.TransientFailures=2;
                Apply(new Updater(source,state));Complete(state.patches_dir,"Small");Assert(source.Downloads.Count==3,"Transient transfer did not recover in three attempts.");
            });
            Test("persistent-network-failure-has-three-attempt-limit", delegate {
                var state=Fresh();var source=new RepairSource();source.Publish(new Dictionary<string,Dictionary<string,byte[]>> {{"Small",Files("a.ogg")}});source.TransientFailures=99;
                Rejected(()=>Apply(new Updater(source,state)));Assert(source.Downloads.Count==3,"Transfer retry limit exceeded.");Assert(!File.Exists(Path.Combine(state.patches_dir,"Small/patch.json")),"Failed transfer advertised a complete patch.");
            });
            Console.WriteLine("TOTAL " + passed + " recovery/selection cases passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
