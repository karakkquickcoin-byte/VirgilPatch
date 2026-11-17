using System;
using System.IO;
using System.Linq;
using System.Threading;
using VirgilPatch;
class LockTests {
 static string root,fixtures; static int count;
 static void Test(string name,Action fn){fn();Console.WriteLine("PASS "+name);count++;}
 static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
 static void Reject(Action fn){try{fn();}catch(Exception){return;}throw new Exception("Expected rejection");}
 static State Fresh(){string dir=Path.Combine(root,Guid.NewGuid().ToString("N"));Config.StateDir=Path.Combine(dir,"state");var s=new State{patches_dir=Path.Combine(dir,"Patches")};Directory.CreateDirectory(s.patches_dir);return s;}
 static LockedSource Source(string week,string password){return new LockedSource(new FolderSource(Path.Combine(fixtures,week)),(n,r)=>password);}
 static void Apply(Updater u){var plan=u.Check();if(!plan.UpToDate)u.Apply(plan,CancellationToken.None);}
 static int Main(string[] args){try{root=args[0];fixtures=args[1];Directory.CreateDirectory(root);
 Test("wrong-password-writes-no-game-files",()=>{var s=Fresh();Reject(()=>new Updater(Source("week1","wrong password"),s).Catalog());Assert(!Directory.EnumerateFileSystemEntries(s.patches_dir).Any(),"Wrong password wrote game files");});
 Test("cancel-writes-no-game-files",()=>{var s=Fresh();var source=new LockedSource(new FolderSource(Path.Combine(fixtures,"week1")),(n,r)=>null);Reject(()=>new Updater(source,s).Catalog());Assert(!Directory.EnumerateFileSystemEntries(s.patches_dir).Any(),"Cancel wrote files");});
 Test("weekly-password-installs-and-no-op-recheck",()=>{var s=Fresh();var u=new Updater(Source("week1","fixture-week-one"),s);Apply(u);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="first secret","Wrong installed plaintext");Assert(u.Check().UpToDate,"No-op check failed");Assert(!Directory.GetFiles(Path.GetDirectoryName(s.patches_dir),"*.encrypted",SearchOption.AllDirectories).Any(),"Ciphertext temporary left behind");});
 Test("recovery-password-installs",()=>{var s=Fresh();var u=new Updater(Source("week2","fixture-recovery-only"),s);Apply(u);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="second secret","Recovery mismatch");});
 Test("last-weeks-password-rejected",()=>{var s=Fresh();Reject(()=>new Updater(Source("week2","fixture-week-one"),s).Catalog());});
 Test("rotation-updates-selected-files-and-preserves-existing",()=>{var s=Fresh();Apply(new Updater(Source("week1","fixture-week-one"),s));var u=new Updater(Source("week2","fixture-week-two"),s);var plan=u.Check();Assert(plan.Fetch.Count==1,"Rotation redownloaded unchanged assets");u.Apply(plan,CancellationToken.None);Assert(File.ReadAllText(u.Local("Test/spoiler.ogg"))=="second secret","Update failed");});
 Test("tampered-payload-does-not-place-plaintext",()=>{byte[] key=new byte[64];byte[] blob=new byte[68];blob[0]=86;blob[1]=71;blob[2]=76;blob[3]=49;using(var inp=new MemoryStream(blob))using(var output=new MemoryStream()){Reject(()=>LockedSource.Decrypt(inp,output,key,100));Assert(output.Length==0,"Unauthenticated bytes escaped");}});
 Console.WriteLine("PASSED "+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
}
