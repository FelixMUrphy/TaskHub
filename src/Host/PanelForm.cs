using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Windows.UI.ViewManagement;

namespace TaskHub;

/// <summary>
/// The panel: a frameless, top-most WebView2 window docked to the right edge of the work area of the monitor with the
/// tray / cursor, full height, like the Windows 11 notification center. Slides in from the right; width is changed by
/// dragging its left edge (config panel_width); hides on focus loss (unless pinned), Esc, or tray click.
/// Acrylic blur of the windows behind a transparent WebView2; the page sets its background alpha (panel_transparency). Light / dark theme and accent colour follow Windows unless set in the panel.
/// </summary>
sealed class PanelForm : Form
{
    readonly Settings S;
    readonly Hub hub;
    readonly WebView2 web;
    const int Grip = 6;                  // resize edge (px at 96 dpi)
    const int MinWidth = 320, MaxWidth = 960;
    const double SlideMs = 180;
    bool ready;
    string pendingTask;
    bool allowClose;
    bool animating, hiding;
    bool sliding;                        // window narrower than the WebView2 (slide in progress)
    int animGen;                         // a newer slide cancels the running one
    int shownPx;                         // visible width (target.Width when fully shown)
    int slideW = int.MaxValue;           // window width the slide asked for
    TaskCompletionSource<bool> painted;  // the page drew a frame after being shown
    Rectangle target;                    // where the panel sits when fully shown
    public DateTime LastAutoHide = DateTime.MinValue;
    public volatile bool IsShown;      // readable from any thread (toast worker)
    DateTime lastDeactivate = DateTime.MinValue;
    UISettings uiSettings;               // kept alive for ColorValuesChanged
    bool dark, backdrop;
    string lookSent;

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
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        dark = EffectiveDark();
        BackColor = SolidBack(dark);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };   // undocked (fixed size) while sliding
        Controls.Add(web);        Size = new Size(MinWidth, 400);
        Deactivate += (_, _) => OnDeactivated();
        VisibleChanged += (_, _) => IsShown = Visible;
        ResizeEnd += (_, _) => SaveWidth();
        hub.PanelSettingsChanged += () => { if (IsHandleCreated) BeginInvoke(ApplyLook); };
        try
        {
            uiSettings = new UISettings();
            uiSettings.ColorValuesChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(ApplyLook); };
        }
        catch (Exception e) { Log.W("UISettings unavailable: " + e.Message); }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Native.WS_THICKFRAME;              // DWM shadow + system resizing from the left edge
            cp.Style |= unchecked((int)Native.WS_POPUP);   // not an overlapped window: those always get a caption area
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;          // no taskbar / alt-tab entry
            return cp;
        }
    }

    int Px(int dip) => (int)Math.Round(dip * DeviceDpi / 96.0);

    // ------------------------------------------------------------ theme, accent, backdrop
    static bool SystemDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    string Theme => J.Str(S.Get("theme"), "system");
    bool EffectiveDark() => Theme == "dark" || (Theme == "system" && SystemDark());
    static Color SolidBack(bool dark) => dark ? Color.FromArgb(0x14, 0x16, 0x1b) : Color.FromArgb(0xf4, 0xf5, 0xf7);

    /// <summary>Windows accent colour, in the shade Windows itself uses on this theme (darker on light, lighter on dark).</summary>
    string Accent()
    {
        try
        {
            if (uiSettings != null)
            {
                var c = uiSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
                return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            }
        }
        catch { }
        try
        {
            if (Native.DwmGetColorizationColor(out var argb, out _) == 0)
                return $"#{(argb >> 16) & 0xff:x2}{(argb >> 8) & 0xff:x2}{argb & 0xff:x2}";
        }
        catch { }
        return null;
    }

    /// <summary>Applies theme / accent / backdrop to the window and WebView2 and tells the page (idempotent).</summary>
    void ApplyLook()
    {
        if (!IsHandleCreated) return;
        dark = EffectiveDark();
        int v = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
        v = 1;   // DWMWCP_DONOTROUND: flush with the screen edge
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
        v = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE: no coloured border, the page draws a hairline
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref v, 4);
        // Blur of the windows behind: acrylic accent policy with an almost clear tint - the page supplies the tint
        // (panel_transparency). The documented DWMWA_SYSTEMBACKDROP_TYPE acrylic tints so heavily in dark mode that
        // nothing of the windows behind shows, and it needs the frame extended over the client area, which DWM fills
        // with the accent colour when "Show accent colour on title bars" is on - so: no system backdrop, no extended frame.
        v = 1;   // DWMSBT_NONE
        if (Environment.OSVersion.Version.Build >= 22621) Native.DwmSetWindowAttribute(Handle, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, 4);
        var bg = SolidBack(dark);
        backdrop = Native.SetAccent(Handle, Native.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x01000000u | (uint)(bg.B << 16 | bg.G << 8 | bg.R));
        var m = new Native.MARGINS();
        Native.DwmExtendFrameIntoClientArea(Handle, ref m);
        // black GDI pixels (alpha 0) show the blur
        var back = backdrop ? Color.Black : SolidBack(dark);
        if (BackColor != back) BackColor = back;
        var wb = backdrop ? Color.Transparent : back;
        if (web.DefaultBackgroundColor != wb) web.DefaultBackgroundColor = wb;
        var cw = web.CoreWebView2;
        if (cw != null)
        {
            try
            {
                cw.Profile.PreferredColorScheme = Theme switch
                {
                    "light" => CoreWebView2PreferredColorScheme.Light,
                    "dark" => CoreWebView2PreferredColorScheme.Dark,
                    _ => CoreWebView2PreferredColorScheme.Auto,
                };
            }
            catch { }
            var look = new JsonObject { ["look"] = new JsonObject { ["dark"] = dark, ["accent"] = Accent(), ["backdrop"] = backdrop } }.ToJsonString();
            if (ready && look != lookSent)
            {
                try { cw.PostWebMessageAsJson(look); lookSent = look; } catch { }
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLook();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                m.Result = IntPtr.Zero;    // client area = whole window -> no visible frame / caption
                return;
            // there is no non-client area, but DefWindowProc would still paint a classic caption and border into the
            // window on (de)activation - visible through the transparent WebView2
            case Native.WM_NCPAINT:
            case Native.WM_NCUAHDRAWCAPTION:
            case Native.WM_NCUAHDRAWFRAME:
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_NCACTIVATE:
                m.LParam = new IntPtr(-1);  // update the active state without repainting the frame
                base.WndProc(ref m);
                return;
            case Native.WM_NCHITTEST:
            {
                long lp = (long)m.LParam;
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)(p.X < Px(Grip) ? Native.HTLEFT : Native.HTCLIENT);   // only the left edge resizes
                return;
            }
            case Native.WM_GETMINMAXINFO:
                base.WndProc(ref m);
                if (target.Height > 0)
                {
                    var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MINMAXINFO>(m.LParam);
                    // while sliding the window is as narrow as 1 px (the system minimum of ~20 px would reach past the edge)
                    mmi.ptMinTrackSize = new Point(sliding ? 1 : Px(MinWidth), target.Height);
                    mmi.ptMaxTrackSize = new Point(sliding ? target.Width : MaxPx(), target.Height);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, m.LParam, false);
                }
                return;
            case Native.WM_WINDOWPOSCHANGING when sliding:
            {
                // the system keeps a resizable window at least ~20 px wide, which would reach past the screen edge
                base.WndProc(ref m);
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
                if ((wp.flags & Native.SWP_NOSIZE) == 0 && wp.cx > slideW)
                {
                    wp.cx = slideW;
                    wp.x = target.Right - slideW;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wp, m.LParam, false);
                }
                return;
            }
            case Native.WM_DPICHANGED when sliding:
                m.Result = IntPtr.Zero;    // keep the slide geometry; re-placed when it ends
                return;
            case Native.WM_DPICHANGED:
                base.WndProc(ref m);
                if (Visible && target.Width > 0) BeginInvoke(() => { if (Visible && !animating) { Place(); ShowFull(); } });
                return;
            case Native.WM_SETTINGCHANGE:
                base.WndProc(ref m);
                if (m.LParam != IntPtr.Zero && System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
                    BeginInvoke(ApplyLook);
                return;
            case Native.WM_DWMCOLORIZATIONCOLORCHANGED:
                base.WndProc(ref m);
                BeginInvoke(ApplyLook);
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
        cw.NavigationStarting += (_, _) => { ready = false; lookSent = null; };
        cw.NavigationCompleted += (_, e) =>
        {
            ready = e.IsSuccess;
            if (!e.IsSuccess) { Log.W("panel navigation failed: " + e.WebErrorStatus); ReloadSoon(); return; }
            ApplyLook();
            if (PanelVisible) PostShown(true);
            if (pendingTask != null) { hub.SetFocus(pendingTask); pendingTask = null; }
        };
        cw.ProcessFailed += (_, e) => { Log.W("webview process failed: " + e.ProcessFailedKind); ReloadSoon(); };
        ApplyLook();
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
            case "resize":   // the page's left-edge grip
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTLEFT, IntPtr.Zero);
                break;
            case "look":
                lookSent = null;
                ApplyLook();
                break;
            case "painted":
                painted?.TrySetResult(true);
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
    Screen TargetScreen() => Screen.FromPoint(Cursor.Position);   // tray click / toast click: the cursor is there

    static double ScreenScale(Screen s)
    {
        try
        {
            var mon = Native.MonitorFromPoint(new Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2);
            if (Native.GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0) return dx / 96.0;
        }
        catch { }
        return 1;
    }

    int MaxPx()
    {
        var wa = Screen.FromRectangle(target.Width > 0 ? target : Bounds).WorkingArea;
        return Math.Max(Px(MinWidth), Math.Min(Px(MaxWidth), wa.Width * 7 / 10));
    }

    /// <summary>Full height of the work area (taskbar excluded, whichever side it is on), at the right edge.</summary>
    Rectangle ComputeTarget()
    {
        var sc = TargetScreen();
        var wa = sc.WorkingArea;
        double scale = ScreenScale(sc);
        int dip = Math.Clamp(S.Int("panel_width", 480), MinWidth, MaxWidth);
        int w = Math.Min((int)Math.Round(dip * scale), wa.Width * 7 / 10);
        w = Math.Max(Math.Min((int)Math.Round(MinWidth * scale), wa.Width), w);
        return new Rectangle(wa.Right - w, wa.Top, w, wa.Height);
    }

    void Place()
    {
        target = ComputeTarget();
        Native.SetWindowPos(Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void SaveWidth()
    {
        if (animating || !Visible) return;
        var wa = Screen.FromControl(this).WorkingArea;
        int dip = Math.Clamp((int)Math.Round(Width * 96.0 / DeviceDpi), MinWidth, MaxWidth);
        target = new Rectangle(wa.Right - Width, wa.Top, Width, wa.Height);
        shownPx = Width;
        if (S.Int("panel_width", 480) != dip) S.Set(("panel_width", dip));
        if (Bounds != target) Native.SetWindowPos(Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------ slide animation
    static bool AnimationsOn()
    {
        try { return Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) ? on : true; }
        catch { return true; }
    }

    /// <summary>Slides the panel in from / out to the right edge of its work area, one step per DWM frame. The window grows
    /// leftwards from the edge while the WebView2 keeps its full width anchored to the window's left edge, so the
    /// content moves with the edge without re-layout, and the blur behind (which ignores window regions) matches the
    /// visible part exactly - nothing shows on a monitor to the right. The wait for the frame runs off the UI thread,
    /// so the form and WebView2 keep painting. False if a newer slide took over.</summary>
    async Task<bool> Slide(bool show)
    {
        int gen = ++animGen;
        int w = target.Width, from = shownPx, to = show ? w : 0;
        double ms = SlideMs * Math.Abs(to - from) / Math.Max(1, w);
        animating = true;
        try
        {
            var sw = Stopwatch.StartNew();
            for (;;)
            {
                double t = ms <= 0 ? 1 : Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
                double e = show ? 1 - Math.Pow(1 - t, 3) : t * t * t;   // ease-out in, ease-in out
                SetShown((int)Math.Round(from + (to - from) * e));
                if (t >= 1) return true;
                await Task.Run(() => Native.DwmFlush());
                if (gen != animGen || !Visible) return false;
            }
        }
        finally { if (gen == animGen) animating = false; }
    }

    /// <summary>The window is the rightmost `px` of the target area (at least 1 px: a zero-size window stops rendering).</summary>
    void SetShown(int px)
    {
        shownPx = Math.Clamp(px, 0, target.Width);
        int w = slideW = Math.Max(1, shownPx);
        Native.SetWindowPos(Handle, IntPtr.Zero, target.Right - w, target.Y, w, target.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Fully shown at the target; the WebView2 follows the window size again.</summary>
    void ShowFull()
    {
        sliding = false;
        shownPx = target.Width;
        Native.SetWindowPos(Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        web.Dock = DockStyle.Fill;
    }

    /// <summary>The WebView2 keeps the full panel width while the window is narrower (slide).</summary>
    void PinWebSize()
    {
        web.Dock = DockStyle.None;
        web.SetBounds(0, 0, target.Width, target.Height);
    }

    /// <summary>Until the page has drawn a frame (or 150 ms): the slide then shows content, not an empty window.</summary>
    async Task WaitPainted()
    {
        var cw = web.CoreWebView2;
        if (!ready || cw == null) { await Task.Delay(30); return; }
        painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { cw.PostWebMessageAsJson("{\"paint\":1}"); } catch { }
        await Task.WhenAny(painted.Task, Task.Delay(150));
    }

    // ------------------------------------------------------------ show / hide
    public bool PanelVisible => Visible && WindowState != FormWindowState.Minimized;
    bool Pinned => S.Bool("panel_pinned", false);

    public void ShowPanel(string task)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible || hiding) _ = SlideIn();
        Activate();
        Native.ForceForeground(Handle);
        try { web.Focus(); } catch { }
        if (task != null)
        {
            if (ready) hub.SetFocus(task);
            else pendingTask = task;
        }
        PostShown(true);
    }

    /// <summary>Tells the page whether the panel is on screen: finished tasks count as seen only while it is.</summary>
    void PostShown(bool on)
    {
        if (!ready) return;
        try { web.CoreWebView2?.PostWebMessageAsJson(on ? "{\"shown\":1}" : "{\"hidden\":1}"); } catch { }
    }

    async Task SlideIn()
    {
        hiding = false;
        bool anim = AnimationsOn();
        if (!Visible)
        {
            ApplyLook();
            target = ComputeTarget();
            if (!anim) { Show(); animGen++; animating = false; ShowFull(); return; }
            sliding = true;
            PinWebSize();
            SetShown(0);
            Show();
            await WaitPainted();
            if (!sliding) return;    // hidden meanwhile
        }
        if (!anim) { animGen++; animating = false; ShowFull(); return; }
        sliding = true;
        if (await Slide(true)) ShowFull();
    }

    public void HidePanel(bool auto)
    {
        if (!Visible || hiding) return;
        if (auto) LastAutoHide = DateTime.UtcNow;
        if (WindowState == FormWindowState.Minimized || !AnimationsOn()) { HideNow(); return; }
        _ = SlideOut();
    }

    async Task SlideOut()
    {
        hiding = true;
        if (!sliding)
        {
            target = Bounds;
            shownPx = Width;
            sliding = true;
            PinWebSize();
        }
        if (await Slide(false) && hiding) HideNow();
    }

    void HideNow()
    {
        hiding = false;
        sliding = false;
        animGen++;
        animating = false;
        Hide();
        shownPx = 0;
        PostShown(false);   // also: nothing stays open in a hidden panel (settings popover etc.)
    }

    public void Toggle()
    {
        var now = DateTime.UtcNow;
        if (PanelVisible)
        {
            // the tray click itself deactivates the panel a moment before the click event arrives
            if (Pinned || Native.GetForegroundWindow() == Handle || (now - lastDeactivate).TotalMilliseconds < 600) { HidePanel(false); return; }
            ShowPanel(null);
            return;
        }
        if ((now - LastAutoHide).TotalMilliseconds < 600) return;
        ShowPanel(null);
    }

    void OnDeactivated()
    {
        lastDeactivate = DateTime.UtcNow;
        if (!S.Bool("panel_autohide", true) || Pinned) return;
        BeginInvoke(async () =>
        {
            await Task.Delay(120);
            var fg = Native.GetForegroundWindow();
            if (fg == Handle || !Visible || Pinned) return;
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
