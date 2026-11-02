using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// Plan limits of the AI agents (opt-in): the last reading of each limit window as the agent reported it, for the panel, and a
/// warning when a threshold is crossed (config limits_warn, default 80 and 95), once per limit window. On or off is what the agent's
/// settings file says right now (the user or a script may change it while the hub runs), not a copy of it. Several sessions report at
/// once and an idle one repeats what it was told long ago, so resets_at decides: later is a new window, earlier or already past is old
/// news, the same is this window, where the highest percentage stands. Only without resets_at does a falling percentage mean a reset.
/// Kept in plan_limits.json: percentages, reset times, when they were read and which warnings were given; nothing else. Off deletes them.
/// </summary>
partial class Hub
{
    public const string LimitsFile = "plan_limits.json";
    const double ResetSlackSec = 300, FallPoints = 1;   // resets_at or a rounded percentage may wobble a little within one window
    const double SameAgainSec = 60;                     // the same numbers again: no write, no redraw (several sessions, refreshInterval)
    const double MaxEpoch = 253402300799;               // the last second DateTimeOffset can show
    static readonly string[] LimitWindows = { "five_hour", "seven_day" };
    static readonly double[] LimitWarnDefault = { 80, 95 };
    readonly object limitsSave = new();
    JsonObject limits;
    DateTime limitsStamp;
    bool limitsKnown, limitsOn = true;   // until the file can be read for the first time: readings stay, they may be fine

    static double? NumOnly(JsonNode n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>TaskHub's status line is in Claude Code's settings now. The file is read again only when it changed; while it can't be
    /// read (being edited, broken) the last answer stands. Under Lock.</summary>
    bool LimitsOn()
    {
        var p = AgentFiles.ClaudeHooks;
        var stamp = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default;
        if (limitsKnown && stamp == limitsStamp) return limitsOn;
        try
        {
            limitsOn = stamp != default && AgentFiles.IsOurStatusLine(AgentFiles.LoadJson(p)["statusLine"] as JsonObject);
            limitsStamp = stamp;
            limitsKnown = true;
        }
        catch (Exception e) { Log.W("plan limits: " + p + ": " + e.Message); }
        return limitsOn;
    }

    JsonObject LimitsStore()   // under Lock
    {
        if (limits != null) return limits;
        limits = new JsonObject();
        try
        {
            var p = S.File_(LimitsFile);
            if (File.Exists(p) && JsonNode.Parse(File.ReadAllText(p))?["claude"] is JsonObject c) limits["claude"] = c.DeepClone();
        }
        catch (Exception e) { Log.W("plan_limits.json unreadable: " + e.Message); }
        return limits;
    }

    /// <summary>What the panel and GET /api/limits show: {agent: {window: {used_percentage, resets_at, at}}}. Off: nothing, and nothing kept.</summary>
    public JsonObject LimitsView()
    {
        lock (Lock)
        {
            var r = new JsonObject();
            var store = LimitsStore();
            if (store.Count > 0 && !LimitsOn())
            {
                store.Clear();
                SaveLimits("{}");
            }
            foreach (var (agent, v) in store)
            {
                var a = new JsonObject();
                foreach (var (w, x) in (JsonObject)v)
                    a[w] = new JsonObject
                    {
                        ["used_percentage"] = x?["pct"]?.DeepClone(), ["resets_at"] = x?["resets_at"]?.DeepClone(), ["at"] = x?["at"]?.DeepClone(),
                    };
                r[agent] = a;
            }
            return r;
        }
    }

    double[] LimitThresholds() => S.Get("limits_warn") is JsonArray a
        ? a.Select(NumOnly).Where(x => x is > 0 and <= 100).Select(x => x.Value).Distinct().OrderBy(x => x).ToArray()
        : LimitWarnDefault;

    /// <summary>A reading from `taskhub statusline`. Ignored while the switch is off, so off means nothing is kept.</summary>
    public JsonObject ReportLimits(string agent, JsonObject b)
    {
        var notes = new List<JsonObject>();
        var now = J.Now();
        var warnAt = LimitThresholds();
        string text = null;
        lock (Lock)
        {
            if (!LimitsOn()) return new JsonObject { ["ok"] = true, ["ignored"] = true };
            var store = LimitsStore();
            var a = store[agent] as JsonObject ?? new JsonObject();
            bool any = false, changed = false;
            foreach (var w in LimitWindows)
            {
                if (b[w] is not JsonObject x || NumOnly(x["used_percentage"]) is not double raw) continue;
                any = true;
                var pct = Math.Clamp(raw, 0, 100);
                var resets = NumOnly(x["resets_at"]) is double rv && rv > 0 && rv <= MaxEpoch ? rv : (double?)null;
                if (resets <= now) continue;   // a session that hasn't heard of the reset yet
                var prev = a[w] as JsonObject;
                var prevPct = NumOnly(prev?["pct"]);
                var prevResets = NumOnly(prev?["resets_at"]);
                bool fresh = prev == null || prevResets <= now;
                if (!fresh && resets != null && prevResets != null)
                {
                    if (resets < prevResets - ResetSlackSec) continue;   // a session still in an earlier window
                    fresh = resets > prevResets + ResetSlackSec;
                }
                else if (!fresh) fresh = pct < prevPct - FallPoints;
                if (!fresh) pct = Math.Max(pct, prevPct ?? 0);
                if (!fresh && pct == prevPct && resets == prevResets && now - (NumOnly(prev["at"]) ?? 0) < SameAgainSec) continue;
                var warned = fresh ? new List<double>() : (prev["warned"] as JsonArray ?? new JsonArray()).Select(NumOnly).Where(n => n != null).Select(n => n.Value).ToList();
                var crossed = warnAt.Where(t => pct >= t && !warned.Contains(t)).ToList();
                if (crossed.Count > 0)
                {
                    warned.AddRange(crossed);
                    notes.Add(LimitWarning(agent, w, pct, resets, crossed.Max()));
                }
                a[w] = new JsonObject
                {
                    ["pct"] = pct, ["resets_at"] = resets, ["at"] = now,
                    ["warned"] = new JsonArray(warned.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
                };
                changed = true;
            }
            if (!any) throw new ApiError(400, "no limit window in the reading");
            if (changed)
            {
                store[agent] = a;
                Bump();
                text = store.ToJsonString();
            }
        }
        if (text != null) SaveLimits(text);
        foreach (var n in notes) Notify(n);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>The switch went off: forget the readings.</summary>
    public void ClearLimits(string agent)
    {
        string text;
        lock (Lock)
        {
            if (!LimitsStore().Remove(agent)) return;
            Bump();
            text = limits.ToJsonString();
        }
        SaveLimits(text);
    }

    void SaveLimits(string text)
    {
        try
        {
            lock (limitsSave)
            {
                var p = S.File_(LimitsFile);
                File.WriteAllText(p + ".tmp", text);
                File.Move(p + ".tmp", p, true);
            }
        }
        catch (Exception e) { Log.W("plan_limits.json: " + e.Message); }
    }

    static JsonObject LimitWarning(string agent, string window, double pct, double? resets, double threshold) => new()
    {
        ["title"] = L.T("limit_title", agent == "claude" ? "Claude Code" : "Codex"),
        ["message"] = L.T("limit_msg", L.T(window == "five_hour" ? "limit_5h" : "limit_7d"), Math.Floor(pct).ToString(CultureInfo.InvariantCulture), resets is double r ? ResetText(r) : "—"),
        ["level"] = threshold >= 95 ? "error" : "warn",
    };

    /// <summary>A reset time in the UI language's format: the time if it is today, else the weekday too.</summary>
    static string ResetText(double epoch)
    {
        CultureInfo c;
        try { c = CultureInfo.GetCultureInfo(L.Lang); } catch { c = CultureInfo.InvariantCulture; }
        var t = DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime();
        return t.ToString(t.Date == DateTime.Now.Date ? "t" : "ddd t", c);
    }
}
