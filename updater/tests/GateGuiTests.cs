// 1.5.13 Update-button gate in the actual launcher window. Pass a fresh scratch folder, a gated fixture snapshot
// (week2 from gate_publish_smoke.py) and optionally a PNG path for a picture of the real code box.
// Compile with LauncherTests' sources/resources/references and /main:GateGuiTests.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using VirgilPatch;
static class GateGuiTests {
    static object Field(MainForm f, string n) { return typeof(MainForm).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f); }
    static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
    static MainForm observed; static int promptCount;
    static void Pump(Func<bool> ready, int seconds = 30) {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready()) { if (DateTime.UtcNow > end) throw new Exception("Timed out; prompts=" + promptCount + " forms=" + string.Join(",", Application.OpenForms.Cast<Form>().Select(f => f.Text)) + (observed == null ? "" : " busy=" + Field(observed, "isBusy") + " status=" + ((Label)Field(observed, "status")).Text + " log=" + ((TextBox)Field(observed, "log")).Text)); Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            var root = Path.GetFullPath(args[0]); var fixture = args[1]; string png = args.Length > 2 ? args[2] : null;
            // A valid-looking Epsilon layout so the periodic folder check passes.
            var main = Path.Combine(root, "Epsilon"); var retail = Path.Combine(main, "_retail_"); var patches = Path.Combine(retail, "Patches");
            Directory.CreateDirectory(patches); Directory.CreateDirectory(Path.Combine(main, "Data"));
            File.WriteAllText(Path.Combine(main, "Epsilon.exe"), "fixture"); File.WriteAllText(Path.Combine(retail, "Wow.exe"), "fixture"); File.WriteAllText(Path.Combine(retail, "EpsilonAddon.dll"), "fixture");
            Config.StateDir = Path.Combine(root, "state"); new State { patches_dir = patches }.Save();
            Config.StartAmbienceMuted = true;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int prompts = 0; string shown = null; bool catalogReached = false;
            var watch = new System.Windows.Forms.Timer { Interval = 100 };
            watch.Tick += delegate {
                foreach (Form o in Application.OpenForms.Cast<Form>().ToArray()) {
                    if (!o.Text.StartsWith("Weekly patch password") || o.Tag as string == "answered") continue;
                    o.Tag = "answered"; prompts++; promptCount = prompts;
                    shown = string.Join(" / ", o.Controls.OfType<Label>().Select(l => l.Text));
                    if (png != null && prompts == 1) using (var b = new Bitmap(o.Width, o.Height)) { o.DrawToBitmap(b, new Rectangle(Point.Empty, b.Size)); b.Save(png); }
                    o.Controls.OfType<TextBox>().First().Text = "fixture-week-two";
                    o.Controls.OfType<Button>().First(b => b.Text == "Unlock").PerformClick();
                }
            };
            watch.Start();
            using (var form = new MainForm(new FolderSource(fixture), null, true) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) }) {
                observed = form; form.ConfirmInstallation = x => DialogResult.Yes;
                form.ChoosePatches = catalog => { catalogReached = true; return null; };     // stop before any download
                form.Show(); Pump(() => !(bool)Field(form, "isBusy"));
                var update = (Button)Field(form, "updateBtn");
                update.PerformClick(); Pump(() => catalogReached && !(bool)Field(form, "isBusy"));
                Assert(prompts == 1, "Expected one code prompt, saw " + prompts);
                Assert(shown.Contains("Enter this week's patch password") && shown.Contains("Pass 0.2"), "Prompt text: " + shown);
                Console.WriteLine("PASS Update in the real window asks for the weekly code (" + shown + ") before the patch list");
                catalogReached = false; update.PerformClick(); Pump(() => catalogReached && !(bool)Field(form, "isBusy"));
                Assert(prompts == 1, "Unlocked Pass asked again");
                Console.WriteLine("PASS second Update with the same Pass does not ask again");
                form.Close();
            }
            Console.WriteLine("TOTAL 2 gate GUI scenarios passed"); return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
    }
}
