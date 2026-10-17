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
    }

    static class PatchDialogs
    {
        static readonly Color Background = Color.FromArgb(23, 21, 18), Foreground = Color.FromArgb(230, 217, 190);
        static Form Window(string title, int height)
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

        sealed class Choice
        {
            public string Name;
            public long Bytes;
            public bool Existing;
            public override string ToString() { return Name + "  |  " + Size(Bytes) + " total  |  " + (Existing ? "on this PC" : "not installed"); }
        }
        public static PatchSelection Choose(IWin32Window owner, Plan catalog, State state)
        {
            using (var dialog = Window("Choose patches to update or repair", 460))
            {
                var list = new CheckedListBox { CheckOnClick = true, BackColor = Color.FromArgb(31, 29, 25), ForeColor = Foreground,
                    BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 88), Size = new Size(682, 218), IntegralHeight = false };
                bool saved = string.Equals(state.selection_root, state.patches_dir, StringComparison.OrdinalIgnoreCase);
                foreach (var name in catalog.Patches)
                {
                    bool existing = false;
                    try { existing = Directory.Exists(Safety.Under(state.patches_dir, name)); }
                    catch (IOException) { } // An unselected junction must not prevent checking unrelated patches.
                    var row = new Choice { Name = name, Bytes = catalog.Files.Values.Where(f => f.Patch == name && f.Name != "patch.json").Sum(f => f.Size), Existing = existing };
                    list.Items.Add(row, saved ? state.selected_patches.Contains(name, StringComparer.OrdinalIgnoreCase) : existing);
                }
                var verify = new CheckBox { Text = "Deep repair: re-verify unchanged files too (extra local disk reading)", AutoSize = true, Location = new Point(24, 320), ForeColor = Foreground };
                var clear = new LauncherButton { Text = "Clear all", Location = new Point(24, 363), Size = new Size(110, 34) };
                var all = new LauncherButton { Text = "Select all", Location = new Point(144, 363), Size = new Size(110, 34) };
                var accept = new LauncherButton { Text = "Check selected", Primary = true, Location = new Point(508, 405), Size = new Size(198, 36), DialogResult = DialogResult.OK, Enabled = list.CheckedItems.Count > 0 };
                var cancel = new LauncherButton { Text = "Cancel", Location = new Point(380, 405), Size = new Size(116, 36), DialogResult = DialogResult.Cancel };
                list.ItemCheck += (s, e) => accept.Enabled = list.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : -1) > 0;
                clear.Click += delegate { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
                all.Click += delegate { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
                dialog.Controls.AddRange(new Control[] { Text("Choose only the folders you want. Unselected patches stay untouched.", 24, 20, 682, 28),
                    Text("Totals are package sizes; only changed files download. Classic and HD can be selected separately.", 24, 51, 682, 30),
                    list, verify, clear, all, Text("Only missing or changed files are downloaded, including after a failed update.", 24, 406, 340, 40), accept, cancel });
                dialog.AcceptButton = accept; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
                return new PatchSelection { Names = list.CheckedItems.Cast<Choice>().Select(c => c.Name).ToArray(), Verify = verify.Checked };
            }
        }

        public static string Summary(Plan plan)
        {
            var text = new StringBuilder();
            text.AppendLine("Version: " + plan.Published).AppendLine("Download: " + Size(plan.FetchBytes) + " across " + plan.Fetch.Count + " files").AppendLine();
            foreach (var patch in plan.Patches)
            {
                Func<string, bool> here = k => k.StartsWith(patch + "/", StringComparison.OrdinalIgnoreCase);
                var files = plan.Fetch.Where(f => f.Patch == patch).ToList();
                text.AppendLine(patch).AppendLine("  " + plan.Added.Count(here) + " new / missing, " + plan.Changed.Count(here) + " changed, " + plan.Repairs.Count(here) + " repairs, " + plan.Remove.Count(here) + " retired");
                string latest; if (plan.LatestUpdates.TryGetValue(patch, out latest) && latest.Length > 0) text.AppendLine("  " + latest);
                text.AppendLine("  Transfer: " + Size(files.Sum(f => f.Size)) + (plan.PatchJsons.Any(f => f.Patch == patch) ? "; patch.json will be restored or refreshed after files are complete." : "; patch.json is already current."));
                foreach (var f in files.Take(80)) text.AppendLine("    " + f.Name);
                if (files.Count > 80) text.AppendLine("    ... " + (files.Count - 80) + " more files");
                foreach (var key in plan.Remove.Where(here).Take(20)) text.AppendLine("    Remove: " + key.Substring(patch.Length + 1));
                text.AppendLine();
            }
            if (plan.UpToDate) text.AppendLine("Your selected patches are complete and up to date. No game files need downloading.");
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
                    if (t.IsFaulted) Say("Error: " + t.Exception.GetBaseException().Message);
                    cts.Dispose(); cts = null; return;
                }
                plan = t.Result; lastPlan = plan; changesButton.Enabled = true;
                Say(PatchDialogs.Summary(plan));
                status.Text = plan.UpToDate ? "Your selected patches are complete and up to date." : "Ready: " + plan.Fetch.Count + " files, " + PatchDialogs.Size(plan.FetchBytes) + ".";
                bool confirmed = ConfirmChanges != null ? ConfirmChanges(plan) : PatchDialogs.Review(this, plan);
                cts.Dispose(); cts = null;
                if (apply && confirmed && !plan.UpToDate) ApplyUpdate();
                else if (plan.UpToDate) { Say("You're up to date."); Phase("Complete"); transferText.Text = "Complete 100% | Selected patches verified | ETA done"; }
            }));
        }
    }
}
