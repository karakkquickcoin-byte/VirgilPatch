using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VirgilPatch;
class CaseRepairTests {
 static string root; static int passed;
 static byte[] B(string s){return Encoding.UTF8.GetBytes(s);}
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
 static void Reject(Action a){try{a();}catch(Exception){return;}throw new Exception("Expected rejection");}
 static void Test(string n,Action a){a();passed++;Console.WriteLine("PASS "+n);}
 static State Fresh(){var p=Path.Combine(root,Guid.NewGuid().ToString("N"));Config.StateDir=Path.Combine(p,"state");var s=new State{patches_dir=Path.Combine(p,"Patches")};Directory.CreateDirectory(s.patches_dir);return s;}
 static RepairSource Source(params string[] names){var s=new RepairSource();Publish(s,names);return s;}
 static void Publish(RepairSource s,params string[] names){s.Publish(new Dictionary<string,Dictionary<string,byte[]>>{{"Small",names.ToDictionary(n=>n,n=>B("unchanged asset bytes"))}});}
 static void Apply(Updater u){var p=u.Check();if(!p.UpToDate)u.Apply(p,CancellationToken.None);}
 static bool Exact(string root,string relative){foreach(var part in relative.Split('/')){if(!Directory.EnumerateFileSystemEntries(root).Any(p=>Path.GetFileName(p)==part))return false;root=Path.Combine(root,part);}return true;}
 static void Rename(string before,string after,bool dir=false){string temp=before+".fixture";if(dir){Directory.Move(before,temp);Directory.Move(temp,after);}else{File.Move(before,temp);File.Move(temp,after);}}
 static void Leaf(){var s=Fresh();var src=Source("wallpiece05.wmo","wallpiece05_000.wmo");var u=new Updater(src,s);Apply(u);Publish(src,"Wallpiece05.wmo","Wallpiece05_000.wmo");src.Downloads.Clear();Apply(u);Assert(Exact(Path.Combine(s.patches_dir,"Small"),"Wallpiece05.wmo"),"Epsilon exact-name lookup still fails");Assert(Exact(Path.Combine(s.patches_dir,"Small"),"Wallpiece05_000.wmo"),"Companion spelling not fixed");Assert(src.Downloads.Count==0,"Casing change redownloaded unchanged files");Assert(u.Check().UpToDate,"Repair repeats");}
 static int Main(string[] a){try{root=Path.GetFullPath(a[0]);Directory.CreateDirectory(root);
 Test("case-only-published-update-repairs-reused-assets-without-downloads",Leaf);
 if(a.Length>1&&a[1]=="baseline")return 0;
 Test("current-metadata-with-wrong-disk-case-is-repaired",delegate{var s=Fresh();var src=Source("Wallpiece05.wmo");var u=new Updater(src,s);Apply(u);string dir=Path.Combine(s.patches_dir,"Small");Rename(Path.Combine(dir,"Wallpiece05.wmo"),Path.Combine(dir,"wallpiece05.wmo"));var p=u.Check();Assert(!p.UpToDate&&p.Fetch.Count==0&&p.PatchJsons.Count==0,"Current metadata hides disk spelling mismatch");Assert(PatchDialogs.Summary(p).Contains("Filename casing repairs: 1"),"Preview hides spelling repair");u.Apply(p,CancellationToken.None);Assert(Exact(dir,"Wallpiece05.wmo")&&u.Check().UpToDate,"Repair incomplete");});
 Test("nested-directories-and-replaced-files-get-exact-case",delegate{var s=Fresh();var src=Source("world/wmo/wallpiece05.wmo","world/wmo/other.wmo");var u=new Updater(src,s);Apply(u);Publish(src,"World/WMO/Wallpiece05.wmo","World/WMO/Other.wmo");Apply(u);Assert(Exact(Path.Combine(s.patches_dir,"Small"),"World/WMO/Wallpiece05.wmo")&&Exact(Path.Combine(s.patches_dir,"Small"),"World/WMO/Other.wmo"),"Nested spelling mismatch");Assert(u.Check().UpToDate,"Nested repair repeats");});
 Test("check-and-cancel-preserve-installed-spelling",delegate{var s=Fresh();var src=Source("lower.wmo");var u=new Updater(src,s);Apply(u);Publish(src,"Lower.wmo");var p=u.Check();Assert(Exact(Path.Combine(s.patches_dir,"Small"),"lower.wmo"),"Check renamed files");var c=new CancellationTokenSource();c.Cancel();Reject(()=>u.Apply(p,c.Token));Assert(Exact(Path.Combine(s.patches_dir,"Small"),"lower.wmo"),"Cancelled repair renamed files");});
 Test("metadata-with-different-case-is-repaired",delegate{var s=Fresh();var src=Source("a.wmo");var u=new Updater(src,s);Apply(u);string dir=Path.Combine(s.patches_dir,"Small");Rename(Path.Combine(dir,"patch.json"),Path.Combine(dir,"Patch.JSON"));Apply(u);Assert(Exact(dir,"patch.json")&&u.Check().UpToDate,"Metadata spelling mismatch");});
 Test("bad-payload-keeps-old-spelling-and-metadata",delegate{var s=Fresh();var src=Source("old.wmo");var u=new Updater(src,s);Apply(u);string meta=File.ReadAllText(u.Local("Small/patch.json"));Publish(src,"Old.wmo","new.wmo");src.FailAll=true;Reject(()=>Apply(u));Assert(File.ReadAllText(u.Local("Small/patch.json"))==meta&&Exact(Path.Combine(s.patches_dir,"Small"),"old.wmo"),"Failed update changed advertised spelling");});
 Test("inconsistent-directory-casing-rejected-before-install",delegate{var s=Fresh();var u=new Updater(Source("World/a.wmo","world/b.wmo"),s);Reject(()=>u.Check());Assert(!Directory.EnumerateFileSystemEntries(s.patches_dir).Any(),"Inconsistent manifest wrote files");});
 Test("unselected-patches-and-unmanaged-files-are-preserved",delegate{var s=Fresh();var src=Source("lower.wmo");var u=new Updater(src,s);Apply(u);string dir=Path.Combine(s.patches_dir,"Small");File.WriteAllText(Path.Combine(dir,"Keep.TXT"),"keep");string other=Path.Combine(s.patches_dir,"Other");Directory.CreateDirectory(other);File.WriteAllText(Path.Combine(other,"lower.wmo"),"other");Publish(src,"Lower.wmo");Apply(u);Assert(File.ReadAllText(Path.Combine(dir,"Keep.TXT"))=="keep"&&Exact(other,"lower.wmo"),"Unselected/unmanaged files changed");});
 Console.WriteLine("TOTAL "+passed+" filename casing cases passed");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
