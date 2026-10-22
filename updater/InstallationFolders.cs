using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
namespace VirgilPatch {
 sealed class InstallationInspection {
  public string Root, Main, Retail;
  public bool MainExecutable, Data, RetailDirectory, RetailExecutable, EpsilonAddon, Patches;
  public string InspectionError;
  public List<string> Nearby = new List<string>();
  public bool Valid { get { return InspectionError==null && MainExecutable && Data && RetailDirectory && RetailExecutable && EpsilonAddon && Patches; } }
  public string Describe() {
   var s=new StringBuilder("Is this your correct Patches folder? Please check.\r\n\r\n");
   s.AppendLine("1. Main Epsilon installation: "+Main).AppendLine("   Epsilon.exe: "+MainExecutable+"; Data folder: "+Data);
   s.AppendLine("2. Retail folder: "+Retail).AppendLine("   _retail_ layout: "+RetailDirectory+"; Wow.exe: "+RetailExecutable+"; EpsilonAddon.dll: "+EpsilonAddon);
   s.AppendLine("3. Patches folder: "+Root).AppendLine("   Existing Patches folder: "+Patches);
   if(InspectionError!=null)s.AppendLine("Inspection failed: "+InspectionError);
   s.AppendLine().AppendLine("Nearby Patches folders (bounded parent/sibling search):");
   foreach(var p in Nearby)s.AppendLine("   "+p+(string.Equals(p,Root,StringComparison.OrdinalIgnoreCase)?" [selected]":" [other installation candidate]"));
   if(Nearby.Count>1)s.AppendLine().AppendLine("More than one candidate exists. Confirm the installation you actually launch.");
   s.AppendLine().AppendLine(Valid?"Layout checks passed. Confirm this exact path before continuing.":"Required installation markers are missing. Select the correct installation to continue.");
   s.AppendLine("This check does not download files, move folders, or enable patches. File presence verifies layout; it does not verify the game executable's signature.");return s.ToString();
  }
 }
 static class InstallationFolders {
  static bool FileMarker(string p){Safety.NoLinks(p);return File.Exists(p);}
  static bool DirectoryMarker(string p){Safety.NoLinks(p);return Directory.Exists(p);}
  public static List<string> Nearby(string root) {
   var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var probes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   Action<string> probe=p=>{if(!probes.Add(p))return;try{Safety.NoLinks(p);if(Directory.Exists(p))result.Add(Path.GetFullPath(p));}catch(IOException){}catch(UnauthorizedAccessException){}};
   probe(root);string ancestor=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(root)));
   for(int depth=0;depth<3&&!string.IsNullOrEmpty(ancestor);depth++,ancestor=Path.GetDirectoryName(ancestor)) {
    probe(Path.Combine(ancestor,"_retail_","Patches"));
    try{Safety.NoLinks(ancestor);foreach(string child in Directory.EnumerateDirectories(ancestor).Take(64)){Safety.NoLinks(child);probe(Path.Combine(child,"_retail_","Patches"));}}catch(IOException){}catch(UnauthorizedAccessException){}
   }
   return result.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(64).ToList();
  }
  public static InstallationInspection Inspect(string root) {
   var x=new InstallationInspection{Root=root};
   try{
    x.Root=Path.GetFullPath(root);Safety.NoLinks(x.Root);x.Retail=Path.GetDirectoryName(x.Root);x.Main=Path.GetDirectoryName(x.Retail);
    x.MainExecutable=FileMarker(Path.Combine(x.Main,"Epsilon.exe"));x.Data=DirectoryMarker(Path.Combine(x.Main,"Data"));
    x.RetailDirectory=string.Equals(Path.GetFileName(x.Retail),"_retail_",StringComparison.OrdinalIgnoreCase)&&DirectoryMarker(x.Retail);
    x.RetailExecutable=FileMarker(Path.Combine(x.Retail,"Wow.exe"));x.EpsilonAddon=FileMarker(Path.Combine(x.Retail,"EpsilonAddon.dll"));
    x.Patches=string.Equals(Path.GetFileName(x.Root),"Patches",StringComparison.OrdinalIgnoreCase)&&DirectoryMarker(x.Root);x.Nearby=Nearby(x.Root);
   }catch(Exception e){x.InspectionError=PatchDiagnostics.ErrorCode(e);}return x;
  }
 }
 partial class MainForm {
  internal Func<InstallationInspection,DialogResult> ConfirmInstallation;
  internal Func<bool> SelectFolderForCheck;
  internal bool CheckInstallationFolders() {
   while(true) {
    var result=InstallationFolders.Inspect(state.patches_dir);diagnostics.Record("folder check",null,state.patches_dir);
    var answer=ConfirmInstallation!=null?ConfirmInstallation(result):PatchDialogs.Installation(this,result);
    if(answer==DialogResult.Yes&&result.Valid){state.folder_check_uses=0;state.Save();return true;}
    if(answer!=DialogResult.Retry)return false;
    if(!(SelectFolderForCheck!=null?SelectFolderForCheck():PickFolder()))return false;
   }
  }
 }
}
