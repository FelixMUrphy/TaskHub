using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>Watchdog: stale / lost detection, retention, periodic save.</summary>
sealed partial class Hub
{
    // ------------------------------------------------------------ watchdog
    int watchTicks;

    public async Task Watchdog(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { break; }
            if (watchTicks % 20 == 0) _ = Task.Run(RefreshHookOffer);   // first tick, then every minute: agents' files and PATH, which can be slow
            if (++watchTicks % 200 == 0)   // ~10 min: history older than history_days
                lock (Lock) if (Prune()) Bump();
            try
            {
                var lost = new List<(string tid, Internal gen)>();
                var stale = new List<JsonObject>();
                lock (Lock)
                {
                    var now = J.Now();
                    foreach (var (tid, t) in tasks.ToList())
                    {
                        if (!IsActive(St(t))) continue;
                        if (J.Num(t["pid"]) is double pd && pd > 0 && !Native.PidAlive((int)pd, In(tid).Ct))
                            lost.Add((tid, In(tid)));
                        else if (J.Num(t["heartbeat_timeout"]) is double hb && hb > 0 && St(t) == "running"
                                 && now - (J.Num(t["last_seen"]) ?? now) > hb)
                        {
                            t["state"] = "stale";
                            AddLog(t, "no update for " + FmtDur(now - (J.Num(t["last_seen"]) ?? now)), "warn");
                            stale.Add((JsonObject)t.DeepClone());
                            Bump();
                        }
                    }
                }
                foreach (var (tid, gen) in lost)
                    try { Finish(tid, new JsonObject { ["state"] = "lost", ["message"] = "process exited without reporting" }, gen); } catch { }
                foreach (var t in stale)
                    ToastFor(t, "⚠ " + J.Str(t["name"]), L.T("no_updates", FmtDur(J.Num(t["heartbeat_timeout"]) ?? 0)), "warn", "stale");
                Save();
            }
            catch (Exception e) { Log.E("hub", "watchdog error " + e); }
        }
        Save();
    }
}
