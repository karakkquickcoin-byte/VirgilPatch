using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VirgilPatch;

class VideoSource : ISource
{
    public int Requests;
    public string ResolveCommit() { return "local"; }
    public string Describe() { return "offline video fixture"; }
    public byte[] Get(string commit, string path) { Requests++; if(path!="index.json")throw new Exception("Unexpected request");return Encoding.UTF8.GetBytes("{\"format\":2,\"patches\":{}}"); }
    public void Download(string commit,string path,string dest,long max,Action<long> progress) { throw new Exception("No fixture assets should download."); }
}
class VideoTests
{
    static void Assert(bool ok,string text) { if(!ok)throw new Exception(text); }
    static void Pump(Func<bool> ready)
    {
        var end=DateTime.UtcNow.AddSeconds(25);
        while(!ready()) {if(DateTime.UtcNow>end)throw new Exception("Video operation timed out.");Application.DoEvents();Thread.Sleep(10);}
        Application.DoEvents();
    }
    static void Wait(int milliseconds) {var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}}
    static Stream Resource() {return Assembly.GetExecutingAssembly().GetManifestResourceStream(LauncherVideo.ResourceName);}
    static object Field(MainForm f,string name) {return typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);}
    static string Hash(Bitmap image) {using(var output=new MemoryStream()){image.Save(output,System.Drawing.Imaging.ImageFormat.Png);using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(output.ToArray()));}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);Config.StateDir=Path.Combine(root,"state");Config.StartAmbienceMuted=true;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string cached;
            using(var resource=Resource())using(var locked=LauncherVideo.PrepareVideo(resource,Config.StateDir,out cached))
            {
                Assert(locked.Length==resource.Length,"Video truncated.");bool blocked=false;
                try {using(File.Open(cached,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)) { }}catch(IOException){blocked=true;}
                Assert(blocked,"Video cache was writable during decoding.");
            }
            var stamp=File.GetLastWriteTimeUtc(cached);
            using(var resource=Resource())using(var locked=LauncherVideo.PrepareVideo(resource,Config.StateDir,out cached))Assert(File.GetLastWriteTimeUtc(cached)==stamp,"Matching video rewritten.");
            Console.WriteLine("PASS embedded video hash/size verification, cache reuse and read-only decode lock");
            using(var video=new LauncherVideo(delegate{},Console.WriteLine))
            {
                video.Start();Pump(()=>video.Frames>=3 || video.Closed);Assert(!video.Closed,"Native video failed.");
                Assert(video.Player.IsMuted && video.Player.Volume==0 && !video.Player.HasAudio && Math.Abs(video.Player.NaturalDuration.TimeSpan.TotalSeconds-12)<.05,"Unexpected video audio/duration: mute="+video.Player.IsMuted+" volume="+video.Player.Volume+" audio="+video.Player.HasAudio+" duration="+video.Player.NaturalDuration);
                var first=Hash(video.Frame);var frames=video.Frames;Wait(650);Assert(video.Frames>frames && Hash(video.Frame)!=first,"Painted video frames do not move.");
                video.Frame.Save(args[1]+".frame.png");Console.WriteLine("PASS actual embedded silent 12-second loop decodes and paints changing frames");
                video.Player.Position=video.Player.NaturalDuration.TimeSpan-TimeSpan.FromMilliseconds(300);
                Pump(()=>video.Loops>0 && video.Player.Position>TimeSpan.FromMilliseconds(150) && video.Player.Position<TimeSpan.FromSeconds(2));
                Assert(video.Frame.GetPixel(600,180).R>20,"Loop boundary displayed black.");Console.WriteLine("PASS native end-of-video loop resumes while retaining a painted frame");
                video.Pause(true);frames=video.Frames;Wait(350);Assert(video.Paused && video.Frames==frames,"Paused video kept rendering.");
                video.Pause(false);Pump(()=>video.Frames>frames+2);Console.WriteLine("PASS pause stops rendering and resume restarts playback");
            }
            using(File.Open(cached,FileMode.Open,FileAccess.Write,FileShare.None)){}Console.WriteLine("PASS closing playback releases decoder/cache resources");
            var patches=Path.Combine(root,"DEV-Patches");Directory.CreateDirectory(patches);var dev=Path.Combine(patches,"unpublished.blp");File.WriteAllText(dev,"UNPUBLISHED");
            var devStamp=File.GetLastWriteTimeUtc(dev);new State{patches_dir=patches}.Save();var stateFile=Path.Combine(Config.StateDir,"state.json");var stateBytes=File.ReadAllBytes(stateFile);
            var source=new VideoSource();LauncherVideo closedVideo;
            using(var form=new MainForm(source){Opacity=0,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-32000,-32000)})
            {
                form.Show();Pump(()=>form.BackgroundVideo!=null && form.BackgroundVideo.Frames>2);closedVideo=form.BackgroundVideo;
                Assert(source.Requests==0 && File.ReadAllText(dev)=="UNPUBLISHED" && File.GetLastWriteTimeUtc(dev)==devStamp && Convert.ToBase64String(File.ReadAllBytes(stateFile))==Convert.ToBase64String(stateBytes),"Video startup touched patch files/state or network.");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1]+".launcher.png");}
                form.WindowState=FormWindowState.Minimized;Application.DoEvents();Assert(closedVideo.Paused,"Minimizing did not pause.");
                var frames=closedVideo.Frames;Wait(300);Assert(closedVideo.Frames==frames,"Minimized window kept rendering.");form.WindowState=FormWindowState.Normal;Pump(()=>closedVideo.Frames>frames+1);
                Console.WriteLine("PASS actual launcher starts media without patch requests/changes and pauses while minimized");
                form.Close();
            }
            Assert(closedVideo.Closed && closedVideo.Player.Source==null,"Closing launcher did not close video.");
            using(File.Open(cached,FileMode.Open,FileAccess.Write,FileShare.None)){}Console.WriteLine("PASS actual launcher shutdown closes video and releases its file");
            var bad=File.ReadAllBytes(cached);bad[bad.Length/2]^=1;File.WriteAllBytes(cached,bad);source=new VideoSource();
            using(var form=new MainForm(source){Opacity=0,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-32000,-32000)})
            {
                form.Show();Application.DoEvents();Assert(form.BackgroundVideo==null,"Corrupt cached media reached decoder.");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1]+".fallback.png");}
                ((Button)Field(form,"updateBtn")).PerformClick();Pump(()=>source.Requests==1 && !(bool)Field(form,"isBusy"));
                Assert(((TextBox)Field(form,"log")).Text.Contains("You're up to date.") && File.ReadAllText(dev)=="UNPUBLISHED","Still fallback blocked patch checks or changed dev files.");form.Close();
            }
            Console.WriteLine("PASS corrupt video uses cropped still fallback and Update remains usable");
            bool rejected=false;try{Safety.Asset("untrusted-video.mp4",false);}catch(System.IO.InvalidDataException){rejected=true;}Assert(rejected,"Game manifest accepted video payloads.");
            Console.WriteLine("PASS video remains forbidden as a downloaded game-patch asset");
            Console.WriteLine("TOTAL 9 video integration scenarios passed");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
