using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TaskHub;

/// <summary>
/// The tray menu drawn like a Windows 11 context menu: rounded window (DWM), neutral greys of the panel's theme, rounded
/// hover, icons from the system icon font (Segoe Fluent Icons) - one colour, state by shape.
/// </summary>
sealed class Win11Menu : ToolStripRenderer
{
    public bool Dark;
    Color Back => Dark ? Color.FromArgb(0x2c, 0x2c, 0x2c) : Color.FromArgb(0xf9, 0xf9, 0xf9);
    Color Fore => Dark ? Color.White : Color.FromArgb(0x1a, 0x1a, 0x1a);
    Color Hover => Dark ? Color.FromArgb(0x3a, 0x3a, 0x3a) : Color.FromArgb(0xec, 0xec, 0xec);
    Color Line => Dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xe2, 0xe2, 0xe2);
    public Color Border => Dark ? Color.FromArgb(0x48, 0x48, 0x48) : Color.FromArgb(0xd8, 0xd8, 0xd8);

    public static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly string TextFont = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }   // DWM draws the rounded border

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = e.ToolStrip.DeviceDpi / 96f;
        var r = new RectangleF(4 * s, 1 * s, e.Item.Width - 8 * s, e.Item.Height - 2 * s);
        using var path = Round(r, 4 * s);
        using var b = new SolidBrush(Hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Fore;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }   // state is in the icon's shape

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is not string glyph) return;
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = e.ToolStrip.DeviceDpi / 96f;
        using var font = new Font(IconFont, 16 * s, GraphicsUnit.Pixel);
        var col = e.Item.Name == "support" ? Mix(Color.FromArgb(0xe0, 0x52, 0x6b), Fore, 0.55f) : Fore;
        using var b = new SolidBrush(col);
        var r = e.ImageRectangle;
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        // a little further from the edge than WinForms puts images, as in the Windows 11 menus
        g.DrawString(glyph, font, b, new RectangleF(r.X + 4 * s, r.Y - 2 * s, r.Width + 4 * s, r.Height + 4 * s), fmt);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = e.ToolStrip.DeviceDpi / 96f;
        int y = e.Item.Height / 2;
        using var p = new Pen(Line, Math.Max(1, s));
        e.Graphics.DrawLine(p, 4 * s, y, e.Item.Width - 4 * s, y);
    }

    static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

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

    /// <summary>Rounded corners and a thin border from DWM (Windows 11; ignored on Windows 10).</summary>
    public void StyleWindow(ToolStripDropDown m)
    {
        try
        {
            int round = 2;   // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int col = Border.R | Border.G << 8 | Border.B << 16;
            Native.DwmSetWindowAttribute(m.Handle, Native.DWMWA_BORDER_COLOR, ref col, sizeof(int));
        }
        catch { }
    }

    /// <summary>Sizes like the Windows 11 menus: 14 px text, ~32 px rows, a little room around the items.</summary>
    public void Apply(ContextMenuStrip m)
    {
        float s = m.DeviceDpi / 96f;
        m.Renderer = this;
        m.Font = new Font(TextFont, 10.5f);
        m.ShowCheckMargin = false;
        m.ShowImageMargin = true;
        m.ImageScalingSize = new Size((int)(16 * s), (int)(16 * s));
        m.Padding = new Padding(0, (int)(4 * s), 0, (int)(4 * s));
        m.BackColor = Back;
        foreach (ToolStripItem it in m.Items)
        {
            if (it is ToolStripSeparator) { it.Margin = new Padding(0, (int)(2 * s), 0, (int)(2 * s)); continue; }
            it.Padding = new Padding((int)(4 * s), (int)(7 * s), (int)(24 * s), (int)(7 * s));
            it.ForeColor = Fore;
            // a placeholder image gives the icon its place; OnRenderItemImage draws the glyph
            it.Image ??= new Bitmap(1, 1);
        }
    }
}
