using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TaskHub;

/// <summary>
/// The tray menu drawn like a Windows 11 menu: rounded window (DWM), the panel's neutral greys, a soft rounded plate under
/// the hovered row, icons from the system icon font (Segoe Fluent Icons) - one colour, state by shape. The layout is ours,
/// not WinForms': sizes of the system menus (menu font, ~23.5 px rows at 100 %), icon and text centred on the same line
/// (the icon by its drawn outline, the text by the middle of its capitals / small letters), at any scale and in any language.
/// </summary>
sealed class Win11Menu : ToolStripRenderer
{
    public bool Dark;
    float s = 1;   // scale of the monitor the menu opens on (1 = 100 %)

    // the panel's colours (ui.html: --fg, --mut; the support heart = #e0526b mixed 55 % into the text colour)
    static Color Back(bool dark) => dark ? Color.FromArgb(0x2c, 0x2c, 0x2c) : Color.FromArgb(0xf9, 0xf9, 0xf9);
    static Color Fore(bool dark) => dark ? Color.FromArgb(0xf3, 0xf3, 0xf3) : Color.FromArgb(0x1a, 0x1a, 0x1a);
    static Color Hover(bool dark) => dark ? Color.FromArgb(0x3a, 0x3a, 0x3a) : Color.FromArgb(0xeb, 0xeb, 0xeb);
    static Color Line(bool dark) => dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xe0, 0xe0, 0xe0);
    static Color Border(bool dark) => dark ? Color.FromArgb(0x48, 0x48, 0x48) : Color.FromArgb(0xd6, 0xd6, 0xd6);
    public static Color Heart(bool dark) => Mix(Color.FromArgb(0xe0, 0x52, 0x6b), Fore(dark), 0.55f);

    public static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    // ------------------------------------------------------------ layout (in 100 % pixels, times the scale)
    public const float Row = 23.5f, Sep = 7.5f, PadY = 3, IconX = 20, TextX = 38, PadRight = 26, Icon = 16, PlateX = 4, PlateY = 1.5f, Radius = 4;

    /// <summary>The system menu font (follows Windows' text size) in pixels at this scale.</summary>
    public static Font TextFont(float scale, bool bold) =>
        new(SystemFonts.MenuFont.FontFamily, SystemFonts.MenuFont.SizeInPoints * 96f / 72f * scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static int Px(float v, float scale) => (int)Math.Round(v * scale);

    /// <summary>Paints one row: the hover plate, the icon and the text, both centred on the row's middle line.</summary>
    public static void PaintRow(Graphics g, Rectangle r, string glyph, string text, bool bold, bool hover, bool heart, float scale, bool dark)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (hover)
        {
            var pr = new RectangleF(r.X + PlateX * scale, r.Y + PlateY * scale, r.Width - 2 * PlateX * scale, r.Height - 2 * PlateY * scale);
            using var path = Round(pr, Radius * scale);
            using var b = new SolidBrush(Hover(dark));
            g.FillPath(b, path);
        }
        float cy = r.Y + r.Height / 2f;
        if (!string.IsNullOrEmpty(glyph))
        {
            // the icon by its drawn outline: exactly centred whatever the font's ascent / descent
            using var fam = new FontFamily(IconFont);
            using var path = new GraphicsPath();
            path.AddString(glyph, fam, 0, Icon * scale, PointF.Empty, StringFormat.GenericTypographic);
            var bb = path.GetBounds();
            if (bb.Width > 0)
            {
                using var m = new Matrix();
                m.Translate(r.X + IconX * scale - (bb.X + bb.Width / 2), cy - (bb.Y + bb.Height / 2));
                path.Transform(m);
                using var b = new SolidBrush(heart ? Heart(dark) : Fore(dark));
                g.FillPath(b, path);
            }
        }
        if (!string.IsNullOrEmpty(text))
        {
            // the text by the middle between its capitals and small letters (what the eye takes for the middle of a word),
            // not by its line box, which has more room above than below and makes the text look high
            using var f = TextFont(scale, bold);
            var ff = f.FontFamily;
            float em = f.Size, unit = em / ff.GetEmHeight(f.Style);
            float ascent = ff.GetCellAscent(f.Style) * unit;
            float baseline = cy + em * 0.31f;                  // Segoe UI: capitals 0.70 em, small letters 0.50 em -> 0.30-0.31
            int top = (int)Math.Round(baseline - ascent);
            var tr = new Rectangle(r.X + Px(TextX, scale), top, r.Width - Px(TextX + PadRight / 2, scale), (int)Math.Ceiling(ascent + ff.GetCellDescent(f.Style) * unit) + 1);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(g, text, f, tr, Fore(dark),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
        }
    }

    public static void PaintSeparator(Graphics g, Rectangle r, float scale, bool dark)
    {
        using var p = new Pen(Line(dark), Math.Max(1, (int)scale));
        int y = r.Y + r.Height / 2;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawLine(p, r.X + PlateX * scale, y, r.Right - PlateX * scale, y);
    }

    /// <summary>Width of the menu for these texts.</summary>
    public static int Width(IEnumerable<(string text, bool bold)> items, float scale)
    {
        int w = 0;
        foreach (var (t, b) in items)
        {
            using var f = TextFont(scale, b);
            w = Math.Max(w, TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
        }
        return Px(TextX + PadRight, scale) + w;
    }

    // ------------------------------------------------------------ ToolStrip glue
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Back(Dark));
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }   // DWM draws the rounded border
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { }   // drawn with the row below

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var it = e.Item;
        PaintRow(e.Graphics, new Rectangle(Point.Empty, it.Size), it.Tag as string, it.Text, it.Font.Bold,
            it.Selected && it.Enabled, it.Name == "support", s, Dark);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) =>
        PaintSeparator(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), s, Dark);

    /// <summary>Lays the menu out for the monitor under the cursor (where the tray menu opens).
    /// ToolStripDropDownMenu sizes itself from the measured text and resets its Padding on every layout, so the menu gets
    /// an explicit size (AutoSize off) and the gap above the first / below the last row comes from two empty spacer rows.</summary>
    public void Apply(ContextMenuStrip m)
    {
        s = ScaleAt(Cursor.Position);
        m.Renderer = this;
        m.ShowCheckMargin = false;
        m.ShowImageMargin = false;
        m.BackColor = Back(Dark);
        if (m.Items.Count == 0 || m.Items[0].Name != "pad")
        {
            m.Items.Insert(0, new ToolStripLabel { Name = "pad" });
            m.Items.Add(new ToolStripLabel { Name = "pad" });
        }
        var rows = m.Items.OfType<ToolStripMenuItem>().ToList();
        int w = Width(rows.Select(i => (i.Text, i.Font.Bold)), s);
        int h = 0;
        foreach (ToolStripItem it in m.Items)
        {
            it.AutoSize = false;
            it.Margin = Padding.Empty;
            it.Padding = Padding.Empty;
            int ih = it.Name == "pad" ? Math.Max(0, Px(PadY, s) - m.Padding.Top) : Px(it is ToolStripSeparator ? Sep : Row, s);
            it.Size = new Size(w, ih);
            h += ih;
        }
        m.AutoSize = false;
        m.Size = new Size(w, h + m.Padding.Vertical);
    }

    /// <summary>Rounded corners and a thin border from DWM (Windows 11; ignored on Windows 10).</summary>
    public void StyleWindow(ToolStripDropDown m)
    {
        try
        {
            int round = 2;   // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            var c = Border(Dark);
            int col = c.R | c.G << 8 | c.B << 16;
            Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_BORDER_COLOR, ref col, sizeof(int));
        }
        catch { }
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

    static float ScaleAt(Point p)
    {
        try { if (GetDpiForMonitor(MonitorFromPoint(p, 2 /*nearest*/), 0, out var x, out _) == 0 && x > 0) return x / 96f; }
        catch { }
        return 1;
    }

    static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)Math.Round(a.R * t + b.R * (1 - t)), (int)Math.Round(a.G * t + b.G * (1 - t)), (int)Math.Round(a.B * t + b.B * (1 - t)));

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
