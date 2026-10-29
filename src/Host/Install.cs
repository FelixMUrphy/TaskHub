using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskHub;

/// <summary>
/// Per-user installation (no admin, HKCU only):
///   files -> %LOCALAPPDATA%\Programs\TaskHub, autostart (Run key, forced enabled), taskhub:// protocol,
///   install dir on the user PATH (taskhub.exe CLI), entry in "Apps &amp; features" for uninstall.
/// Data (%APPDATA%\TaskHub) is kept on uninstall unless --purge.
/// </summary>
static class Install
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ProtoKey = @"Software\Classes\taskhub";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskHub";
    const string HostExe = "TaskHubHost.exe";

    static string Exe => Environment.ProcessPath;
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskHub");
    static string InstalledExe => Path.Combine(InstallDir, HostExe);

    static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalledCopy => SameDir(Path.GetDirectoryName(Exe), InstallDir);
    public static bool IsInstalled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(UninstKey); return k != null && File.Exists(InstalledExe); }
    }

    // ------------------------------------------------------------ autostart
    /// <summary>"TaskHub" for the default port, "TaskHub-PORT" otherwise.</summary>
    public static string RunValueName(Settings s) => s.Port == Settings.DefaultPort ? Settings.AppId : $"{Settings.AppId}-{s.Port}";
    static string RunCommand => $"\"{Exe}\"";

    public static bool AutostartEnabled(Settings s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        var v = k?.GetValue(RunValueName(s)) as string;
        if (v == null || !v.Contains(Path.GetFileName(Exe), StringComparison.OrdinalIgnoreCase)) return false;
        return !DisabledInTaskManager(s);
    }

    static bool DisabledInTaskManager(Settings s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // StartupApproved: first byte 02/06 = enabled, 03/07 = disabled (Task Manager "Startup apps")
        return k?.GetValue(RunValueName(s)) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    public static void SetAutostart(bool on, Settings s)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        if (on)
        {
            k.SetValue(RunValueName(s), RunCommand);
            a.DeleteValue(RunValueName(s), false);          // no entry = enabled
        }
        else
        {
            var v = k.GetValue(RunValueName(s)) as string;
            if (v != null && v.Contains(Path.GetFileName(Exe), StringComparison.OrdinalIgnoreCase)) k.DeleteValue(RunValueName(s), false);
            a.DeleteValue(RunValueName(s), false);
        }
    }

    /// <summary>Self-healing autostart for the installed copy: restore the Run entry / re-enable it if it was removed, changed or disabled.</summary>
    public static void EnsureAutostart(Settings s)
    {
        try
        {
            if (!IsInstalledCopy || s.Port != Settings.DefaultPort) return;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            var v = k?.GetValue(RunValueName(s)) as string;
            if (v == RunCommand && !DisabledInTaskManager(s)) return;
            SetAutostart(true, s);
            Log.W("autostart entry restored");
        }
        catch (Exception e) { Log.W("autostart check failed: " + e.Message); }
    }

    // ------------------------------------------------------------ protocol
    public static void SetProtocol(bool on)
    {
        if (on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(ProtoKey);
            k.SetValue("", "URL:TaskHub");
            k.SetValue("URL Protocol", "");
            using var c = Registry.CurrentUser.CreateSubKey(ProtoKey + @"\shell\open\command");
            c.SetValue("", $"\"{Exe}\" --open-url \"%1\"");
        }
        else
        {
            string v;
            using (var c = Registry.CurrentUser.OpenSubKey(ProtoKey + @"\shell\open\command")) v = c?.GetValue("") as string;
            if (v != null && v.Contains(HostExe, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(ProtoKey, false);
        }
    }

    // ------------------------------------------------------------ user PATH
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr res);

    static void SetPath(string dir, bool add)
    {
        using var k = Registry.CurrentUser.CreateSubKey("Environment");
        var cur = k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool has = parts.Any(p => SameDirSafe(p, dir));
        if (add == has) return;
        if (add) parts.Add(dir); else parts.RemoveAll(p => SameDirSafe(p, dir));
        k.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
        SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "Environment", 2, 3000, out _);   // WM_SETTINGCHANGE
    }

    static bool SameDirSafe(string a, string b)
    {
        try { return SameDir(Environment.ExpandEnvironmentVariables(a), b); } catch { return false; }
    }

    // ------------------------------------------------------------ apps & features entry
    static void SetUninstallEntry(bool on)
    {
        if (!on) { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); return; }
        using var k = Registry.CurrentUser.CreateSubKey(UninstKey);
        k.SetValue("DisplayName", "TaskHub");
        k.SetValue("DisplayVersion", Settings.Version);
        k.SetValue("Publisher", "TaskHub");
        k.SetValue("DisplayIcon", Exe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            long bytes = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }

    // ------------------------------------------------------------ install / uninstall
    /// <summary>Kill other hub processes (they would lock the files / the port).</summary>
    public static void StopOtherHubs()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HostExe)))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }
    }

    /// <summary>Copy this build into the install dir and register from there. Returns the installed exe path.</summary>
    public static string InstallFromHere()
    {
        var src = Path.GetDirectoryName(Exe);
        StopOtherHubs();
        Directory.CreateDirectory(InstallDir);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel.Equals("TaskHubHost.settings.json", StringComparison.OrdinalIgnoreCase)) continue;   // dev/test override
            var dst = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            for (int i = 0; ; i++)
            {
                try { File.Copy(f, dst, true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
            }
        }
        return InstalledExe;
    }

    /// <summary>Registration done by the installed copy itself.</summary>
    public static void Register(Settings s)
    {
        SetAutostart(true, s);
        SetProtocol(true);
        SetPath(InstallDir, true);
        SetUninstallEntry(true);
    }

    public static void Unregister(Settings s)
    {
        SetAutostart(false, s);
        SetProtocol(false);
        SetPath(InstallDir, false);
        SetUninstallEntry(false);
    }

    /// <summary>Remove the install dir after this process exits.</summary>
    public static void ScheduleDeleteInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return;
        var cmd = $"/d /c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })?.Dispose();
    }

    public static string Describe(Settings s)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        using var p = Registry.CurrentUser.OpenSubKey(ProtoKey + @"\shell\open\command");
        return $"Installed: {(IsInstalled ? InstallDir : "no")}\n" +
               $"Autostart [{RunValueName(s)}] = {k?.GetValue(RunValueName(s)) ?? "(none)"}{(DisabledInTaskManager(s) ? " (DISABLED in Task Manager)" : "")}\n" +
               $"taskhub:// = {p?.GetValue("") ?? "(none)"}";
    }
}
