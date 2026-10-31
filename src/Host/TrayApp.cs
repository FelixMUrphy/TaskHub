using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

sealed class TrayApp : ApplicationContext
{
    readonly Settings S;
    readonly Hub hub;
    readonly Api api;
    readonly NotifyIcon tray;
    public readonly PanelForm Panel;
    readonly Control ui;            // marshalling target
    readonly System.Windows.Forms.Timer refresh;
    volatile bool dirty = true;
    string lastIconKey;
    IntPtr lastHicon;
    readonly CancellationTokenSource cts = new();
    public TelegramBridge Telegram;
    public Updater Updater;
    public GitHubConnector GitHub;
    public MachineConnector[] Machines = Array.Empty<MachineConnector>();

    public TrayApp(Settings s, Hub h, Api a, Notifier n, string openTask)
    {
        S = s; hub = h; api = a;
        Telegram = n.Telegram;
        ui = new Control();
        ui.CreateControl();
        _ = ui.Handle;

        Panel = new PanelForm(s, h);
        _ = Panel.Handle;   // create window so WebView2 can initialise while hidden

        tray = new NotifyIcon { Text = "TaskHub", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Panel.Toggle(); };
        tray.BalloonTipClicked += (_, _) => Panel.ShowPanel(null);
        tray.ContextMenuStrip = BuildMenu();

        n.OpenPanel = tid => Ui(() => Panel.ShowPanel(tid));
        n.PanelOpen = () => Panel.IsShown;
        n.Balloon = (t, m) => Ui(() => { try { tray.ShowBalloonTip(5000, J.Cut(t, 60), string.IsNullOrEmpty(m) ? " " : J.Cut(m, 250), ToolTipIcon.None); } catch { } });
        api.UiOpen = tid => { Ui(() => Panel.ShowPanel(tid)); return true; };
        api.UiHide = () =>
        {
            bool vis = false;
            try { ui.Invoke(() => { vis = Panel.Visible; Panel.HidePanel(true); }); } catch { }
            return vis;
        };

        hub.Changed += () => dirty = true;
        refresh = new System.Windows.Forms.Timer { Interval = 700 };
        refresh.Tick += (_, _) => { if (dirty) { dirty = false; UpdateTray(); } };
        refresh.Start();
        UpdateTray();

        _ = Task.Run(() => hub.Watchdog(cts.Token));
        Ui(async () =>
        {
            // here, not above: GitHub / Updater are set by the caller after the constructor
            Panel.SettingsPage = new SettingsBridge(S, hub, Telegram, GitHub, Machines, () => Updater, Ui);
            try
            {
                // no WebView2 Runtime: say so once (a click on the balloon / the next open offers the download)
                if (!await Panel.InitAsync())
                    tray.ShowBalloonTip(10000, "TaskHub", J.Cut(L.T("webview2_missing"), 250), ToolTipIcon.Warning);
            }
            catch (Exception e)
            {
                Log.W("panel unavailable: " + e);
                tray.ShowBalloonTip(8000, "TaskHub", "WebView2 panel failed: " + e.Message, ToolTipIcon.Warning);
            }
            if (openTask != null && !Panel.RuntimeMissing) Panel.ShowPanel(openTask == "" ? null : openTask);
        });
    }

    void Ui(Action a)
    {
        try { if (ui.IsHandleCreated) ui.BeginInvoke(a); } catch { }
    }

    // Only the quick actions; everything else (language, AI agents, Telegram, connectors, updates, data folder, API
    // reference, test notification, autostart) is on the panel's settings page (SettingsBridge).
    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var look = new Win11Menu();
        var items = new List<(ToolStripMenuItem item, string key)>();
        ToolStripMenuItem Add(string key, string glyph, Action a)
        {
            var it = new ToolStripMenuItem(key, null, (_, _) => a()) { Name = key, Tag = glyph };
            m.Items.Add(it);
            items.Add((it, key));
            return it;
        }
        var miOpen = Add("open_panel", "", () => Panel.ShowPanel(null));
        var miMute = Add("mute", "", () => hub.SetMuted(!hub.Muted));
        Add("settings_menu", "", () => Panel.OpenSettings());
        Add("support", "", () => Hub.OpenTarget(L.SupportUrl));
        m.Items.Add(new ToolStripSeparator());
        Add("quit", "", Quit);
        m.Opening += (_, _) =>
        {
            look.Dark = Panel.EffectiveDark();
            foreach (var (it, key) in items) it.Text = L.T(key).TrimStart('❤', ' ', '️');
            // state by shape and words, like the panel's bell: crossed bell + "Turn notifications on" while muted
            miMute.Text = L.T(hub.Muted ? "unmute" : "mute");
            miMute.Tag = hub.Muted ? "" : "";
            miOpen.Font = new Font(m.Font, FontStyle.Bold);   // the default item, as in the system menus (the renderer uses its own font)
            look.Apply(m);
        };
        m.Opened += (_, _) => look.StyleWindow(m);
        return m;
    }

    // ------------------------------------------------------------ tray icon
    void UpdateTray()
    {
        var s = hub.GetSummary();
        try
        {
            var key = $"{s.ActiveT.Count}|{s.Failed.Count}|{s.Stale.Count}|{s.RecentDone.Count > 0}|{(s.Progress is double p ? Math.Round(p * 40) : -1)}";
            if (key != lastIconKey)
            {
                lastIconKey = key;
                int size = Math.Max(16, (int)Math.Round(16 * Panel.DeviceDpi / 96.0));
                using var bmp = DrawIcon(s, size);
                var h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (lastHicon != IntPtr.Zero) Native.DestroyIcon(lastHicon);
                old?.Dispose();
                lastHicon = h;
            }
            tray.Text = Tooltip(s);
        }
        catch (Exception e) { Log.W("tray update: " + e.Message); }
    }

    string Tooltip(Hub.Summary s)
    {
        var parts = new List<string>();
        foreach (var t in s.ActiveT.Take(4))
        {
            var p = J.Num(t["progress"]) is double pr ? $" {Math.Round(pr * 100)}%" : "";
            parts.Add(J.Cut(J.Str(t["name"]), 28) + p + (J.Str(t["state"]) == "stale" ? " ⚠" : ""));
        }
        if (s.Failed.Count > 0) parts.Add(L.N("failed_n", s.Failed.Count));
        var head = "TaskHub — " + L.N("active_n", s.ActiveT.Count) + (hub.Muted ? " " + L.T("muted") : "");
        return J.Cut(head + (parts.Count > 0 ? "\n" + string.Join("\n", parts) : ""), 127);
    }

    public static Bitmap DrawIcon(Hub.Summary s, int size)
    {
        int S4 = size * 4;   // supersample
        using var big = new Bitmap(S4, S4, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            int n = s?.ActiveT.Count ?? 0;
            Color col = s == null ? Color.FromArgb(120, 128, 140)
                : s.Failed.Count > 0 ? Color.FromArgb(239, 68, 68)
                : s.Stale.Count > 0 ? Color.FromArgb(245, 158, 11)
                : n > 0 ? Color.FromArgb(59, 130, 246)
                : s.RecentDone.Count > 0 ? Color.FromArgb(34, 197, 94)
                : Color.FromArgb(120, 128, 140);
            float u = S4 / 64f;
            using (var bg = new SolidBrush(Color.FromArgb(255, 30, 33, 40))) g.FillEllipse(bg, 2 * u, 2 * u, S4 - 5 * u, S4 - 5 * u);
            float pw = 7 * u;
            var rc = new RectangleF(4 * u + pw / 2, 4 * u + pw / 2, S4 - 9 * u - pw, S4 - 9 * u - pw);
            using (var track = new Pen(Color.FromArgb(255, 70, 75, 85), pw)) g.DrawEllipse(track, rc);
            using (var pen = new Pen(col, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (n > 0 && s.Progress is double p) { if (p > 0.005) g.DrawArc(pen, rc, -90, (float)(360 * p)); }
                else g.DrawEllipse(pen, rc);
            }
            string txt = n > 0 ? (n > 99 ? "99" : n.ToString()) : s != null && s.Failed.Count > 0 ? "!" : s != null && s.RecentDone.Count > 0 ? "✓" : "";
            if (s != null && s.ActiveT.Count > 0 && s.ActiveT[0].Count == 0) txt = "";   // brand icon (icon.png)
            if (txt.Length > 0)
            {
                float fs = (txt.Length == 1 ? 34 : 27) * u;
                using var fam = new FontFamily(txt == "✓" ? "Segoe UI Symbol" : "Segoe UI");
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var path = new GraphicsPath();
                path.AddString(txt, fam, (int)FontStyle.Bold, fs, new RectangleF(0, 0, S4, S4 + 2 * u), fmt);
                g.FillPath(Brushes.White, path);
            }
            else
            {
                using var b = new SolidBrush(n > 0 ? Color.White : col);
                g.FillEllipse(b, S4 / 2f - 7 * u, S4 / 2f - 7 * u, 14 * u, 14 * u);
            }
        }
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(big, new Rectangle(0, 0, size, size));
        }
        return bmp;
    }

    /// <summary>Quit from another thread (the installer's quit event, Install.StopHubs).</summary>
    public void QuitSoon() => Ui(Quit);

    public void Quit()
    {
        try
        {
            refresh.Stop();
            cts.Cancel();
            hub.MarkDirty();
            hub.Save();
            tray.Visible = false;
            tray.Dispose();
            _ = api.Stop();
            Panel.ReallyClose();
        }
        catch { }
        ExitThread();
    }
}
