using System.Runtime.InteropServices;

namespace TaskHub;

static class Native
{
    public const int WM_NCLBUTTONDOWN = 0xA1, WM_NCHITTEST = 0x84, WM_NCCALCSIZE = 0x83;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const int WS_THICKFRAME = 0x00040000;
    public const uint WS_POPUP = 0x80000000;
    public const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("dwmapi.dll")] public static extern int DwmGetColorizationColor(out uint argb, out bool opaque);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34,
        DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // panel geometry / animation
    public const int WM_NCPAINT = 0x85, WM_NCACTIVATE = 0x86, WM_NCUAHDRAWCAPTION = 0xAE, WM_NCUAHDRAWFRAME = 0xAF;   // last two: uxtheme, undocumented
    public const int WM_GETMINMAXINFO = 0x24, WM_SETTINGCHANGE = 0x1A, WM_DPICHANGED = 0x2E0, WM_DWMCOLORIZATIONCOLORCHANGED = 0x320;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOREDRAW = 8, SWP_NOACTIVATE = 0x10;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public Point ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    public const int WM_WINDOWPOSCHANGING = 0x46;
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint ini);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    // blur behind a window with a tint we choose (the page supplies the tint, so the accent's own stays almost clear)
    [StructLayout(LayoutKind.Sequential)] struct ACCENT_POLICY { public int State, Flags; public uint Gradient; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    /// <summary>WCA_ACCENT_POLICY (undocumented, Windows 10+); gradient = tint as 0xAABBGGRR.</summary>
    public static bool SetAccent(IntPtr h, int state, uint gradient)
    {
        var a = new ACCENT_POLICY { State = state, Flags = 2, Gradient = gradient };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(a, p, false);
            var d = new WINCOMPATTRDATA { Attribute = 19, Data = p, Size = Marshal.SizeOf<ACCENT_POLICY>() };
            return SetWindowCompositionAttribute(h, ref d) != 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(p); }
    }
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Process creation FILETIME, or null if not accessible / not running.</summary>
    public static long? CreateTime(int pid)
    {
        if (pid <= 0) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out var c, out _, out _, out _) ? c : null; }
        finally { CloseHandle(h); }
    }

    /// <summary>true if alive (or unknown); false if exited or pid reused (create time mismatch).</summary>
    public static bool PidAlive(int pid, long? createTime)
    {
        if (pid <= 0) return true;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == 5; // access denied -> exists
        }
        try
        {
            if (GetExitCodeProcess(h, out var code) && code != 259) return false;
            if (createTime is long ct && GetProcessTimes(h, out var c, out _, out _, out _) && Math.Abs(c - ct) > 10_000_000)
                return false; // pid reused
            return true;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsOurWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Workaround for the foreground lock: share the foreground thread's input state for a moment;
    /// only if that fails too, a synthetic ALT tap lets SetForegroundWindow succeed.</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (GetForegroundWindow() == h) return;
        if (SetForegroundWindow(h)) { BringWindowToTop(h); return; }
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
        {
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { AttachThreadInput(me, fgThread, false); }
        }
        if (GetForegroundWindow() != h)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);       // ALT down
            keybd_event(0x12, 0, 2, UIntPtr.Zero);       // ALT up
            SetForegroundWindow(h);
        }
        BringWindowToTop(h);
    }
}
