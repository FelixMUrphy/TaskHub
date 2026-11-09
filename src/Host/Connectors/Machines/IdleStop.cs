using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Auto-stop on GPU idle (opt-in, connectors.&lt;id&gt;.idle_stop_minutes, 0 = off): a running machine whose GPUs all stay
/// under idle_util for that long gets a pending stop - the balance guard's: the same task, Stop now / Snooze (toast,
/// Telegram, panel), the grace time (guard_grace_min), "never auto-stop" (guard_exempt) and the log (trigger "idle").
/// The GPU busy again before the deadline cancels it; at the deadline the machine must still be idle - seen idle by a load
/// sample taken after the stop was announced and recent, and for at least the current idle_stop_minutes. Stop only - never
/// delete. Works with the balance guard off; a balance stop takes the place of a pending idle one. Only with a key that
/// may run actions. Not kept over a restart (the idle time is counted again).
/// </summary>
abstract partial class MachineConnector
{
    /// <summary>An idle stop is pending for this machine.</summary>
    bool IdlePendingFor(string id)
    {
        lock (gate) return pending is { Idle: true } p && p.Ids.Contains(id);
    }

    /// <summary>One load sample of a running machine (after the idle warning): starts or cancels its idle stop.</summary>
    void IdleStopSample(Track t, Conf c, bool idle, double now)
    {
        var m = t.M;
        if (!idle)
        {
            t.IdleStopAsked = false;
            if (IdlePendingFor(m.Id)) CancelPending(_ => L.T("g_idle_busy", m.Name ?? m.Id), p => p.Idle && p.Ids.Contains(m.Id));
            return;
        }
        if (c.IdleStopMinutes <= 0 || t.IdleStopAsked || t.IdleSince is not double since || now - since < c.IdleStopMinutes * 60) return;
        if (canAct != true || GCfg.Exempt.Contains(m.Id)) return;   // can't stop / never auto-stop: the idle warning is all
        if (IdleStopNeedsBusySeen && !t.GpuBusySeen)
        {
            if (t.IdleUnprovenSaid) return;
            t.IdleUnprovenSaid = true;
            Try(() => hub.Update(Tid(m.Id), new JsonObject { ["log"] = L.T("g_idle_unproven"), ["level"] = "warn" }, trusted: true));
            return;
        }
        StartIdlePending(t, c);
    }

    void StartIdlePending(Track t, Conf c)
    {
        var g = GCfg;
        var m = t.M;
        var now = J.Now();
        PendingStop p;
        lock (gate)
        {
            if (pending != null || gStopping) return;   // one pending stop at a time: tried again at the next sample
            t.IdleStopAsked = true;
            pending = p = new PendingStop
            {
                Idle = true, Seq = ++guardSeq, Created = now, Deadline = now + g.GraceMin * 60,
                Ids = new List<string> { m.Id }, Names = new Dictionary<string, string> { [m.Id] = m.Name ?? m.Id },
            };
        }
        SaveGuard();   // the counter only: an idle stop isn't kept over a restart
        var text = L.T("g_idle_pending", m.Name ?? m.Id, Math.Max(1, (int)Math.Round(c.IdleStopMinutes)), When(p.Deadline));
        ShowPending(p, g, text, register: true);
        GuardNotify(text, "warn", withButtons: true);
        Log.I(Id, $"idle stop #{p.Seq} pending for {m.Id} at {p.Deadline:0}");
    }

    /// <summary>The deadline of a pending idle stop: stopped if the machine still runs idle, else cancelled.</summary>
    async Task IdleTick(PendingStop p, int ver, string key, CancellationToken ct)
    {
        var c = Cfg;
        var now = J.Now();
        bool runs, stillIdle, seen, longEnough;
        string name;
        lock (gate)
        {
            var t = p.Ids.Count == 1 && machines.TryGetValue(p.Ids[0], out var tt) ? tt : null;
            runs = t != null && t.M.Active;
            stillIdle = runs && t.IdleSince != null;
            // a fresh idle sample: after the announcement and not older than a few detail rounds (a failed read keeps the old one)
            seen = stillIdle && t.IdleSeenAt is double at && at >= Math.Max(p.Created, now - 3 * c.DetailEvery - 30);
            longEnough = stillIdle && now - t.IdleSince.Value >= c.IdleStopMinutes * 60;
            name = t?.M.Name ?? p.Names.GetValueOrDefault(p.Ids.FirstOrDefault() ?? "", "?");
        }
        if (c.IdleStopMinutes <= 0) { CancelPending(_ => L.T("g_idle_off"), x => x == p); return; }
        if (!runs) { CancelPending(_ => L.T("g_idle_gone", name), x => x == p); return; }
        if (!stillIdle) { CancelPending(_ => L.T("g_idle_busy", name), x => x == p); return; }
        if (!seen) { CancelPending(_ => L.T("g_idle_unknown", name), x => x == p); return; }
        if (!longEnough) { CancelPending(_ => L.T("g_idle_changed", name), x => x == p); return; }
        await RunStop(p, "idle", key, ct, ver);
    }
}
