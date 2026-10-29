using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>Renders the chart widgets of a finished task into one PNG (sent to Telegram once, when the task ends).</summary>
static class ChartImage
{
    static readonly Color Bg = Color.FromArgb(23, 24, 28), Card = Color.FromArgb(32, 33, 39), GridC = Color.FromArgb(52, 54, 62),
        Fg = Color.FromArgb(230, 231, 235), Dim = Color.FromArgb(150, 153, 163);
    static readonly Color[] Pal =
    {
        Color.FromArgb(79, 142, 247), Color.FromArgb(245, 165, 36), Color.FromArgb(46, 194, 126),
        Color.FromArgb(229, 72, 77), Color.FromArgb(160, 108, 240), Color.FromArgb(23, 181, 200),
    };

    /// <summary>Chart widgets worth drawing (at least 2 points), max 4.</summary>
    public static List<JsonObject> Pick(JsonObject task) =>
        (task?["widgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(w => J.Str(w["type"]) == "chart" && (w["points"] as JsonArray)?.Count >= 2).Take(4).ToList();

    public static byte[] Render(string title, IList<JsonObject> charts)
    {
        const int W = 960, CH = 320, Pad = 16, Top = 54;
        int H = Top + charts.Count * (CH + Pad);
        using var bmp = new Bitmap(W, H);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Bg);
        using var fTitle = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fLabel = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fSmall = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Fg);
        using var dim = new SolidBrush(Dim);
        g.DrawString(J.Cut(title, 70), fTitle, fg, Pad, 14);
        int y = Top;
        foreach (var w in charts)
        {
            try { DrawChart(g, w, new Rectangle(Pad, y, W - 2 * Pad, CH), fLabel, fSmall, fg, dim); }
            catch (Exception e) { Log.W("chart image: " + e.Message); }
            y += CH + Pad;
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static void DrawChart(Graphics g, JsonObject w, Rectangle r, Font fLabel, Font fSmall, Brush fg, Brush dim)
    {
        using (var card = new SolidBrush(Card)) g.FillRectangle(card, r);
        var series = (w["series"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToList();
        var rows = (w["points"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Where(x => x.Count >= 2).ToList();
        int n = rows.Count;
        if (n == 0 || series.Count == 0) return;
        var label = J.Str(w["label"]);
        if (label.Length == 0) label = J.Str(w["id"]);
        var unit = J.Str(w["unit"]);
        g.DrawString(J.Cut(label + (unit.Length > 0 ? ", " + unit : ""), 60), fLabel, fg, r.X + 12, r.Y + 10);

        if (series.Count > 1)   // legend, right-aligned
        {
            float lx = r.Right - 12;
            for (int i = series.Count - 1; i >= 0; i--)
            {
                var sz = g.MeasureString(series[i], fSmall);
                lx -= sz.Width;
                g.DrawString(series[i], fSmall, dim, lx, r.Y + 13);
                lx -= 14;
                using var b = new SolidBrush(Pal[i % Pal.Length]);
                g.FillRectangle(b, lx, r.Y + 18, 10, 10);
                lx -= 14;
            }
        }

        var plot = new RectangleF(r.X + 64, r.Y + 46, r.Width - 64 - 20, r.Height - 46 - 32);
        var kind = J.Str(w["kind"], "line");
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var row in rows)
            for (int i = 1; i < row.Count; i++)
                if (J.Num(row[i]) is double v && double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (lo > hi) { lo = 0; hi = 1; }
        if (kind == "bar") { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        if (J.Num(w["y_min"]) is double ymin) lo = ymin;
        if (J.Num(w["y_max"]) is double ymax) hi = ymax;
        if (hi - lo < 1e-12)
        {
            var d = hi == 0 ? 1 : Math.Abs(hi) * 0.1;
            lo -= d; hi += d;
        }

        double t0 = J.Num(rows[0][0]) ?? 0, t1 = J.Num(rows[^1][0]) ?? t0, span = t1 - t0;
        bool byIndex = span <= 0;
        float X(int i) => plot.Left + plot.Width * (float)(byIndex
            ? (n == 1 ? 0.5 : i / (double)(n - 1))
            : Math.Clamp(((J.Num(rows[i][0]) ?? t0) - t0) / span, 0, 1));
        float Y(double v) => plot.Bottom - plot.Height * (float)((Math.Clamp(v, lo, hi) - lo) / (hi - lo));

        using (var gp = new Pen(GridC, 1))
            for (int k = 0; k <= 4; k++)
            {
                float yy = plot.Bottom - plot.Height * k / 4f;
                g.DrawLine(gp, plot.Left, yy, plot.Right, yy);
                var s = Num(lo + (hi - lo) * k / 4);
                var sz = g.MeasureString(s, fSmall);
                g.DrawString(s, fSmall, dim, plot.Left - 8 - sz.Width, yy - sz.Height / 2);
            }
        for (int k = 0; k <= 4; k++)
        {
            var s = byIndex ? "#" + ((int)Math.Round((n - 1) * k / 4.0) + 1) : k == 0 ? "0"
                : span < 20 ? (span * k / 4).ToString("0.#", CultureInfo.InvariantCulture) + "s" : Hub.FmtDur(span * k / 4);
            var sz = g.MeasureString(s, fSmall);
            float xx = plot.Left + plot.Width * k / 4f - sz.Width / 2;
            xx = Math.Clamp(xx, r.X + 4, r.Right - 4 - sz.Width);
            g.DrawString(s, fSmall, dim, xx, plot.Bottom + 6);
        }

        g.SetClip(RectangleF.Inflate(plot, 2, 2));
        float slot = plot.Width / Math.Max(n, 1);
        for (int s = 0; s < series.Count; s++)
        {
            var col = Pal[s % Pal.Length];
            var pts = new List<PointF>();
            for (int i = 0; i < n; i++)
                if (s + 1 < rows[i].Count && J.Num(rows[i][s + 1]) is double v && double.IsFinite(v))
                    pts.Add(new PointF(X(i), Y(v)));
            if (pts.Count == 0) continue;
            if (kind == "bar")
            {
                float bw = Math.Max(1f, slot * 0.8f / series.Count);
                float y0 = Y(Math.Clamp(0, lo, hi));
                using var b = new SolidBrush(col);
                foreach (var p in pts)
                    g.FillRectangle(b, p.X - slot * 0.4f + s * bw, Math.Min(p.Y, y0), bw, Math.Max(1f, Math.Abs(y0 - p.Y)));
                continue;
            }
            if (kind == "area" && pts.Count > 1)
            {
                var poly = new List<PointF>(pts) { new(pts[^1].X, plot.Bottom), new(pts[0].X, plot.Bottom) };
                using var fb = new SolidBrush(Color.FromArgb(50, col));
                g.FillPolygon(fb, poly.ToArray());
            }
            using var pen = new Pen(col, 2.2f) { LineJoin = LineJoin.Round };
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            else using (var b = new SolidBrush(col)) g.FillEllipse(b, pts[0].X - 3, pts[0].Y - 3, 6, 6);
        }
        g.ResetClip();
    }

    static string Num(double v)
    {
        var a = Math.Abs(v);
        var f = a >= 1000 ? "#,0" : a >= 10 ? "0.#" : a >= 1 ? "0.##" : "0.###";
        return v.ToString(f, CultureInfo.InvariantCulture);
    }
}
