using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public int Reads;
    public ManualResetEvent DownloadGate;
    public string ResolveCommit() { return "local"; }
    public string Describe() { return "offline launcher test"; }
    public byte[] Get(string commit, string path) { Interlocked.Increment(ref Reads); return Data[path]; }
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
    delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    // The owner uses only synthetic files in the scratch folder. If the real game is open,
    // accept ONLY this test form's specific WoW warning, leaving the user's game untouched.
    static System.Windows.Forms.Timer AcceptFixtureWarning(MainForm owner)
    {
        var thread = GetCurrentThreadId();
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += delegate {
            EnumThreadWindows(thread, delegate(IntPtr dialog, IntPtr unused) {
                if (GetWindow(dialog, 4) != owner.Handle) return true;
                var name = new StringBuilder(64); GetClassName(dialog, name, name.Capacity);
                if (name.ToString() != "#32770") return true;
                bool warning = false;
                EnumChildWindows(dialog, delegate(IntPtr child, IntPtr ignored) {
                    var text = new StringBuilder(512); GetWindowText(child, text, text.Capacity);
                    if (text.ToString().StartsWith("WoW is running. Close it first")) warning = true;
                    return true;
                }, IntPtr.Zero);
                if (warning) PostMessage(dialog, 0x0111, new IntPtr(6), IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        };
        timer.Start(); return timer;
    }
    static MainForm observed;
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pump(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!ready()) { if (DateTime.UtcNow > end) throw new Exception("Launcher operation timed out. " + (observed == null ? "" : "busy=" + Field(observed, "isBusy") + "; " + ((TextBox)Field(observed, "log")).Text)); Application.DoEvents(); Thread.Sleep(10); }
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
            Config.StartAmbienceMuted = true;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(source) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
            {
                using (var warnings = AcceptFixtureWarning(form))
                {
                observed = form;
                form.Show(); Pump(() => Field(form, "ambience") != null && ((LauncherAmbience)Field(form, "ambience")).Player.Position > TimeSpan.FromMilliseconds(150));
                Assert(source.Reads == 0 && source.Downloads.Count == 0 && Field(form, "plan") == null && !File.Exists(Path.Combine(patches, "Test/a.ogg")), "Startup must not check or update patches.");
                Console.WriteLine("PASS launcher startup makes no patch requests or changes");
                var update = (Button)Field(form, "updateBtn"); var browse = (Button)Field(form, "browse");
                Assert(form.Text == "Chronicles of Lordaeron" && update.Text == "Update" && browse.Text == "Select folder", "Incorrect launcher branding/actions.");
                using (source.DownloadGate = new ManualResetEvent(false))
                {
                    update.PerformClick();
                    Assert((bool)Field(form, "isBusy"), "Update click did not start a check; selectable=" + update.CanSelect + "; visible=" + update.Visible + "; enabled=" + update.Enabled);
                    Pump(() => source.Downloads.Count > 0);
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
                var before = update.Bounds; var size = form.ClientSize;
                form.ClientSize = new Size(size.Width * 13 / 10, size.Height * 13 / 10); Application.DoEvents();
                Assert(Math.Abs(update.Width - before.Width * 1.3) <= 2 && Math.Abs(update.Left - before.Left * 1.3) <= 2, "Resizing did not scale the launcher layout.");
                if (args.Length > 1) using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.ChangeExtension(args[1], ".resized.png")); }
                form.ClientSize = size; Application.DoEvents();
                Assert(update.Bounds == before, "Restoring the size did not restore the layout.");
                Console.WriteLine("PASS resizing scales every control and restores exactly");
                Assert(ChroniclesLinks.All.Length == 7 && ChroniclesLinks.All.All(l => ChroniclesLinks.IsAllowed(l.Url)) && ChroniclesLinks.All.Count(l => l.Featured) == 1, "Guides & Links list is incomplete or unsafe.");
                Assert(!ChroniclesLinks.IsAllowed("http://docs.google.com/x") && !ChroniclesLinks.IsAllowed("https://example.com/") && !ChroniclesLinks.IsAllowed("file:///C:/Windows"), "Link allow-list accepted an unexpected address.");
                Assert(ChroniclesLinks.PhaseId == "35602" && Field(form, "phasePlaque") is PhasePlaque && ((Control)Field(form, "phasePlaque")).Visible, "Phase ID plaque is missing.");
                using (var linksWindow = new LinksWindow { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
                {
                    linksWindow.Show(); Application.DoEvents();
                    Assert(linksWindow.Cards.Length == 7 && linksWindow.Cards.All(c => c.Visible && c.Width > 0), "Guides & Links window did not show every link.");
                    if (args.Length > 1) using (var bitmap = new Bitmap(linksWindow.Width, linksWindow.Height)) { linksWindow.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.ChangeExtension(args[1], ".links.png")); }
                    linksWindow.Close();
                }
                Console.WriteLine("PASS Guides & Links window lists the seven allowed links and the phase ID");
                form.Close();
                }
            }
            Console.WriteLine("TOTAL 8 launcher integration scenarios passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
