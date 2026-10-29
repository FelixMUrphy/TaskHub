using System.Runtime.InteropServices;

namespace TaskHub;

static class Native
{
    public const int WM_NCLBUTTONDOWN = 0xA1, WM_NCHITTEST = 0x84, WM_NCCALCSIZE = 0x83;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const int WS_THICKFRAME = 0x00040000;
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

    /// <summary>Workaround for the foreground lock: a synthetic ALT tap lets SetForegroundWindow succeed.</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (GetForegroundWindow() == h) return;
        if (!SetForegroundWindow(h))
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);       // ALT down
            keybd_event(0x12, 0, 2, UIntPtr.Zero);       // ALT up
            SetForegroundWindow(h);
        }
        BringWindowToTop(h);
    }
}
