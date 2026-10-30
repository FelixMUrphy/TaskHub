using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Toolkit.Uwp.Notifications;

namespace TaskHub;

static class Program
{
    const string Usage = "TaskHubHost [--port N] [--data DIR] [--install [--dir PATH] [--no-autostart] [--no-path] [--claude|--no-claude] [--codex|--no-codex] [--claude-mcp|--no-claude-mcp] [--codex-mcp|--no-codex-mcp] [--quiet] | --uninstall [--quiet] [--purge] | --status | --panel [ID] | --open-url taskhub://open/ID | --no-tray]";

    [STAThread]
    static int Main(string[] args)
    {
        // taskhub:// activation: the URL comes from a web page or another app, so it must not smuggle in other flags
        // (--uninstall, --data ...) through quotes in the URL - only the URL itself is kept
        int ou = Array.IndexOf(args, "--open-url");
        if (ou >= 0) args = new[] { "--open-url", ou + 1 < args.Length ? args[ou + 1] : "" };
        var S = Settings.Load(args);
        L.S = S;
        string Arg(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
        bool Has(string k) => args.Contains(k);
        bool quiet = Has("--quiet");

        if (Has("--help") || Has("-h") || Has("/?")) { Msg(Usage); return 0; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (Has("--uninstall"))
        {
            if (!quiet && MessageBox.Show(L.T("uninstall_q"), "TaskHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            var dir = Install.InstallDir;
            bool wasInstalled = Install.IsInstalled;
            Install.StopOtherHubs();
            Install.Unregister(S);
            Integrations.RemoveAll(S);
            try { ToastNotificationManagerCompat.Uninstall(); } catch { }
            if (Has("--purge")) try { Directory.Delete(S.DataDir, true); } catch { }
            if (wasInstalled) Install.ScheduleRemoveFiles(dir);
            if (!quiet) Msg(L.T("uninstalled", S.DataDir));
            return 0;
        }

        // scripted: TaskHubHost.exe --integrations claude=on codex=off claude-mcp=on codex-mcp=on   (prints the state, exit 1 on error)
        if (Has("--integrations"))
        {
            int rc = 0;
            var outp = new List<string>();
            foreach (var a in Integrations.All)
                foreach (var k in Integrations.Kinds)
                {
                    var key = Integrations.ArgName(a, k);
                    var v = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
                    if (v is "on" or "off")
                    {
                        bool changed = false;
                        try { changed = Integrations.Apply(a, k, v == "on", S); S.Set((Integrations.CfgKey(a, k), v == "on")); }
                        catch (Exception e) { rc = 1; outp.Add($"{key}: error {e.Message}"); continue; }
                        outp.Add($"{key}: {v}{(changed ? " (changed)" : " (unchanged)")}");
                    }
                    else outp.Add($"{key}: {(Integrations.Enabled(a, k) ? "on" : "off")}");
                }
            Msg(string.Join("\n", outp));
            return rc;
        }

        // second stage of the setup: the installed copy registers itself from its own folder
        if (Has("--register"))
        {
            try
            {
                bool auto = !Has("--no-autostart");
                S.Set(("autostart", auto));
                Install.Register(S, auto, !Has("--no-path"));
                // AI agents: explicit --claude/--no-claude, --codex[-mcp]/--no-codex[-mcp] ...; otherwise unchanged
                var notes = new List<string>();
                foreach (var a in Integrations.All)
                    foreach (var k in Integrations.Kinds)
                    {
                        string on = "--" + Integrations.ArgName(a, k), off = "--no-" + Integrations.ArgName(a, k);
                        if (!Has(on) && !Has(off)) continue;
                        var err = Integrations.Set(a, k, Has(on), S);
                        if (err != null) notes.Add(err);
                        else if (Has(on)) notes.Add("• " + Integrations.Hint(a, k));
                    }
                if (!Running(S)) Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--run") { UseShellExecute = true })?.Dispose();
                if (!quiet) Msg(L.T("installed") + "\n\n" + Install.Describe(S) + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""));
                return 0;
            }
            catch (Exception e)
            {
                Log.W("register failed: " + e);
                Msg("TaskHub: install failed\n" + e.Message);
                return 1;
            }
        }

        // started by double-click from anywhere but the install folder (downloaded setup exe): setup dialog
        bool install = Has("--install");
        string installDir = Arg("--dir");
        bool optAuto = !Has("--no-autostart"), optPath = !Has("--no-path");
        string optAgents = string.Join("", Integrations.All.SelectMany(a => Integrations.Kinds.Select(k => Integrations.ArgName(a, k)))
            .SelectMany(n => new[] { "--" + n, "--no-" + n }).Where(Has).Select(x => " " + x));
        if (!install && args.Length == 0 && !Install.IsInstalledCopy && Settings.ReadExeSettings() == null
            && !ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            using var f = new InstallForm(Install.IsInstalled, Install.InstalledVersion, S);
            f.ShowDialog();
            if (f.Result == InstallForm.Choice.Cancel) return 0;
            if (f.Result == InstallForm.Choice.Install)
            {
                install = true;
                installDir = f.Folder;
                optAuto = f.Autostart;
                optPath = f.AddPath;
                optAgents = string.Concat(f.Agents.Select(x => (x.on ? " --" : " --no-") + Integrations.ArgName(x.agent, x.kind)));
            }
        }
        if (install)
        {
            try
            {
                installDir ??= Install.IsInstalled ? Install.InstallDir : Install.DefaultDir;
                installDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installDir));
                var err = Install.CheckWritable(installDir);
                if (err != null) { Msg(L.T("setup_no_access", installDir) + "\n\n" + err); return 1; }
                var exe = Install.InstallFiles(installDir);
                var a2 = "--register" + (quiet ? " --quiet" : "") + (optAuto ? "" : " --no-autostart") + (optPath ? "" : " --no-path") + optAgents;
                Process.Start(new ProcessStartInfo(exe, a2) { UseShellExecute = true })?.Dispose();
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
        Install.EnsurePayload();
        if (Install.IsInstalledCopy) Integrations.Ensure(S);   // never from dev/test hubs
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
        var telegram = new TelegramBridge(S, hub);
        notifier.Telegram = telegram;
        var api = new Api(S, hub) { Telegram = telegram };
        try { api.Start().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            Log.W("cannot listen: " + e);
            Msg($"TaskHub: cannot listen on {S.Url}\n{e.GetBaseException().Message}");
            return 1;
        }

        telegram.Start();
        var updater = new Updater(S, hub, notifier);
        notifier.UpdateClicked = updater.OpenPage;
        // dev / test hubs never call GitHub on their own
        if (Install.IsInstalledCopy) _ = Task.Run(() => updater.Loop(CancellationToken.None));
        if (Has("--no-tray"))
        {
            var cts = new CancellationTokenSource();
            hub.Watchdog(cts.Token).GetAwaiter().GetResult();
            return 0;
        }

        Application.Run(new TrayApp(S, hub, api, notifier, openTask) { Updater = updater });
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
