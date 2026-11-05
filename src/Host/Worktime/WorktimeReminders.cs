using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TaskHub;

/// <summary>The work hours reminders in the hub: a task with the reminder's buttons (panel, toast, Telegram, Discord), one at a time, gone once
/// answered or no longer due; a notice without buttons is only a notification. At the PC (input within 5 minutes, session not locked) it comes as
/// a toast, otherwise to the messengers when the module's setting allows it. Registered as a connector only so the buttons come back here; it has
/// no settings of its own and isn't listed with the connectors.</summary>
sealed class WorktimeReminders : IConnector
{
    public const string Source = "worktime";
    public const string Tid = "worktime-reminder";
    readonly Worktime work;
    readonly Hub hub;
    readonly object gate = new();
    System.Threading.Timer timer;
    volatile bool locked;
    bool listening;
    readonly Dictionary<DateOnly, long> away = new();   // a day's last input before a lock or sleep
    bool mine;   // the task on the hub is the one registered here (an API client may have taken the id first)

    public WorktimeReminders(Worktime work, Hub hub)
    {
        this.work = work;
        this.hub = hub;
        work.Remind = Show;
    }

    public string Id => Source;

    public void Start()
    {
        // a reminder kept from before a restart: what it asked is checked again, its buttons would only be refused
        Drop();
        lock (gate)
        {
            if (!listening)
                try { SystemEvents.SessionSwitch += OnSwitch; SystemEvents.PowerModeChanged += OnPower; listening = true; }
                catch (Exception e) { Log.W("worktime", "session lock events: " + e.Message); }   // then only the idle time says "away"
            // a test hub checks only when its test route asks: the real clock and this PC's input would make it say things at random
            if (timer == null && Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is not { Length: > 0 })
                timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
            if (listening) { SystemEvents.SessionSwitch -= OnSwitch; SystemEvents.PowerModeChanged -= OnPower; listening = false; }
        }
    }

    void OnSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; Away(); }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
    }

    void OnPower(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) Away(); }

    /// <summary>The PC is left: the last input so far is when work stopped that day, whatever input wakes it later.</summary>
    void Away()
    {
        var last = Worktime.Clock - (long)TelegramBridge.IdleSeconds();
        var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime);
        lock (away)
        {
            away[day] = Math.Max(away.GetValueOrDefault(day), last);
            foreach (var d in away.Keys.Where(d => d < day.AddDays(-7)).ToList()) away.Remove(d);
        }
    }

    void Drop() { mine = false; try { hub.Delete(Tid, true); } catch (ApiError) { } }   // already gone

    long? AwayOn(DateOnly d) { lock (away) return away.TryGetValue(d, out var v) ? v : null; }

    void Tick()
    {
        try { work.RemindTick(Worktime.Clock, TelegramBridge.IdleSeconds(), locked, AwayOn); }
        catch (Exception e) { Log.W("worktime", "reminders: " + e.Message); }
    }

    public JsonObject State() => new() { ["enabled"] = work.Enabled };

    public JsonObject OnControl(string tid, string cid, JsonNode value, string via)
    {
        if (tid != Tid) throw new ApiError(404, "not a work hours reminder");
        var r = work.Answer(cid);
        Show(null);
        return new JsonObject { ["ok"] = true, ["status"] = r };
    }

    void Show(Worktime.Reminder r)
    {
        if (r == null) { Drop(); return; }
        var only = r.AtPc || !r.Messengers ? "toast" : "messengers";
        if (r.Buttons.Count == 0) { hub.Remind(r.Title, r.Message, null, only); return; }
        var controls = new JsonArray(r.Buttons.Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.Key, ["label"] = b.Value, ["kind"] = "connector", ["toast"] = true, ["style"] = b.Key.StartsWith("finish_now.") || b.Key.StartsWith("start_now.") ? "primary" : "default",
            ["states"] = new JsonArray("running", "paused", "stale"),
        }).ToArray());
        var u = new JsonObject { ["name"] = r.Title, ["status"] = r.Message, ["attention"] = r.Message, ["controls"] = controls };
        bool updated = false;
        if (mine)
            try { hub.Update(Tid, u, trusted: true); updated = true; }
            catch (ApiError) { }   // removed from the panel meanwhile
        if (!updated)
        {
            Drop();   // whatever else holds the id is not this reminder: its links, buttons or source must not stay on it
            var b = (JsonObject)u.DeepClone();
            b["id"] = Tid;
            b["source"] = Source;
            b["project"] = L.T("wt_r_need_t");
            b["notify"] = new JsonObject { ["finish"] = false, ["fail"] = false, ["stale"] = false };
            b["meta"] = new JsonObject { [Source] = new JsonObject { ["reminder"] = r.Kind } };
            hub.Register(b, trusted: true);
            mine = true;
        }
        hub.Remind(r.Title, r.Message, Tid, only);
    }
}
