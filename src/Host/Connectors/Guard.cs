using System.Globalization;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Balance guard of a machine connector (opt-in, connectors.&lt;id&gt;.guard): a warning at guard_warn ($) with the balance,
/// the burn rate and the time left until guard_stop; at guard_stop a pending stop of the running machines (except the
/// ones in guard_exempt) with "Stop now" / "Snooze N min" (toast, Telegram, panel); no answer -> stopped when the grace
/// time runs out (never later than the projected $0, minus a margin). Stop only - never delete / terminate / destroy.
/// At the deadline the balance is read again: unreadable -> nothing is stopped ("held"), the user is told. A top-up
/// above the stop threshold cancels it. Every stop goes to guard_log.json (<see cref="GuardLog"/>).
/// State lives under the connector's <c>gate</c>; the pending stop is shown as the task "&lt;prefix&gt;--guard".
/// </summary>
abstract partial class MachineConnector
{
    protected sealed record GuardConf(bool On, double Warn, double Stop, double GraceMin, double SnoozeMin, HashSet<string> Exempt);

    public const double GuardWarnDefault = 10, GuardStopDefault = 2;

    protected GuardConf GCfg
    {
        get
        {
            var c = Connectors.Config(S, Id);
            double Get(string k, double def, double lo, double hi, double loMock) =>
                Math.Clamp(J.Num(c[k]) ?? def, Mock ? loMock : lo, hi);
            var ex = (c["guard_exempt"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Where(x => x.Length > 0).ToHashSet();
            return new GuardConf(J.Truthy(c["guard"]), J.Num(c["guard_warn"]) ?? GuardWarnDefault, J.Num(c["guard_stop"]) ?? GuardStopDefault,
                Get("guard_grace_min", 10, 1, 120, 0.02), Get("guard_snooze_min", 15, 5, 240, 0.02), ex);
        }
    }

    /// <summary>Where the balance comes from is on (Runpod: the GraphQL module).</summary>
    protected virtual bool BalanceSource => true;

    /// <summary>What a stopped machine still costs for its storage, $/day (≈, from the service's price list), and a note
    /// (e.g. a network volume billed as before); null = unknown.</summary>
    protected virtual (double? PerDay, string Note) StoppedStorage(Machine m) => (null, "");

    sealed class PendingStop
    {
        public int Seq;
        public double Created, Deadline;
        public double Balance, Burn;
        public bool Held, HeldWarned, Capped;
        public List<string> Ids = new();
    }

    PendingStop pending;
    int guardSeq;
    bool gWarned, gBelowWarned;          // warning / "below stop, can't or nothing to stop" sent for this crossing
    readonly HashSet<string> gTried = new();   // machines a stop was tried on during this crossing (not tried again)
    int gMisses;                          // balance readings missed in a row (guard on)
    bool gOutage;                         // the "balance unreadable" warning was sent
    string gMissWhy = "";
    double gLastMiss;
    readonly SemaphoreSlim stopRun = new(1, 1);

    string GuardTid => Prefix + "--guard";

    double Margin => Mock ? 0 : 300;   // the stop never lands later than this before the projected $0

    // ------------------------------------------------------------ settings
    static readonly (string Key, double Lo, double Hi, double LoMock)[] GuardNums =
    {
        ("guard_grace_min", 1, 120, 0.02), ("guard_snooze_min", 5, 240, 0.02),
    };

    /// <summary>guard, guard_warn, guard_stop, guard_grace_min, guard_snooze_min, guard_exempt; warn > stop >= 0. Applied to
    /// <paramref name="c"/> only when all of them are valid.</summary>
    void ConfigureGuard(JsonObject b, JsonObject c)
    {
        var n = (JsonObject)c.DeepClone();
        foreach (var (k, lo0, hi, loMock) in GuardNums)
        {
            if (!b.ContainsKey(k)) continue;
            double lo = Mock ? loMock : lo0;
            if (J.Num(b[k]) is not double d || d < lo || d > hi) throw new ApiError(400, $"{k} must be {J.Fmt(lo)}-{J.Fmt(hi)}");
            n[k] = d;
        }
        foreach (var k in new[] { "guard_warn", "guard_stop" })
        {
            if (!b.ContainsKey(k)) continue;
            if (b[k] == null || J.Str(b[k]) == "") n.Remove(k);
            else if (J.Num(b[k]) is double d && d >= 0 && d <= 1e6) n[k] = d;
            else throw new ApiError(400, $"{k} must be a number >= 0 (dollars)");
        }
        if (b.ContainsKey("guard")) n["guard"] = J.Truthy(b["guard"]);
        if (b.ContainsKey("guard_exempt"))
        {
            if (b["guard_exempt"] is not JsonArray ea || ea.Count > 500 || ea.Any(x => !J.IsStr(x) || J.Str(x).Length is 0 or > 100))
                throw new ApiError(400, "guard_exempt must be a list of machine ids");
            n["guard_exempt"] = J.StrArray(ea.Select(x => J.Str(x)).Distinct());
        }
        double warn = J.Num(n["guard_warn"]) ?? GuardWarnDefault, stop = J.Num(n["guard_stop"]) ?? GuardStopDefault;
        if (stop < 0) throw new ApiError(400, "guard_stop must be >= 0");
        if (warn <= stop) throw new ApiError(400, $"guard_warn ({J.Fmt(warn)}) must be above guard_stop ({J.Fmt(stop)})");
        foreach (var k in new[] { "guard", "guard_warn", "guard_stop", "guard_grace_min", "guard_snooze_min", "guard_exempt" })
            if (n.ContainsKey(k)) c[k] = J.C(n[k]); else c.Remove(k);
        bool rearm = b.ContainsKey("guard_warn") || b.ContainsKey("guard_stop") || b.ContainsKey("guard");
        if (rearm) lock (gate) { gWarned = false; gBelowWarned = false; gTried.Clear(); }
        // the guard (or the whole connector, or its key) off: a pending stop goes with it
        if ((b.ContainsKey("guard") && !J.Truthy(b["guard"])) || (b.ContainsKey("enabled") && !J.Truthy(b["enabled"])) || J.Truthy(b["forget_key"]))
            CancelPending(L.T("g_off"));
    }

    // ------------------------------------------------------------ numbers
    /// <summary>Hours until the balance reaches <paramref name="level"/> at the current burn; null when it isn't decreasing.</summary>
    double? HoursTo(double level) =>
        balance?.Amount is double a && SpendRate() is double r && r > 0 ? Math.Max(0, a - level) / r : null;

    /// <summary>When the balance is at $0 at the current burn (unix s), or null.</summary>
    double? ZeroAt() => HoursTo(0) is double h ? J.Now() + h * 3600 : null;

    static string When(double t)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime();
        var now = DateTimeOffset.Now;
        var fmt = d.Date == now.Date ? "HH:mm" : d - now < TimeSpan.FromDays(6) ? "ddd HH:mm" : "d MMM HH:mm";
        CultureInfo ci;
        try { ci = CultureInfo.GetCultureInfo(L.Lang); } catch { ci = CultureInfo.InvariantCulture; }
        return d.ToString(fmt, ci);
    }

    List<Machine> GuardTargets(GuardConf g)
    {
        lock (gate)
            return machines.Values.Select(t => t.M).Where(m => m.Active && !g.Exempt.Contains(m.Id) && !gTried.Contains(m.Id))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    (double? Sp, bool Est) SpentOf(Machine m)
    {
        if (spent != null && spent.TryGetValue(m.Id, out var s1)) return (s1, SpendLags);
        if (m.Spent is double s2) return (s2, m.SpentEstimated);
        if (m.Active && m.Price is double pr && (m.Uptime ?? (m.Started is double st0 ? J.Now() - st0 : null)) is double u0 && u0 > 0)
            return (pr * u0 / 3600, true);
        return (null, false);
    }

    void GuardNotify(string msg, string level = "warn", bool withButtons = false)
    {
        var b = new JsonObject
        {
            ["title"] = "TaskHub · " + Title, ["level"] = level, ["message"] = msg, ["force"] = true,   // not muted: it's about money
        };
        if (withButtons) b["task"] = GuardTid; else b["click"] = "dismiss";
        Try(() => hub.Notify(b));
    }

    // ------------------------------------------------------------ the balance was read (or not)
    /// <summary>After each balance reading: warning, pending stop, top-up. <paramref name="ok"/> false = the reading failed.</summary>
    void GuardOnBalance(bool ok, string why)
    {
        var g = GCfg;
        if (!g.On || !BalanceSource) { lock (gate) { gMisses = 0; gOutage = false; } return; }
        if (!ok) { GuardMiss(why); return; }
        bool recovered;
        lock (gate) { recovered = gOutage; gOutage = false; gMisses = 0; }
        if (recovered) GuardNotify(L.T("g_readable", BalanceText()), "info");
        if (balance?.Amount is not double a) return;
        double burn = SpendRate() ?? 0;
        double hw = Math.Max(Mock ? 0.01 : 0.5, g.Warn * 0.05), hs = Math.Max(Mock ? 0.01 : 0.25, g.Stop * 0.05);
        if (a > g.Warn + hw) lock (gate) gWarned = false;   // topped up: warn again next time
        if (a > g.Stop)
        {
            if (a > g.Stop + hs) lock (gate) { gBelowWarned = false; gTried.Clear(); }
            if (pending != null) CancelPending(L.T("g_topup", Money(a), Money(g.Stop)));
        }
        if (a <= g.Warn && a > g.Stop)
        {
            bool send;
            lock (gate) { send = !gWarned; gWarned = true; }
            if (send)
            {
                var msg = burn > 0 && HoursTo(g.Stop) is double h
                    ? L.T("g_warn", Money(a), Money(g.Warn), Money(burn), Hours(h), Money(g.Stop))
                    : L.T("g_warn_flat", Money(a), Money(g.Warn));
                if (canAct == false) msg += " " + L.T("g_readonly");
                GuardNotify(msg);
                Log.W($"{Id}: guard warning, balance {a}, burn {burn}");
            }
        }
        if (a <= g.Stop)
        {
            lock (gate) gWarned = true;   // no separate warning any more in this crossing
            if (pending != null || canAct == null) return;   // not probed yet: wait for the probe
            var targets = GuardTargets(g);
            if (canAct == false || targets.Count == 0)
            {
                bool send;
                lock (gate) { send = !gBelowWarned; gBelowWarned = true; }
                if (!send) return;
                GuardNotify(canAct == false ? L.T("g_below_ro", Money(a), Money(g.Stop)) + " " + L.T("g_readonly")
                    : L.T("g_nothing", Money(a), Money(g.Stop)));
                return;
            }
            StartPending(g, a, burn, targets);
        }
    }

    void GuardMiss(string why)
    {
        bool send;
        lock (gate)
        {
            gLastMiss = J.Now();
            gMisses++;
            gMissWhy = why ?? "";
            send = gMisses >= 2 && !gOutage;
            if (send) gOutage = true;
        }
        if (!send) return;
        GuardNotify(L.T("g_unreadable", gMissWhy.Length > 0 ? J.Cut(gMissWhy, 100) : "?"));
        Log.W($"{Id}: guard: balance unreadable ({gMissWhy})");
    }

    /// <summary>A poll failed altogether (exception in the loop): counts as a missed balance reading once per reading slot.</summary>
    void GuardMissFromError(string why)
    {
        if (!GCfg.On || J.Now() - gLastMiss < BalanceEvery(Cfg) * 0.9) return;
        GuardOnBalance(false, why);
    }

    /// <summary>The balance is read every billing_interval; while a stop is pending, every minute (a top-up cancels it).</summary>
    double BalanceEvery(Conf c) => pending != null ? Math.Min(c.BillingEvery, Mock ? 1 : 60) : c.BillingEvery;

    // ------------------------------------------------------------ pending stop
    void StartPending(GuardConf g, double a, double burn, List<Machine> targets)
    {
        var now = J.Now();
        var dl = now + g.GraceMin * 60;
        bool capped = false;
        if (ZeroAt() is double z && z - Margin < dl) { dl = Math.Max(now, z - Margin); capped = true; }
        PendingStop p;
        lock (gate)
        {
            if (pending != null) return;
            pending = p = new PendingStop
            {
                Seq = ++guardSeq, Created = now, Deadline = dl, Balance = a, Burn = burn, Capped = capped,
                Ids = targets.Select(m => m.Id).ToList(),
            };
        }
        var names = string.Join(", ", targets.Select(m => m.Name ?? m.Id));
        var text = L.T("g_pending", Money(a), Money(g.Stop), Money(burn), J.Cut(names, 600), When(dl));
        if (capped && ZeroAt() is double z2) text += " " + L.T("g_capped", When(z2));
        ShowPending(p, g, text, register: true);
        GuardNotify(text, "warn", withButtons: true);
        Log.W($"{Id}: guard: pending stop #{p.Seq} of {p.Ids.Count} machine(s) at {dl:0} (balance {a}, burn {burn})");
    }

    JsonArray GuardControls(PendingStop p, GuardConf g) => new(
        new JsonObject
        {
            ["id"] = "gstop." + p.Seq, ["label"] = L.T("g_stop_now"), ["kind"] = "connector", ["style"] = "danger", ["toast"] = true,
            ["states"] = new JsonArray("running", "paused", "stale"),
        },
        new JsonObject
        {
            ["id"] = "gsnooze." + p.Seq, ["label"] = L.T("g_snooze", J.Fmt(Math.Round(g.SnoozeMin, 2))), ["kind"] = "connector", ["toast"] = true,
            ["states"] = new JsonArray("running", "paused", "stale"),
        });

    string PendingShort(PendingStop p) =>
        p.Held ? L.T("g_held_short") : L.N("g_pending_short", p.Ids.Count, When(p.Deadline));

    /// <summary>The pending stop as a task: running while it waits, with Stop now / Snooze (panel, toasts, Telegram).</summary>
    void ShowPending(PendingStop p, GuardConf g, string log, bool register)
    {
        var tid = GuardTid;
        var status = PendingShort(p);
        if (register)
        {
            var b = new JsonObject
            {
                ["id"] = tid, ["name"] = L.T("g_task", Title), ["source"] = Id, ["project"] = Title,
                ["notify"] = new JsonObject { ["finish"] = false, ["fail"] = false, ["stale"] = false },
                ["meta"] = new JsonObject { [Id] = new JsonObject { ["guard"] = true } },
                ["controls"] = GuardControls(p, g), ["status"] = status, ["attention"] = status,
            };
            try { hub.Register(b, trusted: true); }
            catch (Exception e) { Log.W($"{Id}: guard task: " + e.Message); }
        }
        var u = new JsonObject { ["status"] = status, ["attention"] = status, ["controls"] = GuardControls(p, g) };
        if (!string.IsNullOrEmpty(log)) { u["log"] = log; u["level"] = "warn"; }
        Try(() => hub.Update(tid, u, trusted: true));
    }

    void CancelPending(string why)
    {
        PendingStop p;
        lock (gate) { p = pending; pending = null; }
        if (p == null) return;
        Try(() => hub.Update(GuardTid, new JsonObject { ["controls"] = new JsonArray(), ["log"] = J.Cut(why, 1900) }, trusted: true));
        Try(() => hub.Finish(GuardTid, new JsonObject { ["state"] = "done", ["message"] = J.Cut(why, 300) }));
        GuardNotify(why, "info");
        Log.W($"{Id}: guard: pending stop #{p.Seq} cancelled ({why})");
    }

    /// <summary>Each loop round: a pending stop whose time has come. The balance is read again first - unreadable: held.</summary>
    async Task GuardTick(string key, CancellationToken ct)
    {
        var p = pending;
        if (p == null || J.Now() < p.Deadline) return;
        var g = GCfg;
        if (!g.On) { CancelPending(L.T("g_off")); return; }
        Balance b = null;
        string why = "";
        lastCode = 0;
        try { b = await FetchBalance(key, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { why = e.GetBaseException().Message; }
        if (why.Length == 0 && lastCode is > 0 and not 200) why = "HTTP " + lastCode;
        if (b?.Amount is not double a)
        {
            bool warn;
            lock (gate) { warn = !p.HeldWarned; p.HeldWarned = true; p.Held = true; p.Deadline = J.Now() + (Mock ? 1 : 60); }
            if (warn)
            {
                ShowPending(p, g, L.T("g_held"), register: false);
                GuardNotify(L.T("g_held"), "warn", withButtons: true);
                Log.W($"{Id}: guard: balance unreadable at the deadline ({why}) - auto-stop held");
            }
            return;
        }
        balance = b; balanceAt = J.Now();
        if (a > g.Stop) { CancelPending(L.T("g_topup", Money(a), Money(g.Stop))); return; }
        await RunStop(p, "auto", key, ct);
    }

    // ------------------------------------------------------------ buttons
    /// <summary>Stop now / Snooze on the guard task (panel, toast, Telegram) or from the settings page.</summary>
    JsonObject GuardControl(string cid)
    {
        var parts = cid.Split('.', 2);
        PendingStop p;
        lock (gate) p = pending;
        if (p == null || parts.Length != 2 || parts[1] != p.Seq.ToString(CultureInfo.InvariantCulture))
            throw new ApiError(409, L.T("g_handled")) { Stale = true };
        return GuardAct(parts[0] == "gstop" ? "stop_now" : parts[0] == "gsnooze" ? "snooze" : throw new ApiError(404, $"unknown control '{cid}'"), p.Seq);
    }

    /// <summary>"stop_now" / "snooze" for the current pending stop (seq 0 = whichever is pending).</summary>
    public JsonObject GuardAct(string act, int seq = 0)
    {
        PendingStop p;
        lock (gate) p = pending;
        if (p == null || (seq != 0 && p.Seq != seq)) throw new ApiError(409, L.T("g_handled")) { Stale = true };
        var g = GCfg;
        if (act == "stop_now")
        {
            var key = Secrets.Read(S, Id);
            if (string.IsNullOrEmpty(key)) throw new ApiError(400, $"no {Title} API key");
            _ = Task.Run(() => RunStop(p, "stop_now", key, CancellationToken.None));   // the user asked: acts even if the balance can't be read
            return new JsonObject { ["ok"] = true, ["message"] = L.T("g_stopping") };
        }
        if (act != "snooze") throw new ApiError(400, "act must be stop_now or snooze");
        var now = J.Now();
        string text;
        lock (gate)
        {
            if (pending != p) throw new ApiError(409, L.T("g_handled")) { Stale = true };
            var dl = Math.Max(now, p.Deadline) + g.SnoozeMin * 60;
            p.Capped = false;
            if (ZeroAt() is double z && z - Margin < dl) { dl = Math.Max(now, z - Margin); p.Capped = true; }
            p.Deadline = dl;
            text = L.T("g_snoozed", When(dl)) + (p.Capped && ZeroAt() is double z2 ? " " + L.T("g_capped", When(z2)) : "");
        }
        ShowPending(p, g, text, register: false);
        GuardNotify(text, "info");
        Log.W($"{Id}: guard: pending stop #{p.Seq} snoozed to {p.Deadline:0}");
        return new JsonObject { ["ok"] = true, ["message"] = text, ["deadline"] = p.Deadline };
    }

    // ------------------------------------------------------------ the stop
    /// <summary>Stops the pending machines (never deletes), confirms each by polling the list, logs every machine, says what
    /// was stopped and what its storage costs while stopped. Failures are reported per machine.</summary>
    async Task RunStop(PendingStop p, string trigger, string key, CancellationToken ct)
    {
        lock (gate) { if (pending != p) return; pending = null; }
        await stopRun.WaitAsync(CancellationToken.None);
        try
        {
            var g = GCfg;
            List<Machine> ms;
            lock (gate)
            {
                ms = p.Ids.Select(id => machines.TryGetValue(id, out var t) ? t.M : null)
                    .Where(m => m != null && m.Active && !g.Exempt.Contains(m.Id)).ToList();
                foreach (var m in ms) gTried.Add(m.Id);
            }
            double? bal = balance?.Amount, burn = SpendRate();
            var results = new List<(Machine M, string Error)>();
            var sent = new List<Machine>();
            foreach (var m in ms)
            {
                string err = null;
                try
                {
                    var r = await Act(m, "stop", key, CancellationToken.None);
                    if (r.Code is not (200 or 201 or 202 or 204))
                    {
                        if (r.Code == 403 && !r.Limited) canAct = false;
                        err = ActionError(r, "stop") ?? r.Code switch
                        {
                            403 => L.T("m_err_403"), 409 => L.T("m_err_409"), 404 => L.T("m_err_404"), 429 => L.T("m_err_429"),
                            _ => $"HTTP {r.Code}" + (ErrText(r) is { Length: > 0 } et ? " · " + et : ""),
                        };
                    }
                }
                catch (Exception e) { err = e.GetBaseException().Message; }
                if (err == null) sent.Add(m); else results.Add((m, err));
                var tid = Tid(m.Id);
                Try(() => hub.Update(tid, new JsonObject
                {
                    ["log"] = err == null ? "stop (" + L.T("g_task", Title) + "): " + L.T("m_sent") : "stop: " + err, ["level"] = err == null ? "warn" : "error",
                }, trusted: true));
            }
            // confirm: the list shows them stopped (Runpod EXITED, Vast.ai not running)
            var left = sent.ToDictionary(m => m.Id);
            var until = J.Now() + (Mock ? 15 : 180);
            var lastState = new Dictionary<string, string>();
            while (left.Count > 0 && J.Now() < until)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Mock ? 0.5 : 5), CancellationToken.None); } catch { }
                List<Machine> list = null;
                try { list = await List(key, CancellationToken.None); } catch (Exception e) { Log.W($"{Id}: guard confirm: " + e.GetBaseException().Message); }
                if (list == null) continue;
                foreach (var id in left.Keys.ToList())
                {
                    var now = list.FirstOrDefault(x => x.Id == id);
                    if (now == null || !now.Active) { results.Add((left[id], null)); left.Remove(id); }
                    else lastState[id] = now.RawStatus.Length > 0 ? now.RawStatus : now.State;
                }
            }
            foreach (var m in left.Values) results.Add((m, L.T("g_not_confirmed", lastState.GetValueOrDefault(m.Id, m.State))));
            nextList = 0;   // the tasks follow at once
            // the log and the summary
            var entries = new List<JsonObject>();
            var lines = new List<string>();
            int okN = 0, failN = 0;
            foreach (var (m, err) in results.OrderBy(x => x.M.Name, StringComparer.OrdinalIgnoreCase))
            {
                var (sp, est) = SpentOf(m);
                var (perDay, note) = err == null ? StoppedStorage(m) : (null, "");
                entries.Add(new JsonObject
                {
                    ["t"] = J.Now(), ["provider"] = Id, ["machine_id"] = m.Id, ["name"] = m.Name ?? m.Id, ["trigger"] = trigger,
                    ["balance"] = bal, ["threshold"] = g.Stop, ["burn_per_hr"] = burn,
                    ["spent"] = sp is double s ? Math.Round(s, 4) : null, ["spent_estimated"] = est,
                    ["result"] = err == null ? "stopped" : "failed", ["error"] = err,
                    ["storage_per_day"] = perDay is double pd ? Math.Round(pd, 4) : null, ["storage_note"] = note.Length > 0 ? note : null,
                    ["storage_approx"] = true,
                });
                if (err == null) okN++; else failN++;
                lines.Add("• " + (m.Name ?? m.Id) + " — " + (err == null ? L.T("m_stopped") + " · " + StorageText(perDay, note) : L.T("g_res_failed", err)));
            }
            GuardLog.Add(S, entries);
            var head = L.T("g_done", okN, failN);
            var text = head + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
            Try(() => hub.Update(GuardTid, new JsonObject
            {
                ["log"] = J.Cut(text, 1900), ["level"] = failN > 0 ? "error" : "info", ["controls"] = new JsonArray(),
            }, trusted: true));
            Try(() => hub.Finish(GuardTid, new JsonObject { ["state"] = failN > 0 ? "failed" : "done", ["message"] = J.Cut(head, 300) }));
            GuardNotify(text, failN > 0 ? "error" : "warn");
            Log.W($"{Id}: guard: stop #{p.Seq} ({trigger}): {okN} stopped, {failN} failed");
        }
        finally { stopRun.Release(); }
    }

    string StorageText(double? perDay, string note) =>
        (perDay is double d ? L.T("g_storage", Money(d), Title) : L.T("g_storage_unknown")) + (note.Length > 0 ? " " + note : "");

    string BalanceText() => balance?.Amount is double a ? (balance.Label.Length > 0 ? balance.Label + " " : "") + Money(a) : "—";

    // ------------------------------------------------------------ state
    JsonObject GuardState()
    {
        var g = GCfg;
        PendingStop p;
        bool outage;
        lock (gate) { p = pending; outage = gOutage; }
        var h = HoursTo(g.Stop);
        return new JsonObject
        {
            ["enabled"] = g.On, ["warn"] = g.Warn, ["stop"] = g.Stop, ["grace_min"] = g.GraceMin, ["snooze_min"] = g.SnoozeMin,
            ["exempt"] = J.StrArray(g.Exempt.OrderBy(x => x)), ["can_act"] = canAct, ["balance_source"] = BalanceSource,
            ["burn_per_hr"] = SpendRate(), ["hours_to_stop"] = h is double hh ? Math.Round(hh, 3) : null,
            ["stop_at"] = h is double h2 && balance?.Amount > g.Stop ? Math.Round(J.Now() + h2 * 3600, 1) : null,
            ["zero_at"] = ZeroAt() is double z ? Math.Round(z, 1) : null,
            ["unreadable"] = outage, ["warned"] = gWarned,
            ["pending"] = p == null ? null : new JsonObject
            {
                ["seq"] = p.Seq, ["deadline"] = Math.Round(p.Deadline, 2), ["held"] = p.Held, ["capped"] = p.Capped,
                ["machines"] = J.StrArray(p.Ids), ["task"] = GuardTid,
            },
        };
    }

    /// <summary>The guard part of the settings page's row: values, the forecast / pending line, machines with their "never
    /// auto-stop" switch, a note when the guard can only warn, the last log entries - all texts in the UI language.</summary>
    public JsonObject GuardRow()
    {
        var g = GCfg;
        PendingStop p;
        bool outage;
        lock (gate) { p = pending; outage = gOutage; }
        string note = !BalanceSource || (balance == null && keyCheck == "ok" && probedAt > 0 && J.Now() - probedAt > 120) ? L.T("g_no_balance")
            : outage ? L.T("g_unreadable", J.Cut(gMissWhy, 100))
            : canAct == false ? L.T("g_readonly") : null;
        string forecast = "";
        if (balance?.Amount is double a)
        {
            if (a <= g.Stop) forecast = L.T("g_forecast_below", Money(g.Stop));
            else if (SpendRate() is double r && r > 0 && HoursTo(g.Stop) is double h)
                forecast = L.T("g_forecast", Money(r), Money(g.Stop), When(J.Now() + h * 3600));
            else forecast = L.T("g_forecast_flat");
        }
        var ms = new JsonArray();
        lock (gate)
            foreach (var t in machines.Values.Where(t => t.M.State != "terminated").OrderBy(t => t.M.Name, StringComparer.OrdinalIgnoreCase))
                ms.Add(new JsonObject { ["id"] = t.M.Id, ["name"] = t.M.Name ?? t.M.Id, ["active"] = t.M.Active, ["exempt"] = g.Exempt.Contains(t.M.Id) });
        var log = new JsonArray();
        foreach (var e in GuardLog.Read(S, Id, 5)) log.Add((JsonNode)JsonValue.Create(GuardLog.Line(e)));
        return new JsonObject
        {
            ["enabled"] = g.On, ["warn"] = g.Warn, ["stop"] = g.Stop, ["grace_min"] = g.GraceMin, ["snooze_min"] = g.SnoozeMin,
            ["note"] = note, ["forecast"] = forecast, ["machines"] = ms, ["log"] = log, ["exempt"] = J.StrArray(g.Exempt.OrderBy(x => x)),
            ["pending"] = p == null ? null : new JsonObject
            {
                ["text"] = PendingShort(p), ["stop_label"] = L.T("g_stop_now"), ["snooze_label"] = L.T("g_snooze", J.Fmt(Math.Round(g.SnoozeMin, 2))),
            },
        };
    }

    /// <summary>After a restart: a pending stop shown by the hub is gone with the old process - say so, drop its buttons.</summary>
    void AdoptGuard()
    {
        var t = TryTask(GuardTid);
        if (t == null || !Hub.IsActive(J.Str(t["state"]))) return;
        lock (gate) if (pending != null) return;
        Try(() => hub.Update(GuardTid, new JsonObject { ["controls"] = new JsonArray() }, trusted: true));
        Try(() => hub.Finish(GuardTid, new JsonObject { ["state"] = "cancelled", ["message"] = L.T("g_restart") }));
    }
}

/// <summary>guard_log.json in the data folder: what the balance guard stopped (newest last, at most 200 entries), written
/// atomically (.tmp + move). Read by GET /api/guard/log and the settings page; deleted with the data folder.</summary>
static class GuardLog
{
    const int Max = 200;
    static readonly object L_ = new();

    static string PathOf(Settings s) => s.File_("guard_log.json");

    static JsonArray Load(Settings s)
    {
        try
        {
            var p = PathOf(s);
            if (File.Exists(p) && J.Parse(File.ReadAllText(p)) is JsonObject o && o["entries"] is JsonArray a)
            {
                o.Remove("entries");   // detached: it goes into a new object when saved
                return a;
            }
        }
        catch (Exception e) { Log.W("guard_log.json: " + e.Message); }
        return new JsonArray();
    }

    public static void Add(Settings s, IEnumerable<JsonObject> entries)
    {
        lock (L_)
        {
            var a = Load(s);
            foreach (var e in entries) a.Add(e);
            while (a.Count > Max) a.RemoveAt(0);
            try
            {
                var p = PathOf(s);
                File.WriteAllText(p + ".tmp", new JsonObject { ["entries"] = a }.ToJsonString(J.Pretty));
                File.Move(p + ".tmp", p, true);
            }
            catch (Exception e) { Log.W("guard_log.json not saved: " + e.Message); }
        }
    }

    /// <summary>Newest first; provider null = all.</summary>
    public static List<JsonObject> Read(Settings s, string provider, int limit)
    {
        lock (L_)
            return Load(s).OfType<JsonObject>().Where(e => provider == null || J.Str(e["provider"]) == provider)
                .Reverse().Take(Math.Clamp(limit, 1, Max)).Select(e => (JsonObject)e.DeepClone()).ToList();
    }

    /// <summary>One entry in words (UI language): when, which machine, the result, why, spent, storage while stopped.</summary>
    public static string Line(JsonObject e)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds((long)((J.Num(e["t"]) ?? 0) * 1000)).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        static string M(double? v) => v is double d ? "$" + d.ToString(d < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) : "?";
        var parts = new List<string> { t, J.Str(e["name"]) };
        parts.Add(J.Str(e["result"]) == "stopped" ? L.T("m_stopped") : L.T("g_res_failed", J.Str(e["error"])));
        parts.Add(J.Str(e["trigger"]) == "stop_now" ? L.T("g_stop_now")
            : L.T("g_why", M(J.Num(e["balance"])), M(J.Num(e["threshold"])), M(J.Num(e["burn_per_hr"]))));
        if (J.Num(e["spent"]) is double sp) parts.Add(L.T("m_spent") + " " + (J.Truthy(e["spent_estimated"]) ? "≈ " : "") + M(sp));
        if (J.Str(e["result"]) == "stopped")
            parts.Add(J.Num(e["storage_per_day"]) is double pd ? L.T("g_storage", M(pd), J.Str(e["provider"]) == "vastai" ? "Vast.ai" : "Runpod")
                : L.T("g_storage_unknown"));
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }
}
