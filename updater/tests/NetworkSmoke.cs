using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using VirgilPatch;
class NetworkSmoke
{
    static int Main(string[] args)
    {
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            ServicePointManager.CheckCertificateRevocationList = true;
            var source = new GitHubSource();
            var commit = source.ResolveCommit();
            var bytes = source.Get(commit, "index.json");
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var index = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
            var patches = (Dictionary<string, object>)index["patches"];
            var patch = (Dictionary<string, object>)patches.First().Value;
            var files = (Dictionary<string, object>)patch["files"];
            var asset = files.Where(f => Convert.ToInt64(((Dictionary<string, object>)f.Value)["size"]) >= 128)
                .OrderBy(f => Convert.ToInt64(((Dictionary<string, object>)f.Value)["size"])).First();
            var info = (Dictionary<string, object>)asset.Value;
            var repoPath = (string)patch["repo_dir"] + "/" + asset.Key;
            var dest = Path.GetFullPath(args[0]);
            source.Download(commit, repoPath, dest, Convert.ToInt64(info["size"]), null);
            if (Updater.BlobSha(dest) != (string)info["sha"]) throw new Exception("Live asset checksum mismatch.");
            Console.WriteLine("PASS live HTTPS resolve, index retrieval and size-limited asset download");
            Console.WriteLine("Commit: " + commit);
            Console.WriteLine("Index bytes: " + bytes.Length);
            Console.WriteLine("Verified asset: " + repoPath + " (" + new FileInfo(dest).Length + " bytes)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
