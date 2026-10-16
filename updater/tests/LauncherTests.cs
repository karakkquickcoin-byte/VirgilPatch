using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using VirgilPatch;

class LauncherFixtureSource : ISource
{
    public Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
    public List<string> Downloads = new List<string>();
    public ManualResetEvent DownloadGate;
    public string ResolveCommit() { return "local"; }
    public string Describe() { return "offline launcher test"; }
    public byte[] Get(string commit, string path) { return Data[path]; }
    public void Download(string commit, string path, string dest, long max, Action<long> progress)
    {
        lock (Downloads) Downloads.Add(path);
        if (DownloadGate != null && !DownloadGate.WaitOne(10000)) throw new IOException("Test download gate timed out.");
        using (var input = new MemoryStream(Data[path]))
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write)) GitHubSource.CopyLimited(input, output, max, progress);
    }
    static object Info(byte[] data, int id)
    {
        using (var sha = SHA1.Create()) return new { size = data.Length, id = id,
            sha = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("blob " + data.Length + "\0").Concat(data).ToArray())).Replace("-", "").ToLowerInvariant() };
    }
    public void Publish(string content)
    {
        var js = new JavaScriptSerializer();
        Data["patches/Test/a.ogg"] = Encoding.UTF8.GetBytes(content);
        Data["patches/Test/b.ogg"] = Encoding.UTF8.GetBytes("unchanged asset");
        var metadata = Encoding.UTF8.GetBytes(js.Serialize(new { name = "Test", version = "1", files = new[] { new { file = "a.ogg", id = 1 }, new { file = "b.ogg", id = 2 } } }));
        Data["patches/Test/patch.json"] = metadata;
        Data["index.json"] = Encoding.UTF8.GetBytes(js.Serialize(new { format = 2, published = "launcher test", patches = new Dictionary<string, object> {
            { "Test", new { repo_dir = "patches/Test", patch_json = Info(metadata, 0), files = new Dictionary<string, object> {
                { "a.ogg", Info(Data["patches/Test/a.ogg"], 1) }, { "b.ogg", Info(Data["patches/Test/b.ogg"], 2) } } } } } }));
    }
}

class LauncherTests
{
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pump(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!ready()) { if (DateTime.UtcNow > end) throw new Exception("Launcher operation timed out."); Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var patches = Path.Combine(root, "Patches"); Directory.CreateDirectory(patches);
            Config.StateDir = Path.Combine(root, "state"); new State { patches_dir = patches }.Save();
            var source = new LauncherFixtureSource(); source.Publish("first asset");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(source) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
            {
                form.Show(); Pump(() => Field(form, "plan") != null && !(bool)Field(form, "isBusy"));
                Assert(source.Downloads.Count == 0 && !File.Exists(Path.Combine(patches, "Test/a.ogg")), "Startup must only check; patch installation needs a click.");
                Console.WriteLine("PASS launcher startup checks without installing assets");
                var update = (Button)Field(form, "updateBtn"); var browse = (Button)Field(form, "browse");
                Assert(form.Text == "Chronicles of Lordaeron" && update.Text == "Update" && browse.Text == "Select folder", "Incorrect launcher branding/actions.");
                using (source.DownloadGate = new ManualResetEvent(false))
                {
                    update.PerformClick(); Pump(() => source.Downloads.Count > 0);
                    Assert(!update.Enabled && !browse.Enabled, "Actions must be disabled while updating.");
                    update.PerformClick();
                    if (args.Length > 1) using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); }
                    source.DownloadGate.Set(); Pump(() => !(bool)Field(form, "isBusy"));
                }
                source.DownloadGate = null;
                Assert(source.Downloads.Count == 2 && File.ReadAllText(Path.Combine(patches, "Test/a.ogg")) == "first asset", "Update click did not install exactly the required files.");
                Assert(State.Load().patches_dir == patches, "Folder selection was not preserved.");
                Console.WriteLine("PASS Update installs required files once and preserves folder selection");
                update.PerformClick(); Pump(() => !(bool)Field(form, "isBusy"));
                Assert(source.Downloads.Count == 2, "Current files were downloaded again.");
                Console.WriteLine("PASS Update rechecks an up-to-date installation without downloading assets");
                var keep = Path.Combine(patches, "Test/b.ogg"); var stamp = File.GetLastWriteTimeUtc(keep);
                source.Publish("changed asset"); update.PerformClick(); Pump(() => !(bool)Field(form, "isBusy"));
                Assert(source.Downloads.Count == 3 && File.ReadAllText(Path.Combine(patches, "Test/a.ogg")) == "changed asset" && File.GetLastWriteTimeUtc(keep) == stamp, "Changed-file update redownloaded unchanged assets.");
                Console.WriteLine("PASS one Update click checks and downloads only the changed file");
                var details = (Button)Field(form, "details"); var log = (TextBox)Field(form, "log");
                Assert(!log.Visible, "Diagnostics should be tucked away initially."); details.PerformClick(); Assert(log.Visible, "Details did not open."); details.PerformClick(); Assert(!log.Visible, "Details did not close.");
                Console.WriteLine("PASS diagnostic details open and close without replacing the launcher");
                source.Data["index.json"] = Encoding.UTF8.GetBytes("invalid manifest"); update.PerformClick(); Pump(() => !(bool)Field(form, "isBusy"));
                Assert(update.Enabled && browse.Enabled && ((Label)Field(form, "status")).Text.Contains("Details") && source.Downloads.Count == 3, "Failure must be visible and allow a safe retry.");
                Console.WriteLine("PASS invalid manifest leaves assets untouched and exposes a recoverable error");
                form.Close();
            }
            Console.WriteLine("TOTAL 6 launcher integration scenarios passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
