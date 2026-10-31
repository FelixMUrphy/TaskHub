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
        // tests only: one install step against a throw-away registry key / Start-menu folder (Install.SelfTest)
        if (Has("--selftest-install") && Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } testHome)
            return Install.SelfTest(S, testHome, args[(Array.IndexOf(args, "--selftest-install") + 1)..]);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Store edition: Windows installs, updates and removes the package - no setup of our own
        if (Install.IsPackaged && (Has("--install") || Has("--register") || Has("--uninstall")))
        {
            if (!quiet) Msg(L.T("store_managed"));
            return 0;
        }

        if (Has("--uninstall") && !quiet) return UninstallWindow(S);
        if (Has("--uninstall"))
        {
            var dir = Install.InstallDir;
            bool wasInstalled = Install.IsInstalled;
            Install.StopOtherHubs();
            Install.Unregister(S);
            Integrations.RemoveAll(S);
            try { ToastNotificationManagerCompat.Uninstall(); } catch { }
            if (Has("--purge")) Install.PurgeData(S);
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
                // the setup window asked for the outcome: the notes (AI agents' hints) for its last page
                if (Arg("--result") is { } rf) try { File.WriteAllLines(rf, notes); } catch { }
                if (!quiet) Msg(L.T("installed") + "\n\n" + Install.Describe(S) + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""));
                return 0;
            }
            catch (Exception e)
            {
                Log.W("register failed: " + e);
                if (Arg("--result") is { } rf) { try { File.WriteAllText(rf, "!" + e.Message); } catch { } return 1; }
                Msg("TaskHub: install failed\n" + e.Message);
                return 1;
            }
        }

        // Store edition next to a setup-exe installation: offer to switch over (on a plain start, until "keep both")
        if (Install.IsPackaged && args.Length == 0 && Install.IsInstalled && J.Str(S.Get("other_edition")) != "keep"
            && !ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            var answer = SetupForm.Ask(S, L.T("store_found_exe", Install.InstalledVersion?.ToString(3) ?? "?", Install.InstallDir), L.T("store_switch"), L.T("store_keep"));
            if (answer == true)
            {
                try { Install.RemoveExeEdition(S); }
                catch (Exception e) { Log.W("switch to Store edition: " + e); Msg(L.T("edition_failed", e.Message)); }
            }
            else if (answer == false)   // "keep both": don't ask again (closed without choosing = null: ask again next time)
            {
                S.Set(("other_edition", "keep"));
                try { Install.SetAutostart(false, S); } catch { }   // one autostart is enough: the setup-exe copy keeps its own
            }
        }

        // started by double-click from anywhere but the install folder (downloaded setup exe): setup dialog
        bool install = Has("--install");
        bool setup = install || (args.Length == 0 && !Install.IsInstalledCopy && !Install.IsPackaged && Settings.ReadExeSettings() == null
            && !ToastNotificationManagerCompat.WasCurrentProcessToastActivated());
        // setup exe over the Store edition: only one of them at a time. Asked here (or in the setup window), removed only
        // once the install itself goes ahead (not on Cancel / Run, not if the folder is not writable)
        string installDir = Arg("--dir");
        bool optAuto = !Has("--no-autostart"), optPath = !Has("--no-path");
        // --install --quiet over an installation whose autostart the user turned off in Windows: it stays off
        // (the setup window shows the current state on its toggle instead)
        if (install && quiet && Install.IsInstalled && Install.DisabledInTaskManager(S)) optAuto = false;
        string optAgents = string.Join("", Integrations.All.SelectMany(a => Integrations.Kinds.Select(k => Integrations.ArgName(a, k)))
            .SelectMany(n => new[] { "--" + n, "--no-" + n }).Where(Has).Select(x => " " + x));
        if (setup && !quiet)
        {
            // the window first (it must appear at once), the slower look-ups behind it
            var storeTask = Task.Run(() => Install.IsPackaged ? null : Install.FindStoreEdition());
            using var f = new SetupForm(S, "TaskHub " + Settings.Version);
            var (title, _, _, cmp) = SetupForm.Describe(Install.IsInstalled, Install.InstalledVersion);
            int rc = 0;
            void Go(string dir, bool auto, bool path, string agents) =>
                _ = RunInstall(f, S, dir, auto, path, agents, storeTask.Result, cmp < 0 && Install.IsInstalled, r => rc = r);
            if (install)   // --install without --quiet: straight to the progress
                f.Load += (_, _) =>
                {
                    if (storeTask.Result is { } sp
                        && SetupForm.Ask(S, L.T("setup_store_found", sp.Version.ToString(3), Settings.Version), L.T("setup_store_replace"), L.T("setup_cancel")) != true)
                    { f.Close(); return; }
                    Go(installDir, optAuto, optPath, optAgents);
                };
            else
                f.ShowOptions(Install.IsInstalled, Install.InstalledVersion,
                    () => storeTask.Result is { } sp ? L.T("setup_store_found", sp.Version.ToString(3), Settings.Version) : null,
                    () => Go(f.Folder, f.Autostart, f.AddPath, string.Concat(f.Agents.Select(x => (x.on ? " --" : " --no-") + Integrations.ArgName(x.agent, x.kind)))));
            Application.Run(f);
            if (f.Result != SetupForm.Choice.Run) return rc;
        }
        else if (install)   // --install --quiet: scripted, no window (as before)
        {
            var storePkg = !Install.IsPackaged ? Install.FindStoreEdition() : null;
            if (storePkg != null) { Log.W("setup: " + L.T("setup_store_quiet")); return 1; }
            try
            {
                installDir ??= Install.IsInstalled ? Install.InstallDir : Install.DefaultDir;
                installDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installDir));
                var err = Install.CheckWritable(installDir);
                if (err != null) { Msg(L.T("setup_no_access", installDir) + "\n\n" + err); return 1; }
                var exe = Install.InstallFiles(installDir);
                var a2 = "--register --quiet" + (optAuto ? "" : " --no-autostart") + (optPath ? "" : " --no-path") + optAgents;
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
        if (Install.IsPackaged) Install.CleanExeLeftover(S);
        if (Install.IsInstalledCopy || Install.IsPackaged) Integrations.Ensure(S);   // never from dev/test hubs
        if (Install.IsInstalledCopy) Install.RemoveStoreCliLeftover();
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
        var connectors = new Connectors(S, hub);
        var github = connectors.Add(new GitHubConnector(S, hub));
        var runpod = connectors.Add(new RunpodConnector(S, hub));
        var vastai = connectors.Add(new VastAiConnector(S, hub));
        var api = new Api(S, hub) { Telegram = telegram, Conns = connectors };
        try { api.Start().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            Log.W("cannot listen: " + e);
            Msg($"TaskHub: cannot listen on {S.Url}\n{e.GetBaseException().Message}");
            return 1;
        }

        // the installer / uninstaller asks a running hub to quit through this event (Install.StopHubs)
        using var quit = new EventWaitHandle(false, EventResetMode.ManualReset, Install.QuitEventName(Environment.ProcessId));
        telegram.Start();
        connectors.StartAll();
        var updater = new Updater(S, hub, notifier);
        notifier.UpdateClicked = updater.OpenPage;
        // dev / test hubs never call GitHub on their own
        if (Install.IsInstalledCopy) _ = Task.Run(() => updater.Loop(CancellationToken.None));
        if (Has("--no-tray"))
        {
            var cts = new CancellationTokenSource();
            ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => cts.Cancel(), null, -1, true);
            hub.Watchdog(cts.Token).GetAwaiter().GetResult();
            _ = api.Stop();
            connectors.StopAll();
            hub.MarkDirty();
            hub.Save();
            Log.W("exit (asked to quit)");
            return 0;
        }

        // Store edition: updates come from the Store, so no update check / menu
        var trayApp = new TrayApp(S, hub, api, notifier, openTask) { Updater = Install.IsPackaged ? null : updater, GitHub = github,
            Machines = new MachineConnector[] { runpod, vastai } };
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => trayApp.QuitSoon(), null, -1, true);
        Application.Run(trayApp);
        connectors.StopAll();
        hub.MarkDirty();
        hub.Save();
        Log.W("exit");
        return 0;
    }

    /// <summary>TASKHUB_SETUP_DRYRUN=1: the setup / uninstall windows go through their steps without changing anything
    /// (for checking the windows themselves).</summary>
    static readonly bool DryRun = Environment.GetEnvironmentVariable("TASKHUB_SETUP_DRYRUN") == "1";

    /// <summary>The install job inside the setup window: progress by steps, then "done" (Open TaskHub) or the error.</summary>
    static async Task RunInstall(SetupForm f, Settings S, string dir, bool auto, bool path, string agents, Install.StorePackage storePkg,
        bool update, Action<int> rc)
    {
        f.ShowProgress(L.T(update ? "setup_progress_update" : "setup_progress_install"));
        var notes = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                if (DryRun)
                {
                    foreach (var (k, v) in new[] { ("step_stop", 0.03), ("step_copy", 0.2), ("step_copy", 0.5), ("step_copy", 0.75), ("step_register", 0.8), ("step_start", 0.9) })
                    { f.Step(L.T(k), v); Thread.Sleep(900); }
                    notes.Add("• " + Integrations.Hint(Integrations.Agent.Claude, Integrations.Kind.Mcp));
                    return;
                }
                dir ??= Install.IsInstalled ? Install.InstallDir : Install.DefaultDir;
                dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
                var err = Install.CheckWritable(dir);
                if (err != null) throw new IOException(L.T("setup_no_access", dir) + "\n\n" + err);
                if (storePkg != null)
                {
                    f.Step(L.T("step_store"), null);
                    Install.ReplaceStoreEdition(storePkg, S);
                }
                f.Step(L.T("step_stop"), 0.03);
                Install.StopOtherHubs();
                f.Step(L.T("step_copy"), 0.05);
                var exe = Install.InstallFiles(dir, p => f.Step(L.T("step_copy"), 0.05 + 0.7 * p));
                f.Step(L.T("step_register"), 0.8);
                var res = Path.Combine(Path.GetTempPath(), $"taskhub-setup-{Environment.ProcessId}.txt");
                try { File.Delete(res); } catch { }
                var a2 = "--register --quiet" + (auto ? "" : " --no-autostart") + (path ? "" : " --no-path") + agents + $" --result \"{res}\"";
                using (var p = Process.Start(new ProcessStartInfo(exe, a2) { UseShellExecute = false }))
                {
                    if (p == null) throw new IOException("cannot start " + exe);
                    if (!p.WaitForExit(120_000)) throw new TimeoutException(exe + " --register");
                    var outp = File.Exists(res) ? File.ReadAllText(res) : "";
                    try { File.Delete(res); } catch { }
                    if (p.ExitCode != 0 || outp.StartsWith("!")) throw new IOException(outp.StartsWith("!") ? outp[1..] : $"--register: exit code {p.ExitCode}");
                    notes.AddRange(outp.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
                }
                f.Step(L.T("step_start"), 0.9);
                for (int i = 0; i < 80 && !Running(S); i++) Thread.Sleep(250);
                if (!Running(S)) throw new TimeoutException(L.T("setup_not_started"));
                f.Step(L.T("step_start"), 1);
            });
            rc(0);
            f.ShowDone(L.T(update ? "setup_done_update" : "setup_done_install"), L.T("installed"), notes, L.T("setup_open"), () => OpenPanel(S));
        }
        catch (Exception e)
        {
            Log.W("install failed: " + e);
            rc(1);
            f.ShowFailed(L.T("setup_failed"), e.Message);
        }
    }

    /// <summary>Opens the installed hub's panel (this process has the foreground and passes it on).</summary>
    static void OpenPanel(Settings S)
    {
        try
        {
            Native.AllowSetForegroundWindow(-1);
            Post(S, "/api/ui/open", new JsonObject { ["task"] = null });
        }
        catch (Exception e) { Log.W("open panel: " + e.Message); }
    }

    /// <summary>Uninstall from Apps &amp; features: confirm (and "also delete my data"), progress, done.</summary>
    static int UninstallWindow(Settings S)
    {
        var dir = Install.InstallDir;
        bool wasInstalled = Install.IsInstalled, removed = false;
        using var f = new SetupForm(S, "TaskHub");
        f.ShowUninstall(async () =>
        {
            bool purge = f.Purge;
            f.ShowProgress(L.T("uninstall_progress"));
            try
            {
                await Task.Run(() =>
                {
                    if (DryRun)
                    {
                        foreach (var (k, v) in new[] { ("step_stop", 0.1), ("step_unregister", 0.35), ("step_agents", 0.6), ("step_purge", 0.85) })
                        { f.Step(L.T(k), v); Thread.Sleep(900); }
                        return;
                    }
                    f.Step(L.T("step_stop"), 0.1);
                    Install.StopOtherHubs();
                    f.Step(L.T("step_unregister"), 0.35);
                    Install.Unregister(S);
                    f.Step(L.T("step_agents"), 0.6);
                    Integrations.RemoveAll(S);
                    try { ToastNotificationManagerCompat.Uninstall(); } catch { }
                    if (purge)
                    {
                        f.Step(L.T("step_purge"), 0.85);
                        Install.PurgeData(S);
                    }
                    f.Step("", 1);
                });
                removed = true;
                f.ShowDone(L.T("uninstall_done"), purge ? L.T("uninstall_purged") : L.T("uninstalled", S.DataDir), null, null, null);
            }
            catch (Exception e)
            {
                Log.W("uninstall failed: " + e);
                f.ShowFailed(L.T("uninstall_failed"), e.Message);
            }
        });
        Application.Run(f);
        // the files (this exe among them) go once this process has exited
        if (removed && wasInstalled && !DryRun) Install.ScheduleRemoveFiles(dir);
        return removed ? 0 : 1;
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
