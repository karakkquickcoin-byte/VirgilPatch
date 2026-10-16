using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using VirgilPatch;

class FailingProgramSource : GitHubSource, ISource
{
    public string Mode;
    public int PatchChecks;
    string ISource.ResolveCommit() { return "local"; }
    string ISource.Describe() { return "offline GUI failure fixture"; }
    byte[] ISource.Get(string commit,string path)
    {
        if(path==ProgramRelease.ManifestPath)
        {
            if(Mode=="network") throw new WebException("simulated program-metadata network failure");
            return Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"9.0.0.0\",\"file\":\"updater/VirgilUpdater.exe\",\"size\":4096,\"sha256\":\""+
                new string('a',64)+"\",\"signature\":\""+Convert.ToBase64String(new byte[512])+"\"}");
        }
        if(path=="index.json") {PatchChecks++;return Encoding.UTF8.GetBytes("{\"format\":2,\"patches\":{},\"published\":\"fixture\"}");}
        throw new Exception("Unexpected metadata request.");
    }
    void ISource.Download(string commit,string path,string dest,long max,Action<long> progress) { throw new Exception("No payload should download in this fixture."); }
}
class SelfUpdateGuiTests
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            Config.StateDir=Path.Combine(root,"state");
            var patches=Path.Combine(root,"Patches");Directory.CreateDirectory(patches);
            new State {patches_dir=patches}.Save();
            var source=new FailingProgramSource {Mode=args[1]};
            Config.StartAmbienceMuted = true;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var form=new MainForm(source) {Opacity=0,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new System.Drawing.Point(-32000,-32000)};
            var log=(TextBox)typeof(MainForm).GetField("log",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
            int result=1;var started=DateTime.UtcNow;var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=delegate {
                if(log.Text.Contains("Program upgrade unavailable:") && log.Text.Contains("You're up to date.") && source.PatchChecks==1)
                {result=0;timer.Stop();form.Close();}
                else if(DateTime.UtcNow-started>TimeSpan.FromSeconds(15)) {Console.WriteLine(log.Text);timer.Stop();form.Close();}
            };
            form.Shown+=delegate {((Button)typeof(MainForm).GetField("updateBtn",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form)).PerformClick();timer.Start();};Application.Run(form);
            if(result==0) Console.WriteLine("PASS GUI "+args[1]+" program-upgrade failure: current program retained and patch check completed");
            return result;
        }
        catch(Exception ex) {Console.WriteLine("FAIL "+ex);return 1;}
    }
}
