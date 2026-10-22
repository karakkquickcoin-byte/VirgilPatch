using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VirgilPatch
{
    // Artwork is embedded in the program so an EXE-only automatic upgrade is complete.
    // The window is laid out on a fixed 1000 x 650 design grid. Dragging an edge or corner resizes it with the aspect
    // ratio locked; every control and font is scaled from its design bounds (DPI included).
    partial class LauncherWindow : Form
    {
        public const int DesignW = 1000, DesignH = 650;
        readonly Image landscape;
        readonly List<KeyValuePair<Control, Rectangle>> design = new List<KeyValuePair<Control, Rectangle>>();
        readonly Dictionary<Control, Font> designFonts = new Dictionary<Control, Font>();
        readonly Dictionary<string, Font> scaledFonts = new Dictionary<string, Font>();
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        public LauncherWindow()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VirgilPatch.LauncherBackground"))
            {
                if (stream == null) throw new InvalidOperationException("The launcher background is missing from this build.");
                using (var original = Image.FromStream(stream)) landscape = new Bitmap(original);
            }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(21, 17, 14);
            ForeColor = Color.FromArgb(232, 224, 207);
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
        }

        float Dpi { get { return DeviceDpi / 96f; } }
        public float LayoutScale { get { return ClientSize.Width / (float)DesignW; } }

        // Call once after the controls are placed in design units: records them, then sizes the window for the DPI.
        protected void CaptureLayout()
        {
            design.Clear(); designFonts.Clear();
            foreach (Control c in Controls)
            {
                c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                design.Add(new KeyValuePair<Control, Rectangle>(c, c.Bounds));
                designFonts[c] = c.Font;
            }
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            float fit = Math.Min(1f, Math.Min(screen.Width / (DesignW * Dpi), screen.Height / (DesignH * Dpi)));
            MinimumSize = new Size((int)(700 * Dpi * Math.Min(1f, fit / 0.7f)), (int)(455 * Dpi * Math.Min(1f, fit / 0.7f)));
            ClientSize = new Size((int)Math.Round(DesignW * Dpi * fit), (int)Math.Round(DesignH * Dpi * fit));
            ApplyLayout();
        }

        void ApplyLayout()
        {
            if (design.Count == 0) return;
            float s = LayoutScale, fs = s / Dpi;
            SuspendLayout();
            foreach (var item in design)
            {
                var r = item.Value;
                item.Key.SetBounds((int)Math.Round(r.X * s), (int)Math.Round(r.Y * s), Math.Max(1, (int)Math.Round(r.Width * s)), Math.Max(1, (int)Math.Round(r.Height * s)));
                Font f;
                if (designFonts.TryGetValue(item.Key, out f)) item.Key.Font = Scaled(f, fs);
            }
            ResumeLayout(false);
            Invalidate(true);
        }

        Font Scaled(Font f, float fs)
        {
            float size = (float)Math.Round(f.SizeInPoints * fs * 4f) / 4f;
            if (Math.Abs(size - f.SizeInPoints) < .01f) return f;
            var key = f.FontFamily.Name + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)f.Style;
            Font cached;
            if (!scaledFonts.TryGetValue(key, out cached)) scaledFonts[key] = cached = new Font(f.FontFamily, size, f.Style);
            return cached;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) ApplyLayout();
            SyncVideoPlayback();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214;
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int grip = Math.Max(6, (int)(8 * Dpi));
                bool left = p.X < grip, right = p.X >= ClientSize.Width - grip, top = p.Y < grip, bottom = p.Y >= ClientSize.Height - grip;
                if (right && bottom || p.X >= ClientSize.Width - grip * 3 && p.Y >= ClientSize.Height - grip * 3) m.Result = (IntPtr)17;
                else if (left && bottom) m.Result = (IntPtr)16;
                else if (right && top) m.Result = (IntPtr)14;
                else if (left && top) m.Result = (IntPtr)13;
                else if (bottom) m.Result = (IntPtr)15;
                else if (top) m.Result = (IntPtr)12;
                else if (right) m.Result = (IntPtr)11;
                else if (left) m.Result = (IntPtr)10;
                return;
            }
            if (m.Msg == WM_SIZING)
            {
                // Keep the 1000:650 artwork ratio while dragging any edge or corner.
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int edge = m.WParam.ToInt32();
                int w = Math.Max(MinimumSize.Width, r.Right - r.Left), h = Math.Max(MinimumSize.Height, r.Bottom - r.Top);
                if (edge == 3 || edge == 6) w = (int)Math.Round(h * DesignW / (float)DesignH);
                else h = (int)Math.Round(w * DesignH / (float)DesignW);
                if (edge == 1 || edge == 4 || edge == 7) r.Left = r.Right - w; else r.Right = r.Left + w;
                if (edge == 3 || edge == 4 || edge == 5) r.Top = r.Bottom - h; else r.Bottom = r.Top + h;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            // Draw in the same logical coordinates as the DPI-scaled child controls.
            float sx = ClientSize.Width / 1000f, sy = ClientSize.Height / 650f;
            g.ScaleTransform(sx, sy);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            DrawBackgroundMedia(g);
            using (var shade = new LinearGradientBrush(new Rectangle(0, 0, 1000, 650), Color.FromArgb(205, 10, 9, 8), Color.FromArgb(35, 10, 9, 8), 0f))
                g.FillRectangle(shade, 0, 0, 1000, 650);
            using (var shade = new LinearGradientBrush(new Rectangle(0, 0, 1000, 650), Color.FromArgb(20, 10, 9, 8), Color.FromArgb(180, 10, 9, 8), 90f))
                g.FillRectangle(shade, 0, 0, 1000, 650);
            using (var fill = new SolidBrush(Color.FromArgb(204, 15, 14, 12))) g.FillRectangle(fill, 48, 206, 420, 226);
            using (var border = new Pen(Color.FromArgb(105, 167, 130, 75))) g.DrawRectangle(border, 48, 206, 420, 226);
            using (var fill = new SolidBrush(Color.FromArgb(238, 17, 16, 14))) g.FillRectangle(fill, 0, 466, 1000, 184);
            using (var line = new Pen(Color.FromArgb(112, 156, 114, 59))) g.DrawLine(line, 48, 466, 952, 466);
            using (var line = new Pen(Color.FromArgb(100, 173, 130, 65))) g.DrawRectangle(line, 0, 0, 999, 649);
            using (var grip = new Pen(Color.FromArgb(130, 173, 130, 65)))          // resize grip, bottom-right corner
                for (int i = 0; i < 3; i++) g.DrawLine(grip, 996 - i * 5, 646, 996, 646 - i * 5);
            g.ResetTransform();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < 190 * ClientSize.Height / 650f) DragWindow();
        }
        protected void DragWindow() { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeBackgroundVideo(); landscape.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class LauncherButton : Button
    {
        public bool Primary;
        bool hover, down;
        public LauncherButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var top = Primary ? Color.FromArgb(157, 100, 37) : Color.FromArgb(42, 36, 27);
            var bottom = Primary ? Color.FromArgb(94, 51, 21) : Color.FromArgb(27, 24, 20);
            if (!Enabled) { top = Color.FromArgb(57, 47, 34); bottom = Color.FromArgb(39, 32, 25); }
            else if (hover || down) { top = ControlPaint.Light(top, down ? .1f : .25f); bottom = ControlPaint.Light(bottom, .1f); }
            using (var brush = new LinearGradientBrush(rect, top, bottom, 90f)) e.Graphics.FillRectangle(brush, rect);
            using (var border = new Pen(Enabled ? Color.FromArgb(161, 119, 61) : Color.FromArgb(77, 66, 47))) e.Graphics.DrawRectangle(border, rect);
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? Color.FromArgb(251, 221, 162) : Color.FromArgb(133, 118, 94),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) { rect.Inflate(-4, -4); ControlPaint.DrawFocusRectangle(e.Graphics, rect); }
        }
    }

    class LauncherProgress : Control
    {
        int value;
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1000, value)); Invalidate(); } }
        public LauncherProgress() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(53, 44, 31));
            if (value > 0) using (var fill = new SolidBrush(Color.FromArgb(221, 167, 79))) e.Graphics.FillRectangle(fill, 0, 0, Width * value / 1000, Height);
        }
    }

    partial class MainForm
    {
        readonly Button details = new Button();
        readonly Button sound = new Button();
        LauncherAmbience ambience;
        bool isBusy;

        void StartAmbience()
        {
            try
            {
                ambience = new LauncherAmbience(Say, delegate(bool ready) {
                    if (IsDisposed || Disposing) return;
                    sound.Enabled = ready;
                    if (!ready) sound.Text = "Sound unavailable";
                });
                ambience.Start(Config.StartAmbienceMuted);
                sound.Text = ambience.Muted ? "Sound off" : "Sound on (40%)";
            }
            catch (Exception ex)
            {
                if (ambience != null) { ambience.Dispose(); ambience = null; }
                sound.Enabled = false; sound.Text = "Sound unavailable";
                Say("Ambience unavailable: " + ex.Message + " Patch updates can continue.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ambience != null) { ambience.Dispose(); ambience = null; }
            base.Dispose(disposing);
        }

        Label Caption(string text, int x, int y, int w, int h, Font font, Color color)
        {
            var label = new Label { Text = text, BackColor = Color.Transparent, ForeColor = color, Font = font, AutoSize = false, UseMnemonic = false };
            label.SetBounds(x, y, w, h);
            Controls.Add(label);
            return label;
        }

        void BuildLauncher()
        {
            Text = "Chronicles of Lordaeron";
            ClientSize = new Size(1000, 650);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var gold = Color.FromArgb(220, 180, 112);
            var muted = Color.FromArgb(171, 164, 149);
            var small = new Font("Segoe UI", 9f);
            var normal = new Font("Segoe UI", 10f);
            var heading = Caption("CHRONICLES", 45, 68, 700, 50, new Font("Georgia", 31f), Color.FromArgb(248, 224, 177));
            var title = Caption("OF LORDAERON", 47, 119, 700, 42, new Font("Georgia", 23f), gold);
            var kicker = Caption("P A T C H   L A U N C H E R", 50, 44, 400, 20, small, gold);
            foreach (var label in new[] { heading, title, kicker }) label.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) DragWindow(); };
            Caption("Before you begin", 72, 226, 370, 28, new Font("Georgia", 16f), Color.FromArgb(237, 207, 156));
            Caption("01", 73, 275, 35, 24, normal, gold);
            Caption("Select your Epsilon Patches folder.", 115, 275, 330, 24, normal, ForeColor);
            Caption("02", 73, 316, 35, 24, normal, gold);
            Caption("Close World of Warcraft before updating.", 115, 316, 333, 24, normal, ForeColor);
            Caption("03", 73, 357, 35, 24, normal, gold);
            Caption("Click Update, then choose the patches you want.", 115, 357, 330, 24, normal, ForeColor);
            Caption("Patch files change only after you confirm Update.", 73, 400, 375, 20, small, muted);

            Caption("PATCHES FOLDER", 48, 485, 500, 23, small, gold);
            dirBox.BorderStyle = BorderStyle.FixedSingle;
            dirBox.BackColor = Color.FromArgb(28, 26, 22);
            dirBox.ForeColor = Color.FromArgb(224, 216, 200);
            dirBox.Font = normal;
            dirBox.SetBounds(48, 515, 680, 29);
            dirBox.AccessibleName = "Selected Epsilon Patches folder";
            dirBox.TabIndex = 0;
            browse.Text = "Select folder";
            browse.Font = normal;
            browse.SetBounds(750, 507, 202, 40);
            browse.TabIndex = 1;
            status.BackColor = Color.Transparent;
            status.ForeColor = Color.FromArgb(223, 211, 187);
            status.Font = normal;
            status.SetBounds(48, 565, 680, 37);
            updateBtn.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            updateBtn.SetBounds(750, 565, 202, 48);
            updateBtn.TabIndex = 2;
            updateBtn.AccessibleName = "Update patches";
            bar.SetBounds(48, 625, 904, 4);
            Caption("Chronicles of Lordaeron  /  " + SelfUpdate.CurrentVersion.ToString(3), 48, 634, 650, 16, new Font("Segoe UI", 8f), Color.FromArgb(130, 122, 107));
            details.Text = "Details";
            details.FlatStyle = FlatStyle.Flat;
            details.FlatAppearance.BorderSize = 0;
            details.BackColor = Color.FromArgb(17, 16, 14);
            details.ForeColor = muted;
            details.Font = small;
            details.Cursor = Cursors.Hand;
            details.SetBounds(648, 478, 80, 28);
            details.TabIndex = 3;
            log.Visible = false;
            log.BorderStyle = BorderStyle.FixedSingle;
            log.BackColor = Color.FromArgb(21, 20, 17);
            log.ForeColor = Color.FromArgb(211, 201, 178);
            log.SetBounds(48, 206, 904, 226);
            log.TabIndex = 4;
            details.Click += delegate { log.Visible = !log.Visible; details.Text = log.Visible ? "Hide details" : "Details"; if (log.Visible) log.BringToFront(); };
            Controls.AddRange(new Control[] { dirBox, browse, status, updateBtn, bar, details, log });
            sound.FlatStyle = FlatStyle.Flat;
            sound.FlatAppearance.BorderSize = 0;
            sound.BackColor = Color.FromArgb(17, 16, 14);
            sound.ForeColor = muted;
            sound.Font = new Font("Segoe UI", 8f);
            sound.Text = "Sound on (40%)";
            sound.AccessibleName = "Mute or unmute launcher ambience at 40 percent";
            sound.Cursor = Cursors.Hand;
            sound.TabIndex = 5;
            sound.SetBounds(812, 630, 140, 20);
            sound.Click += delegate {
                if (ambience == null) return;
                ambience.Muted = !ambience.Muted;
                sound.Text = ambience.Muted ? "Sound off" : "Sound on (40%)";
            };
            Controls.Add(sound);
            var minimize = new Button { Text = "\u2013", AccessibleName = "Minimize", TabStop = false };
            var close = new Button { Text = "\u00d7", AccessibleName = "Close", TabStop = false };
            foreach (var button in new[] { minimize, close })
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.BackColor = Color.FromArgb(24, 21, 18);
                button.ForeColor = Color.FromArgb(220, 208, 186);
                button.Font = new Font("Segoe UI", 14f);
                Controls.Add(button);
            }
            minimize.SetBounds(912, 8, 36, 32); close.SetBounds(952, 8, 36, 32);
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            close.Click += delegate { Close(); };

            phasePlaque.SetBounds(752, 56, 200, 74);
            phasePlaque.TabIndex = 6;
            linksBtn.Text = "Guides && Links";
            linksBtn.Font = normal;
            linksBtn.AccessibleName = "Open Chronicles guides and links";
            linksBtn.SetBounds(752, 142, 200, 40);
            linksBtn.TabIndex = 7;
            linksBtn.Click += delegate { ShowLinks(); };
            Controls.Add(phasePlaque);
            Controls.Add(linksBtn);
            AcceptButton = updateBtn;
            transferText.SetBounds(48, 604, 594, 19);
            transferText.Font = new Font("Segoe UI", 8.5f);
            transferText.Text = "Ready 0% | Choose patches with Update | ETA --";
            cancelWork.SetBounds(650, 601, 78, 22);
            cancelWork.Font = new Font("Segoe UI", 8f);
            cancelWork.Click += delegate { if (cts != null) { cts.Cancel(); cancelWork.Enabled = false; status.Text = "Cancelling safely; completed files will be kept."; } };
            changesButton.SetBounds(752, 190, 200, 32);
            changesButton.Enabled = false;
            changesButton.Click += delegate { if (lastPlan != null) PatchDialogs.Review(this, lastPlan, false); };
            copyDiagnostics.SetBounds(460, 478, 180, 28);
            copyDiagnostics.Font = small;
            copyDiagnostics.Click += delegate { CopyDiagnostics(); };
            Controls.AddRange(new Control[] { transferText, cancelWork, changesButton, copyDiagnostics });
            var gear = new LauncherButton {Text="\u2699",AccessibleName="Open updater changelog",Font=new Font("Segoe UI Symbol",12f)};
            gear.SetBounds(8,628,32,22);gear.Click+=delegate{PatchDialogs.Changelog(this);};Controls.Add(gear);
            CaptureLayout();
        }

        readonly PhasePlaque phasePlaque = new PhasePlaque();
        readonly Button linksBtn = new LauncherButton();
        LinksWindow links;

        void ShowLinks()
        {
            if (links != null && !links.IsDisposed) { links.Activate(); return; }
            links = new LinksWindow();
            links.FormClosed += delegate { links.Dispose(); links = null; };
            links.StartPosition = FormStartPosition.Manual;
            var area = Screen.FromControl(this).WorkingArea;
            links.Location = new Point(Math.Max(area.Left, Math.Min(area.Right - links.Width, Left + (Width - links.Width) / 2)),
                                       Math.Max(area.Top, Math.Min(area.Bottom - links.Height, Top + (Height - links.Height) / 2)));
            links.Show(this);
        }

        void Post(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (!IsDisposed && !Disposing) action(); })); }
            catch (InvalidOperationException) { if (!IsDisposed && !Disposing) throw; }
        }
    }
}
