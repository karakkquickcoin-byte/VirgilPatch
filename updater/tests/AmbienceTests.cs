using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VirgilPatch;

class AmbiencePatchSource : ISource
{
    public int Checks;
    public string ResolveCommit() { return "local"; }
    public string Describe() { return "offline ambience fixture"; }
    public byte[] Get(string commit, string path)
    {
        if (path != "index.json") throw new Exception("Unexpected request");
        Checks++;
        return Encoding.UTF8.GetBytes("{\"format\":2,\"published\":\"ambience test\",\"patches\":{}}");
    }
    public void Download(string commit, string path, string dest, long max, Action<long> progress) { throw new Exception("No asset downloads expected."); }
}

class AmbienceTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
    static void Pump(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!ready()) { if (DateTime.UtcNow > end) throw new Exception("Audio operation timed out."); Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
    }
    static Stream Resource() { return Assembly.GetExecutingAssembly().GetManifestResourceStream(LauncherAmbience.ResourceName); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            Config.StateDir = Path.Combine(root, "state"); Config.StartAmbienceMuted = true;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string cached;
            using (var resource = Resource())
            using (var locked = LauncherAmbience.PrepareAudio(resource, Config.StateDir, out cached))
            {
                Assert(locked.Length == resource.Length, "Cached track was truncated.");
                bool protectedFile = false;
                try { using (File.Open(cached, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                catch (IOException) { protectedFile = true; }
                Assert(protectedFile, "Verified audio must remain locked against writes while being decoded.");
            }
            var stamp = File.GetLastWriteTimeUtc(cached);
            using (var resource = Resource()) using (var locked = LauncherAmbience.PrepareAudio(resource, Config.StateDir, out cached))
                Assert(File.GetLastWriteTimeUtc(cached) == stamp, "Unchanged audio was unnecessarily rewritten.");
            Console.WriteLine("PASS ambience cache verifies bytes, blocks writes and reuses matching audio");

            var source = new AmbiencePatchSource();
            var patches = Path.Combine(root, "Patches"); Directory.CreateDirectory(patches);
            new State { patches_dir = patches }.Save();
            LauncherAmbience audio;
            using (var form = new MainForm(source) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) })
            {
                form.Show();
                Pump(() => Field(form, "ambience") != null || ((TextBox)Field(form, "log")).Text.Contains("Ambience unavailable:"));
                audio = (LauncherAmbience)Field(form, "ambience");
                Assert(audio != null, "Launcher did not create ambience. " + ((TextBox)Field(form, "log")).Text);
                Pump(() => audio.Player.Position > TimeSpan.FromMilliseconds(250));
                Assert(source.Checks == 0, "Audio startup must not trigger patch checks.");
                Assert(audio.Player.Volume == .4 && audio.Muted && audio.Player.NaturalDuration.TimeSpan.TotalSeconds > 917, "Track/volume/startup mute mismatch.");
                Console.WriteLine("PASS actual launcher starts the full embedded track at volume 0.4");
                var sound = (Button)Field(form, "sound");
                sound.PerformClick(); Assert(!audio.Muted && audio.Player.Volume == .4 && sound.Text.Contains("40%"), "Unmute lost 40% volume.");
                sound.PerformClick(); Assert(audio.Muted && audio.Player.Volume == .4 && sound.Text == "Sound off", "Mute control failed.");
                Console.WriteLine("PASS sound control toggles mute and always retains the 40 percent level");
                bool ended = false;
                audio.Player.MediaEnded += delegate { ended = true; };
                audio.Player.Position = audio.Player.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(100);
                Pump(() => ended && audio.Player.Position > TimeSpan.FromMilliseconds(100) && audio.Player.Position < TimeSpan.FromSeconds(3));
                Assert(audio.Player.Volume == .4 && audio.Muted, "Loop changed volume/mute state.");
                Console.WriteLine("PASS end-of-track playback loops without changing volume or mute");
                form.Close();
            }
            Assert(audio.Player.Source == null, "Player was not closed with its launcher.");
            using (File.Open(cached, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            Console.WriteLine("PASS closing launcher stops playback and releases the cached file");

            var bytes = File.ReadAllBytes(cached); bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(cached, bytes);
            bool rejected = false;
            try { using (var resource = Resource()) using (var locked = LauncherAmbience.PrepareAudio(resource, Config.StateDir, out cached)) { } }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Changed cached audio was accepted.");
            source = new AmbiencePatchSource();
            using (var form = new MainForm(source) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) })
            {
                form.Show();
                Pump(() => ((TextBox)Field(form, "log")).Text.Contains("Ambience unavailable:"));
                Assert(source.Checks == 0, "Failed audio startup unexpectedly checked patches.");
                ((Button)Field(form, "updateBtn")).PerformClick();
                Pump(() => ((TextBox)Field(form, "log")).Text.Contains("You're up to date."));
                Assert(((TextBox)Field(form, "log")).Text.Contains("Ambience unavailable:") && !((Button)Field(form, "sound")).Enabled && ((Button)Field(form, "updateBtn")).Enabled && source.Checks == 1, "Audio failure interrupted patch checks.");
                form.Close();
            }
            Console.WriteLine("PASS corrupt cached audio is rejected while patch checks remain usable");
            Console.WriteLine("TOTAL 6 ambience integration scenarios passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
