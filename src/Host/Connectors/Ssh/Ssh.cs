using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// The user's own Linux hosts over SSH, read-only, nothing installed on them: each host a task with CPU, memory, disk, GPU, tmux sessions; a word
/// when a process the user chose or a tmux job ends, a disk fills up, or the connection is lost. Runs Windows' own ssh.exe, so the user's ssh config,
/// agent and known_hosts apply: TaskHub stores no key and trusts no host key by itself. Unlike the other connectors this isn't HTTP and has no
/// SafeHttp: it goes only to the hosts the user listed in Settings, and sends a fixed script (.ai/decisions.md).
/// One long ssh.exe per host, with the script written to its shell's stdin every tick. Remote output is untrusted: cleaned and cut.
/// Config (connectors.ssh): enabled, hosts, interval, disk_warn, tmux_notify, watched ({host: [{pid, start, comm, boot}]}).
/// </summary>
sealed partial class SshConnector : ISettingsConnector
{
    public string Id => "ssh";
    public string Title => "SSH";

    const double DefaultInterval = 60, MinInterval = 30, DefaultDiskWarn = 90;
    const int MaxHosts = 20, MaxWatched = 20;
    internal static readonly Regex HostRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,59}$");   // never an option, never shell text, never a user@; 60: a task id holds 64 with "ssh-"

    readonly Settings S;
    readonly Hub hub;
    readonly object gate = new(), configLock = new();
    readonly Dictionary<string, Watch> watches = new();

    public SshConnector(Settings s, Hub h) { S = s; hub = h; }

    /// <summary>Tests only: a stand-in for ssh.exe, never for the real data folder and never from a setting, which could run any program.</summary>
    string FakeSsh => Secrets.IsTagged(S) ? Environment.GetEnvironmentVariable("TASKHUB_TEST_SSH") is { Length: > 0 } f ? f : null : null;
    bool Fake => FakeSsh != null;
    string SshExe => FakeSsh ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");

    JsonObject Cfg => Connectors.Config(S, Id);
    bool On => J.Truthy(Cfg["enabled"]);
    double Interval => Math.Clamp(J.Num(Cfg["interval"]) ?? DefaultInterval, Fake ? 0.2 : MinInterval, 3600);
    double DiskWarn => J.Num(Cfg["disk_warn"]) is double d && d is >= 0 and <= 100 ? d : DefaultDiskWarn;
    bool TmuxNotify => !Cfg.ContainsKey("tmux_notify") || J.Truthy(Cfg["tmux_notify"]);
    List<string> Hosts => (Cfg["hosts"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Where(x => HostRx.IsMatch(x)).ToList();
    static string Tid(string host) => "ssh-" + host;

    // ------------------------------------------------------------ settings
    public JsonObject SettingsChange(JsonObject page) => SettingsRows.Change(page, "key", "forget_key", new[] { "hosts", "interval", "disk_warn", "tmux_notify" });

    /// <summary>The local API's way in: which hosts TaskHub connects to, and so which of the ssh config's commands it runs, is chosen only on the
    /// settings page.</summary>
    public JsonObject ConfigureHttp(JsonObject b)
    {
        if (b.Count == 0) return State();
        if (!Fake) throw new ApiError(403, "SSH can only be changed in Settings");
        return Configure(b);
    }

    public JsonObject Configure(JsonObject b)
    {
        lock (configLock)
        {
            var c = Cfg;
            if (b.ContainsKey("hosts"))
            {
                if (b["hosts"] is not JsonArray ha) throw new ApiError(400, "hosts must be a list");
                var hosts = new List<string>();
                foreach (var x in ha)
                {
                    var h = J.Str(x).Trim();
                    if (!HostRx.IsMatch(h)) throw new ApiError(400, "bad host name") { Ui = L.T("ssh_err_name") };
                    if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase)) hosts.Add(h);
                }
                if (hosts.Count > MaxHosts) throw new ApiError(400, $"at most {MaxHosts} hosts") { Ui = L.T("ssh_err_many", MaxHosts) };
                c["hosts"] = J.StrArray(hosts);
                if (c["watched"] is JsonObject wa)   // what a removed host ran stays out of config.json
                    foreach (var k in wa.Select(kv => kv.Key).Where(k => !hosts.Contains(k)).ToList()) wa.Remove(k);
            }
            if (b.ContainsKey("interval"))
            {
                double lo = Fake ? 0.2 : MinInterval;
                if (J.Num(b["interval"]) is not double d || d < lo || d > 3600) throw new ApiError(400, $"interval must be {J.Fmt(lo)}-3600") { Ui = L.T("gh_bad_interval", J.Fmt(lo), 3600) };
                c["interval"] = d;
            }
            if (b.ContainsKey("disk_warn"))
            {
                if (J.Num(b["disk_warn"]) is not double d || d < 0 || d > 100) throw new ApiError(400, "disk_warn must be 0-100") { Ui = L.T("ssh_bad_disk") };
                c["disk_warn"] = d;
            }
            if (b.ContainsKey("tmux_notify")) c["tmux_notify"] = J.Truthy(b["tmux_notify"]);
            if (b.ContainsKey("enabled")) c["enabled"] = J.Truthy(b["enabled"]);
            Connectors.SetConfig(S, Id, c);
            Start();
            return State();
        }
    }

    public (string Check, string Text) KeyVerdict() => ("", "");

    public JsonObject SettingsRow()
    {
        var on = On;
        var items = new JsonArray();
        foreach (var h in Hosts) items.Add(new JsonObject { ["v"] = h, ["note"] = Find(h)?.Text() ?? "" });
        var interval = SettingsRows.Field("interval", null, MinInterval, 3600, 10, Interval);
        interval["text"] = L.T("ssh_interval_field");
        var disk = SettingsRows.Field("disk_warn", null, 0, 100, 1, DiskWarn);
        disk["text"] = L.T("ssh_disk_field");
        var notes = new JsonArray();
        if (on && !File.Exists(SshExe)) notes.Add((JsonNode)L.T("ssh_st_no_client"));
        if (Hosts.Any(h => Find(h) is { } w && Blocked(w.State))) notes.Add((JsonNode)L.T("ssh_note_recheck"));
        notes.Add((JsonNode)L.T("ssh_note_readonly"));
        notes.Add((JsonNode)L.T("ssh_note_config"));
        return new JsonObject
        {
            ["id"] = Id, ["name"] = Title, ["kind"] = "local", ["enabled"] = on, ["uses_key"] = false,
            ["summary"] = !on ? "" : Summary(),
            ["help_url"] = SettingsRows.KeyHelpUrl(Id), ["help_label"] = L.T("ssh_help"),
            ["list"] = new JsonObject
            {
                ["key"] = "hosts", ["label"] = L.T("ssh_hosts"), ["items"] = items, ["placeholder"] = L.T("ssh_host_ph"),
                ["add"] = L.T("ssh_host_add"), ["remove"] = L.T("ssh_host_remove"), ["empty"] = L.T("ssh_hosts_empty"), ["dup"] = L.T("ssh_host_dup"),
            },
            ["fields"] = new JsonArray(interval, disk),
            ["switches"] = new JsonArray(new JsonObject { ["key"] = "tmux_notify", ["text"] = L.T("ssh_tmux_switch"), ["value"] = TmuxNotify }),
            ["notes"] = notes,
        };
    }

    string Summary()
    {
        List<Watch> all;
        lock (gate) all = watches.Values.ToList();
        return all.Count == 0 ? "" : L.T("ssh_summary", all.Count(w => w.State == "ok"), all.Count);
    }

    public JsonObject State()
    {
        var a = new JsonArray();
        foreach (var h in Hosts)
        {
            var w = Find(h);
            a.Add(new JsonObject { ["host"] = h, ["state"] = w?.State ?? "off", ["text"] = w?.Text() ?? "" });
        }
        return new JsonObject { ["enabled"] = On, ["client"] = File.Exists(SshExe), ["interval"] = Interval, ["disk_warn"] = DiskWarn, ["tmux_notify"] = TmuxNotify, ["hosts"] = a };
    }

    Watch Find(string host) { lock (gate) return watches.TryGetValue(host, out var w) ? w : null; }

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        var want = On ? Hosts : new List<string>();
        List<Watch> gone;
        lock (gate)
        {
            gone = watches.Values.Where(w => !want.Contains(w.Host)).ToList();
            foreach (var w in gone) watches.Remove(w.Host);
            foreach (var h in want)
                if (!watches.ContainsKey(h))
                {
                    var w = new Watch(h);
                    watches[h] = w;
                    new Thread(() => Run(w)) { IsBackground = true, Name = "ssh " + Log.Id(h) }.Start();
                }
                else watches[h].Wake.Set();   // a changed interval counts from now
        }
        foreach (var w in gone) w.Cts.Cancel();
        foreach (var w in gone) Release(w.Host);
        // tasks of hosts removed while the hub was closed
        foreach (var t in hub.Brief())
        {
            var tid = J.Str(t["id"]);
            if (tid.StartsWith(Id + "-", StringComparison.Ordinal) && Hub.IsActive(J.Str(t["state"])) && !want.Any(h => Tid(h) == tid) && TryTask(tid) is { } full && J.Str(full["source"]) == Id)
                Try(() => hub.FinishQuietly(tid, new JsonObject { ["state"] = "done", ["message"] = L.T("ssh_unwatched") }));
        }
    }

    public void Stop()
    {
        List<Watch> all;
        lock (gate) { all = watches.Values.ToList(); watches.Clear(); }
        foreach (var w in all) w.Cts.Cancel();
    }

    void Release(string host) => Try(() =>
    {
        if (TryTask(Tid(host)) is { } t && Hub.IsActive(J.Str(t["state"])))
            hub.FinishQuietly(Tid(host), new JsonObject { ["state"] = "done", ["message"] = L.T("ssh_unwatched") });
    });

    JsonObject TryTask(string tid) { try { return hub.GetTask(tid); } catch { return null; } }
    void Try(Action a) { try { a(); } catch (Exception e) { Log.I(Id, "task: " + e.GetType().Name); } }   // its message would name the host

    // ------------------------------------------------------------ the buttons of a host's task
    public JsonObject OnControl(string tid, string cid, JsonNode value, string via)
    {
        var task = hub.GetTask(tid);
        var host = tid.StartsWith(Id + "-", StringComparison.Ordinal) ? tid[(Id.Length + 1)..] : "";   // the id, not meta: anyone can write that
        if (J.Str(task["source"]) != Id || Find(host) is not { } w) throw new ApiError(404, "not an SSH host");
        switch (cid)
        {
            case "recheck":
                w.Recheck = true;
                w.SaidBlocked = null;
                w.Wake.Set();
                return new JsonObject { ["ok"] = true };
            case "watch":
            case "unwatch":
                if (J.Num(value) is not double d || d != Math.Floor(d) || d < 1 || d > 4194304) throw new ApiError(400, "a process number") { Ui = L.T("ssh_bad_pid") };
                var pid = (long)d;
                lock (configLock)
                {
                    var c = Cfg;
                    var all = c["watched"] as JsonObject ?? new JsonObject();
                    var list = all[host] as JsonArray ?? new JsonArray();
                    var found = list.FirstOrDefault(x => (long)(J.Num(x?["pid"]) ?? 0) == pid);
                    if (cid == "watch")
                    {
                        if (found == null && list.Count >= MaxWatched) throw new ApiError(400, "too many") { Ui = L.T("ssh_err_many", MaxWatched) };
                        if (found == null) list.Add(new JsonObject { ["pid"] = pid });
                    }
                    else if (found != null) list.Remove(found);
                    else throw new ApiError(400, "not watched") { Ui = L.T("ssh_not_watched", pid) };
                    all[host] = list.DeepClone();
                    c["watched"] = all;
                    Connectors.SetConfig(S, Id, c);
                }
                w.Wake.Set();
                return new JsonObject { ["ok"] = true };
            default: throw new ApiError(404, $"unknown action '{cid}'");
        }
    }

    JsonArray Controls(Watch w)
    {
        var a = new JsonArray();
        if (w.State is "ok")
            a.Add(new JsonObject
            {
                ["id"] = "watch", ["label"] = L.T("ssh_watch"), ["kind"] = "connector", ["states"] = new JsonArray("running", "paused", "stale"),
                ["input"] = new JsonObject { ["type"] = "number", ["label"] = L.T("ssh_watch_pid"), ["min"] = 1, ["max"] = 4194304, ["step"] = 1 },
            });
        if (w.State is "ok" && Watched(w.Host).Count > 0)
            a.Add(new JsonObject
            {
                ["id"] = "unwatch", ["label"] = L.T("ssh_unwatch"), ["kind"] = "connector", ["states"] = new JsonArray("running", "paused", "stale"),
                ["input"] = new JsonObject { ["type"] = "number", ["label"] = L.T("ssh_watch_pid"), ["min"] = 1, ["max"] = 4194304, ["step"] = 1 },
            });
        if (w.State is not ("ok" or "connecting"))
            a.Add(new JsonObject { ["id"] = "recheck", ["label"] = L.T("ssh_recheck"), ["kind"] = "connector", ["states"] = new JsonArray("running", "paused", "stale") });
        return a;
    }

    JsonArray Watched(string host) => (Cfg["watched"] as JsonObject)?[host] as JsonArray ?? new JsonArray();

    void SaveWatched(string host, Func<JsonArray, JsonArray> change)
    {
        lock (configLock)
        {
            var c = Cfg;
            var all = c["watched"] as JsonObject ?? new JsonObject();
            all[host] = change(all[host] as JsonArray ?? new JsonArray()).DeepClone();
            c["watched"] = all;
            Connectors.SetConfig(S, Id, c);
        }
    }

    // ------------------------------------------------------------ one host
    sealed class Watch
    {
        public readonly string Host;
        public readonly CancellationTokenSource Cts = new();
        public readonly AutoResetEvent Wake = new(false);
        public volatile string State = "connecting";
        public volatile bool Recheck;
        public int Fails;
        public bool LostSaid;
        public double LastOk;
        public string Boot;
        public long CpuBusy, CpuAll;
        public string Line = "";
        public Dictionary<string, (string Session, string Cmd)> Panes;   // null: not read yet
        public readonly HashSet<string> FullDisks = new();
        public string SigControls, SaidBlocked;
        public volatile string Why = "";   // ssh's own last line, while a host has never connected
        public Watch(string host) { Host = host; }
        public string Text() => State switch
        {
            "ok" => Line,
            "connecting" => L.T("ssh_connecting"),
            "unreachable" => LastOk <= 0 && Why.Length > 0 ? L.T("ssh_st_never", Why) : L.T("ssh_st_unreachable"),
            "hostkey" => L.T("ssh_st_hostkey", Host),
            "hostkey_changed" => L.T("ssh_st_changed", Host),
            "auth" => L.T("ssh_st_auth"),
            "restricted" => L.T("ssh_st_restricted"),
            "not_linux" => L.T("ssh_st_not_linux"),
            "no_client" => L.T("ssh_st_no_client"),
            _ => State,
        };
    }

    /// <summary>A state only the user can solve: no more attempts until Check again.</summary>
    static bool Blocked(string state) => state is not ("ok" or "connecting" or "unreachable" or "off");

    void Run(Watch w)
    {
        var ct = w.Cts.Token;
        Register(w);
        while (!ct.IsCancellationRequested)
        {
            // this thread's own: nothing may leave it, or it would take the whole hub along
            try { Session(w, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.W(Id, $"{Log.Id(w.Host)}: {e.GetType().Name}"); w.Fails++; SetState(w, "unreachable"); }
            if (ct.IsCancellationRequested) return;
            WaitBeforeRetry(w, ct);
        }
    }

    void Register(Watch w)
    {
        Try(() => hub.Register(new JsonObject
        {
            ["id"] = Tid(w.Host), ["name"] = w.Host, ["source"] = Id, ["project"] = Title,
            ["notify"] = new JsonObject { ["finish"] = false, ["fail"] = false, ["stale"] = false },
            ["meta"] = new JsonObject { [Id] = new JsonObject { ["host"] = w.Host } },
            ["controls"] = Controls(w),
        }, trusted: true));
    }

    /// <summary>One connection, ticks until it fails. Comes back to be retried, or with the state saying why it waits for the user.</summary>
    void Session(Watch w, CancellationToken ct)
    {
        w.Recheck = false;
        if (!File.Exists(SshExe)) { SetState(w, "no_client"); return; }
        using var c = new Conn(SshExe, w.Host);
        if (w.State != "ok") SetState(w, "connecting");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var watched = Watched(w.Host).Select(x => (long)(J.Num(x?["pid"]) ?? 0)).Where(p => p > 0).ToList();
            var r = c.Tick(Script(nonce, watched), nonce, TimeSpan.FromSeconds(35), ct);
            if (r == null)
            {
                Failed(w, c);
                return;
            }
            if (!Apply(w, r, watched)) return;
            SleepTick(w, c, ct);
            w.Recheck = false;
        }
    }

    void Failed(Watch w, Conn c)
    {
        var err = c.Stop();
        Log.D(Id, $"{Log.Id(w.Host)}: exit {c.ExitCode}");
        // ssh's own words are in English whatever the language of Windows; its exit code 255 is its own failure, anything else the remote command's
        var state =
            err.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) ? "hostkey_changed"
            : err.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || err.Contains("host key is known", StringComparison.OrdinalIgnoreCase) ? "hostkey"
            : err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || err.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase) ? "auth"
            // only a host that has never answered: once it has, a bad moment isn't "this account can't"
            : w.LastOk <= 0 && c.ExitCode is int and not 255 && !c.TimedOut ? "restricted"
            : "unreachable";
        if (state != "unreachable") Log.W(Id, $"{Log.Id(w.Host)}: {state}");
        w.Why = Clean(err.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0), 160);
        Lost(w, state);
    }

    void Lost(Watch w, string state)
    {
        w.Fails++;
        SetState(w, state);
        if (state == "unreachable" && w.Fails >= 2 && !w.LostSaid)
        {
            w.LostSaid = true;
            Say(w, L.T(w.LastOk > 0 ? "ssh_lost" : "ssh_cant", w.Host), "warn");
        }
    }

    /// <summary>A failure that the user has to solve stops the attempts until the Check again button: repeated failed logins could get the address
    /// banned. A lost connection is retried, less and less often.</summary>
    void WaitBeforeRetry(Watch w, CancellationToken ct)
    {
        bool blocked = Blocked(w.State);
        double[] steps = { 60, 120, 300, 900 };
        var wait = Fake ? 0.3 : steps[Math.Clamp(w.Fails - 1, 0, steps.Length - 1)];
        var until = J.Now() + wait;
        while (!ct.IsCancellationRequested && (blocked || J.Now() < until))
        {
            var left = blocked ? 60 : Math.Min(60, until - J.Now());
            if (WaitHandle.WaitAny(new[] { w.Wake, ct.WaitHandle }, TimeSpan.FromSeconds(Math.Max(0.05, left))) == 0 && w.Recheck) return;
            Try(() => hub.Heartbeat(Tid(w.Host)));
        }
    }

    void SleepTick(Watch w, Conn c, CancellationToken ct)
    {
        var i = Interval;
        var wait = i + Random.Shared.NextDouble() * Math.Min(5, i * 0.1);   // not every host at the same moment
        WaitHandle.WaitAny(new[] { w.Wake, ct.WaitHandle, c.Gone }, TimeSpan.FromSeconds(wait));   // ssh ending shows now, not at the next tick
    }

    void SetState(Watch w, string state)
    {
        w.State = state;
        var text = w.Text();
        var problem = state is not ("ok" or "connecting");
        Try(() =>
        {
            hub.Update(Tid(w.Host), new JsonObject
            {
                ["status"] = J.Cut(text, 1000), ["attention"] = problem ? J.Cut(text, 300) : "",
            }, trusted: true);
        });
        SyncControls(w);
        if (Blocked(state) && w.SaidBlocked != state)
        {
            w.SaidBlocked = state;   // watching has stopped until the user acts: that needs saying, once
            Say(w, text, "warn");
        }
    }

    /// <summary>The buttons, only when they change: a tick shouldn't redraw them.</summary>
    void SyncControls(Watch w)
    {
        var c = Controls(w);
        var sig = c.ToJsonString();
        if (sig == w.SigControls) return;
        w.SigControls = sig;
        Try(() => hub.Update(Tid(w.Host), new JsonObject { ["controls"] = c }, trusted: true));
    }

    void Say(Watch w, string text, string level) =>
        Try(() => hub.Update(Tid(w.Host), new JsonObject { ["log"] = J.Cut(text, 1000), ["level"] = level, ["toast"] = J.Cut(text, 300) }, trusted: true));

    void Log1(Watch w, string text, string level = "info") =>
        Try(() => hub.Update(Tid(w.Host), new JsonObject { ["log"] = J.Cut(text, 1000), ["level"] = level }, trusted: true));
}
