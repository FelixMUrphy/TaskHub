using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TaskHub;

/// <summary>
/// Per-user installation (no admin, HKCU only):
///   files -> chosen folder (default %LOCALAPPDATA%\Programs\TaskHub), autostart (Run key, restored if removed; turned
///   off in Task Manager / Settings -> Startup apps is respected), taskhub:// protocol, folder on the user PATH (taskhub
///   CLI), Start-menu shortcut, entry in "Apps &amp; features" for uninstall.
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

    // tests only (SelfTest): the registry entries go under HKCU\Software\TaskHubTest\<test home folder name> and the
    // Start-menu folder under the test home - never the real ones
    static string testHome;
    static RegistryKey testRoot;
    static RegistryKey Hkcu => testRoot ?? Registry.CurrentUser;

    static string Exe => Environment.ProcessPath;
    static string ExeDir => Path.GetDirectoryName(Exe);
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskHub");

    /// <summary>Folder of the current installation (from the uninstall entry), else the default.</summary>
    public static string InstallDir
    {
        get
        {
            using var k = Hkcu.OpenSubKey(UninstKey);
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
        get { using var k = Hkcu.OpenSubKey(UninstKey); return k != null && File.Exists(InstalledExe); }
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

    /// <summary>Store edition: the `taskhub` copy the AI agents' settings point to, outside the package. Windows runs
    /// nothing of a package when it is removed, so this copy (with store.json naming the package next to it) is what
    /// removes TaskHub's entries from the agents' settings afterwards: the next time an agent starts it and the package
    /// is gone (src\Cli\StoreCleanup.cs). Refreshed from the package on every start, so it follows Store updates.</summary>
    public static string StoreCliDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskHub", "bin");

    public static string EnsureStoreCli()
    {
        var dir = StoreCliDir;
        var src = Path.Combine(AppContext.BaseDirectory, "taskhub.exe");
        var dst = Path.Combine(dir, "taskhub.exe");
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "store.json");
        var info = new System.Text.Json.Nodes.JsonObject { ["package_family"] = Package.Current.Id.FamilyName }.ToJsonString();
        if (!File.Exists(marker) || File.ReadAllText(marker) != info) File.WriteAllText(marker, info);
        if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length
            || FileVersionInfo.GetVersionInfo(dst).FileVersion != FileVersionInfo.GetVersionInfo(src).FileVersion)
        {
            var tmp = dst + ".new";
            File.Copy(src, tmp, true);
            try { File.Move(tmp, dst, true); }
            catch (IOException)   // running (an agent's MCP server): a running exe can still be renamed
            {
                File.Move(dst, dst + ".old-" + DateTime.UtcNow.Ticks);
                File.Move(tmp, dst, true);
            }
        }
        foreach (var f in Directory.GetFiles(dir, "taskhub.exe.old-*")) try { File.Delete(f); } catch { }
        return dst;
    }

    /// <summary>Setup-exe edition: the Store edition's `taskhub` copy once its package is gone (the agents' settings
    /// point to this edition again after Integrations.Ensure).</summary>
    public static void RemoveStoreCliLeftover()
    {
        try
        {
            var marker = Path.Combine(StoreCliDir, "store.json");
            if (!File.Exists(marker)) return;
            var pfn = (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker)) as System.Text.Json.Nodes.JsonObject)?["package_family"]?.GetValue<string>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(pfn) || Directory.Exists(Path.Combine(local, "Packages", pfn))) return;
            Directory.Delete(StoreCliDir, true);
        }
        catch (Exception e) { Log.W("Store edition leftover: " + e.Message); }
    }

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
            using var k = Hkcu.OpenSubKey(UninstKey);
            return Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v) ? v : null;
        }
    }

    /// <summary>The folder is on the user PATH.</summary>
    public static bool OnPath(string dir)
    {
        using var k = Hkcu.OpenSubKey("Environment");
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
        using var k = Hkcu.OpenSubKey(RunKey);
        var v = k?.GetValue(RunValueName(s)) as string;
        if (v == null || !v.Contains(HostExe, StringComparison.OrdinalIgnoreCase)) return false;
        return !DisabledInTaskManager(s);
    }

    /// <summary>The user turned the Run entry off in Task Manager / Settings -> Startup apps.</summary>
    public static bool DisabledInTaskManager(Settings s)
    {
        using var k = Hkcu.OpenSubKey(ApprovedKey);
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
        using var k = Hkcu.CreateSubKey(RunKey);
        using var a = Hkcu.CreateSubKey(ApprovedKey);
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

    /// <summary>Self-healing autostart for the installed copy: restore the Run entry if it was removed or changed. Turned off
    /// by the user in Task Manager / Settings -> Startup apps: that choice stands - the app's own setting follows it (the
    /// settings page shows it off; turning it on there is the user's explicit choice and clears the Windows switch).</summary>
    public static void EnsureAutostart(Settings s)
    {
        try
        {
            if (!IsInstalledCopy || s.Port != Settings.DefaultPort) return;
            if (DisabledInTaskManager(s))
            {
                s.Set(("autostart", false));
                Log.W("autostart turned off in Windows: left off");
                return;
            }
            using var k = Hkcu.OpenSubKey(RunKey);
            var v = k?.GetValue(RunValueName(s)) as string;
            if (v == RunCommand) return;
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
            using var k = Hkcu.CreateSubKey(ProtoKey);
            k.SetValue("", "URL:TaskHub");
            k.SetValue("URL Protocol", "");
            using var c = Hkcu.CreateSubKey(ProtoKey + @"\shell\open\command");
            c.SetValue("", $"\"{Exe}\" --open-url \"%1\"");
        }
        else
        {
            string v;
            using (var c = Hkcu.OpenSubKey(ProtoKey + @"\shell\open\command")) v = c?.GetValue("") as string;
            if (v != null && v.Contains(HostExe, StringComparison.OrdinalIgnoreCase))
                Hkcu.DeleteSubKeyTree(ProtoKey, false);
        }
    }

    // ------------------------------------------------------------ user PATH
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr res);

    static void SetPath(string dir, bool add)
    {
        using var k = Hkcu.CreateSubKey("Environment");
        var cur = k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool has = parts.Any(p => SameDir(Environment.ExpandEnvironmentVariables(p), dir));
        if (add == has) return;
        if (add) parts.Add(dir); else parts.RemoveAll(p => SameDir(Environment.ExpandEnvironmentVariables(p), dir));
        k.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
        if (testRoot == null) SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "Environment", 2, 3000, out _);   // WM_SETTINGCHANGE
    }

    // ------------------------------------------------------------ Start-menu shortcut
    // Start / Search find desktop apps only through it. No System.AppUserModel.ID on it: the toasts use the Toolkit's own
    // registered AUMID (it never sets one on the process), so the shortcut's implicit one doesn't touch them.
    static string StartMenuDir => testHome != null ? Path.Combine(testHome, "Start Menu", "Programs")
        : Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string StartMenuLink => Path.Combine(StartMenuDir, Settings.AppId + ".lnk");

    static void SetShortcut(bool on, string exe = null)
    {
        var lnk = StartMenuLink;
        if (!on)
        {
            try { File.Delete(lnk); } catch (Exception e) { Log.W("shortcut not deleted: " + e.Message); }
            return;
        }
        try
        {
            Directory.CreateDirectory(StartMenuDir);
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetIconLocation(exe, 0);
                link.SetDescription("TaskHub");
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch (Exception e) { Log.W("Start-menu shortcut not created: " + e.Message); }   // the install itself still works
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    // ------------------------------------------------------------ apps & features entry
    static void SetUninstallEntry(bool on, string dir)
    {
        if (!on) { Hkcu.DeleteSubKeyTree(UninstKey, false); return; }
        using var k = Hkcu.CreateSubKey(UninstKey);
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
    /// <summary>Stop other hub processes (they would lock the files / the port).</summary>
    public static void StopOtherHubs() => StopHubs(_ => true);

    /// <summary>The event a running hub waits on to quit (Program.Main); Local\ = this logon session only.</summary>
    public static string QuitEventName(int pid) => $@"Local\TaskHub_Quit_{pid}";

    /// <summary>Stop the matching hub processes of this logon session (never this process): ask each one to quit (it saves
    /// its tasks and exits), wait up to 5 s, then end the ones still running (older versions have no quit event). Only the
    /// hub itself, not its process tree: what it ShellExecute'd (a browser opened from the update page) is the user's, and
    /// its WebView2 processes exit with it.</summary>
    static void StopHubs(Func<Process, bool> match)
    {
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;
        var hubs = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HostExe)))
        {
            bool take = false;
            try { take = p.Id != Environment.ProcessId && p.SessionId == session && match(p); } catch { }
            if (take) hubs.Add(p); else p.Dispose();
        }
        try
        {
            var asked = new List<Process>();
            foreach (var p in hubs)
                try
                {
                    if (!EventWaitHandle.TryOpenExisting(QuitEventName(p.Id), out var e)) continue;
                    using (e) e.Set();
                    asked.Add(p);
                }
                catch { }
            var until = DateTime.UtcNow.AddSeconds(5);
            foreach (var p in asked)
                try { p.WaitForExit((int)Math.Max(0, (until - DateTime.UtcNow).TotalMilliseconds)); } catch { }
            foreach (var p in hubs)
                try
                {
                    if (p.HasExited) continue;
                    p.Kill(false);
                    p.WaitForExit(5000);
                    Log.W($"hub {p.Id} did not quit: ended");
                }
                catch (Exception e) { Log.W($"hub {p.Id} not stopped: " + e.Message); }
        }
        finally { foreach (var p in hubs) p.Dispose(); }
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

    /// <summary>Copy this single-file exe into dir and unpack the CLI / client / docs next to it. Returns the installed exe.
    /// progress (0..1) follows the copy of the exe, which is nearly all of the work.</summary>
    public static string InstallFiles(string dir, Action<double> progress = null)
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
                try { CopyFile(Exe, exe, progress); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
            }
        Res.Extract(dir);
        return exe;
    }

    static void CopyFile(string from, string to, Action<double> progress)
    {
        if (progress == null) { File.Copy(from, to, true); return; }
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
        using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long total = Math.Max(1, src.Length), done = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                done += n;
                progress(done / (double)total);
            }
        }
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
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
        SetShortcut(true, Exe);
    }

    public static void Unregister(Settings s)
    {
        var dir = InstallDir;
        SetAutostart(false, s);
        SetProtocol(false);
        SetPath(dir, false);
        SetShortcut(false);
        SetUninstallEntry(false, dir);
    }

    /// <summary>Uninstall with "also delete my data": the data folder and the tokens TaskHub saved in the Windows Credential
    /// Manager (a plain uninstall keeps both, so a reinstall finds them).</summary>
    public static void PurgeData(Settings s)
    {
        foreach (var name in Secrets.Names)
            try { Secrets.Delete(s, name); } catch (Exception e) { Log.W($"purge: {name} token: " + e.Message); }
        try { Directory.Delete(s.DataDir, true); } catch (Exception e) { Log.W("purge: " + e.Message); }
    }

    // ------------------------------------------------------------ the other edition (setup exe <-> Microsoft Store)
    /// <summary>Stop hub processes started from this folder (not this process).</summary>
    static void StopHubsIn(string dir) => StopHubs(p => SameDir(Path.GetDirectoryName(p.MainModule!.FileName), dir));

    /// <summary>Store edition switching over: remove the setup-exe installation (its hub, Run entry, taskhub://, PATH
    /// entry, Apps &amp; features entry and files). Its data folder is the one this edition reads, so nothing is lost.
    /// Works from inside the package: HKCU and these files are not virtualized for full-trust packages.</summary>
    public static void RemoveExeEdition(Settings s)
    {
        var dir = InstallDir;
        StopHubsIn(dir);
        using (var k = Hkcu.CreateSubKey(RunKey))
        {
            var v = k.GetValue(RunValueName(s)) as string;
            if (v != null && v.Contains(HostExe, StringComparison.OrdinalIgnoreCase)) k.DeleteValue(RunValueName(s), false);
        }
        SetProtocol(false);
        SetPath(dir, false);
        SetShortcut(false);
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

    /// <summary>Setup exe replacing the Store edition: take over the data the package wrote into its private copy of
    /// %APPDATA% (only where write virtualization was still on: Windows before 1903 / package versions before 2.9),
    /// then remove the package.</summary>
    public static void ReplaceStoreEdition(StorePackage pkg, Settings s)
    {
        StopHubsIn(pkg.Dir);
        var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", pkg.FamilyName, "LocalCache", "Roaming", Settings.AppId);
        if (Directory.Exists(src) && SameDir(s.DataDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Settings.AppId)))
            CopyMissing(src, s.DataDir);
        var r = new Windows.Management.Deployment.PackageManager().RemovePackageAsync(pkg.FullName).AsTask().GetAwaiter().GetResult();
        if (r.ExtendedErrorCode != null) throw new IOException(r.ErrorText);
        Log.W($"Store edition {pkg.Version} removed");
    }

    /// <summary>Copy the files of src that dst does not have yet: the real data folder may be newer than the package's
    /// copy (used since), so nothing there is overwritten.</summary>
    static void CopyMissing(string src, string dstDir)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel.StartsWith("webview", StringComparison.OrdinalIgnoreCase)) continue;   // browser cache, rebuilt
            var dst = Path.Combine(dstDir, rel);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, false);
        }
    }

    /// <summary>Remove our files from the install folder after this process exits.</summary>
    public static void ScheduleRemoveFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var dels = string.Join(" & ", Payload.Append("taskhub.exe.old-*").Select(f => $"del /f /q \"{Path.Combine(dir, f)}\""));
        var cmd = $"/d /c timeout /t 3 /nobreak >nul & {dels} & rmdir \"{dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })?.Dispose();
    }

    /// <summary>Hidden test entry: `TaskHubHost --data DIR --selftest-install STEP ...`, honoured only with TASKHUB_TEST_HOME
    /// set (Program.Main). Runs one install step against a throw-away registry key and Start-menu folder (testRoot /
    /// testHome) - never the real entries, never the real install. Returns the exit code.</summary>
    public static int SelfTest(Settings s, string home, string[] a)
    {
        testHome = home;
        testRoot = Registry.CurrentUser.CreateSubKey(@"Software\TaskHubTest\" + Path.GetFileName(home.TrimEnd('\\')));
        try
        {
            switch (a.FirstOrDefault())
            {
                case "register": Register(s, !a.Contains("--no-autostart"), true); return 0;
                case "unregister": Unregister(s); return 0;
                case "ensure-autostart": EnsureAutostart(s); return 0;
                case "stop" when a.Length > 1 && int.TryParse(a[1], out var pid): StopHubs(p => p.Id == pid); return 0;
                case "copy-missing" when a.Length > 2: CopyMissing(a[1], a[2]); return 0;
                case "purge": PurgeData(s); return 0;
                default: return 2;
            }
        }
        catch (Exception e) { Log.W("selftest: " + e); return 1; }
        finally { testRoot.Dispose(); testRoot = null; testHome = null; }
    }

    public static string Describe(Settings s)
    {
        using var k = Hkcu.OpenSubKey(RunKey);
        using var p = Hkcu.OpenSubKey(ProtoKey + @"\shell\open\command");
        return $"Installed: {(IsInstalled ? InstallDir : "no")}\n" +
               $"Autostart [{RunValueName(s)}] = {k?.GetValue(RunValueName(s)) ?? "(none)"}{(DisabledInTaskManager(s) ? " (DISABLED in Task Manager)" : "")}\n" +
               $"taskhub:// = {p?.GetValue("") ?? "(none)"}";
    }
}
