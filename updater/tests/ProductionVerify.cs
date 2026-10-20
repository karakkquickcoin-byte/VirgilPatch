using System;using System.IO;using System.Linq;using System.Threading;using VirgilPatch;
class ProductionVerify {static int Main(string[] a){try{
 var release=ProgramRelease.Parse(File.ReadAllBytes(Path.Combine(a[0],"updater-release.json")));release.ValidateBinary(Path.Combine(a[0],"updater/VirgilUpdater.exe"));
 Console.WriteLine("PASS trusted release signature, binary checksum, size and assembly version "+release.Version);
 if(a.Length>1){var src=new FolderSource(a[1]);var p=new Updater(src,new State()).Catalog();
 foreach(var patch in p.Patches){var info=p.Files[patch+"/patch.json"];var bytes=src.Get("local",info.RepoPath);if(bytes.Length!=info.Size || RepairSource.Sha(bytes)!=info.Sha)throw new Exception("Metadata hash/size mismatch");Safety.PatchMetadata(bytes,patch,p.Files);}
 Console.WriteLine("PASS production catalog and all patch manifests: "+p.TotalFiles+" assets across "+p.Patches.Count+" patches");}
 return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}}
