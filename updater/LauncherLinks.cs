using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace VirgilPatch
{
    // Community resources shown in the Guides & Links window. Only these fixed https addresses can be opened.
    static class ChroniclesLinks
    {
        public const string PhaseId = "35602";

        public class Entry
        {
            public string Title, Kind, Note, Url;
            public bool Featured;
            public Entry(string title, string kind, string note, string url, bool featured)
            { Title = title; Kind = kind; Note = note; Url = url; Featured = featured; }
        }

        public static readonly Entry[] All = {
            new Entry("Chronicles Beginner Guide", "GOOGLE DOC", "Start here if you are new to Chronicles.",
                      "https://docs.google.com/document/d/1hJgGtKooFyIuZWzQoWurIBKNjnvmr8uv4yf84Bu_I3s/edit?usp=sharing", false),
            new Entry("Chronicles Stat Register", "GOOGLE SHEET", "Character stats and registrations.",
                      "https://docs.google.com/spreadsheets/d/1D8dRKhCcE19c_5ZcaO4nw1NjtQODNGPi0KHcWd0mmlg/edit", false),
            new Entry("Chronicles Combat Manual", "GOOGLE DOC", "How combat works.",
                      "https://docs.google.com/document/d/17Wy0YNOV260jLBAeWrSWZuogleqAisdo2XQA-2vUMvw/edit?tab=t.0", false),
            new Entry("Chronicles Wiki", "GOOGLE DOC  /  WORK IN PROGRESS", "The growing lore and world reference.",
                      "https://docs.google.com/document/d/1i9cH1GWdXma7MoVMEuG3JRwLOPylKM7KolvLCYNIByw/edit?tab=t.0", false),
            new Entry("Spell Register", "GOOGLE SHEET  /  MAIN REGISTER", "The main spell register.",
                      "https://docs.google.com/spreadsheets/d/1vfkuuX1OLEDiJbDosCZ1DT7OqKVg24_CSBKWd1LPyLU/edit", true),
            new Entry("Chronicles Systems Manual", "GOOGLE DOC", "Game systems and rules.",
                      "https://docs.google.com/document/d/1y9EjBVGe_QMlH7DWjK6YCO6mvC_xfo_dpcSiYABK4zE/edit?tab=t.0", false),
            new Entry("Watch2gether Music Room", "WATCH2GETHER", "Where we play music together.",
                      "https://w2g.tv/en/room/?access_key=thmhmj0gcah3fhw10ttf98", false),
        };

        public static bool IsAllowed(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host == "docs.google.com" || uri.Host == "w2g.tv";
        }

        public static void Open(IWin32Window owner, string url)
        {
            if (!IsAllowed(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "The link could not be opened in your browser:\r\n" + url + "\r\n\r\n" + ex.Message, "Chronicles of Lordaeron",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    static class LauncherPalette
    {
        public static readonly Color Gold = Color.FromArgb(220, 180, 112);
        public static readonly Color Cream = Color.FromArgb(248, 224, 177);
        public static readonly Color Text = Color.FromArgb(232, 224, 207);
        public static readonly Color Muted = Color.FromArgb(171, 164, 149);
        public static readonly Color Panel = Color.FromArgb(17, 16, 14);
        public static readonly Color Edge = Color.FromArgb(161, 119, 61);

        // Fonts sized in pixels from the control height, so they follow any window scale.
        public static Font Px(string family, float px, FontStyle style)
        { return new Font(family, Math.Max(6f, px), style, GraphicsUnit.Pixel); }
    }

    // "EPSILON PHASE ID IS 35602" plaque. Click copies the id.
    class PhasePlaque : Control
    {
        bool hover;
        DateTime copiedUntil = DateTime.MinValue;
        readonly Timer clear = new Timer { Interval = 1600 };

        public PhasePlaque()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AccessibleName = "Epsilon phase ID is " + ChroniclesLinks.PhaseId + ". Click to copy.";
            AccessibleRole = AccessibleRole.PushButton;
            clear.Tick += delegate { clear.Stop(); Invalidate(); };
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            try { Clipboard.SetText(ChroniclesLinks.PhaseId); copiedUntil = DateTime.UtcNow.AddMilliseconds(1500); clear.Stop(); clear.Start(); }
            catch (Exception) { }
            Invalidate();
        }
        protected override void Dispose(bool disposing) { if (disposing) clear.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var fill = new LinearGradientBrush(r, Color.FromArgb(hover ? 236 : 222, 24, 21, 17), Color.FromArgb(hover ? 236 : 222, 12, 11, 10), 90f))
                g.FillRectangle(fill, r);
            using (var border = new Pen(Color.FromArgb(hover ? 200 : 150, LauncherPalette.Edge))) g.DrawRectangle(border, r);
            var inner = Rectangle.Inflate(r, -3, -3);
            using (var border = new Pen(Color.FromArgb(60, LauncherPalette.Edge))) g.DrawRectangle(border, inner);
            float h = Height;
            var copied = DateTime.UtcNow < copiedUntil;
            using (var kicker = LauncherPalette.Px("Segoe UI", h * 0.15f, FontStyle.Bold))
            using (var value = LauncherPalette.Px("Georgia", h * 0.40f, FontStyle.Regular))
            using (var hint = LauncherPalette.Px("Segoe UI", h * 0.13f, FontStyle.Regular))
            using (var gold = new SolidBrush(LauncherPalette.Gold))
            using (var cream = new SolidBrush(LauncherPalette.Cream))
            using (var muted = new SolidBrush(copied ? LauncherPalette.Gold : LauncherPalette.Muted))
            {
                var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("E P S I L O N   P H A S E   I D   I S", kicker, gold, new RectangleF(0, h * 0.06f, Width, h * 0.26f), centre);
                g.DrawString(ChroniclesLinks.PhaseId, value, cream, new RectangleF(0, h * 0.27f, Width, h * 0.48f), centre);
                g.DrawString(copied ? "Copied to clipboard" : "Click to copy", hint, muted, new RectangleF(0, h * 0.73f, Width, h * 0.22f), centre);
            }
        }
    }

    // One resource row in the Guides & Links window.
    class LinkCard : Control
    {
        readonly ChroniclesLinks.Entry entry;
        bool hover, down;
        public ChroniclesLinks.Entry Entry { get { return entry; } }

        public LinkCard(ChroniclesLinks.Entry entry)
        {
            this.entry = entry;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = entry.Title + ", " + entry.Kind + ". Opens in your browser.";
            AccessibleRole = AccessibleRole.Link;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); ChroniclesLinks.Open(FindForm(), entry.Url); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; ChroniclesLinks.Open(FindForm(), entry.Url); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            var top = entry.Featured ? Color.FromArgb(232, 44, 33, 20) : Color.FromArgb(222, 26, 23, 19);
            var bottom = entry.Featured ? Color.FromArgb(232, 28, 21, 13) : Color.FromArgb(222, 16, 15, 13);
            if (hover || Focused) { top = ControlPaint.Light(top, down ? .05f : .18f); bottom = ControlPaint.Light(bottom, .08f); }
            using (var fill = new LinearGradientBrush(r, top, bottom, 90f)) g.FillRectangle(fill, r);
            using (var border = new Pen(hover || Focused || entry.Featured ? LauncherPalette.Edge : Color.FromArgb(90, 167, 130, 75))) g.DrawRectangle(border, r);
            float h = Height, w = Width, pad = h * 0.28f;
            using (var accent = new SolidBrush(entry.Featured ? Color.FromArgb(221, 167, 79) : Color.FromArgb(120, 156, 114, 59)))
                g.FillRectangle(accent, 0, 0, Math.Max(2f, h * 0.05f), h);
            using (var kind = LauncherPalette.Px("Segoe UI", h * 0.16f, FontStyle.Bold))
            using (var title = LauncherPalette.Px("Georgia", h * 0.30f, FontStyle.Regular))
            using (var note = LauncherPalette.Px("Segoe UI", h * 0.19f, FontStyle.Regular))
            using (var open = LauncherPalette.Px("Segoe UI", h * 0.21f, FontStyle.Bold))
            using (var gold = new SolidBrush(LauncherPalette.Gold))
            using (var cream = new SolidBrush(LauncherPalette.Cream))
            using (var muted = new SolidBrush(LauncherPalette.Muted))
            using (var action = new SolidBrush(hover || Focused ? LauncherPalette.Cream : LauncherPalette.Gold))
            {
                var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                float textW = w - pad * 2 - h * 1.6f;
                g.DrawString(entry.Kind, kind, gold, new RectangleF(pad, h * 0.08f, textW, h * 0.26f), left);
                g.DrawString(entry.Title, title, cream, new RectangleF(pad, h * 0.30f, textW, h * 0.38f), left);
                g.DrawString(entry.Note, note, muted, new RectangleF(pad, h * 0.66f, textW, h * 0.28f), left);
                g.DrawString("OPEN  ›", open, action, new RectangleF(0, 0, w - pad, h), right);
            }
            if (Focused && ShowFocusCues) { var f = Rectangle.Inflate(r, -3, -3); ControlPaint.DrawFocusRectangle(g, f); }
        }
    }

    // Guides & Links window in the launcher's style. Fixed design grid of 560 x 690, scaled for the screen DPI.
    class LinksWindow : Form
    {
        const int DesignW = 560, DesignH = 690;
        readonly Image landscape;
        readonly float k;
        public readonly LinkCard[] Cards;
        public readonly PhasePlaque Plaque = new PhasePlaque();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public LinksWindow()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VirgilPatch.LauncherBackground"))
            {
                if (stream == null) throw new InvalidOperationException("The launcher background is missing from this build.");
                using (var original = Image.FromStream(stream)) landscape = new Bitmap(original);
            }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            Text = "Chronicles Guides & Links";
            BackColor = Color.FromArgb(21, 17, 14);
            ForeColor = LauncherPalette.Text;
            k = DeviceDpi / 96f;
            ClientSize = new Size(S(DesignW), S(DesignH));

            var kicker = Label("C H R O N I C L E S   O F   L O R D A E R O N", 32, 26, 420, 18, 9f, FontStyle.Regular, "Segoe UI", LauncherPalette.Gold);
            var heading = Label("Guides & Links", 30, 46, 440, 46, 24f, FontStyle.Regular, "Georgia", LauncherPalette.Cream);
            var sub = Label("Everything you need for Chronicles, one click away.", 32, 94, 470, 22, 10f, FontStyle.Regular, "Segoe UI", LauncherPalette.Muted);
            foreach (var l in new[] { kicker, heading, sub }) l.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Drag(); };

            Cards = new LinkCard[ChroniclesLinks.All.Length];
            for (int i = 0; i < Cards.Length; i++)
            {
                Cards[i] = new LinkCard(ChroniclesLinks.All[i]) { TabIndex = i };
                Cards[i].SetBounds(S(32), S(138 + i * 66), S(496), S(58));
                Controls.Add(Cards[i]);
            }
            Plaque.SetBounds(S(32), S(612), S(230), S(60));
            Plaque.TabIndex = Cards.Length;
            Controls.Add(Plaque);
            var done = new LauncherButton { Text = "Close", Font = new Font("Segoe UI", 10f), TabIndex = Cards.Length + 1 };
            done.SetBounds(S(388), S(622), S(140), S(40));
            done.Click += delegate { Close(); };
            Controls.Add(done);
            var close = new Button { Text = "×", AccessibleName = "Close", TabStop = false, FlatStyle = FlatStyle.Flat,
                                     BackColor = Color.FromArgb(24, 21, 18), ForeColor = Color.FromArgb(220, 208, 186), Font = new Font("Segoe UI", 14f) };
            close.FlatAppearance.BorderSize = 0;
            close.SetBounds(S(DesignW - 44), S(8), S(36), S(32));
            close.Click += delegate { Close(); };
            Controls.Add(close);
            CancelButton = done;
            ActiveControl = done;                       // no link card looks pre-selected on open
        }

        int S(float v) { return (int)Math.Round(v * k); }

        Label Label(string text, int x, int y, int w, int h, float pt, FontStyle style, string family, Color color)
        {
            var label = new Label { Text = text, BackColor = Color.Transparent, ForeColor = color, Font = new Font(family, pt, style), AutoSize = false, UseMnemonic = false };
            label.SetBounds(S(x), S(y), S(w), S(h));
            Controls.Add(label);
            return label;
        }

        void Drag() { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < S(128)) Drag();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.ScaleTransform(ClientSize.Width / (float)DesignW, ClientSize.Height / (float)DesignH);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // The castle side of the launcher art, darkened into a parchment-dark panel.
            g.DrawImage(landscape, new Rectangle(0, 0, DesignW, DesignH), new Rectangle(landscape.Width * 45 / 100, 0, landscape.Width * 45 / 100, landscape.Height), GraphicsUnit.Pixel);
            using (var shade = new LinearGradientBrush(new Rectangle(0, 0, DesignW, DesignH), Color.FromArgb(150, 10, 9, 8), Color.FromArgb(242, 12, 11, 10), 90f))
                g.FillRectangle(shade, 0, 0, DesignW, DesignH);
            using (var line = new Pen(Color.FromArgb(112, 156, 114, 59))) g.DrawLine(line, 32, 124, DesignW - 32, 124);
            using (var line = new Pen(Color.FromArgb(112, 156, 114, 59))) g.DrawLine(line, 32, 600, DesignW - 32, 600);
            using (var line = new Pen(Color.FromArgb(100, 173, 130, 65))) g.DrawRectangle(line, 0, 0, DesignW - 1, DesignH - 1);
            g.ResetTransform();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) landscape.Dispose();
            base.Dispose(disposing);
        }
    }
}
