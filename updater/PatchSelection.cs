using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace VirgilPatch
{
    sealed class PatchSelection
    {
        public string[] Names;
        public bool Verify;
        public bool CheckFolders;
    }

    static class PatchDialogs
    {
        static readonly Color Background = Color.FromArgb(23, 21, 18), Foreground = Color.FromArgb(230, 217, 190);
        internal static Form Window(string title, int height)
        {
            return new Form { Text = title + " - Chronicles of Lordaeron", ClientSize = new Size(730, height),
                BackColor = Background, ForeColor = Foreground, Font = new Font("Segoe UI", 10f),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
        }
        static Label Text(string text, int x, int y, int width, int height)
        { return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), ForeColor = Foreground, UseMnemonic = false }; }
        public static string Size(long bytes) { return bytes >= 1000000000 ? (bytes / 1e9).ToString("0.00") + " GB" : (bytes / 1e6).ToString("0.0") + " MB"; }
        public static string Latest(object value)
        {
            var data = value as Dictionary<string, object>;
            if (data == null) return "";
            var text = new StringBuilder("Latest published change: ");
            foreach (var kind in new[] { "added", "changed", "removed" })
            {
                object count; int number;
                if (data.TryGetValue(kind + "_count", out count) && int.TryParse(Convert.ToString(count), out number) && number >= 0)
                    text.Append(number).Append(' ').Append(kind).Append("; ");
            }
            object names;
            foreach (var kind in new[] { "added", "changed", "removed" })
                if (data.TryGetValue(kind, out names) && names is System.Collections.IEnumerable && !(names is string))
                    foreach (var name in ((System.Collections.IEnumerable)names).Cast<object>().OfType<string>().Take(12)) text.AppendLine().Append("    ").Append(kind).Append(": ").Append(name.Length > 220 ? name.Substring(0, 220) : name);
            return text.ToString();
        }

        public static PatchSelection Choose(IWin32Window owner, Plan catalog, State state)
        {
            using (var dialog = Window("Choose patches to update or repair", 460))
            {
                var list = new ListView { CheckBoxes = true, FullRowSelect = true, View = View.Details, HideSelection = false,
                    BackColor = Color.FromArgb(31, 29, 25), ForeColor = Foreground,
                    BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 88), Size = new Size(682, 218) };
                list.Columns.Add("Patch", 280); list.Columns.Add("Installed", 95); list.Columns.Add("Available", 95);
                list.Columns.Add("Size", 90); list.Columns.Add("Status", 115);
                bool saved = string.Equals(state.selection_root, state.patches_dir, StringComparison.OrdinalIgnoreCase);
                foreach (var name in catalog.Patches)
                {
                    bool existing = false;
                    string existingRoot = null;
                    try {
                        string canonical;
                        existingRoot = catalog.InstallFolders.TryGetValue(name, out canonical) ? FolderMigration.Discover(state, name, canonical).Preferred : Safety.Under(state.patches_dir, name);
                        existing = Directory.Exists(existingRoot);
                    }
                    catch (IOException) { } // An unselected junction must not prevent checking unrelated patches.
                    string available, display;
                    if (!catalog.Versions.TryGetValue(name, out available)) available = "legacy";
                    if (!catalog.DisplayNames.TryGetValue(name, out display)) display = name;
                    string installed = existing ? "unknown" : "-";
                    if (existing) try {
                        var metadata = Safety.Under(existingRoot, "patch.json");
                        if (File.Exists(metadata) && new FileInfo(metadata).Length <= Safety.MaxMetadataBytes) {
                            var data = StrictJson.Read(File.ReadAllBytes(metadata), (int)Safety.MaxMetadataBytes);
                            object version; if (data.TryGetValue("version", out version) && version is string)
                                installed = ((string)version).Length <= 40 ? (string)version : "unknown";
                        }
                    } catch (Exception) { installed = "unknown"; }
                    long bytes = catalog.Files.Values.Where(f => f.Patch == name && f.Name != "patch.json").Sum(f => f.Size);
                    var row = new ListViewItem(new[] { display, installed, available, Size(bytes), existing ? "on this PC" : "not installed" }) { Tag = name };
                    row.Checked = saved ? state.selected_patches.Contains(name, StringComparer.OrdinalIgnoreCase) : existing;
                    list.Items.Add(row);
                }
                var verify = new CheckBox { Text = "Deep repair: re-verify unchanged files too (extra local disk reading)", AutoSize = true, Location = new Point(24, 320), ForeColor = Foreground };
                var clear = new LauncherButton { Text = "Clear all", Location = new Point(24, 363), Size = new Size(110, 34) };
                var all = new LauncherButton { Text = "Select all", Location = new Point(144, 363), Size = new Size(110, 34) };
                var troubleshoot = new LauncherButton { Text = "Problem installing? Check folders", Location = new Point(276, 363), Size = new Size(430, 34), DialogResult = DialogResult.Retry };
                var accept = new LauncherButton { Text = "Check selected", Primary = true, Location = new Point(508, 405), Size = new Size(198, 36), DialogResult = DialogResult.OK, Enabled = list.CheckedItems.Count > 0 };
                var cancel = new LauncherButton { Text = "Cancel", Location = new Point(380, 405), Size = new Size(116, 36), DialogResult = DialogResult.Cancel };
                list.ItemChecked += (s, e) => accept.Enabled = list.CheckedItems.Count > 0;
                clear.Click += delegate { for (int i = 0; i < list.Items.Count; i++) list.Items[i].Checked = false; };
                all.Click += delegate { for (int i = 0; i < list.Items.Count; i++) list.Items[i].Checked = true; };
                dialog.Controls.AddRange(new Control[] { Text("Choose only the folders you want. Unselected patches stay untouched.", 24, 20, 682, 28),
                    Text("Totals are package sizes; only changed files download. Classic and HD can be selected separately.", 24, 51, 682, 30),
                    list, verify, clear, all, troubleshoot, Text("Only missing or changed files are downloaded, including after a failed update.", 24, 406, 340, 40), accept, cancel });
                dialog.AcceptButton = accept; dialog.CancelButton = cancel;
                var answer = dialog.ShowDialog(owner);
                if (answer == DialogResult.Retry) return new PatchSelection { CheckFolders = true };
                if (answer != DialogResult.OK) return null;
                return new PatchSelection { Names = list.CheckedItems.Cast<ListViewItem>().Select(c => (string)c.Tag).ToArray(), Verify = verify.Checked };
            }
        }

        public static DialogResult Installation(IWin32Window owner, InstallationInspection inspection) {
            using(var dialog=Window("Check installation folders",560)) {
                var details=new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Background,ForeColor=Foreground,Location=new Point(24,24),Size=new Size(682,448),Text=inspection.Describe() };
                var yes=new LauncherButton {Text="Yes, this is correct",Primary=true,Enabled=inspection.Valid,Location=new Point(486,496),Size=new Size(220,36),DialogResult=DialogResult.Yes};
                var no=new LauncherButton {Text="No, select folder",Location=new Point(248,496),Size=new Size(220,36),DialogResult=DialogResult.Retry};
                var cancel=new LauncherButton {Text="Cancel",Location=new Point(24,496),Size=new Size(200,36),DialogResult=DialogResult.Cancel};
                dialog.Controls.AddRange(new Control[]{details,yes,no,cancel});dialog.AcceptButton=yes;dialog.CancelButton=cancel;return dialog.ShowDialog(owner);
            }
        }
        public static void Changelog(IWin32Window owner) {
            using(var dialog=Window("Updater changelog",560)) {
                var details=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Background,ForeColor=Foreground,Location=new Point(24,24),Size=new Size(682,448),Text=ChangelogText.Replace("\n",Environment.NewLine)};
                var close=new LauncherButton{Text="Close",Location=new Point(486,496),Size=new Size(220,36),DialogResult=DialogResult.OK};dialog.Controls.AddRange(new Control[]{details,close});dialog.AcceptButton=close;dialog.ShowDialog(owner);
            }
        }
        internal const string ChangelogText = "1.5.8 - Diagnostics button layout\nCorrects the oversized Patches folder label covering Copy diagnostics at different window sizes. All 1.5.7 security protections and existing update behavior retained.\n\n1.5.7 - Five additive security checks\nStrict signed-release and saved-state JSON validation; immutable download commit/path validation; complete Windows reserved-device name rejection; exclusive per-Patches update lock. Existing migration, targeted verification, repair, privacy and five-use folder confirmation retained. Antivirus acceptance is a separate verdict.\n\n1.5.6 - Installation folder checks\nChecks Epsilon.exe and Data, _retail_/Wow.exe and EpsilonAddon.dll, and nearby Patches folders. Every fifth Update use asks you to confirm the exact path. Problem installing? Check folders runs it immediately. No automatic switching or deletion. Changelog gear added.\n\n1.5.5 - Private support diagnostics\nCopy diagnostics reports tokenized paths, detected folders, nearby installations, migration events and error codes. Personal path parts, network/drive identifiers and raw error messages are omitted. No automatic upload.\n\n1.5.4 - Targeted three-stage verification\nRecent added/changed assets bypass cached hashes. New files are checked before placement, at their installed path, and before finalization. Plans are bound to the checked folder. Full Deep repair remains available.\n\n1.5.3 - Existing folder migration\nConfirmed Update reuses existing versions, consolidates into fixed (5.0.0) folders, and archives duplicates outside Patches. Repairs, retry and independent selections retained.\n\n1.5.2 - Online patch numbering\nFive online packages start at 5.0.0, then advance independently through 5.0.0.1 and later revisions. Installed/Available version columns and publisher source compatibility.\n\n1.5.1 and earlier - Signed launcher updates and safety\nProgram self-update on launch; game patch changes require explicit Update confirmation. Path/link validation, checksums, atomic file placement, metadata-last updates, cancellation and retry protections. Launcher video and ambience with fallbacks.\n\nGame patch releases are separate from updater versions. Opening the updater does not install or rename game patches.";

        public static string Summary(Plan plan)
        {
            var text = new StringBuilder();
            text.AppendLine("Patches folder: " + plan.TargetRoot).AppendLine("This verifies the selected folder; confirm it belongs to the Epsilon installation you launch.").AppendLine("Recent added / changed files are checked without the hash cache. New installs pass three verification checks.").AppendLine().AppendLine("Published: " + plan.Published).AppendLine("Download: " + Size(plan.FetchBytes) + " across " + plan.Fetch.Count + " files").AppendLine();
            foreach (var patch in plan.Patches)
            {
                Func<string, bool> here = k => k.StartsWith(patch + "/", StringComparison.OrdinalIgnoreCase);
                var files = plan.Fetch.Where(f => f.Patch == patch).ToList();
                string display; if (!plan.DisplayNames.TryGetValue(patch, out display)) display = patch;
                text.AppendLine(display).AppendLine("  " + plan.Added.Count(here) + " new / missing, " + plan.Changed.Count(here) + " changed, " + plan.Repairs.Count(here) + " repairs, " + plan.Remove.Count(here) + " retired");
                string destination; if (plan.Destinations.TryGetValue(patch, out destination)) text.AppendLine("  Install folder: " + destination);
                text.AppendLine("  Fresh verification: " + plan.VerificationTargets.Count(here) + " affected files.");
                FolderMigration migration;
                if (plan.Migrations.TryGetValue(patch, out migration)) text.AppendLine("  Rename / reconcile: " + string.Join(", ", migration.Sources.Select(Path.GetFileName)) + " -> " + Path.GetFileName(migration.Target) + ". Duplicates move outside Patches after verification.");
                string latest; if (plan.LatestUpdates.TryGetValue(patch, out latest) && latest.Length > 0) text.AppendLine("  " + latest);
                text.AppendLine("  Transfer: " + Size(files.Sum(f => f.Size)) + (plan.PatchJsons.Any(f => f.Patch == patch) ? "; patch.json will be restored or refreshed after files are complete." : "; patch.json is already current."));
                foreach (var f in files.Take(80)) text.AppendLine("    " + f.Name);
                if (files.Count > 80) text.AppendLine("    ... " + (files.Count - 80) + " more files");
                foreach (var key in plan.Remove.Where(here).Take(20)) text.AppendLine("    Remove: " + key.Substring(patch.Length + 1));
                text.AppendLine();
            }
            if (plan.UpToDate) text.AppendLine("Your selected patches are up to date in the Patches folder shown above. No game files need downloading.");
            text.AppendLine("Successful files from earlier attempts are reused. A patch is finalized only after its files verify.");
            return text.ToString();
        }

        public static bool Review(IWin32Window owner, Plan plan, bool allowInstall = true)
        {
            using (var dialog = Window("What's new / repair preview", 540))
            {
                var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(31, 29, 25),
                    ForeColor = Foreground, Font = new Font("Segoe UI", 10f), Location = new Point(24, 24), Size = new Size(682, 442), Text = Summary(plan) };
                var accept = new LauncherButton { Text = !allowInstall || plan.UpToDate ? "Done" : "Update / repair selected", Primary = true,
                    Location = new Point(470, 487), Size = new Size(236, 34), DialogResult = DialogResult.OK };
                var cancel = new LauncherButton { Text = "Cancel", Location = new Point(342, 487), Size = new Size(116, 34), DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[] { details, accept, cancel }); dialog.AcceptButton = accept; dialog.CancelButton = cancel;
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    sealed class TransferEstimate
    {
        readonly Stopwatch watch = Stopwatch.StartNew();
        string phase = "Ready";
        long previous;
        public void Reset(string value) { phase = value; previous = 0; watch.Restart(); }
        public string Format(long done, long total)
        {
            done = Math.Max(0, done); total = Math.Max(0, total);
            if (done < previous) watch.Restart();
            previous = done;
            int percent = total == 0 ? (phase == "Complete" ? 100 : 0) : (int)Math.Min(100, done * 100.0 / total);
            string eta = "calculating...";
            double elapsed = watch.Elapsed.TotalSeconds;
            if (total > 0 && done >= total) eta = "done";
            else if (done > 0 && elapsed >= .5)
            {
                double remaining = Math.Min(86400 * 7, Math.Max(0, (total - done) / (done / elapsed)));
                var time = TimeSpan.FromSeconds(remaining);
                eta = time.TotalHours >= 1 ? ((int)time.TotalHours) + "h " + time.Minutes + "m" : time.TotalMinutes >= 1 ? ((int)time.TotalMinutes) + "m " + time.Seconds + "s" : time.Seconds + "s";
            }
            return phase + "  " + percent + "%  |  " + PatchDialogs.Size(done) + " / " + PatchDialogs.Size(total) + "  |  ETA " + eta;
        }
    }

    partial class MainForm
    {
        internal Func<Plan, PatchSelection> ChoosePatches;
        internal Func<Plan, bool> ConfirmChanges;
        readonly Label transferText = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Color.FromArgb(209, 190, 153) };
        readonly Button cancelWork = new LauncherButton { Text = "Cancel", Enabled = false };
        readonly Button changesButton = new LauncherButton { Text = "What's new" };
        readonly Button copyDiagnostics = new LauncherButton { Text = "Copy diagnostics", AccessibleName = "Copy privacy-redacted patch diagnostics" };
        readonly PatchDiagnostics diagnostics = new PatchDiagnostics();
        internal Action<string> CopyDiagnosticText = text => Clipboard.SetText(text);
        internal string DiagnosticText() { return diagnostics.Build(state, lastPlan); }
        void CopyDiagnostics()
        {
            try { CopyDiagnosticText(DiagnosticText()); status.Text = "Diagnostics copied with personal paths redacted. Paste them into your support message."; }
            catch { status.Text = "Could not copy diagnostics. The clipboard may be busy; try again."; }
        }
        readonly TransferEstimate estimate = new TransferEstimate();
        Plan lastPlan;

        void Phase(string phase)
        {
            if (InvokeRequired) { Post(delegate { Phase(phase); }); return; }
            estimate.Reset(phase); bar.Value = phase == "Complete" ? 1000 : 0;
            transferText.Text = estimate.Format(0, 0);
        }

        void CheckChosen(Updater updater, Plan catalog, PatchSelection choice, bool apply)
        {
            state.selection_root = state.patches_dir; state.selected_patches = choice.Names.ToList(); state.Save();
            updater.VerifyExisting = choice.Verify;
            status.Text = "Checking and repairing only: " + string.Join(", ", choice.Names);
            var token = cts.Token;
            TaskCheck(updater, catalog, choice, apply, token);
        }

        void TaskCheck(Updater updater, Plan catalog, PatchSelection choice, bool apply, System.Threading.CancellationToken token)
        {
            System.Threading.Tasks.Task.Factory.StartNew(() => updater.Check(choice.Names, catalog, token)).ContinueWith(t => Post(delegate {
                Busy(false);
                if (t.IsFaulted || t.IsCanceled)
                {
                    status.Text = t.IsCanceled || t.Exception.GetBaseException() is OperationCanceledException ? "Check cancelled. No patch files were changed." : "Cannot check these patches. Open Details for the error.";
                    if (t.IsFaulted) { diagnostics.Record("check failed", null, state.patches_dir, null, t.Exception.GetBaseException()); Say("Error: " + t.Exception.GetBaseException().Message); }
                    else diagnostics.Record("check cancelled");
                    cts.Dispose(); cts = null; return;
                }
                plan = t.Result; lastPlan = plan; changesButton.Enabled = true;
                Say(PatchDialogs.Summary(plan));
                status.Text = plan.UpToDate ? "Selected patches verified in: " + plan.TargetRoot : "Ready: " + plan.Fetch.Count + " files, " + PatchDialogs.Size(plan.FetchBytes) + ".";
                bool confirmed = ConfirmChanges != null ? ConfirmChanges(plan) : PatchDialogs.Review(this, plan);
                cts.Dispose(); cts = null;
                if (apply && confirmed && !plan.UpToDate) ApplyUpdate();
                else if (plan.UpToDate) { Say("You're up to date in: " + plan.TargetRoot); Phase("Complete"); transferText.Text = "Complete 100% | Selected patches verified | ETA done"; }
            }));
        }
    }
}
