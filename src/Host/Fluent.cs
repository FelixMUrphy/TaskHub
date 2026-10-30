using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Windows.UI.ViewManagement;

namespace TaskHub;

/// <summary>
/// A few owner-drawn controls in the Windows 11 style and the panel's colours, for the WinForms windows (setup): accent and
/// standard buttons, toggle switches, a progress bar, a text field. Sizes are in 100 % pixels times the window's scale
/// (<see cref="Theme.S"/>); the windows lay themselves out (no WinForms auto-scaling).
/// </summary>
static class Fluent
{
    public sealed class Theme
    {
        public bool Dark;
        public float S = 1;   // DeviceDpi / 96
        public Color Bg, Card, Fg, Mut, Line, Ctl, CtlHover, CtlDown, Acc, AccFg, AccHover, Red;

        public static Theme For(Settings s)
        {
            var theme = J.Str(s?.Get("theme"), "system");
            bool dark = theme == "dark" || (theme == "system" && SystemDark());
            var t = new Theme { Dark = dark };
            // as ui.html: --bg, --card, --fg, --mut, controls lighten / darken what is under them by a few percent
            t.Bg = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xf3, 0xf3, 0xf3);
            t.Card = dark ? Color.FromArgb(0x2b, 0x2b, 0x2b) : Color.FromArgb(0xfb, 0xfb, 0xfb);
            t.Fg = dark ? Color.FromArgb(0xf3, 0xf3, 0xf3) : Color.FromArgb(0x1a, 0x1a, 0x1a);
            t.Mut = dark ? Color.FromArgb(0x9e, 0x9e, 0x9e) : Color.FromArgb(0x61, 0x61, 0x61);
            t.Line = Mix(t.Fg, t.Card, 0.12f);
            t.Ctl = Mix(t.Fg, t.Card, 0.06f);
            t.CtlHover = Mix(t.Fg, t.Card, 0.10f);
            t.CtlDown = Mix(t.Fg, t.Card, 0.04f);
            t.Red = dark ? Color.FromArgb(0xf8, 0x71, 0x71) : Color.FromArgb(0xdc, 0x26, 0x26);
            var acc = AccentColor(dark) ?? (dark ? Color.FromArgb(0x5b, 0x8f, 0xf9) : Color.FromArgb(0x2f, 0x6f, 0xed));
            // a grey accent (no colour chosen in Windows): the text colour instead, as the panel does
            if (Chroma(acc) < 0.12f) acc = t.Fg;
            t.Acc = acc;
            t.AccFg = Lum(acc) > 0.179 ? Color.Black : Color.White;
            t.AccHover = Mix(t.Fg, acc, 0.12f);
            return t;
        }

        public int Px(float v) => (int)Math.Round(v * S);
        public Font Font(float px, FontStyle st = FontStyle.Regular) => new(TextFamily, px * S, st, GraphicsUnit.Pixel);
    }

    public static readonly string TextFamily = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static readonly string IconFamily = Win11Menu.IconFont;

    public static bool SystemDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Windows accent colour in the shade Windows uses on this theme (lighter on dark, darker on light).</summary>
    public static Color? AccentColor(bool dark)
    {
        try
        {
            var c = new UISettings().GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            return Color.FromArgb(c.R, c.G, c.B);
        }
        catch { return null; }
    }

    /// <summary>Dark / light title bar to match (Windows 10 20H1+ / 11).</summary>
    public static void TitleBar(Form f, bool dark)
    {
        try { int v = dark ? 1 : 0; Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4); } catch { }
    }

    public static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)Math.Round(a.R * t + b.R * (1 - t)), (int)Math.Round(a.G * t + b.G * (1 - t)), (int)Math.Round(a.B * t + b.B * (1 - t)));

    static float Chroma(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))) / 255f;

    static double Lum(Color c)
    {
        static double L(int v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Base: double-buffered, transparent to its parent's colour, repaints on hover / press.</summary>
    public abstract class Item : Control
    {
        protected Theme T;
        protected bool hover, down;
        protected Item(Theme t)
        {
            T = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Default;
        }
        public void SetTheme(Theme t) { T = t; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        /// <summary>The focus ring of Windows 11 (keyboard focus only).</summary>
        protected void FocusRing(Graphics g, RectangleF r, float rad)
        {
            if (!Focused || !ShowFocusCues) return;
            using var p = new Pen(T.Fg, Math.Max(1.5f, 2 * T.S));
            using var path = Round(RectangleF.Inflate(r, -1, -1), rad);
            g.DrawPath(p, path);
        }

        protected static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0) =>
            TextRenderer.DrawText(g, s, f, r, c, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | extra);
    }

    /// <summary>Hover / press tracking and the Windows 11 focus ring for the owner-drawn standard controls below (they stay
    /// a real Button / CheckBox: keyboard, default / cancel button, UI Automation Invoke / Toggle all work as usual).</summary>
    static void FocusRing(Control c, Theme t, Graphics g, RectangleF r, float rad, bool show)
    {
        if (!c.Focused || !show) return;
        using var p = new Pen(t.Fg, Math.Max(1.5f, 2 * t.S));
        using var path = Round(RectangleF.Inflate(r, -1, -1), rad);
        g.DrawPath(p, path);
    }

    static Color Under(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return SystemColors.Control;
    }

    /// <summary>Rounded button: accent (primary) or subtle, as the panel's .btn / .btn.primary.</summary>
    public sealed class Button : System.Windows.Forms.Button
    {
        Theme T;
        public bool Primary;
        bool hover, down;
        /// <summary>The colour behind the button (its parent's, unless set).</summary>
        public Color? Behind;

        public Button(Theme t, string text, bool primary = false)
        {
            T = t; Text = text; Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            UseMnemonic = false;
        }
        public void SetTheme(Theme t) { T = t; Invalidate(); }

        public int PreferredWidth(Font f) => Math.Max(T.Px(96), TextRenderer.MeasureText(Text, f, Size.Empty, TextFormatFlags.NoPadding).Width + T.Px(28));

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Behind ?? Under(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = Primary ? (down ? Mix(T.Acc, T.Fg, 0.85f) : hover ? T.AccHover : T.Acc) : (down ? T.CtlDown : hover ? T.CtlHover : T.Ctl);
            if (!Enabled) fill = Mix(fill, T.Bg, 0.5f);
            using (var path = Round(r, 4 * T.S))
            {
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                if (!Primary) { using var p = new Pen(T.Line); g.DrawPath(p, path); }
            }
            var fc = Primary ? T.AccFg : T.Fg;
            if (!Enabled) fc = Mix(fc, fill, 0.5f);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fc,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            FocusRing(this, T, g, r, 4 * T.S, ShowFocusCues);
        }
    }

    /// <summary>A settings row: title (and a grey description) on the left, a toggle switch on the right; the whole row
    /// toggles. The switch as in Windows 11 / the panel's .tgl.</summary>
    public sealed class Toggle : CheckBox
    {
        Theme T;
        bool hover;
        public string Description;
        public Font DescFont;

        public Toggle(Theme t, string text, bool value, string desc = null)
        {
            T = t; Text = text; Checked = value; Description = desc;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            UseMnemonic = false;
        }
        public void SetTheme(Theme t) { T = t; Invalidate(); }

        public int PreferredHeight(int width)
        {
            int h = TextRenderer.MeasureText(Text, Font, new Size(width - T.Px(76), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            if (!string.IsNullOrEmpty(Description) && DescFont != null)
                h += T.Px(2) + TextRenderer.MeasureText(Description, DescFont, new Size(width - T.Px(76), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            return Math.Max(T.Px(40), h + T.Px(16));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(T.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = T.S;
            bool on = Checked;
            // the switch: 40 x 20, outlined with a dot when off, accent with a dot of the accent's text colour when on
            var sw = new RectangleF(Width - T.Px(12) - 40 * s, Height / 2f - 10 * s, 40 * s, 20 * s);
            using (var path = Round(sw, 10 * s))
            {
                if (on) { using var b = new SolidBrush(Enabled ? T.Acc : T.Mut); g.FillPath(b, path); }
                else
                {
                    using var b = new SolidBrush(T.Ctl); g.FillPath(b, path);
                    using var p = new Pen(Mix(T.Fg, T.Card, 0.55f), Math.Max(1, s)); g.DrawPath(p, path);
                }
            }
            float d = (hover ? 12 : 10) * s, cx = on ? sw.Right - 10 * s : sw.X + 10 * s, cy = sw.Y + sw.Height / 2;
            using (var b = new SolidBrush(on ? T.AccFg : Mix(T.Fg, T.Card, 0.8f))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            FocusRing(this, T, g, RectangleF.Inflate(sw, 3 * s, 3 * s), 13 * s, ShowFocusCues);
            // the texts, vertically centred as a block
            int tw = (int)(sw.X - T.Px(12) - T.Px(12));
            const TextFormatFlags F = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var ts = TextRenderer.MeasureText(Text, Font, new Size(tw, 0), F);
            Size ds = Size.Empty;
            bool hasDesc = !string.IsNullOrEmpty(Description) && DescFont != null;
            if (hasDesc) ds = TextRenderer.MeasureText(Description, DescFont, new Size(tw, 0), F);
            int total = ts.Height + (hasDesc ? T.Px(2) + ds.Height : 0), y = (Height - total) / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(T.Px(12), y, tw, ts.Height), Enabled ? T.Fg : T.Mut, F);
            if (hasDesc) TextRenderer.DrawText(g, Description, DescFont, new Rectangle(T.Px(12), y + ts.Height + T.Px(2), tw, ds.Height), T.Mut, F);
        }
    }

    /// <summary>Thin rounded progress bar (Windows 11): determinate, or a sliding segment while the value is unknown.</summary>
    public sealed class Progress : Item
    {
        double? value;
        readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public Progress(Theme t) : base(t)
        {
            anim.Tick += (_, _) => Invalidate();
            anim.Start();
        }
        /// <summary>0..1, or null for "working, how long unknown".</summary>
        public double? Value { get => value; set { this.value = value; Invalidate(); } }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Math.Max(1, T.Px(1)), y = Height / 2f;
            using (var p = new Pen(Mix(T.Fg, T.Bg, 0.35f), h)) g.DrawLine(p, h, y, Width - h, y);   // the track: a hairline
            float bh = 3 * T.S, w = Width;
            RectangleF seg;
            if (value is double v) seg = new RectangleF(0, y - bh / 2, (float)(w * Math.Clamp(v, 0, 1)), bh);
            else
            {
                // indeterminate: a segment of 40 % sliding across in 1.6 s
                double ph = clock.Elapsed.TotalSeconds % 1.6 / 1.6;
                float x = (float)(-0.4 * w + ph * 1.4 * w);
                seg = RectangleF.Intersect(new RectangleF(x, y - bh / 2, 0.4f * w, bh), new RectangleF(0, 0, w, Height));
            }
            if (seg.Width <= 0) return;
            using var path = Round(seg, bh / 2);
            using var b = new SolidBrush(T.Acc);
            g.FillPath(b, path);
        }
    }

    /// <summary>Text field as in Windows 11: rounded, a slightly lighter fill, an accent underline while focused.</summary>
    public sealed class Field : Item
    {
        public readonly TextBox Box;
        /// <summary>A small control inside the field at its right end (the "…" of a folder field).</summary>
        public Control Accessory;
        public Field(Theme t, string text) : base(t)
        {
            Box = new TextBox { BorderStyle = BorderStyle.None, Text = text };
            Controls.Add(Box);
            Box.GotFocus += (_, _) => Invalidate();
            Box.LostFocus += (_, _) => Invalidate();
            Box.MouseEnter += (_, _) => { hover = true; Invalidate(); };
            Box.MouseLeave += (_, _) => { hover = false; Invalidate(); };
            ApplyColors();
        }
        public new void SetTheme(Theme t) { base.SetTheme(t); ApplyColors(); }
        void ApplyColors() { Box.BackColor = T.Dark ? Color.FromArgb(0x30, 0x30, 0x30) : Color.White; Box.ForeColor = T.Fg; }
        protected override void OnFontChanged(EventArgs e) { Box.Font = Font; base.OnFontChanged(e); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = T.Px(10), right = 0;
            if (Accessory != null)
            {
                int aw = T.Px(36), inset = T.Px(3);
                Accessory.SetBounds(Width - aw - inset, inset, aw, Height - 2 * inset);
                right = aw + inset;
            }
            Box.SetBounds(pad, (Height - Box.Height) / 2, Width - 2 * pad - right, Box.Height);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using var path = Round(r, 4 * T.S);
            using (var b = new SolidBrush(Box.BackColor)) g.FillPath(b, path);
            using (var p = new Pen(T.Line)) g.DrawPath(p, path);
            if (Box.Focused)
            {
                // accent underline, clipped to the rounded shape
                g.SetClip(path);
                using var ab = new SolidBrush(T.Acc);
                g.FillRectangle(ab, 0, Height - 2 * T.S, Width, 2 * T.S);
                g.ResetClip();
            }
        }
    }

    /// <summary>A glyph of the Windows icon font, centred by its outline (as the tray menu).</summary>
    public static void Glyph(Graphics g, string glyph, float px, PointF center, Color c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fam = new FontFamily(IconFamily);
        using var path = new GraphicsPath();
        path.AddString(glyph, fam, 0, px, PointF.Empty, StringFormat.GenericTypographic);
        var bb = path.GetBounds();
        using var m = new Matrix();
        m.Translate(center.X - (bb.X + bb.Width / 2), center.Y - (bb.Y + bb.Height / 2));
        path.Transform(m);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }
}
