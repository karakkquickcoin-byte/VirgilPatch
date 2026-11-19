// 1.5.13 Update-button gate. Fixtures: run E:\M2FX_Work\virgil\gate_publish_smoke.py, then pass a fresh scratch
// folder and its FIXTURES folder (holding week1/ and week2/ snapshots). Compile like LockTests with /main:GateTests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VirgilPatch;
class GateTests {
 static string root,fixtures; static int count;
 static void Test(string name,Action fn){fn();Console.WriteLine("PASS "+name);count++;}
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
 static void Reject(Action fn){try{fn();}catch(Exception){return;}throw new Exception("Expected rejection");}
 static State Fresh(){string dir=Path.Combine(root,Guid.NewGuid().ToString("N"));Config.StateDir=Path.Combine(dir,"state");var s=new State{patches_dir=Path.Combine(dir,"Patches")};Directory.CreateDirectory(s.patches_dir);return s;}
 static LockedSource Source(string week,Func<string,bool,string> ask){return new LockedSource(new FolderSource(Path.Combine(fixtures,week)),ask);}
 static Func<string,bool,string> Code(string code,List<string> asked=null){return (n,r)=>{if(asked!=null)asked.Add(n);return code;};}
 static Func<string,bool,string> NoPrompt(){return (n,r)=>{throw new Exception("Prompted although this Pass was already unlocked");};}
 static bool Empty(State s){return !Directory.EnumerateFileSystemEntries(s.patches_dir).Any();}
 static void Apply(Updater u){var plan=u.Check();if(!plan.UpToDate)u.Apply(plan,CancellationToken.None);}
 static int Main(string[] args){try{root=args[0];fixtures=args[1];Directory.CreateDirectory(root);
 Test("cancel-writes-no-game-files",()=>{var s=Fresh();Reject(()=>new Updater(Source("week1",(n,r)=>null),s).Catalog());Assert(Empty(s),"Cancel wrote files");});
 Test("wrong-code-three-times-writes-no-game-files",()=>{var s=Fresh();var asked=new List<string>();Reject(()=>new Updater(Source("week1",Code("wrong code",asked)),s).Catalog());Assert(asked.Count==3&&asked.All(n=>n=="0.1"),"Expected three prompts for Pass 0.1");Assert(Empty(s),"Wrong code wrote files");Assert(!File.Exists(LockedSource.GateMemoryPath),"Wrong code was remembered");});
 Test("weekly-code-installs-plain-files-once",()=>{var s=Fresh();var asked=new List<string>();var u=new Updater(Source("week1",Code("fixture-week-one",asked)),s);Apply(u);Assert(asked.Count==1,"Asked "+asked.Count+" times in one Update");Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="first secret","Wrong installed file");Assert(u.Check().UpToDate,"No-op check failed");});
 Test("unlocked-pass-remembered-across-launches-without-storing-code",()=>{var s=Fresh();Apply(new Updater(Source("week1",Code("fixture-week-one")),s));Assert(!File.ReadAllText(LockedSource.GateMemoryPath).Contains("fixture-week-one"),"Code stored in plain text");Assert(new Updater(Source("week1",NoPrompt()),s).Check().UpToDate,"Remembered Pass re-prompted or changed");});
 Test("new-pass-asks-again-and-rejects-last-weeks-code",()=>{var s=Fresh();Apply(new Updater(Source("week1",Code("fixture-week-one")),s));var asked=new List<string>();Reject(()=>new Updater(Source("week2",Code("fixture-week-one",asked)),s).Catalog());Assert(asked.Count==3&&asked[0]=="0.2","New Pass did not ask for its own code");});
 Test("rotation-downloads-only-changed-files",()=>{var s=Fresh();Apply(new Updater(Source("week1",Code("fixture-week-one")),s));var u=new Updater(Source("week2",Code("fixture-week-two")),s);var plan=u.Check();Assert(plan.Fetch.Count==2,"Expected 2 changed files, got "+plan.Fetch.Count);u.Apply(plan,CancellationToken.None);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="second secret","Update failed");Assert(File.ReadAllText(u.Local("Test/same.ogg"))=="unchanged","Unchanged file lost");});
 Test("recovery-code-unlocks",()=>{var s=Fresh();var u=new Updater(Source("week2",Code("fixture-recovery-only")),s);Apply(u);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="second secret","Recovery mismatch");});
 Test("index-without-gate-never-prompts",()=>{string plain=Path.Combine(root,"plain");CopyTree(Path.Combine(fixtures,"week1"),plain);var js=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};var idx=js.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(plain,"index.json")));Assert(idx.Remove("gate"),"Fixture had no gate");File.WriteAllText(Path.Combine(plain,"index.json"),js.Serialize(idx),new UTF8Encoding(false));var s=Fresh();var u=new Updater(new LockedSource(new FolderSource(plain),NoPrompt()),s);Apply(u);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="first secret","Plain install failed");});
 Test("tampered-gate-rejected",()=>{string bad=Path.Combine(root,"bad");CopyTree(Path.Combine(fixtures,"week1"),bad);var p=Path.Combine(bad,"index.json");File.WriteAllText(p,File.ReadAllText(p).Replace("\"iterations\":600000","\"iterations\":1"),new UTF8Encoding(false));var s=Fresh();Reject(()=>new Updater(new LockedSource(new FolderSource(bad),Code("fixture-week-one")),s).Catalog());Assert(Empty(s),"Tampered gate wrote files");});
 Console.WriteLine("PASSED "+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
 static void CopyTree(string from,string to){foreach(var d in Directory.GetDirectories(from,"*",SearchOption.AllDirectories))Directory.CreateDirectory(d.Replace(from,to));Directory.CreateDirectory(to);foreach(var f in Directory.GetFiles(from,"*",SearchOption.AllDirectories))File.Copy(f,f.Replace(from,to));}
}
