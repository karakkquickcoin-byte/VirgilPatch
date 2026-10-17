using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Timer = System.Windows.Forms.Timer;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace VirgilPatch
{
    // Cosmetic playback only. No patch manifest, URL, sidecar or user-provided path can select this media.
    sealed class LauncherVideo : IDisposable
    {
        internal const string ResourceName = "VirgilPatch.LauncherVideo";
        internal const long MaxVideoBytes = 4L * 1024 * 1024;
        const int FrameWidth = 800, FrameHeight = 373;
        readonly MediaPlayer player = new MediaPlayer { Volume = 0, IsMuted = true };
        readonly Timer timer = new Timer { Interval = 50 };
        readonly Stopwatch opening = new Stopwatch();
        readonly Action changed;
        readonly Action<string> log;
        readonly DrawingVisual visual = new DrawingVisual();
        readonly RenderTargetBitmap rendered = new RenderTargetBitmap(FrameWidth, FrameHeight, 96, 96, PixelFormats.Pbgra32);
        Bitmap frame;
        FileStream mediaLock;
        bool ready, paused, disposed;
        internal MediaPlayer Player { get { return player; } }
        internal Bitmap Frame { get { return disposed || Frames == 0 ? null : frame; } }
        internal int Frames { get; private set; }
        internal int Loops { get; private set; }
        internal bool Paused { get { return paused; } }
        internal bool Closed { get { return disposed; } }

        internal LauncherVideo(Action changed, Action<string> log)
        {
            this.changed = changed; this.log = log;
            using (var drawing = visual.RenderOpen()) drawing.DrawVideo(player, new System.Windows.Rect(0, 0, FrameWidth, FrameHeight));
            player.MediaOpened += Opened;
            player.MediaEnded += Ended;
            player.MediaFailed += Failed;
            timer.Tick += Tick;
        }

        static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Decode only the hash-verified embedded clip, held read-only for the complete playback lifetime.
        internal static FileStream PrepareVideo(Stream resource, string stateDirectory, out string path)
        {
            if (resource == null || !resource.CanSeek || resource.Length == 0 || resource.Length > MaxVideoBytes)
                throw new InvalidDataException("Embedded background video is missing or exceeds its size limit.");
            resource.Position = 0; string expected = Hash(resource); resource.Position = 0;
            string directory = Safety.Under(stateDirectory, "launcher-media"); Directory.CreateDirectory(directory);
            path = Safety.Under(directory, expected + ".mp4");
            if (!File.Exists(path))
            {
                string temporary = Safety.Under(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        GitHubSource.CopyLimited(resource, output, MaxVideoBytes, null);
                    if (ProgramRelease.FileHash(temporary) != expected) throw new InvalidDataException("Background cache checksum mismatch.");
                    Safety.NoLinks(path);
                    try { File.Move(temporary, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                finally { Safety.DeleteTemp(temporary); }
            }
            Safety.NoLinks(path);
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (locked.Length != resource.Length || Hash(locked) != expected)
                    throw new InvalidDataException("Cached background does not match the embedded clip.");
                locked.Position = 0; return locked;
            }
            catch { locked.Dispose(); throw; }
        }

        internal void Start()
        {
            if (disposed || mediaLock != null) throw new InvalidOperationException("Background already started or closed.");
            string path;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                mediaLock = PrepareVideo(resource, Config.StateDir, out path);
            var uri = new Uri(path, UriKind.Absolute);
            if (!uri.IsFile) throw new InvalidDataException("Background media must be a local file.");
            opening.Start(); timer.Start(); player.Open(uri);
        }

        void Opened(object sender, EventArgs e)
        {
            if (disposed) return;
            try
            {
                if (!player.HasVideo || player.HasAudio || player.NaturalVideoWidth != 1000 || player.NaturalVideoHeight != 466)
                    throw new InvalidDataException("Unexpected embedded background format.");
                frame = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppPArgb);
                ready = true;
                if (paused) { player.Pause(); timer.Stop(); } else player.Play();
            }
            catch (Exception ex) { Unavailable(ex); }
        }

        void Tick(object sender, EventArgs e)
        {
            if (disposed || paused) return;
            try
            {
                if (!ready)
                {
                    if (opening.Elapsed.TotalSeconds > 15) Unavailable(new TimeoutException("Background decoder did not become ready."));
                    return;
                }
                // Retain the last good frame while the decoder seeks across the pre-blended loop boundary.
                if (player.Position < TimeSpan.FromMilliseconds(80)) return;
                rendered.Clear(); rendered.Render(visual);
                var pixels = frame.LockBits(new Rectangle(0, 0, FrameWidth, FrameHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try { rendered.CopyPixels(new System.Windows.Int32Rect(0, 0, FrameWidth, FrameHeight), pixels.Scan0, pixels.Stride * FrameHeight, pixels.Stride); }
                finally { frame.UnlockBits(pixels); }
                Frames++; changed();
            }
            catch (Exception ex) { Unavailable(ex); }
        }

        void Ended(object sender, EventArgs e)
        {
            if (disposed) return;
            try { Loops++; player.Position = TimeSpan.Zero; if (!paused) player.Play(); }
            catch (Exception ex) { Unavailable(ex); }
        }
        void Failed(object sender, ExceptionEventArgs e) { Unavailable(e.ErrorException); }
        void Unavailable(Exception error)
        {
            if (disposed) return;
            Dispose(); log("Background video unavailable; using the still image. " + error.Message); changed();
        }
        internal void Pause(bool value)
        {
            if (disposed || paused == value) return;
            paused = value;
            if (value) { timer.Stop(); if (ready) player.Pause(); }
            else { if (ready) player.Play(); timer.Start(); }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); timer.Tick -= Tick; timer.Dispose();
            player.MediaOpened -= Opened; player.MediaEnded -= Ended; player.MediaFailed -= Failed;
            try { player.Close(); }
            catch (Exception ex) { log("Background decoder close: " + ex.Message); }
            finally
            {
                if (mediaLock != null) { mediaLock.Dispose(); mediaLock = null; }
                if (frame != null) { frame.Dispose(); frame = null; }
            }
        }
    }

    partial class LauncherWindow
    {
        LauncherVideo backgroundVideo;
        internal LauncherVideo BackgroundVideo { get { return backgroundVideo; } }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                backgroundVideo = new LauncherVideo(delegate {
                    if (!IsDisposed && !Disposing) Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)Math.Ceiling(466 * LayoutScale)), true);
                }, delegate(string message) { Trace.WriteLine(message); });
                backgroundVideo.Start(); SyncVideoPlayback();
            }
            catch (Exception ex)
            {
                DisposeBackgroundVideo(); Trace.WriteLine("Background video unavailable; using the still image. " + ex.Message);
            }
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); SyncVideoPlayback(); }
        void SyncVideoPlayback()
        {
            if (backgroundVideo == null) return;
            try { backgroundVideo.Pause(!Visible || WindowState == System.Windows.Forms.FormWindowState.Minimized); }
            catch (Exception ex) { DisposeBackgroundVideo(); Trace.WriteLine("Background paused after a playback error. " + ex.Message); }
        }
        void DisposeBackgroundVideo()
        {
            if (backgroundVideo == null) return;
            try { backgroundVideo.Dispose(); }
            catch (Exception ex) { Trace.WriteLine("Background close: " + ex.Message); }
            backgroundVideo = null;
        }
        void DrawBackgroundMedia(Graphics graphics)
        {
            var frame = backgroundVideo == null ? null : backgroundVideo.Frame;
            graphics.DrawImage(frame ?? landscape, new Rectangle(0, 0, 1000, 466));
        }
    }
}
