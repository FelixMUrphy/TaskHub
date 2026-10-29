using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaskHub;

/// <summary>
/// Frameless, resizable, top-most WebView2 window that looks like an interactive notification.
/// No caption / min / max / close; resize from the edges; drag by the header (JS posts "drag");
/// hides on focus loss, Esc, or tray click.
/// </summary>
sealed class PanelForm : Form
{
    readonly Settings S;
    readonly Hub hub;
    readonly WebView2 web;
    const int Grip = 6;          // resize border (px at 96 dpi)
    bool ready;
    string pendingTask;
    bool userMoved;
    int placing;
    bool allowClose;
    public DateTime LastAutoHide = DateTime.MinValue;
    DateTime lastDeactivate = DateTime.MinValue;
    Size logicalSize = new(480, 760);

    public PanelForm(Settings s, Hub h)
    {
        S = s;
        hub = h;
        Text = "TaskHub";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = s.Bool("panel_topmost", true);
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        var dark = IsDarkTheme();
        BackColor = dark ? Color.FromArgb(0x14, 0x16, 0x1b) : Color.FromArgb(0xf4, 0xf5, 0xf7);
        Padding = new Padding(4);
        try { Icon = new Icon(Path.Combine(s.ExeDir, "taskhub.ico")); } catch { }
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
        LoadSize();
        Size = logicalSize;
        Deactivate += (_, _) => OnDeactivated();
        ResizeEnd += (_, _) => SaveSize();
        Move += (_, _) => { if (Visible && placing == 0) userMoved = true; };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Native.WS_THICKFRAME;              // enables DWM shadow, rounded corners, snap, resize
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;          // no taskbar / alt-tab entry
            return cp;
        }
    }

    int Px(int dip) => (int)Math.Round(dip * DeviceDpi / 96.0);

    static bool IsDarkTheme()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;   // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        int dark = IsDarkTheme() ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);   // immersive dark mode (border colour)
        var m = new Native.MARGINS { l = 0, r = 0, t = 0, b = 1 };
        Native.DwmExtendFrameIntoClientArea(Handle, ref m);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;    // client area = whole window -> no visible frame / caption
            return;
        }
        if (m.Msg == Native.WM_NCHITTEST)
        {
            long lp = (long)m.LParam;
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int g = Px(Grip), w = ClientSize.Width, h = ClientSize.Height;
            bool l = p.X < g, r = p.X >= w - g, t = p.Y < g, b = p.Y >= h - g;
            m.Result = (IntPtr)(t && l ? Native.HTTOPLEFT : t && r ? Native.HTTOPRIGHT : b && l ? Native.HTBOTTOMLEFT
                : b && r ? Native.HTBOTTOMRIGHT : l ? Native.HTLEFT : r ? Native.HTRIGHT : t ? Native.HTTOP
                : b ? Native.HTBOTTOM : Native.HTCAPTION);
            return;
        }
        base.WndProc(ref m);
    }

    public async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, S.File_("webview2"),
            new CoreWebView2EnvironmentOptions("--disable-features=Translate"));
        await web.EnsureCoreWebView2Async(env);
        var cw = web.CoreWebView2;
        cw.Settings.AreDevToolsEnabled = true;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.IsZoomControlEnabled = true;
        cw.Settings.IsPasswordAutosaveEnabled = false;
        cw.Settings.IsGeneralAutofillEnabled = false;
        cw.WebMessageReceived += OnWebMessage;
        cw.NewWindowRequested += (_, e) => { e.Handled = true; SafeOpen(e.Uri); };
        cw.NavigationCompleted += (_, e) =>
        {
            ready = e.IsSuccess;
            if (!e.IsSuccess) { Log.W("panel navigation failed: " + e.WebErrorStatus); ReloadSoon(); return; }
            if (pendingTask != null) { hub.SetFocus(pendingTask); pendingTask = null; }
        };
        cw.ProcessFailed += (_, e) => { Log.W("webview process failed: " + e.ProcessFailedKind); ReloadSoon(); };
        web.Source = new Uri(S.Url + "/ui");
    }

    void ReloadSoon()
    {
        BeginInvoke(async () =>
        {
            await Task.Delay(1500);
            try { web.CoreWebView2?.Reload(); } catch { }
        });
    }

    static void SafeOpen(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "file" or "mailto")
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else if (!url.Contains("://") && (File.Exists(url) || Directory.Exists(url)))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else Log.W("refused to open " + url);
        }
        catch (Exception e) { Log.W("open failed: " + e.Message); }
    }

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { msg = null; }
        if (msg == null)
        {
            try
            {
                var o = J.Parse(e.WebMessageAsJson) as JsonObject;
                if (o?["open"] != null) SafeOpen(J.Str(o["open"]));
            }
            catch { }
            return;
        }
        switch (msg)
        {
            case "drag":
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                break;
            case "hide":
                HidePanel(false);
                break;
            case "devtools":
                web.CoreWebView2?.OpenDevToolsWindow();
                break;
        }
    }

    // ------------------------------------------------------------ geometry
    void LoadSize()
    {
        int w = S.Int("panel_width", 480), h = S.Int("panel_height", 760);
        try
        {
            if (File.Exists(S.File_("panel.json")) && J.Parse(File.ReadAllText(S.File_("panel.json"))) is JsonObject o)
            {
                w = (int)(J.Num(o["w"]) ?? w);
                h = (int)(J.Num(o["h"]) ?? h);
            }
        }
        catch { }
        logicalSize = new Size(Math.Max(320, w), Math.Max(240, h));
    }

    void SaveSize()
    {
        var f = 96.0 / DeviceDpi;
        logicalSize = new Size((int)(Width * f), (int)(Height * f));
        try { File.WriteAllText(S.File_("panel.json"), new JsonObject { ["w"] = logicalSize.Width, ["h"] = logicalSize.Height }.ToJsonString()); }
        catch { }
    }

    void Place()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        double scale = DeviceDpi / 96.0;
        int w = Math.Min((int)(logicalSize.Width * scale), wa.Width - 24);
        int h = Math.Min((int)(logicalSize.Height * scale), wa.Height - 24);
        int m = (int)(12 * scale);
        placing++;
        try { Bounds = new Rectangle(wa.Right - w - m, wa.Bottom - h - m, w, h); }
        finally { placing--; }
    }

    // ------------------------------------------------------------ show / hide
    public bool PanelVisible => Visible && WindowState != FormWindowState.Minimized;

    public void ShowPanel(string task)
    {
        if (!Visible && (!userMoved || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds)))) Place();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.ForceForeground(Handle);
        try { web.Focus(); } catch { }
        if (task != null)
        {
            if (ready) hub.SetFocus(task);
            else pendingTask = task;
        }
        hub.MarkSeen();
    }

    public void HidePanel(bool auto)
    {
        if (!Visible) return;
        if (auto) LastAutoHide = DateTime.UtcNow;
        Hide();
        userMoved = false;     // next open snaps back to the corner
    }

    public void Toggle()
    {
        var now = DateTime.UtcNow;
        if (PanelVisible)
        {
            // the tray click itself deactivates the panel a moment before the click event arrives
            if (Native.GetForegroundWindow() == Handle || (now - lastDeactivate).TotalMilliseconds < 600) { HidePanel(false); return; }
            ShowPanel(null);
            return;
        }
        if ((now - LastAutoHide).TotalMilliseconds < 600) return;
        ShowPanel(null);
    }

    void OnDeactivated()
    {
        lastDeactivate = DateTime.UtcNow;
        if (!S.Bool("panel_autohide", true)) return;
        BeginInvoke(async () =>
        {
            await Task.Delay(120);
            var fg = Native.GetForegroundWindow();
            if (fg == Handle || !Visible) return;
            if (fg != IntPtr.Zero && Native.IsOurWindow(fg)) return;   // our own window (devtools, dialog)
            HidePanel(true);
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HidePanel(false);
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HidePanel(false);
            return;
        }
        base.OnFormClosing(e);
    }

    public void ReallyClose()
    {
        allowClose = true;
        Close();
    }
}
