using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VirgilPatch;

class ManualUpdateSource : GitHubSource, ISource
{
    public int Resolves, Programs, Indexes, Downloads;
    string ISource.ResolveCommit() { Interlocked.Increment(ref Resolves); return "local"; }
    string ISource.Describe() { return "offline explicit-update fixture"; }
    byte[] ISource.Get(string commit, string path)
    {
        if (path == ProgramRelease.ManifestPath) { Interlocked.Increment(ref Programs); throw new WebException("fixture program check unavailable"); }
        if (path == "index.json") { Interlocked.Increment(ref Indexes); return Encoding.UTF8.GetBytes("{\"format\":2,\"published\":\"manual test\",\"patches\":{}}"); }
        throw new Exception("Unexpected request");
    }
    void ISource.Download(string commit, string path, string dest, long max, Action<long> progress) { Interlocked.Increment(ref Downloads); throw new Exception("Unexpected download"); }
}
class ManualUpdateTests
{
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pump(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!ready()) { if (DateTime.UtcNow > end) throw new Exception("Manual-update test timed out."); Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var patches = Path.Combine(root, "DEV-Patches"); Directory.CreateDirectory(Path.Combine(patches, "Dev"));
            var local = Path.Combine(patches, "Dev/work.blp"); File.WriteAllText(local, "UNPUBLISHED DEVELOPMENT CONTENT");
            var stamp = File.GetLastWriteTimeUtc(local);
            Config.StateDir = Path.Combine(root, "state"); Config.StartAmbienceMuted = true;
            new State { patches_dir = patches }.Save();
            var statePath = Path.Combine(Config.StateDir, "state.json");
            var stateBytes = File.ReadAllText(statePath); var stateStamp = File.GetLastWriteTimeUtc(statePath);
            var source = new ManualUpdateSource();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(source) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) })
            {
                form.Show();
                Pump(() => Field(form, "ambience") != null && ((LauncherAmbience)Field(form, "ambience")).Player.Position > TimeSpan.FromMilliseconds(300) &&
                           source.Programs == 1 && !(bool)Field(form, "isBusy"));
                Assert(source.Resolves == 1 && source.Programs == 1 && source.Indexes == 0 && source.Downloads == 0 && Field(form, "plan") == null, "Launch must check only the launcher channel, never the patch index.");
                Assert(File.ReadAllText(local) == "UNPUBLISHED DEVELOPMENT CONTENT" && File.GetLastWriteTimeUtc(local) == stamp, "Launch touched unpublished files.");
                Assert(File.ReadAllText(statePath) == stateBytes && File.GetLastWriteTimeUtc(statePath) == stateStamp, "Launch rewrote saved patch state.");
                Assert(((TextBox)Field(form, "log")).Text.Contains("Launcher update check unavailable") && ((Button)Field(form, "updateBtn")).Enabled, "A failed launch check must be reported and leave Update usable.");
                Console.WriteLine("PASS launch checks only for a launcher upgrade (no patch requests) and preserves unpublished files and saved patch state");
                var other = Path.Combine(root, "Other-DEV-Patches"); Directory.CreateDirectory(other);
                var otherFile = Path.Combine(other, "local.blp"); File.WriteAllText(otherFile, "ANOTHER UNPUBLISHED FILE");
                typeof(MainForm).GetMethod("RememberFolder", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { other });
                Application.DoEvents();
                Assert(source.Resolves == 1 && source.Indexes == 0 && source.Programs == 1 && File.ReadAllText(otherFile) == "ANOTHER UNPUBLISHED FILE" && State.Load().patches_dir == other, "Selecting a folder triggered an update.");
                Console.WriteLine("PASS folder selection only remembers the path; it does not check or change patches");
                ((Button)Field(form, "updateBtn")).PerformClick();
                Pump(() => ((TextBox)Field(form, "log")).Text.Contains("You're up to date.") && !(bool)Field(form, "isBusy"));
                Assert(source.Programs == 2 && source.Indexes == 1 && source.Resolves == 3 && source.Downloads == 0, "Explicit Update did not check both channels exactly once.");
                Assert(File.ReadAllText(local) == "UNPUBLISHED DEVELOPMENT CONTENT" && File.ReadAllText(otherFile) == "ANOTHER UNPUBLISHED FILE", "Unmanaged development files changed.");
                Console.WriteLine("PASS explicit Update checks both program and patch channels and preserves unmanaged development files");
                form.Close();
            }
            var restarted = new ManualUpdateSource();          // the relaunch after an upgrade (--skip-self-update-once)
            using (var form = new MainForm(restarted, null, true) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) })
            {
                form.Show();
                Pump(() => Field(form, "ambience") != null && ((LauncherAmbience)Field(form, "ambience")).Player.Position > TimeSpan.FromMilliseconds(300));
                Assert(restarted.Resolves == 0 && restarted.Programs == 0 && restarted.Indexes == 0 && restarted.Downloads == 0, "The post-upgrade relaunch must not check again.");
                Console.WriteLine("PASS the relaunch after an upgrade makes no requests");
                form.Close();
            }
            Console.WriteLine("TOTAL 4 manual-update integration scenarios passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
