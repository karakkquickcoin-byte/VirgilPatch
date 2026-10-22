using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VirgilPatch
{
    // A separate, signed channel for replacing this program. Game manifests cannot install executables.
    class ProgramRelease
    {
        internal const string PublicKey = "<RSAKeyValue><Modulus>r0ldk2nXb9A0LWNwKJOL2BO4r0XQeHdl9L0zu2X11rTr5enESmkJjBQXTdb9N9QEKH2mdjUsxNieBkubs+JszpX0/my2U7sdwyabQtdF/ZpiEAheZJgkIcqEXTq8w6Dq4koqvl/t3g3g87KmR1T0IWMBjl9HzbRoSv5lrRe7aGDCIVGVdmHtI0aHbfMScxBOpkPSXG5nyQLYsG/dNIrmSYotSdePNlUBZeAjHY++L+bP5kahhQYd2/8lT5TkGyJk7ijblFFalBfjhtEtMZXU1ZqZuHk0U+I6jnVb8KuS2sOHYBo8e9RMT2BlcNC39La3D0pUxOwCrI2MJd3a76WY1uFAdRfb34L7vboXr29LbPRhIoB4mGAv/cpHKve2fwKBYnzmeLKSYod1oZglTiIM/DWhf7J0rKKz0HZJBUOfax4vlA6lBiXonuNHqnGxkt93sRg10UNqakbzMKGJ17NAF9rhH0rOTul/Oz5gXWiXfGSG3VvMnyNng2d6oln+Y8WBwHoO+J+O2Q5nzchRgenvggMO1MzjmUEsLaSHGyGUJRUcNinOL9PWDfmjpmyZlBatReejuZ+ry0cmmHJMd6NMuqrm7jmA206tRRiGzjg3oekFH9WR2OfzVbU+w3k0tpbvwA5cON3E7AgMQCKt3HCgX9QRwriP6lNJZOm1zMWLjsU=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        internal const string ManifestPath = "updater-release.json";
        internal const string BinaryPath = "updater/VirgilUpdater.exe";
        internal const long MaxBinaryBytes = 16L * 1024 * 1024;
        internal Version Version;
        internal long Size;
        internal string Sha256;
        internal byte[] SignedDocument;

        internal static byte[] SigningBytes(string version, long size, string hash)
        {
            return Encoding.UTF8.GetBytes("VirgilPatchUpdaterRelease/v1\n" + version + "\n" + BinaryPath + "\n" +
                size.ToString(CultureInfo.InvariantCulture) + "\n" + hash + "\n");
        }

        internal static ProgramRelease Parse(byte[] bytes, string publicKey)
        {
            if (bytes == null || bytes.Length > 65536) throw new InvalidDataException("Updater release metadata is too large.");
            var js = new JavaScriptSerializer { MaxJsonLength = 65536 };
            var d = StrictJson.Read(bytes, 65536);
            if (d == null || d.Count != 6 || new[] { "format", "file", "size", "version", "sha256", "signature" }.Any(k => !d.ContainsKey(k)) || !(d["format"] is int) || (int)d["format"] != 1 ||
                (string)d["file"] != BinaryPath || !(d["size"] is int || d["size"] is long))
                throw new InvalidDataException("Invalid updater release format.");
            var versionText = d["version"] as string;
            var hash = d["sha256"] as string;
            var signatureText = d["signature"] as string;
            if (versionText == null || !Regex.IsMatch(versionText, @"\A[0-9]{1,5}(\.[0-9]{1,5}){3}\z") ||
                hash == null || !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || signatureText == null || signatureText.Length > 2048)
                throw new InvalidDataException("Invalid updater release fields.");
            var version = new Version(versionText);
            var size = Convert.ToInt64(d["size"], CultureInfo.InvariantCulture);
            if (version.ToString() != versionText || size < 4096 || size > MaxBinaryBytes)
                throw new InvalidDataException("Invalid updater release version or size.");
            try
            {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKey);
                    if (!rsa.VerifyData(SigningBytes(versionText, size, hash), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(signatureText)))
                        throw new InvalidDataException("Updater release signature is invalid.");
                }
            }
            catch (CryptographicException ex) { throw new InvalidDataException("Updater release signature is invalid.", ex); }
            catch (FormatException ex) { throw new InvalidDataException("Updater release signature is invalid.", ex); }
            return new ProgramRelease { Version = version, Size = size, Sha256 = hash, SignedDocument = bytes };
        }

        internal static ProgramRelease Parse(byte[] bytes) { return Parse(bytes, PublicKey); }

        internal static string FileHash(string path)
        {
            Safety.NoLinks(path);
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal void ValidateBinary(string path)
        {
            Safety.NoLinks(path);
            if (new FileInfo(path).Length != Size || FileHash(path) != Sha256)
                throw new InvalidDataException("Updater download checksum or size does not match its signed release.");
            var assembly = AssemblyName.GetAssemblyName(path);
            if (assembly.Name != "VirgilUpdater" || assembly.Version != Version)
                throw new InvalidDataException("Downloaded updater identity does not match its signed release.");
        }
    }

    class SelfUpdateJob
    {
        public int format = 1;
        public int parent_pid;
        public long parent_start;
        public string target;
        public string nonce;
        public string[] arguments;
    }

    static class SelfUpdate
    {
        internal const string JobFile = "job.json";
        internal static string Here { get { return Path.GetFullPath(Application.ExecutablePath); } }
        internal static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        internal static string QuoteArgument(string argument)
        {
            var text = new StringBuilder("\"");
            int slashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') text.Append('\\', slashes * 2 + 1);
                else text.Append('\\', slashes);
                slashes = 0;
                text.Append(c);
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static Process Launch(string executable, IEnumerable<string> arguments)
        {
            Safety.NoLinks(executable);
            return Process.Start(new ProcessStartInfo(executable, string.Join(" ", arguments.Select(QuoteArgument))) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)
            });
        }

        static void ExclusiveWrite(string path, byte[] bytes)
        {
            Safety.NoLinks(path);
            // Older helpers poll ready/started.ok with File.ReadAllText. Publish only a closed, complete
            // file, so those already-installed readers never race a writer holding FileShare.None.
            var temporary = Safety.Under(Path.GetDirectoryName(Path.GetFullPath(path)), Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                Safety.NoLinks(path);
                File.Move(temporary, path); // Same-volume atomic publication; still refuses an existing target.
            }
            finally { Safety.DeleteTemp(temporary); }
        }

        internal static bool HandoffMatches(string path, string nonce)
        {
            Safety.NoLinks(path);
            if (!File.Exists(path)) return false;
            try
            {
                if (new FileInfo(path).Length > 128) throw new InvalidDataException("Invalid self-update handoff record.");
                return File.ReadAllText(path) == nonce;
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xffff;
                if (code != 32 && code != 33) throw;
                return false; // The existing 15/45-second polling deadline still applies.
            }
        }

        internal static string Prepare(ISource source, string target, string[] arguments, Action<string> log)
        {
            target = Path.GetFullPath(target);
            Safety.NoLinks(target);
            var commit = source.ResolveCommit();
            var release = ProgramRelease.Parse(source.Get(commit, ProgramRelease.ManifestPath));
            if (release.Version.CompareTo(CurrentVersion) <= 0) return null;
            var failurePath = target + ".self-update-failed";
            Safety.NoLinks(failurePath);
            if (File.Exists(failurePath) && new FileInfo(failurePath).Length > 128)
                throw new InvalidDataException("Invalid self-update failure record.");
            if (File.Exists(failurePath) && File.ReadAllText(failurePath).Trim() == release.Sha256)
            { log("The previous program upgrade failed; keeping this version until a new release is available."); return null; }
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("The updater EXE is read-only. Its current version will be kept.");
            log("Updater " + release.Version + " is available. Downloading a signed program upgrade ...");
            var directory = Safety.Under(Path.GetDirectoryName(target), ".virgil-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
            var payload = Safety.Under(directory, "payload.exe");
            try
            {
                source.Download(commit, ProgramRelease.BinaryPath, payload, release.Size, null);
                release.ValidateBinary(payload);
                ExclusiveWrite(Safety.Under(directory, "release.json"), release.SignedDocument);
                var helper = Safety.Under(directory, "helper.exe");
                using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                var process = Process.GetCurrentProcess();
                var job = new SelfUpdateJob { parent_pid = process.Id, parent_start = process.StartTime.ToUniversalTime().Ticks,
                    target = target, nonce = Guid.NewGuid().ToString("N"), arguments = arguments };
                ExclusiveWrite(Safety.Under(directory, JobFile), Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(job)));
                return directory;
            }
            catch { Safety.DeleteTemp(payload); throw; }
        }

        static SelfUpdateJob ReadJob(string directory)
        {
            directory = Path.GetFullPath(directory);
            Safety.NoLinks(directory);
            var jobFile = Safety.Under(directory, JobFile);
            if (new FileInfo(jobFile).Length > 65536) throw new InvalidDataException("Self-update job is too large.");
            var job = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<SelfUpdateJob>(File.ReadAllText(jobFile));
            if (job == null || job.format != 1 || job.target == null || !Path.IsPathRooted(job.target) ||
                job.nonce == null || !Regex.IsMatch(job.nonce, @"\A[0-9a-f]{32}\z") || job.arguments == null)
                throw new InvalidDataException("Invalid self-update job.");
            job.target = Path.GetFullPath(job.target);
            Safety.NoLinks(job.target);
            if (!Path.GetDirectoryName(directory).Equals(Path.GetDirectoryName(job.target), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).StartsWith(".virgil-update-", StringComparison.Ordinal) ||
                !Path.GetExtension(job.target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Self-update staging folder does not belong to this updater.");
            return job;
        }

        internal static bool StartHelper(string directory)
        {
            var job = ReadJob(directory);
            if (!job.target.Equals(Here, StringComparison.OrdinalIgnoreCase) || job.parent_pid != Process.GetCurrentProcess().Id)
                throw new InvalidDataException("Self-update job does not belong to the running program.");
            var helper = Safety.Under(directory, "helper.exe");
            if (ProgramRelease.FileHash(helper) != ProgramRelease.FileHash(Here)) throw new InvalidDataException("Self-update helper mismatch.");
            using (var child = Launch(helper, new[] { "--apply-self-update", Safety.Under(directory, JobFile) }))
            {
                var timer = Stopwatch.StartNew();
                var ready = Safety.Under(directory, "ready");
                while (timer.Elapsed < TimeSpan.FromSeconds(15))
                {
                    if (HandoffMatches(ready, job.nonce)) return true;
                    if (child.HasExited) throw new IOException("Self-update helper could not prepare the upgrade. Current EXE was kept.");
                    Thread.Sleep(100);
                }
                if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                throw new IOException("Self-update helper did not become ready. Current EXE was kept.");
            }
        }

        internal static void ConfirmStartup(string directory)
        {
            var job = ReadJob(directory);
            var release = ReadRelease(directory);
            if (!job.target.Equals(Here, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Upgrade confirmation target mismatch.");
            release.ValidateBinary(Here);
            ExclusiveWrite(Safety.Under(directory, "started.ok"), Encoding.UTF8.GetBytes(job.nonce));
        }

        static void Note(string directory, string message)
        {
            try { File.AppendAllText(Safety.Under(directory, "result.log"), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static ProgramRelease ReadRelease(string directory)
        {
            var path = Safety.Under(directory, "release.json");
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Updater release metadata is too large.");
            return ProgramRelease.Parse(File.ReadAllBytes(path));
        }

        internal static string[] UserArguments(string[] args)
        {
            var result = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--skip-self-update-once") continue;
                if (args[i] == "--confirm-self-update" || args[i] == "--apply-self-update") { i++; continue; }
                result.Add(args[i]);
            }
            return result.ToArray();
        }

        // Windows may briefly retain the image mapping after process exit. Retry only
        // sharing/lock violations; revalidate every path and both binary identities each time.
        static void ReplaceAfterExit(string payload, string target, string backup, string expectedPayload, string expectedTarget)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                Safety.NoLinks(payload); Safety.NoLinks(target); Safety.NoLinks(backup);
                if (ProgramRelease.FileHash(payload) != expectedPayload || ProgramRelease.FileHash(target) != expectedTarget)
                    throw new InvalidDataException("Updater changed before atomic replacement.");
                try { File.Replace(payload, target, backup); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xffff;
                    if ((code != 32 && code != 33) || timer.Elapsed >= TimeSpan.FromSeconds(5)) throw;
                    Thread.Sleep(100);
                }
            }
        }

        internal static int RunHelper(string jobFile)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(jobFile));
            bool replaced = false;
            bool ready = false;
            SelfUpdateJob job = null;
            Process candidate = null;
            FileStream updateLock = null;
            try
            {
                if (!Path.GetFullPath(jobFile).Equals(Safety.Under(directory, JobFile), StringComparison.OrdinalIgnoreCase) ||
                    !Here.Equals(Safety.Under(directory, "helper.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Self-update helper location mismatch.");
                job = ReadJob(directory);
                var release = ReadRelease(directory);
                if (release.Version.CompareTo(CurrentVersion) <= 0) throw new InvalidDataException("Self-update cannot install the same or an older version.");
                var payload = Safety.Under(directory, "payload.exe");
                release.ValidateBinary(payload);
                var originalHash = ProgramRelease.FileHash(Here);
                if (ProgramRelease.FileHash(job.target) != originalHash) throw new InvalidDataException("Self-update target is not the running updater's original EXE.");
                Safety.NoLinks(job.target + ".self-update.lock");
                updateLock = new FileStream(job.target + ".self-update.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using (var parent = Process.GetProcessById(job.parent_pid))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != job.parent_start ||
                        !Path.GetFullPath(parent.MainModule.FileName).Equals(job.target, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Self-update parent identity mismatch.");
                    ExclusiveWrite(Safety.Under(directory, "ready"), Encoding.UTF8.GetBytes(job.nonce));
                    ready = true;
                    if (!parent.WaitForExit(45000)) throw new IOException("The old updater did not exit; upgrade cancelled.");
                    Safety.NoLinks(job.target);
                    if (ProgramRelease.FileHash(job.target) != originalHash) throw new InvalidDataException("Updater changed before replacement.");
                    release.ValidateBinary(payload);
                    var backup = Safety.Under(directory, "previous.exe");
                    ReplaceAfterExit(payload, job.target, backup, release.Sha256, originalHash);
                    replaced = true;
                    release.ValidateBinary(job.target);
                    candidate = Launch(job.target, job.arguments.Concat(new[] { "--confirm-self-update", directory }));
                    var timer = Stopwatch.StartNew();
                    var started = Safety.Under(directory, "started.ok");
                    while (timer.Elapsed < TimeSpan.FromSeconds(45))
                    {
                        if (HandoffMatches(started, job.nonce))
                        {
                            Note(directory, "Upgrade started successfully. Previous EXE retained as previous.exe.");
                            return 0;
                        }
                        if (candidate.HasExited) break;
                        Thread.Sleep(100);
                    }
                    throw new IOException("The new updater did not confirm a successful start.");
                }
            }
            catch (Exception ex)
            {
                Note(directory, "Upgrade failed: " + ex.Message);
                if (job != null && ready)
                {
                    try
                    {
                        if (candidate != null)
                        {
                            if (!candidate.HasExited) candidate.Kill();
                            if (!candidate.WaitForExit(5000)) throw new IOException("The failed updater is still running; recovery backup was retained.");
                            candidate.Dispose(); candidate = null;
                        }
                        if (replaced)
                        {
                            Safety.NoLinks(job.target);
                            if (ProgramRelease.FileHash(Safety.Under(directory, "previous.exe")) != ProgramRelease.FileHash(Here))
                                throw new InvalidDataException("Previous updater backup checksum mismatch.");
                            var release = ReadRelease(directory);
                            ReplaceAfterExit(Safety.Under(directory, "previous.exe"), job.target, Safety.Under(directory, "failed.exe"),
                                ProgramRelease.FileHash(Here), release.Sha256);
                            Safety.NoLinks(job.target + ".self-update-failed");
                            File.WriteAllText(job.target + ".self-update-failed", release.Sha256);
                            Note(directory, "Previous EXE restored; failed release will not be retried automatically.");
                        }
                        // If the old program is still alive, it will report the helper failure itself.
                        bool parentAlive = false;
                        try { using (var parent = Process.GetProcessById(job.parent_pid)) parentAlive = !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == job.parent_start; }
                        catch (ArgumentException) { }
                        if (!parentAlive && ProgramRelease.FileHash(job.target) == ProgramRelease.FileHash(Here))
                            Launch(job.target, job.arguments.Concat(new[] { "--skip-self-update-once" }));
                    }
                    catch (Exception recovery) { Note(directory, "Automatic recovery failed: " + recovery.Message); }
                }
                return 1;
            }
            finally { if (candidate != null) candidate.Dispose(); if (updateLock != null) updateLock.Dispose(); }
        }
    }
}
