using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TaskHub;

/// <summary>
/// Per-user installation (no admin, HKCU only):
///   files -> chosen folder (default %LOCALAPPDATA%\Programs\TaskHub), autostart (Run key, kept enabled),
///   taskhub:// protocol, folder on the user PATH (taskhub CLI), entry in "Apps &amp; features" for uninstall.
/// The install folder is remembered in the uninstall entry (InstallLocation).
/// Data (%APPDATA%\TaskHub) is kept on uninstall unless --purge.
/// </summary>
static class Install
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ProtoKey = @"Software\Classes\taskhub";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskHub";
    const string HostExe = "TaskHubHost.exe";
    public static readonly string[] Payload = { HostExe, "taskhub.exe", "taskhub_client.py", "API.md" };

    static bool IsOldBuildFile(string name) =>
        !Payload.Contains(name, StringComparer.OrdinalIgnoreCase) &&
        (new[] { ".dll", ".pdb", ".xml", ".deps.json", ".runtimeconfig.json" }.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
         || name is "createdump.exe" or "taskhub.ico" or "ui.html");

    static string Exe => Environment.ProcessPath;
    static string ExeDir => Path.GetDirectoryName(Exe);
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskHub");

    /// <summary>Folder of the current installation (from the uninstall entry), else the default.</summary>
    public static string InstallDir
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstKey);
            var v = k?.GetValue("InstallLocation") as string;
            return string.IsNullOrWhiteSpace(v) ? DefaultDir : v;
        }
    }
    static string InstalledExe => Path.Combine(InstallDir, HostExe);

    static bool SameDir(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool IsInstalled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(UninstKey); return k != null && File.Exists(InstalledExe); }
    }
    public static bool IsInstalledCopy => IsInstalled && SameDir(ExeDir, InstallDir);

    // ------------------------------------------------------------ Microsoft Store edition (MSIX package)
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

    /// <summary>Running from an MSIX package (Microsoft Store edition): Windows installs, updates and removes it, and
    /// autostart, taskhub:// and the `taskhub` command come from the package manifest instead of the registry / PATH.</summary>
    public static readonly bool IsPackaged = DetectPackage();
    public static string Edition => IsPackaged ? "store" : "exe";
    const string StartupTaskId = "TaskHubStartup";   // packaging\AppxManifest.xml

    static bool DetectPackage()
    {
        try { int len = 0; return GetCurrentPackageFullName(ref len, null) != 15700; }   // APPMODEL_ERROR_NO_PACKAGE
        catch { return false; }
    }

    static StartupTask PackageStartup() => StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();

    /// <summary>The `taskhub` command of the package (an app execution alias): stable across updates, unlike the
    /// package folder, and starts the CLI inside the package.</summary>
    public static string PackagedCli => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "taskhub.exe");

    /// <summary>Version of the installed copy: the installed exe's file version (the uninstall entry's DisplayVersion can be
    /// stale if the files were replaced by hand), else DisplayVersion, else null.</summary>
    public static Version InstalledVersion
    {
        get
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(InstalledExe);
                if (fv.FileMajorPart > 0 || fv.FileMinorPart > 0) return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { }
            using var k = Registry.CurrentUser.OpenSubKey(UninstKey);
            return Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v) ? v : null;
        }
    }

    /// <summary>The folder is on the user PATH.</summary>
    public static bool OnPath(string dir)
    {
        using var k = Registry.CurrentUser.OpenSubKey("Environment");
        var cur = k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        return cur.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => SameDir(Environment.ExpandEnvironmentVariables(p), dir));
    }

    // ------------------------------------------------------------ autostart
    /// <summary>"TaskHub" for the default port, "TaskHub-PORT" otherwise.</summary>
    public static string RunValueName(Settings s) => s.Port == Settings.DefaultPort ? Settings.AppId : $"{Settings.AppId}-{s.Port}";
    static string RunCommand => $"\"{Exe}\"";

    public static bool AutostartEnabled(Settings s)
    {
        if (IsPackaged)
        {
            try { return PackageStartup().State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy; }
            catch (Exception e) { Log.W("startup task: " + e.Message); return false; }
        }
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        var v = k?.GetValue(RunValueName(s)) as string;
        if (v == null || !v.Contains(HostExe, StringComparison.OrdinalIgnoreCase)) return false;
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
        if (IsPackaged)
        {
            var t = PackageStartup();
            if (!on) { t.Disable(); return; }
            // turned off by the user in Settings / Task Manager: only they can turn it on again there
            if (t.RequestEnableAsync().AsTask().GetAwaiter().GetResult() == StartupTaskState.DisabledByUser)
                throw new InvalidOperationException(L.T("startup_by_user"));
            return;
        }
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
            if (v != null && v.Contains(HostExe, StringComparison.OrdinalIgnoreCase)) k.DeleteValue(RunValueName(s), false);
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
        bool has = parts.Any(p => SameDir(Environment.ExpandEnvironmentVariables(p), dir));
        if (add == has) return;
        if (add) parts.Add(dir); else parts.RemoveAll(p => SameDir(Environment.ExpandEnvironmentVariables(p), dir));
        k.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
        SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "Environment", 2, 3000, out _);   // WM_SETTINGCHANGE
    }

    // ------------------------------------------------------------ apps & features entry
    static void SetUninstallEntry(bool on, string dir)
    {
        if (!on) { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); return; }
        using var k = Registry.CurrentUser.CreateSubKey(UninstKey);
        k.SetValue("DisplayName", "TaskHub");
        k.SetValue("DisplayVersion", Settings.Version);
        k.SetValue("Publisher", "TaskHub");
        k.SetValue("DisplayIcon", Exe);
        k.SetValue("InstallLocation", dir);
        k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            long bytes = Payload.Select(f => new FileInfo(Path.Combine(dir, f))).Where(f => f.Exists).Sum(f => f.Length);
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

    /// <summary>Null if the folder can be created and written by the current user, else the error text.</summary>
    public static string CheckWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var t = Path.Combine(dir, ".taskhub-write-test");
            File.WriteAllText(t, "x");
            File.Delete(t);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Copy this single-file exe into dir and unpack the CLI / client / docs next to it. Returns the installed exe.</summary>
    public static string InstallFiles(string dir)
    {
        StopOtherHubs();
        Directory.CreateDirectory(dir);
        // a previous installation in another folder: remove its files
        if (IsInstalled && !SameDir(InstallDir, dir)) { RemoveFiles(InstallDir); SetPath(InstallDir, false); }
        // leftovers of the old multi-file build - only in our own default folder, never in a user-chosen one, and only the
        // file kinds that build produced (TaskHubHost.settings.json and anything else the user put there stay)
        if (SameDir(dir, DefaultDir))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (IsOldBuildFile(Path.GetFileName(f))) try { File.Delete(f); } catch { }
            try { Directory.Delete(Path.Combine(dir, "runtimes"), true); } catch { }
        }
        var exe = Path.Combine(dir, HostExe);
        if (!SameDir(ExeDir, dir))
            for (int i = 0; ; i++)
            {
                try { File.Copy(Exe, exe, true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
            }
        Res.Extract(dir);
        return exe;
    }

    /// <summary>Remove only our files from a folder (never anything else the user keeps there), then the folder if empty.</summary>
    static void RemoveFiles(string dir)
    {
        foreach (var f in Payload)
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        try { foreach (var f in Directory.GetFiles(dir, "taskhub.exe.old-*")) try { File.Delete(f); } catch { } } catch { }
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    /// <summary>Installed copy: restore the CLI / client / docs if something deleted them.</summary>
    public static void EnsurePayload()
    {
        try
        {
            if (IsInstalledCopy && !File.Exists(Path.Combine(ExeDir, "taskhub.exe"))) Res.Extract(ExeDir);
        }
        catch (Exception e) { Log.W("payload check failed: " + e.Message); }
    }

    /// <summary>Registration done by the installed copy itself (from its own folder).</summary>
    public static void Register(Settings s, bool autostart, bool addPath)
    {
        var dir = ExeDir;
        SetUninstallEntry(true, dir);
        SetAutostart(autostart, s);
        SetProtocol(true);
        SetPath(dir, addPath);
    }

    public static void Unregister(Settings s)
    {
        var dir = InstallDir;
        SetAutostart(false, s);
        SetProtocol(false);
        SetPath(dir, false);
        SetUninstallEntry(false, dir);
    }

    // ------------------------------------------------------------ the other edition (setup exe <-> Microsoft Store)
    /// <summary>Kill hub processes started from this folder (not this process).</summary>
    static void StopHubsIn(string dir)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HostExe)))
        {
            if (p.Id == Environment.ProcessId) continue;
            try
            {
                if (SameDir(Path.GetDirectoryName(p.MainModule!.FileName), dir)) { p.Kill(true); p.WaitForExit(5000); }
            }
            catch { }
        }
    }

    /// <summary>Store edition switching over: remove the setup-exe installation (its hub, Run entry, taskhub://, PATH
    /// entry, Apps &amp; features entry and files). Its data folder is the one this edition reads, so nothing is lost.
    /// Works from inside the package: HKCU and these files are not virtualized for full-trust packages.</summary>
    public static void RemoveExeEdition(Settings s)
    {
        var dir = InstallDir;
        StopHubsIn(dir);
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            var v = k.GetValue(RunValueName(s)) as string;
            if (v != null && v.Contains(HostExe, StringComparison.OrdinalIgnoreCase)) k.DeleteValue(RunValueName(s), false);
        }
        SetProtocol(false);
        SetPath(dir, false);
        SetUninstallEntry(false, dir);
        RemoveFiles(dir);
        // a taskhub.exe still running (an agent's MCP server) cannot be deleted yet: retried on later starts
        if (Directory.Exists(dir)) s.Set(("exe_leftover", dir));
        Log.W("setup-exe edition removed from " + dir);
    }

    /// <summary>Store edition: delete what RemoveExeEdition could not (files that were in use then).</summary>
    public static void CleanExeLeftover(Settings s)
    {
        var dir = J.Str(s.Get("exe_leftover"));
        if (string.IsNullOrEmpty(dir) || IsInstalled) return;
        RemoveFiles(dir);
        if (!Directory.Exists(dir)) s.Set(("exe_leftover", null));
    }

    public sealed record StorePackage(string FullName, string FamilyName, Version Version, string Dir);

    /// <summary>The Store edition installed for this user, or null. Found by its files rather than its name, which
    /// Partner Center assigns.</summary>
    public static StorePackage FindStoreEdition()
    {
        try
        {
            foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage) continue;
                    var dir = p.InstalledLocation.Path;
                    if (!File.Exists(Path.Combine(dir, HostExe)) || !File.Exists(Path.Combine(dir, "taskhub.exe"))) continue;
                    var v = p.Id.Version;
                    return new StorePackage(p.Id.FullName, p.Id.FamilyName, new Version(v.Major, v.Minor, v.Build), dir);
                }
                catch { }
            }
        }
        catch (Exception e) { Log.W("package lookup failed: " + e.Message); }
        return null;
    }

    /// <summary>Setup exe replacing the Store edition: take over the data the package wrote (it lives in the package's
    /// private copy of %APPDATA%), then remove the package.</summary>
    public static void ReplaceStoreEdition(StorePackage pkg, Settings s)
    {
        StopHubsIn(pkg.Dir);
        var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", pkg.FamilyName, "LocalCache", "Roaming", Settings.AppId);
        if (Directory.Exists(src) && SameDir(s.DataDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Settings.AppId)))
        {
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                if (rel.StartsWith("webview", StringComparison.OrdinalIgnoreCase)) continue;   // browser cache, rebuilt
                var dst = Path.Combine(s.DataDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
            }
        }
        var r = new Windows.Management.Deployment.PackageManager().RemovePackageAsync(pkg.FullName).AsTask().GetAwaiter().GetResult();
        if (r.ExtendedErrorCode != null) throw new IOException(r.ErrorText);
        Log.W($"Store edition {pkg.Version} removed");
    }

    /// <summary>Remove our files from the install folder after this process exits.</summary>
    public static void ScheduleRemoveFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var dels = string.Join(" & ", Payload.Append("taskhub.exe.old-*").Select(f => $"del /f /q \"{Path.Combine(dir, f)}\""));
        var cmd = $"/d /c timeout /t 3 /nobreak >nul & {dels} & rmdir \"{dir}\"";
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
