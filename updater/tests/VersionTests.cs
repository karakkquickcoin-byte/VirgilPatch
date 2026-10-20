using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VirgilPatch;
class VersionTests {
 static JavaScriptSerializer js=new JavaScriptSerializer();
 static string key="Test(3.2.0)";
 static byte[] Bytes(string s){return Encoding.UTF8.GetBytes(s);}
 static void Assert(bool ok,string msg){if(!ok)throw new Exception(msg);}
 static void Publish(RepairSource src,string version,string content){
  string path="patches/Test("+version+")";byte[] asset=Bytes(content);
  byte[] meta=Bytes(js.Serialize(new {name="Test("+version+")",version=version,files=new[]{new{file="a.ogg",id=123}}}));
  src.Data[path+"/a.ogg"]=asset;src.Data[path+"/patch.json"]=meta;
  src.Data["index.json"]=Bytes(js.Serialize(new {format=2,published="test",patches=new Dictionary<string,object>{{key,new {version=version,repo_dir=path,patch_json=RepairSource.Info(meta),files=new Dictionary<string,object>{{"a.ogg",RepairSource.Info(asset,123)}}}}}}));
 }
 static void Apply(Updater u){var p=u.Check(new[]{key});if(!p.UpToDate)u.Apply(p,CancellationToken.None);}
 static int Main(string[] args){try{
  string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);Config.StateDir=Path.Combine(root,"state");
  var state=new State{patches_dir=Path.Combine(root,"Patches")};Directory.CreateDirectory(state.patches_dir);
  var src=new RepairSource();Publish(src,"5.0.0","first");var u=new Updater(src,state);
  var catalog=u.Catalog();Assert(catalog.Patches.Single()==key && catalog.Versions[key]=="5.0.0","Catalog lost stable identity or version");
  Apply(u);string local=Path.Combine(state.patches_dir,key+"/a.ogg");Assert(File.Exists(local),"First install renamed local folder");
  int count=src.Downloads.Count;Publish(src,"5.0.0.1","first");Apply(u);Assert(src.Downloads.Count==count,"Metadata revision redownloaded unchanged assets");
  Assert(!Directory.Exists(Path.Combine(state.patches_dir,"Test(5.0.0.1)")),"Created versioned local folder");
  Publish(src,"5.0.0.2","second");Apply(u);Assert(src.Downloads.Count==count+1 && File.ReadAllText(local)=="second","Differential update failed");
  File.Delete(local);Apply(u);Assert(File.Exists(local),"Repair missing asset failed");File.WriteAllText(local,"corrupt");Apply(u);Assert(File.ReadAllText(local)=="second","Repair corrupt asset failed");
  File.Delete(Path.Combine(state.patches_dir,key+"/patch.json"));Apply(u);Assert(File.Exists(Path.Combine(state.patches_dir,key+"/patch.json")),"Repair metadata failed");
  foreach(string bad in new[]{"patches/Other(5.0.0.2)","patches/../Test(5.0.0.2)","patches/Test(3.2.0)","patches/Test(5.0.0.3)"}){
   Publish(src,"5.0.0.2","second");var index=StrictJson.Read(src.Data["index.json"],100000);var entry=(Dictionary<string,object>)((Dictionary<string,object>)index["patches"])[key];entry["repo_dir"]=bad;src.Data["index.json"]=Bytes(js.Serialize(index));
   bool rejected=false;try{u.Catalog();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Forged repository path accepted");
  }
  foreach(string bad in new[]{"05.0.0","5.0","5.0.0.-1","5.0.0/evil","5.0.0.1.2","5.0.0\n"}){Publish(src,bad,"second");bool rejected=false;try{u.Catalog();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Invalid version accepted");}
  Console.WriteLine("PASS versioned install, metadata-only update, differential assets, missing/corrupt/metadata repair, forged paths and invalid versions");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
