using System.Runtime.InteropServices;

namespace TaskHub;

/// <summary>
/// The pinned panel as an application desktop toolbar (SHAppBarMessage): it reserves its width at the right edge of its
/// monitor, so maximized windows re-fit beside it (the shell does that) and return when the reservation goes.
/// https://learn.microsoft.com/windows/win32/shell/application-desktop-toolbars
/// Coordinates are physical pixels (the hub is per-monitor DPI aware v2; a DPI-unaware caller gets a mis-scaled
/// reservation - .ai/gotchas.md). The shell drops the reservation by itself if the process dies (measured), so no
/// cleanup on the next start; Remove() runs on unpin, hide and exit.
/// Optional (config panel_push_windows, off by default): normal windows on the same monitor that reach under the panel
/// are moved left or narrowed when the strip is first taken (or moves), and put back on unpin / exit if still where
/// they ended up. Best effort: windows of elevated programs can't be moved by us (UIPI) and are left alone.
/// </summary>
sealed class AppBar
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Point min, max; public RECT normal; }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    delegate bool EnumProc(IntPtr h, IntPtr l);

    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    const uint ABE_RIGHT = 2;
    const uint SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const int CallbackMessage = 0x0400 + 0x3A1;   // WM_USER + n: the shell's notifications to us
    public static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");   // Explorer restarted

    readonly IntPtr hwnd;
    // windows we moved: where they were, and where they ended up (read back after the move: an app may refuse a size)
    readonly Dictionary<IntPtr, (RECT before, RECT after)> pushed = new();
    readonly List<IntPtr> justPushed = new();
    public bool Registered { get; private set; }
    public Rectangle Bounds { get; private set; }
    RECT lastRc;
    bool set;

    public AppBar(IntPtr h) { hwnd = h; }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    /// <summary>Registers (if needed) and reserves `width` px at the right edge of the window's monitor. Returns the
    /// rectangle the panel should take: the reserved strip, as tall as the monitor's work area (the taskbar stays).
    /// Normal windows are pushed only when the strip is new or moved, or when `pushNow` (the switch was just turned on):
    /// a window the user put back under the panel afterwards is left there.</summary>
    public Rectangle Reserve(int width, bool push, bool pushNow = false)
    {
        if (!Registered)
        {
            set = false;
            var nd = Data();
            nd.uCallbackMessage = CallbackMessage;
            SHAppBarMessage(ABM_NEW, ref nd);   // FALSE = already registered: fine
            Registered = true;
        }
        var mon = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        var m = mi.rcMonitor;
        width = Math.Clamp(width, 1, Math.Max(1, (m.R - m.L) * 7 / 10));
        var d = Data();
        d.uEdge = ABE_RIGHT;
        d.rc = new RECT { L = m.R - width, T = m.T, R = m.R, B = m.B };
        SHAppBarMessage(ABM_QUERYPOS, ref d);   // the shell moves it off the taskbar / other bars on this edge
        d.rc.L = d.rc.R - width;                // keep our width from the edge it gave us
        // SETPOS notifies every bar (us too) with ABN_POSCHANGED, which brings us back here: the same place -> no SETPOS,
        // so that loop ends
        bool moved = !(set && d.rc.L == lastRc.L && d.rc.T == lastRc.T && d.rc.R == lastRc.R && d.rc.B == lastRc.B);
        if (moved)
        {
            SHAppBarMessage(ABM_SETPOS, ref d);
            lastRc = d.rc;
            set = true;
        }
        // the height: the monitor's work area now (the taskbar at the top / bottom stays visible)
        mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        int top = Math.Max(d.rc.T, mi.rcWork.T), bottom = Math.Min(d.rc.B, mi.rcWork.B);
        if (bottom - top < 100) { top = d.rc.T; bottom = d.rc.B; }
        Bounds = new Rectangle(d.rc.L, top, d.rc.R - d.rc.L, bottom - top);
        if (push && (moved || pushNow)) PushWindows(mon, mi.rcWork);
        return Bounds;
    }

    /// <summary>Gives the edge back. `restoreWindows`: on unpin / exit the windows we moved go back (if still where they
    /// ended up); on a mere hide they stay, so a tray toggle doesn't make them jump.</summary>
    public void Remove(bool restoreWindows)
    {
        if (Registered)
        {
            Registered = false;
            set = false;
            var d = Data();
            SHAppBarMessage(ABM_REMOVE, ref d);
        }
        if (restoreWindows) RestoreWindows();
    }

    /// <summary>Explorer restarted: our registration is gone with it; register again.</summary>
    public void Lost() { Registered = false; set = false; }

    public void Activated() { if (Registered) { var d = Data(); SHAppBarMessage(ABM_ACTIVATE, ref d); } }
    public void PosChanged() { if (Registered) { var d = Data(); SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref d); } }

    /// <summary>A full-screen app reported by the shell: is it on our monitor (the notice isn't per monitor)?</summary>
    public bool ForegroundOnOurMonitor()
    {
        var fg = GetForegroundWindow();
        return fg == IntPtr.Zero || MonitorFromWindow(fg, 2) == MonitorFromWindow(hwnd, 2);
    }

    // ------------------------------------------------------------ optional: move normal windows out of the way
    void PushWindows(IntPtr mon, RECT work)
    {
        int edge = Bounds.Left;
        justPushed.Clear();
        EnumWindows((h, _) =>
        {
            try { Push(h, mon, work, edge); } catch { }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>The visible frame (without the invisible resize border GetWindowRect includes on Windows 10 / 11).</summary>
    static RECT Visible(IntPtr h, RECT r) =>
        DwmGetWindowAttribute(h, 9, out RECT v, Marshal.SizeOf<RECT>()) == 0 && v.R > v.L ? v : r;   // DWMWA_EXTENDED_FRAME_BOUNDS

    void Push(IntPtr h, IntPtr mon, RECT work, int edge)
    {
        if (h == hwnd || !IsWindowVisible(h) || Native.IsOurWindow(h)) return;
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_CHILD = 0x40000000;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        int style = GetWindowLong(h, GWL_STYLE), ex = GetWindowLong(h, GWL_EXSTYLE);
        // an app window: resizable or with a caption (custom title bars - VS Code, Terminal - keep the resize frame)
        if ((style & WS_CHILD) != 0 || ((style & WS_THICKFRAME) == 0 && (style & WS_CAPTION) != WS_CAPTION)) return;
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE)) != 0) return;   // tool / top-most / overlay windows, other bars
        if (GetWindow(h, 4) != IntPtr.Zero) return;   // GW_OWNER: dialogs follow their owner
        if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return;   // DWMWA_CLOAKED: other desktop / hidden app
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(h, ref wp) || wp.showCmd is 2 or 3 or 6 or 7) return;   // minimized / maximized: the shell handles those
        if (MonitorFromWindow(h, 0) != mon) return;
        if (!GetWindowRect(h, out var r)) return;
        var v = Visible(h, r);
        if (v.R <= edge) return;
        int bl = v.L - r.L, br = r.R - v.R;   // invisible borders
        int overlap = v.R - edge;
        RECT to = v;
        if (v.L - overlap >= work.L) { to.L = v.L - overlap; to.R = edge; }                 // room on the left: move
        else { to.L = Math.Max(v.L, work.L); to.R = edge; if (to.R - to.L < 300) return; }   // else narrow it (not below 300 px)
        to.L -= bl; to.R += br; to.T = r.T; to.B = r.B;
        if (!Native.SetWindowPos(h, IntPtr.Zero, to.L, to.T, to.R - to.L, to.B - to.T, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS)) return;
        pushed[h] = (pushed.TryGetValue(h, out var old) ? old.before : r, to);
        justPushed.Add(h);
    }

    /// <summary>A moment after a push: where the windows really ended up (an app may keep a minimum width).</summary>
    public void SettlePushed()
    {
        foreach (var h in justPushed)
            if (pushed.TryGetValue(h, out var p) && GetWindowRect(h, out var now)) pushed[h] = (p.before, now);
        justPushed.Clear();
    }

    public bool HasPushed => pushed.Count > 0 || justPushed.Count > 0;

    /// <summary>Puts the windows we moved back, if they are still where they ended up (the user didn't move them since).</summary>
    public void RestoreWindows()
    {
        SettlePushed();
        foreach (var (h, (before, after)) in pushed)
            try
            {
                if (!GetWindowRect(h, out var now) || Math.Abs(now.L - after.L) > 2 || Math.Abs(now.R - after.R) > 2
                    || Math.Abs(now.T - after.T) > 2 || Math.Abs(now.B - after.B) > 2) continue;
                Native.SetWindowPos(h, IntPtr.Zero, before.L, before.T, before.R - before.L, before.B - before.T,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            }
            catch { }
        pushed.Clear();
    }
}
