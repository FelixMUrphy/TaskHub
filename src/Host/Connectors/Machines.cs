using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>A rented machine (GPU pod / instance) as a machine connector sees it, the same for every service.</summary>
sealed class Machine
{
    public string Id, Name;
    /// <summary>"running" | "starting" | "stopped" | "error" | "terminated"</summary>
    public string State = "running";
    public string RawStatus = "";
    public string Gpu = ""; public int GpuCount;
    public string Region = "", Image = "";
    public double? Price;              // $/h
    public double? Spent { get; set; }               // $ so far
    public bool SpentEstimated { get; set; }         // "≈"
    public double? Started;            // unix s, when it (last) started
    public double? Uptime;             // s
    public string Ssh;                 // a ready-to-run ssh command
    public List<(string Label, string Url)> Links = new();
    // load in %: null = unknown (not reported, or not running)
    public List<double?> GpuUtil = new();
    public double? GpuMem, Cpu, Ram, Disk;
    /// <summary>Actions the service allows in the machine's current state ("start", "stop", "restart"); null = unknown.</summary>
    public HashSet<string> Allowed;
    public bool Volume;                // has persistent storage that survives the balance hitting 0 (Runpod network volume)
    public JsonObject Raw;             // the service's own object (kept for detail calls)

    public bool Active => State is "running" or "starting";
}

/// <summary>Balance of the account: what the service tells (any part may be missing).</summary>
sealed class Balance
{
    public double? Amount, SpendPerHr, SpendLimit;
    public string Label = "";            // what Amount is (e.g. "credit") when the service has several
    public JsonObject Raw = new();       // the raw values, for checks with a real key
}

/// <summary>
/// Base of the connectors for rented machines (Runpod, Vast.ai...): one API key (Credential Manager), one task per machine
/// ("&lt;prefix&gt;-&lt;id&gt;", active while it runs, finished when it stops / is gone), load charts from our own polls,
/// "GPU idle N min" with a Stop button, balance / runway with a low-balance warning, actions (start / stop / restart from
/// the panel, toasts and Telegram; delete only from the panel after typing the machine's name) offered only when a probe
/// says the key may run them. A service adapter fills in the HTTP calls and maps its objects to <see cref="Machine"/>.
/// Config (connectors.&lt;id&gt;): enabled, idle_minutes, idle_util, low_runway_hours, low_balance, list_interval,
/// detail_interval, billing_interval, api (tests: a loopback mock only); the balance guard's keys: see Guard.cs.
/// </summary>
abstract partial class MachineConnector : IConnector
{
    public abstract string Id { get; }
    public abstract string Title { get; }                 // "Runpod"
    protected abstract string Prefix { get; }             // task ids: "rp-<id>"
    protected abstract string DefaultApi { get; }         // https://...
    protected virtual bool HasDisk => false;              // a disk series in the load chart
    protected virtual string RestartAction => "restart";  // the service's word: restart / reboot
    protected virtual string ZeroBalanceNote => "";       // what happens at $0 (low-balance warning)
    protected virtual bool SpendLags => true;             // Spend() figures trail the meter: shown with "≈"

    // ------------------------------------------------------------ the service adapter
    /// <summary>All machines (every page); null when the list could not be read completely (then nothing is declared gone).</summary>
    protected abstract Task<List<Machine>> List(string key, CancellationToken ct);
    /// <summary>The live load of a running machine (when the list doesn't carry it); returns the machine (updated) or null.</summary>
    protected virtual Task<Machine> Detail(Machine m, string key, CancellationToken ct) => Task.FromResult(m);
    /// <summary>Spent so far per machine id ($), or null.</summary>
    protected virtual Task<Dictionary<string, double>> Spend(string key, CancellationToken ct) => Task.FromResult<Dictionary<string, double>>(null);
    protected virtual Task<Balance> FetchBalance(string key, CancellationToken ct) => Task.FromResult<Balance>(null);
    /// <summary>A cheap read that tells whether the key works (200) or not (401 / 403).</summary>
    protected abstract Task<Resp> CheckKey(string key, CancellationToken ct);
    /// <summary>A harmless action request (non-existent id): 403 = the key may not act; 400 / 404 / 409 / 422 = it may.</summary>
    protected abstract Task<Resp> Probe(string key, CancellationToken ct);
    /// <summary>start / stop / restart / delete.</summary>
    protected abstract Task<Resp> Act(Machine m, string action, string key, CancellationToken ct);
    /// <summary>Service-specific reading of a failed action ("GPU taken"...), or null for the generic texts.</summary>
    protected virtual string ActionError(Resp r, string action) => null;
    /// <summary>Rate-limit headers of the service (Runpod RateLimit); call Pause when a window is used up.</summary>
    protected virtual void OnHeaders(HttpResponseMessage r) { }
    protected virtual void ExtraConfig(JsonObject b, JsonObject c) { }
    protected virtual void ExtraState(JsonObject s) { }

    // ------------------------------------------------------------ state
    protected readonly Settings S;
    protected readonly Hub hub;
    readonly SafeHttp http;
    readonly object gate = new();
    CancellationTokenSource cts;

    sealed class Track
    {
        public Machine M; public string LastSig; public double NextDetail;
        public double? IdleSince; public bool IdleWarned;
    }
    readonly Dictionary<string, Track> machines = new();   // machine id -> what we know / show
    readonly HashSet<string> quiet = new();                // stopped machines first seen as stopped (no toast)
    Dictionary<string, double> spent;
    Balance balance;
    double balanceAt;
    bool low;                                              // balance / runway under the limit (warned once per crossing)
    protected string status = "off";
    bool? canAct;                                          // null: not probed yet / probe failed
    double probedAt, nextList, nextBilling, pausedUntil, backoff;
    string keyHash;
    int lastCode;                                          // the status of the last answer (why a balance read failed)
    // the last verdict on the saved key: "ok" | "revoked" (the service stopped accepting it) | "network" | "unavailable"
    // (saved but not checked yet); null = no key / not checked. Never anything of the key itself.
    string keyCheck;
    int keyCheckCode;

    protected MachineConnector(Settings s, Hub h)
    {
        S = s; hub = h;
        http = new SafeHttp(() => new[] { ApiUri.Host.ToLowerInvariant() });
    }

    // ------------------------------------------------------------ settings
    protected sealed record Conf(bool Enabled, double IdleMinutes, double IdleUtil, double LowRunwayHours, double? LowBalance,
        double ListEvery, double DetailEvery, double BillingEvery);

    /// <summary>The API base: a loopback mock (tests) or the service.</summary>
    protected Uri ApiUri
    {
        get
        {
            var o = J.Str(Connectors.Config(S, Id)["api"]);
            return Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback ? u : new Uri(DefaultApi);
        }
    }

    bool Mock => ApiUri.IsLoopback;

    protected JsonObject RawConfig => Connectors.Config(S, Id);

    protected Conf Cfg
    {
        get
        {
            var c = Connectors.Config(S, Id);
            double Get(string k, double def, double lo, double hi, double loMock) =>
                Math.Clamp(J.Num(c[k]) ?? def, Mock ? loMock : lo, hi);
            return new Conf(J.Truthy(c["enabled"]), Get("idle_minutes", 30, 1, 1440, 0.02), Get("idle_util", 5, 1, 50, 1),
                Get("low_runway_hours", 12, 0, 720, 0), J.Num(c["low_balance"]) is double lb && lb >= 0 ? lb : null,
                Get("list_interval", 60, 30, 3600, 1), Get("detail_interval", 30, 15, 3600, 1), Get("billing_interval", 600, 300, 86400, 1));
        }
    }

    static readonly (string Key, double Lo, double Hi)[] NumKeys =
    {
        ("idle_minutes", 1, 1440), ("idle_util", 1, 50), ("low_runway_hours", 0, 720),
        ("list_interval", 30, 3600), ("detail_interval", 15, 3600), ("billing_interval", 300, 86400),
    };

    /// <summary>Changes the settings (settings page / API); unknown keys are ignored, bad values rejected (nothing changed).</summary>
    public JsonObject Configure(JsonObject b)
    {
        var c = Connectors.Config(S, Id);
        foreach (var (k, lo0, hi) in NumKeys)
        {
            if (!b.ContainsKey(k)) continue;
            double lo = Mock ? (k == "idle_minutes" ? 0.02 : k.EndsWith("interval") ? 1 : lo0) : lo0;
            if (J.Num(b[k]) is not double d || d < lo || d > hi) throw new ApiError(400, $"{k} must be {J.Fmt(lo)}-{J.Fmt(hi)}");
            c[k] = d;
        }
        if (b.ContainsKey("low_balance"))
        {
            if (b["low_balance"] == null || J.Str(b["low_balance"]) == "") c.Remove("low_balance");
            else if (J.Num(b["low_balance"]) is double d && d >= 0 && d <= 1e6) c["low_balance"] = d;
            else throw new ApiError(400, "low_balance must be a number >= 0 or null");
        }
        ConfigureGuard(b, c);
        if (b.ContainsKey("enabled")) c["enabled"] = J.Truthy(b["enabled"]);
        ExtraConfig(b, c);
        if (J.IsStr(b["key"])) SetKey(J.Str(b["key"]));   // a rejected key: nothing is changed
        Connectors.SetConfig(S, Id, c);
        if (J.Truthy(b["forget_key"])) { Secrets.Delete(S, Id); canAct = null; keyHash = null; keyCheck = null; balance = null; }
        Start();
        return State();
    }

    static readonly Regex KeyRx = new(@"^[\x21-\x7E]{8,400}$");

    /// <summary>Checks the key with the service, keeps it in the Credential Manager (never config.json), probes what it may do.
    /// A key the service rejects (401 / 403) is not kept (400). When the service can't be asked (no network, 5xx, 429) the
    /// key is kept and checked again by the loop: key_check "network" / "unavailable". A working key: "ok", and the balance
    /// is read at once (for "Connected, balance $X").</summary>
    public void SetKey(string key)
    {
        key = (key ?? "").Trim();
        if (!KeyRx.IsMatch(key)) throw new ApiError(400, $"this does not look like a {Title} API key");
        Resp r = null;
        try { r = CheckKey(key, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        { Log.W($"{Id}: key not checked: " + e.GetBaseException().Message); }
        if (r?.Code is 401 or 403) throw new ApiError(400, $"{Title} rejected the key");
        try { Secrets.Write(S, Id, key); }
        catch (Exception e) { throw new ApiError(500, "the key was not saved: " + e.Message); }
        keyHash = null;   // probe again
        canAct = null;
        balance = null;   // the old key's account
        SetCheck(r == null ? "network" : r.Code == 200 ? "ok" : "unavailable", r?.Code ?? 0);
        if (keyCheck != "ok") return;
        try
        {
            if (FetchBalance(key, CancellationToken.None).GetAwaiter().GetResult() is { } bal) { balance = bal; balanceAt = J.Now(); }
        }
        catch (Exception e) { Log.W($"{Id}: balance after the key: " + e.GetBaseException().Message); }
    }

    protected void SetCheck(string verdict, int code = 0) { keyCheck = verdict; keyCheckCode = code; }

    /// <summary>The service said 401 to the saved key (the adapters' list call): revoked / expired.</summary>
    protected void KeyRejected() { status = "key rejected"; SetCheck("revoked"); }

    /// <summary>The verdict on the saved key in words: "Connected, balance $X" / revoked / no network / unavailable.</summary>
    string CheckText() => keyCheck switch
    {
        "ok" => balance?.Amount is not double a ? L.T("k_ok")
            : balance.Label.Length == 0 ? L.T("k_ok_balance", Money(a))
            : L.T("k_ok") + " · " + balance.Label + " " + Money(a),   // labelled (Vast.ai: "Balance $x · Credit $y")
        "revoked" => L.T("k_revoked", Title),
        "network" => L.T("k_network", Title),
        "unavailable" => L.T("k_unavailable", Title, keyCheckCode > 0 ? "HTTP " + keyCheckCode : "?"),
        _ => null,
    };

    bool KeySet()
    {
        try { return Secrets.Read(S, Id) is { Length: > 0 }; }
        catch (Exception e) { Log.W($"{Id}: key not read: " + e.Message); return false; }
    }

    public JsonObject State()
    {
        var c = Cfg;
        int n, running;
        var list = new JsonArray();
        lock (gate)
        {
            n = machines.Count;
            running = machines.Values.Count(t => t.M.Active);
            foreach (var t in machines.Values.OrderBy(t => t.M.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new JsonObject { ["id"] = t.M.Id, ["name"] = t.M.Name, ["state"] = t.M.State, ["task"] = Tid(t.M.Id) });
        }
        var b = balance;
        var s = new JsonObject
        {
            ["enabled"] = c.Enabled, ["key_set"] = KeySet(), ["status"] = status,
            ["key_check"] = keyCheck, ["key_check_text"] = CheckText(),
            ["can_act"] = canAct, ["probed_at"] = probedAt > 0 ? probedAt : null,
            ["machines"] = list, ["running"] = running,
            ["idle_minutes"] = c.IdleMinutes, ["idle_util"] = c.IdleUtil,
            ["low_runway_hours"] = c.LowRunwayHours, ["low_balance"] = c.LowBalance,
            ["balance"] = b == null ? null : new JsonObject
            {
                ["amount"] = b.Amount, ["label"] = b.Label, ["spend_per_hr"] = SpendRate(),
                ["spend_limit"] = b.SpendLimit, ["runway_hours"] = Runway(), ["raw"] = b.Raw.DeepClone(),
                ["at"] = balanceAt,
            },
            ["low"] = low,
            ["guard"] = GuardState(),
            ["summary"] = Summary(),
        };
        ExtraState(s);
        return s;
    }

    /// <summary>The short line of the settings page: access, machines running, balance and runway.</summary>
    string Summary()
    {
        var parts = new List<string>();
        // first the verdict on the key: "Connected, balance $X" (the balance then isn't repeated below), revoked...
        string bl = BalanceLine();
        if (keyCheck == "ok" && balance?.Amount is double)
        {
            parts.Add((low ? "⚠ " : "") + CheckText());
            bl = Runway() is double h ? L.T("m_runway", Hours(h)) : "";
        }
        else if (CheckText() is { } ct) parts.Add(ct);
        if (pending is { } pd) parts.Insert(0, "⚠ " + PendingShort(pd));
        if (canAct == false) parts.Add(L.T("m_readonly"));
        else if (canAct == true) parts.Add(L.T("m_can_act"));
        int running;
        lock (gate) running = machines.Values.Count(t => t.M.Active);
        if (running > 0) parts.Add(L.N("m_running_n", running));
        if (bl.Length > 0) parts.Add(bl);
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        Stop();
        var c = Cfg;
        lock (gate) { pausedUntil = 0; backoff = 0; nextList = 0; nextBilling = 0; }
        if (!c.Enabled) { status = "off"; return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        status = "starting";
        _ = Task.Run(() => Loop(ct));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
    }

    async Task Loop(CancellationToken ct)
    {
        Adopt();
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                status = "offline: " + e.GetBaseException().Message;
                Log.W($"{Id}: " + e.GetBaseException().Message);
                try { GuardMissFromError(e.GetBaseException().Message); } catch (Exception e2) { Log.W($"{Id}: guard: " + e2.Message); }
                try { await Task.Delay(Mock ? 2000 : 15000, ct); } catch { return; }
            }
            try { await Task.Delay(Mock ? 300 : 1000, ct); } catch { return; }
        }
    }

    /// <summary>After a restart: the machines the hub still shows are followed again (and finished if they are gone).</summary>
    void Adopt()
    {
        foreach (var t in hub.Brief())
        {
            JsonObject full;
            try { full = hub.GetTask(J.Str(t["id"])); } catch { continue; }
            if (J.Str(full["source"]) != Id || full["meta"]?[Id] is not JsonObject g) continue;
            var id = J.Str(g["id"]);
            if (id.Length == 0) continue;
            var st = J.Str(full["state"]);
            var m = new Machine
            {
                Id = id, Name = J.Str(g["name"], id),
                State = Hub.IsActive(st) ? "running" : J.Str(g["state"]) == "terminated" ? "terminated" : "stopped",
            };
            lock (gate) machines.TryAdd(id, new Track { M = m });   // a restart of the loop keeps what it knows
        }
        AdoptGuard();
    }

    async Task Tick(CancellationToken ct)
    {
        var c = Cfg;
        var now = J.Now();
        if (now < pausedUntil) return;
        string key;
        try { key = Secrets.Read(S, Id); }
        catch (Exception e) { status = e.Message; pausedUntil = now + 120; return; }
        if (string.IsNullOrEmpty(key)) { status = "no key"; pausedUntil = now + (Mock ? 2 : 30); return; }
        // a new key (or once a day): may it run actions?
        var kh = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        if (kh != keyHash || now - probedAt > 86400)
        {
            keyHash = kh;
            await RunProbe(key, ct);
        }
        await GuardTick(key, ct);   // a pending stop whose time has come
        if (now >= nextList)
        {
            nextList = now + c.ListEvery;
            var list = await List(key, ct);
            if (list == null)
            {
                if (status == "ok" || status == "starting") status = "list failed";
                if (now >= nextBilling && J.Now() >= pausedUntil) await Billing(key, c, ct);   // the balance may still be readable
                return;
            }
            status = "ok";
            if (keyCheck != "ok") SetCheck("ok");
            Reconcile(list, c);
        }
        List<Track> due;
        lock (gate) due = machines.Values.Where(t => t.M.State == "running" && now >= t.NextDetail).ToList();
        foreach (var t in due)
        {
            if (J.Now() < pausedUntil) return;
            t.NextDetail = now + c.DetailEvery;
            var m = await Detail(t.M, key, ct);
            if (m == null) continue;
            lock (gate) t.M = m;
            Sample(t, c);
            Show(t, c, false);
        }
        if (J.Now() < pausedUntil) return;   // a 429 on the last detail: billing waits too (its answer would reset the back-off)
        if (now >= nextBilling) await Billing(key, c, ct);
    }

    /// <summary>Spent per machine and the balance (billing_interval; every minute while a guard stop is pending), the
    /// low-balance warning and the balance guard.</summary>
    async Task Billing(string key, Conf c, CancellationToken ct)
    {
        nextBilling = J.Now() + BalanceEvery(c);
        var sp = await Spend(key, ct);
        if (sp != null) spent = sp;
        // a 429 on the charges pauses everything: the balance waits too (an answer would also reset the back-off)
        Balance b = null;
        bool tried = J.Now() >= pausedUntil;
        string why = "";
        if (tried)
        {
            lastCode = 0;
            try { b = await FetchBalance(key, ct); }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            { why = e.GetBaseException().Message; status = "offline: " + why; }
            if (b == null && why.Length == 0) why = lastCode is > 0 and not 200 ? "HTTP " + lastCode : "no answer";
            if (b == null && lastCode == 429) tried = false;   // rate limit: not an outage
        }
        if (b != null) { balance = b; balanceAt = J.Now(); }
        CheckLow(c);
        if (tried) GuardOnBalance(b != null, why);
        List<Track> all;
        lock (gate) all = machines.Values.Where(t => t.M.State != "terminated").ToList();
        foreach (var t in all) Show(t, c, false);
    }

    async Task RunProbe(string key, CancellationToken ct)
    {
        var r = await Probe(key, ct);
        probedAt = J.Now();
        bool? was = canAct;
        canAct = r.Code switch
        {
            403 => false,
            400 or 404 or 405 or 409 or 422 => true,
            401 => null,
            _ => was,   // can't tell (network, 5xx, rate limit): keep what we knew
        };
        if (r.Code is not (400 or 401 or 403 or 404 or 405 or 409 or 422)) probedAt = J.Now() - 86400 + 600;   // again in 10 min
        Log.W($"{Id}: action probe -> HTTP {r.Code}, actions {(canAct == true ? "allowed" : canAct == false ? "not allowed" : "unknown")}");
        if (was != canAct)
        {
            List<Track> all;
            lock (gate) all = machines.Values.ToList();
            foreach (var t in all) t.LastSig = null;   // controls change
        }
    }

    // ------------------------------------------------------------ machines -> tasks
    public string Tid(string machineId) => Prefix + "-" + J.Cut(Regex.Replace(machineId ?? "", @"[^A-Za-z0-9_.-]", "_"), 60);

    void Reconcile(List<Machine> list, Conf c)
    {
        var seen = new HashSet<string>();
        foreach (var m in list)
        {
            if (string.IsNullOrEmpty(m.Id)) continue;
            seen.Add(m.Id);
            Track t;
            string prev;
            lock (gate)
            {
                if (!machines.TryGetValue(m.Id, out t)) machines[m.Id] = t = new Track { M = m };
                prev = t.M == m ? null : t.M.State;
                // the list may lack the load (Runpod): keep the last detail until the next one
                if (t.M != m && !HasLoad(m) && m.State == "running")
                {
                    m.GpuUtil = t.M.GpuUtil; m.GpuMem = t.M.GpuMem; m.Cpu = t.M.Cpu; m.Ram = t.M.Ram; m.Disk = t.M.Disk;
                    if (t.M.Ssh != null) m.Ssh = t.M.Ssh;   // the detail's (direct) one, not the list's proxy one
                    if (m.Links.Count == 0) m.Links = t.M.Links;
                    m.Uptime ??= t.M.Uptime;
                }
                t.M = m;
                if (m.State != "running") { t.IdleSince = null; t.IdleWarned = false; }
                if (prev != m.State) t.NextDetail = 0;
            }
            Transition(t, prev, c);
        }
        List<Track> gone;
        lock (gate) gone = machines.Values.Where(t => !seen.Contains(t.M.Id) && t.M.State != "terminated").ToList();
        foreach (var t in gone)
        {
            var prev = t.M.State;
            t.M.State = "terminated";
            Transition(t, prev, c);
        }
    }

    static bool HasLoad(Machine m) => m.GpuUtil.Count > 0 || m.Cpu != null || m.Ram != null;

    /// <summary>Task state follows the machine: active while it runs or starts; stopped -> done "stopped"; error -> failed;
    /// terminated / gone -> done "terminated". A stopped machine first seen as stopped is shown quietly (no toast, seen).</summary>
    void Transition(Track t, string prev, Conf c)
    {
        var m = t.M;
        var tid = Tid(m.Id);
        var task = TryTask(tid);
        bool taskActive = task != null && Hub.IsActive(J.Str(task["state"]));
        if (m.Active)
        {
            if (!taskActive) { Register(t, c, quiet: false); return; }
            Show(t, c, prev != m.State);
            return;
        }
        if (task == null)
        {
            if (m.State == "terminated") return;
            if (prev != null) return;   // known (pruned from the history): stays away
            // first seen while stopped: shown, but without a toast and already seen
            Register(t, c, quiet: true);
            task = TryTask(tid);
            taskActive = true;
        }
        if (taskActive)
        {
            Show(t, c, true);
            var (state, msg) = m.State switch
            {
                "error" => ("failed", L.T("m_error")),
                "terminated" => ("done", L.T("m_terminated")),
                _ => ("done", L.T("m_stopped")),
            };
            Try(() => hub.Finish(tid, new JsonObject { ["state"] = state, ["message"] = msg }));
            bool q;
            lock (gate) q = quiet.Remove(m.Id);
            if (q) hub.MarkSeen(new[] { tid });
            return;
        }
        // already finished: a stopped machine terminated meanwhile -> say so (once)
        if (m.State == "terminated" && prev != "terminated")
        {
            Show(t, c, true);
            Try(() => hub.Finish(tid, new JsonObject { ["state"] = "done", ["message"] = L.T("m_terminated") }));
        }
        else Show(t, c, false);
    }

    JsonObject TryTask(string tid)
    {
        try { return hub.GetTask(tid); } catch { return null; }
    }

    void Register(Track t, Conf c, bool quiet)
    {
        var m = t.M;
        var tid = Tid(m.Id);
        if (quiet) lock (gate) this.quiet.Add(m.Id);
        var b = new JsonObject
        {
            ["id"] = tid, ["name"] = J.Cut(m.Name ?? m.Id, 200), ["source"] = Id, ["project"] = Title,
            ["started"] = m.Started,
            ["notify"] = new JsonObject { ["finish"] = !quiet, ["fail"] = !quiet, ["stale"] = false },
            ["meta"] = new JsonObject { [Id] = new JsonObject { ["id"] = m.Id, ["name"] = m.Name } },
            ["controls"] = Controls(m),
            ["widgets"] = Widgets(),
        };
        try { hub.Register(b, trusted: true); }
        catch (Exception e) { Log.W($"{Id}: register {tid}: " + e.Message); return; }
        t.LastSig = null;
        Show(t, c, true);
    }

    JsonArray Widgets()
    {
        var w = new JsonArray(
            new JsonObject { ["id"] = "info", ["type"] = "kv" },
            new JsonObject
            {
                ["id"] = "gpu", ["type"] = "chart", ["label"] = "GPU", ["unit"] = "%", ["series"] = new JsonArray("GPU", "VRAM"),
                ["y_min"] = 0, ["y_max"] = 100, ["max_points"] = 240, ["height"] = 48, ["span"] = 1,
            },
            new JsonObject
            {
                ["id"] = "load", ["type"] = "chart", ["label"] = L.T("m_load"), ["unit"] = "%",
                ["series"] = HasDisk ? new JsonArray("CPU", "RAM", L.T("m_disk")) : new JsonArray("CPU", "RAM"),
                ["y_min"] = 0, ["y_max"] = 100, ["max_points"] = 240, ["height"] = 48, ["span"] = 1,
            });
        return w;
    }

    /// <summary>Buttons: only when the key may run actions. Stop / restart while it runs (also in toasts and Telegram), start
    /// when stopped (panel), delete after typing the name (panel only; refused from toasts / Telegram).</summary>
    JsonArray Controls(Machine m)
    {
        var a = new JsonArray();
        if (canAct != true || m.State == "terminated") return a;
        var name = m.Name ?? m.Id;
        bool Ok(string act) => m.Allowed == null || m.Allowed.Contains(act);
        if (Ok("stop"))
            a.Add(new JsonObject
            {
                ["id"] = "stop", ["label"] = L.T("m_stop"), ["kind"] = "connector", ["style"] = "danger", ["toast"] = true,
                ["confirm"] = L.T("m_stop_q", name), ["states"] = new JsonArray("running", "paused", "stale"),
            });
        if (m.State == "running" && Ok(RestartAction))
            a.Add(new JsonObject
            {
                ["id"] = "restart", ["label"] = L.T(RestartAction == "reboot" ? "m_reboot" : "m_restart"), ["kind"] = "connector", ["toast"] = true,
                ["confirm"] = L.T("m_restart_q", name), ["states"] = new JsonArray("running", "paused", "stale"),
            });
        if (Ok("start"))
            a.Add(new JsonObject
            {
                ["id"] = "start", ["label"] = L.T("m_start"), ["kind"] = "connector", ["style"] = "primary", ["toast"] = true,
                ["confirm"] = L.T("m_start_q", name), ["states"] = new JsonArray("done", "failed"),
            });
        a.Add(new JsonObject
        {
            ["id"] = "delete", ["label"] = L.T("m_delete"), ["kind"] = "connector", ["style"] = "danger",
            ["confirm"] = L.T("m_delete_q", name), ["states"] = J.StrArray(Hub.AllStates), ["panel_only"] = true,
            ["input"] = new JsonObject { ["type"] = "text", ["label"] = L.T("m_delete_type", name) },
        });
        return a;
    }

    static string Money(double? v) => v is double d ? "$" + d.ToString(d < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) : "";

    static string Hours(double h) =>
        h >= 48 ? L.T("m_days", ((int)(h / 24)).ToString(CultureInfo.InvariantCulture))
        : h >= 1 ? L.T("m_hours", ((int)Math.Floor(h)).ToString(CultureInfo.InvariantCulture))
        : L.T("m_minutes", Math.Max(0, (int)(h * 60)).ToString(CultureInfo.InvariantCulture));

    /// <summary>Current spend rate: what the service says, else the sum of the running machines' prices.</summary>
    double? SpendRate()
    {
        if (balance?.SpendPerHr is double r) return r;
        lock (gate)
        {
            var run = machines.Values.Where(t => t.M.Active && t.M.Price != null).ToList();
            return run.Count == 0 ? (machines.Count > 0 ? 0 : null) : run.Sum(t => t.M.Price.Value);
        }
    }

    double? Runway() => balance?.Amount is double a && SpendRate() is double r && r > 0 ? Math.Max(0, a) / r : null;

    string BalanceLine()
    {
        if (balance?.Amount is not double a) return "";
        var s = (balance.Label.Length > 0 ? balance.Label + " " : "") + Money(a);
        if (Runway() is double h) s += " · " + L.T("m_runway", Hours(h));
        return (low ? "⚠ " : "") + s;
    }

    void CheckLow(Conf c)
    {
        if (balance?.Amount is not double a) return;
        var rw = Runway();
        bool now = (c.LowRunwayHours > 0 && rw is double h && h < c.LowRunwayHours) || (c.LowBalance is double lb && a < lb);
        // the balance guard (opt-in) has its own warning: not two at once
        if (now && !low && !GCfg.On)
        {
            var msg = L.T("m_low", Title, Money(a), rw is double h2 ? Hours(h2) : "—");
            if (ZeroBalanceNote.Length > 0) msg += " " + ZeroBalanceNote;
            hub.Notify(new JsonObject { ["title"] = "TaskHub · " + Title, ["level"] = "warn", ["message"] = msg, ["click"] = "dismiss" });
            Log.W($"{Id}: low balance {a} (runway {rw})");
        }
        low = now;
    }

    /// <summary>One load sample: the charts and the idle watch.</summary>
    void Sample(Track t, Conf c)
    {
        var m = t.M;
        var tid = Tid(m.Id);
        var gpuMax = m.GpuUtil.Where(x => x != null).Select(x => x.Value).DefaultIfEmpty(double.NaN).Max();
        var w = new JsonObject
        {
            ["gpu"] = new JsonArray(double.IsNaN(gpuMax) ? null : JsonValue.Create(Math.Round(gpuMax, 1)), Pct(m.GpuMem)),
            ["load"] = HasDisk ? new JsonArray(Pct(m.Cpu), Pct(m.Ram), Pct(m.Disk)) : new JsonArray(Pct(m.Cpu), Pct(m.Ram)),
        };
        Try(() => hub.Update(tid, new JsonObject { ["widgets"] = w }, trusted: true));
        // idle: every GPU under the threshold for idle_minutes -> once per idle period
        if (m.GpuUtil.Count == 0 || m.GpuUtil.Any(x => x == null)) return;
        var now = J.Now();
        bool idle = m.GpuUtil.All(x => x.Value < c.IdleUtil);
        if (!idle)
        {
            bool was = t.IdleWarned;
            t.IdleSince = null; t.IdleWarned = false;
            if (was) t.LastSig = null;   // attention goes
            return;
        }
        t.IdleSince ??= now;
        if (!t.IdleWarned && now - t.IdleSince >= c.IdleMinutes * 60)
        {
            t.IdleWarned = true;
            var text = L.T("m_idle", Math.Max(1, (int)Math.Round(c.IdleMinutes)));
            t.LastSig = null;
            Try(() => hub.Update(tid, new JsonObject
            {
                ["attention"] = Attention(t), ["log"] = text, ["level"] = "warn", ["toast"] = text,
            }, trusted: true));
        }
    }

    static JsonNode Pct(double? v) => v is double d ? JsonValue.Create(Math.Round(Math.Clamp(d, 0, 100), 1)) : null;

    string Attention(Track t)
    {
        if (!t.M.Active) return "";
        var parts = new List<string>();
        if (t.IdleWarned) parts.Add(L.T("m_idle", Math.Max(1, (int)Math.Round(Cfg.IdleMinutes))));
        if (low) parts.Add(L.T("m_low_short"));
        return string.Join(" · ", parts);
    }

    /// <summary>Puts what we know of the machine on its task (only what changed).</summary>
    void Show(Track t, Conf c, bool force)
    {
        var m = t.M;
        var tid = Tid(m.Id);
        var info = new JsonObject();
        void Kv(string k, string v) { if (!string.IsNullOrEmpty(v)) info[k] = v; }
        Kv("GPU", m.Gpu.Length > 0 ? (m.GpuCount > 1 ? m.GpuCount + " × " : "") + m.Gpu : "");
        Kv(L.T("m_region"), m.Region);
        Kv(L.T("m_image"), m.Image);
        Kv(L.T("m_price"), m.Price is double p ? Money(p) + "/h" : "");
        double? sp = null;
        bool est = m.SpentEstimated;
        if (spent != null && spent.TryGetValue(m.Id, out var s1)) { sp = s1; est = SpendLags; }
        else if (m.Spent is double s2) sp = s2;
        // nothing billed yet (a new machine): price × time it has run, marked as an estimate
        else if (m.Active && m.Price is double pr && (m.Uptime ?? (m.Started is double st0 ? J.Now() - st0 : null)) is double u0 && u0 > 0)
        { sp = pr * u0 / 3600; est = true; }
        if (sp != null) Kv(L.T("m_spent"), (est ? "≈ " : "") + Money(sp));
        if (m.Active && (m.Uptime ?? (m.Started is double st ? J.Now() - st : null)) is double up && up >= 0)
            Kv(L.T("m_uptime"), Hub.FmtDur(up));
        if (m.State == "running") Kv("SSH", m.Ssh);
        Kv(L.T("m_balance"), BalanceLine());
        if (GCfg.Exempt.Contains(m.Id)) Kv(L.T("g_autostop"), L.T("g_exempt"));
        var status = m.State switch
        {
            "starting" => L.T("m_starting") + (m.RawStatus.Length > 0 ? " (" + m.RawStatus + ")" : ""),
            "running" => string.Join(" · ", new[] { m.Price is double p2 ? Money(p2) + "/h" : "", sp is double s3 ? (est ? "≈ " : "") + Money(s3) : "" }.Where(x => x.Length > 0)),
            _ => m.RawStatus,
        };
        var links = new JsonArray();
        if (m.State == "running")
            foreach (var (label, url) in m.Links.Take(8))
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.IsLoopback && Mock)))
                    links.Add(new JsonObject { ["label"] = J.Cut(label, 40), ["url"] = url });
        var desc = string.Join(" · ", new[] { m.Gpu.Length > 0 ? (m.GpuCount > 1 ? m.GpuCount + " × " : "") + m.Gpu : "", m.Region }.Where(x => x.Length > 0));
        var u2 = new JsonObject
        {
            ["name"] = J.Cut(m.Name ?? m.Id, 200), ["description"] = desc, ["status"] = J.Cut(status, 300),
            ["links"] = links, ["controls"] = Controls(m), ["attention"] = Attention(t),
            ["meta"] = new JsonObject { [Id] = new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["state"] = m.State, ["status"] = m.RawStatus } },
        };
        var sig = J.Dump(u2) + J.Dump(info);
        if (!force && sig == t.LastSig) return;
        t.LastSig = sig;
        u2["widgets"] = new JsonObject { ["info"] = info };
        Try(() => hub.Update(tid, u2, trusted: true));
    }

    // ------------------------------------------------------------ buttons
    public JsonObject OnControl(string tid, string cid, JsonNode value, string via)
    {
        if (tid == GuardTid) return GuardControl(cid);   // Stop now / Snooze of a pending balance-guard stop
        var task = hub.GetTask(tid);
        if (J.Str(task["source"]) != Id || task["meta"]?[Id] is not JsonObject g) throw new ApiError(404, $"not a {Title} machine");
        var id = J.Str(g["id"]);
        Track t;
        lock (gate) machines.TryGetValue(id, out t);
        var m = t?.M ?? new Machine { Id = id, Name = J.Str(g["name"], id) };
        if (canAct != true) throw new ApiError(403, L.T("m_err_403"));
        var action = cid switch
        {
            "stop" => "stop", "start" => "start", "restart" => RestartAction, "delete" => "delete",
            _ => throw new ApiError(404, $"unknown action '{cid}'"),
        };
        if (action == "delete")
        {
            // irreversible: only from the panel, and only after the exact name was typed
            if (via != "panel") throw new ApiError(403, L.T("m_delete_panel"));
            if (J.Str(value) != (m.Name ?? m.Id)) throw new ApiError(400, L.T("m_delete_mismatch"));
        }
        var key = Secrets.Read(S, Id);
        if (string.IsNullOrEmpty(key)) throw new ApiError(400, $"no {Title} API key");
        var r = Act(m, action, key, CancellationToken.None).GetAwaiter().GetResult();
        if (r.Code is not (200 or 201 or 202 or 204))
        {
            if (r.Code == 403 && !r.Limited) { canAct = false; lock (gate) if (t != null) t.LastSig = null; }
            var text = ActionError(r, action) ?? r.Code switch
            {
                403 => L.T("m_err_403"),
                409 => L.T("m_err_409"),
                404 => L.T("m_err_404"),
                429 => L.T("m_err_429"),
                _ => $"{Title}: HTTP {r.Code}" + (ErrText(r) is { Length: > 0 } et ? " · " + et : ""),
            };
            Try(() => hub.Update(tid, new JsonObject { ["log"] = $"{action}: {text}", ["level"] = "error" }, trusted: true));
            throw new ApiError(r.Code is 403 or 409 or 400 or 404 ? 409 : 502, text);
        }
        Try(() => hub.Update(tid, new JsonObject { ["log"] = action + ": " + L.T("m_sent"), ["level"] = "info" }, trusted: true));
        nextList = J.Now() + (Mock ? 0.5 : 3);   // see the new state soon
        if (t != null) t.NextDetail = 0;
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------ HTTP
    public sealed class Resp
    {
        public int Code; public JsonNode Body; public bool Limited;
    }

    /// <summary>The service's error text (problem+json detail / message / msg / error).</summary>
    protected static string ErrText(Resp r) =>
        J.Cut(new[] { "detail", "message", "msg", "error", "title" }.Select(k => r.Body?[k]).Where(J.IsStr).Select(x => J.Str(x)).FirstOrDefault() ?? "", 200);

    /// <summary>Waits before the next request: as long as the service says, else 60 s doubling (up to 30 min).</summary>
    protected void Pause(double? secs, string why)
    {
        double wait;
        if (secs is double s && s > 0) wait = Math.Min(s, 3600);
        else { backoff = backoff <= 0 ? 60 : Math.Min(backoff * 2, 1800); wait = backoff; }
        pausedUntil = Math.Max(pausedUntil, J.Now() + wait);
        status = "rate limit, waiting until " + DateTimeOffset.FromUnixTimeSeconds((long)pausedUntil).ToLocalTime().ToString("HH:mm:ss");
        Log.W($"{Id}: {status} ({why})");
    }

    protected Task<Resp> Call(HttpMethod m, string pathAndQuery, JsonNode body, string key, CancellationToken ct)
    {
        var baseUri = ApiUri;
        return Send(m, new Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + pathAndQuery), body, key, ct);
    }

    async Task<Resp> Send(HttpMethod m, Uri uri, JsonNode body, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, uri);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);   // never in the URL
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(30));
        using var r = await http.Send(req, cts2.Token);
        var res = new Resp { Code = (int)r.StatusCode };
        lastCode = res.Code;
        var text = await r.Content.ReadAsStringAsync(cts2.Token);
        try { res.Body = text.Length > 0 ? J.Parse(text) : null; } catch { }
        OnHeaders(r);
        if (res.Code == 429)
        {
            res.Limited = true;
            Pause(r.Headers.RetryAfter?.Delta?.TotalSeconds ?? (r.Headers.RetryAfter?.Date is DateTimeOffset d ? (d - DateTimeOffset.UtcNow).TotalSeconds : null), "429");
        }
        else if (res.Code is >= 200 and < 300) backoff = 0;
        return res;
    }

    protected static double? Ts(JsonNode n)
    {
        if (J.Num(n) is double d && !J.IsStr(n)) return d > 1e12 ? d / 1000 : d;
        return DateTimeOffset.TryParse(J.Str(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() / 1000.0 : null;
    }

    protected void Try(Action a)
    {
        try { a(); } catch (Exception e) { Log.W($"{Id}: " + e.Message); }
    }
}
