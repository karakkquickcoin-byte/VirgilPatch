using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media;

namespace VirgilPatch
{
    // Only the audio embedded in the signed program is played; game manifests cannot supply music.
    sealed class LauncherAmbience : IDisposable
    {
        internal const double DefaultVolume = 0.40;
        internal const long MaxAudioBytes = 12L * 1024 * 1024;
        internal const string ResourceName = "VirgilPatch.LauncherAmbience";
        readonly MediaPlayer player = new MediaPlayer { Volume = DefaultVolume };
        readonly Action<string> log;
        readonly Action<bool> available;
        FileStream audioLock;
        bool disposed;
        internal MediaPlayer Player { get { return player; } }
        internal bool Muted { get { return player.IsMuted; } set { if (!disposed) player.IsMuted = value; } }

        internal LauncherAmbience(Action<string> log, Action<bool> available)
        {
            this.log = log;
            this.available = available;
            player.MediaOpened += Opened;
            player.MediaEnded += Ended;
            player.MediaFailed += Failed;
        }

        static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // MediaPlayer needs a local file. Cache it by content hash and keep a read lock while decoding.
        // Reusing the cache avoids rewriting the full track on every launch; cached bytes are verified every time.
        internal static FileStream PrepareAudio(Stream resource, string stateDirectory, out string path)
        {
            if (resource == null || !resource.CanSeek || resource.Length == 0 || resource.Length > MaxAudioBytes)
                throw new InvalidDataException("Embedded ambience is missing or exceeds its size limit.");
            resource.Position = 0;
            var expected = Hash(resource);
            resource.Position = 0;
            var directory = Safety.Under(stateDirectory, "launcher-media");
            Directory.CreateDirectory(directory);
            path = Safety.Under(directory, expected + ".mp3");
            if (!File.Exists(path))
            {
                var temporary = Safety.Under(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        GitHubSource.CopyLimited(resource, output, MaxAudioBytes, null);
                    if (ProgramRelease.FileHash(temporary) != expected) throw new InvalidDataException("Ambience cache checksum mismatch.");
                    Safety.NoLinks(path);
                    try { File.Move(temporary, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; } // another launcher may have populated this exact hash
                }
                finally { Safety.DeleteTemp(temporary); }
            }
            Safety.NoLinks(path);
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (locked.Length != resource.Length || Hash(locked) != expected)
                    throw new InvalidDataException("Cached ambience does not match the embedded track.");
                locked.Position = 0;
                return locked;
            }
            catch { locked.Dispose(); throw; }
        }

        internal void Start(bool muted)
        {
            if (disposed || audioLock != null) throw new InvalidOperationException("Ambience has already been started or closed.");
            player.Volume = DefaultVolume;
            player.IsMuted = muted; // apply before opening/playing, so there is no full-volume startup burst
            string path;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                audioLock = PrepareAudio(resource, Config.StateDir, out path);
            var uri = new Uri(path, UriKind.Absolute);
            if (!uri.IsFile) throw new InvalidDataException("Ambience must be a local file.");
            player.Open(uri);
        }

        void Opened(object sender, EventArgs e)
        {
            if (disposed) return;
            try { player.Play(); available(true); }
            catch (Exception ex) { Unavailable(ex); }
        }
        void Ended(object sender, EventArgs e)
        {
            if (disposed) return;
            try { player.Position = TimeSpan.Zero; player.Play(); }
            catch (Exception ex) { Unavailable(ex); }
        }
        void Failed(object sender, ExceptionEventArgs e) { Unavailable(e.ErrorException); }
        void Unavailable(Exception error)
        {
            if (disposed) return;
            Dispose();
            log("Ambience unavailable: " + error.Message + " Patch updates can continue.");
            available(false);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            player.MediaOpened -= Opened;
            player.MediaEnded -= Ended;
            player.MediaFailed -= Failed;
            try { player.Close(); }
            finally { if (audioLock != null) { audioLock.Dispose(); audioLock = null; } }
        }
    }
}
