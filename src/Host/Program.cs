using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Toolkit.Uwp.Notifications;

namespace TaskHub;

static class Program
{
    const string Usage = "TaskHubHost [--port N] [--data DIR] [--install [--quiet] | --uninstall [--quiet] [--purge] | --status | --panel [ID] | --open-url taskhub://open/ID | --no-tray]";

    [STAThread]
    static int Main(string[] args)
    {
        var S = Settings.Load(args);
        L.S = S;
        string Arg(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
        bool Has(string k) => args.Contains(k);
        bool quiet = Has("--quiet");

        if (Has("--help") || Has("-h") || Has("/?")) { Msg(Usage); return 0; }

        if (Has("--uninstall"))
        {
            if (!quiet && MessageBox.Show(L.T("uninstall_q"), "TaskHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            Install.StopOtherHubs();
            Install.Unregister(S);
            try { ToastNotificationManagerCompat.Uninstall(); } catch { }
            if (Has("--purge")) try { Directory.Delete(S.DataDir, true); } catch { }
            if (Install.IsInstalledCopy) Install.ScheduleDeleteInstallDir();
            if (!quiet) Msg(L.T("uninstalled", S.DataDir));
            return 0;
        }

        // first start of a downloaded copy: offer to install
        bool install = Has("--install");
        if (!install && args.Length == 0 && !Install.IsInstalledCopy && !Install.IsInstalled && Settings.ReadExeSettings() == null
            && !ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            var r = MessageBox.Show(L.T("install_q", Install.InstallDir), "TaskHub", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return 0;
            install = r == DialogResult.Yes;
        }
        if (install)
        {
            try
            {
                if (!Install.IsInstalledCopy)
                {
                    // copy to %LOCALAPPDATA%\Programs\TaskHub and let the installed copy register itself
                    var exe = Install.InstallFromHere();
                    Process.Start(new ProcessStartInfo(exe, "--install" + (quiet ? " --quiet" : "")) { UseShellExecute = true })?.Dispose();
                    return 0;
                }
                S.Cfg["autostart"] = true;
                S.SaveCfg();
                Install.Register(S);
                if (!Running(S)) Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true })?.Dispose();
                if (!quiet) Msg(L.T("installed") + "\n\n" + Install.Describe(S));
                return 0;
            }
            catch (Exception e)
            {
                Log.W("install failed: " + e);
                Msg("TaskHub: install failed\n" + e.Message);
                return 1;
            }
        }
        if (Has("--status")) { Msg((Running(S) ? "running" : "not running") + $" on {S.Url}\n" + Install.Describe(S)); return 0; }

        string openTask = null;
        if (Has("--open-url"))
        {
            var m = Regex.Match(Arg("--open-url") ?? "", @"taskhub://open/?([A-Za-z0-9_.-]*)");
            openTask = m.Success ? m.Groups[1].Value : "";
        }
        else if (Has("--panel")) openTask = Arg("--panel") ?? "";

        // second instance -> forward to the running hub
        using var mutex = new Mutex(true, $"Local\\TaskHub_{S.Port}", out bool first);
        if (!first || Running(S))
        {
            if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated()) return 0;
            try
            {
                Native.AllowSetForegroundWindow(-1);
                Post(S, "/api/ui/open", new JsonObject { ["task"] = string.IsNullOrEmpty(openTask) ? null : openTask });
            }
            catch { }
            return 0;
        }

        Log.W($"TaskHub {Settings.Version} (.NET) starting; exe={Environment.ProcessPath} data={S.DataDir} port={S.Port} args=[{string.Join(" ", args)}]");
        if (S.Bool("autostart", true)) Install.EnsureAutostart(S);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.W("UI exception: " + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.W("fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.W("task exception: " + e.Exception?.GetBaseException().Message); e.SetObserved(); };

        var iconPng = S.File_("icon.png");
        try
        {
            using var b = TrayApp.DrawIcon(new Hub.Summary { Progress = 0.75, ActiveT = { new JsonObject() } }, 128);
            b.Save(iconPng, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { Log.W("icon.png: " + e.Message); }

        var hub = new Hub(S);
        var notifier = new Notifier(S) { Hub = hub };
        hub.Notifier = notifier;
        notifier.Init(iconPng);
        var api = new Api(S, hub);
        try { api.Start().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            Log.W("cannot listen: " + e);
            Msg($"TaskHub: cannot listen on {S.Url}\n{e.GetBaseException().Message}");
            return 1;
        }

        if (Has("--no-tray"))
        {
            var cts = new CancellationTokenSource();
            hub.Watchdog(cts.Token).GetAwaiter().GetResult();
            return 0;
        }

        Application.Run(new TrayApp(S, hub, api, notifier, openTask));
        hub.MarkDirty();
        hub.Save();
        Log.W("exit");
        return 0;
    }

    static void Msg(string text)
    {
        // WinExe has no console: write to the parent console if there is one, else show a message box.
        if (AttachConsole(-1))
        {
            try
            {
                var o = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                o.WriteLine(); o.WriteLine(text);
                return;
            }
            catch { }
        }
        MessageBox.Show(text, "TaskHub");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    static bool Running(Settings s)
    {
        try
        {
            var r = http.GetStringAsync(s.Url + "/api/health").GetAwaiter().GetResult();
            return J.Str((J.Parse(r) as JsonObject)?["app"]) == Settings.AppId;
        }
        catch { return false; }
    }

    static void Post(Settings s, string path, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, s.Url + path)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-TaskHub", "1");
        if (!string.IsNullOrEmpty(s.Token)) req.Headers.Add("Authorization", "Bearer " + s.Token);
        http.Send(req).Dispose();
    }
}
