using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TaskHub;

/// <summary>The work-hours module: sessions and intervals, what the API may change, and the state worked out of them. Off until it's turned on;
/// while off it has no file. A session belongs to the local date it started on. Nothing here closes a session by itself, only the user does.</summary>
sealed partial class Worktime
{
    const string EnabledKey = "worktime_enabled";
    const long MaxPairSec = 48 * 3600, MaxTotalSec = 24 * 3600, MaxOpeningSec = 100_000 * 3600L;
    const int MaxDays = 366, MaxNote = 500, MaxIntervalsAtOnce = 100;
    static readonly Regex DateRx = new(@"^\d{4}-\d{2}-\d{2}$"), TimeRx = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$"), CurrencyRx = new("^[A-Z]{3}$");

    readonly Settings S;
    readonly Action changed;
    readonly object gate = new();
    WorktimeStore store;
    long mark;

    /// <param name="changed">Called after every change: the panel's long poll wakes up. It may run under this class's lock, so it must not call back in.</param>
    public Worktime(Settings s, Action changed = null) { S = s; this.changed = changed; StartRateTimer(); }

    /// <summary>For the task snapshot: null while off, otherwise a number that changes with every change. The numbers themselves stay out of the
    /// snapshot, which is sent all the time, and are asked for with /api/worktime/status.</summary>
    public long? Mark => Enabled ? Interlocked.Read(ref mark) : null;

    void Changed()
    {
        Interlocked.Increment(ref mark);
        try { changed?.Invoke(); } catch (Exception e) { Log.W("worktime", "changed: " + e.Message); }
    }

    public bool Enabled => S.Bool(EnabledKey);
    WorktimeStore Store => store ??= new WorktimeStore(S.File_("worktime.db"));

    /// <summary>Isolated test hubs only: moves this module's clock, so sums that depend on the month don't depend on the day a test runs.</summary>
    static readonly long ClockShift = Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 }
        && long.TryParse(Environment.GetEnvironmentVariable("TASKHUB_TEST_WORKTIME_SHIFT_DAYS"), out var days) ? days * 86400 : 0;

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ClockShift;
    static DateOnly Today() => LocalDate(Now());
    static DateOnly LocalDate(long utc) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(utc).LocalDateTime);
    static string Iso(long? utc) => utc is long u ? DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : null;

    void On() { if (!Enabled) throw new ApiError(409, "the work hours module is off"); }

    static void Only(JsonObject b, params string[] names)
    {
        foreach (var kv in b) if (Array.IndexOf(names, kv.Key) < 0) throw new ApiError(400, $"unknown field '{J.Cut(kv.Key, 40)}'");
    }

    static DateOnly ParseDate(string s, string what)
    {
        if (!DateRx.IsMatch(s ?? "") || !DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new ApiError(400, $"{what} must be a date, yyyy-MM-dd");
        return d;
    }

    static long ParseTime(JsonNode n, string what)
    {
        var s = J.IsStr(n) ? J.Str(n) : "";
        if (!TimeRx.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new ApiError(400, $"{what} must be a time with its offset, like 2026-11-05T09:00:00+02:00 or ...Z");
        return t.ToUnixTimeSeconds();
    }

    static long Hours(JsonNode n, string what, long max, bool zeroOk = false)
    {
        if (J.Num(n) is not double h || h < 0 || (h == 0 && !zeroOk) || h * 3600 > max) throw new ApiError(400, $"{what} must be a number of hours above 0, at most {max / 3600}");
        return (long)Math.Round(h * 3600);
    }

    static string Note(JsonNode n)
    {
        if (n == null) return "";
        if (!J.IsStr(n)) throw new ApiError(400, "note must be text");
        var s = J.Str(n);
        if (s.Length > MaxNote) throw new ApiError(400, $"note: at most {MaxNote} characters");
        return s;
    }

    // ------------------------------------------------------------ turning it on, and the settings
    static JsonObject Defaults() => new()
    {
        ["weekly_norm_hours"] = 40, ["work_days"] = new JsonArray(1, 2, 3, 4, 5), ["salary"] = 0, ["salary_currency"] = "EUR", ["manual_rate"] = null,
        ["weekday_mult"] = 1.5, ["weekend_mult"] = 2, ["holiday_mult"] = null, ["holidays_reduce_norm"] = true, ["deduct_unclosed"] = true,
        ["tolerance_pct"] = 2, ["count_today"] = true, ["country"] = null, ["region"] = null,
        ["display_currency"] = null, ["rate_source"] = "ecb", ["rate_time"] = "08:00", ["fx_manual_rate"] = null,
        ["remind_start"] = null, ["remind_paused_hours"] = 2, ["remind_open_after"] = "23:00", ["remind_repeat_minutes"] = 30, ["remind_messengers"] = true,
    };

    /// <summary>A saved version of the settings with the keys added since it was saved, at their defaults.</summary>
    static JsonObject Filled(string json)
    {
        var s = Defaults();
        foreach (var kv in (JsonObject)J.Parse(json)) s[kv.Key] = J.C(kv.Value);
        return s;
    }

    static double Range(JsonObject s, string key, double lo, double hi)
    {
        if (J.Num(s[key]) is not double d || d < lo || d > hi) throw new ApiError(400, $"{key} must be a number from {J.Fmt(lo)} to {J.Fmt(hi)}");
        return d;
    }

    static void CheckSettings(JsonObject s)
    {
        if (Range(s, "weekly_norm_hours", 0, 168) <= 0) throw new ApiError(400, "weekly_norm_hours must be above 0");
        var days = s["work_days"] as JsonArray ?? throw new ApiError(400, "work_days must be a list of days, 1 is Monday, 7 Sunday");
        var nums = days.Select(x => J.Num(x)).ToList();
        if (nums.Count == 0 || nums.Any(x => x is not double d || d != Math.Floor(d) || d < 1 || d > 7) || nums.Distinct().Count() != nums.Count)
            throw new ApiError(400, "work_days must be a list of different days, 1 is Monday, 7 Sunday");
        Range(s, "salary", 0, 1e12);
        if (!CurrencyRx.IsMatch(J.IsStr(s["salary_currency"]) ? J.Str(s["salary_currency"]) : "")) throw new ApiError(400, "salary_currency must be an ISO code, like EUR");
        if (s["manual_rate"] != null && Range(s, "manual_rate", 0, 1e9) <= 0) throw new ApiError(400, "manual_rate must be above 0");
        Range(s, "weekday_mult", 0, 100);
        Range(s, "weekend_mult", 0, 100);
        if (s["holiday_mult"] != null) Range(s, "holiday_mult", 0, 100);
        foreach (var k in new[] { "holidays_reduce_norm", "deduct_unclosed", "count_today" })
            if (!J.IsBool(s[k])) throw new ApiError(400, k + " must be true or false");
        Range(s, "tolerance_pct", 0, 100);
        CheckPlace(s);
        CheckRates(s);
        CheckReminders(s);
    }

    static decimal Dec(JsonNode n) => (decimal)J.Num(n).Value;

    static WorktimeCalc.Rules RulesOf(DateOnly from, string json)
    {
        var s = (JsonObject)J.Parse(json);
        var days = ((JsonArray)s["work_days"]).Select(x => (DayOfWeek)((int)J.Num(x).Value % 7)).ToArray();
        return new WorktimeCalc.Rules(from, (long)Math.Round(J.Num(s["weekly_norm_hours"]).Value * 3600), days, Dec(s["salary"]),
            s["manual_rate"] == null ? null : Dec(s["manual_rate"]), Dec(s["weekday_mult"]), Dec(s["weekend_mult"]),
            s["holiday_mult"] == null ? null : Dec(s["holiday_mult"]), J.Truthy(s["holidays_reduce_norm"]), J.Truthy(s["deduct_unclosed"]));
    }

    /// <summary>Turns the module on the first time, with its start date and opening balance; after that a new version of the settings from a date on.</summary>
    public JsonObject Setup(JsonObject b)
    {
        Only(b, "start_date", "opening", "settings", "valid_from");
        if (b["settings"] != null && b["settings"] is not JsonObject) throw new ApiError(400, "settings must be an object");
        var given = b["settings"] as JsonObject ?? new JsonObject();
        foreach (var kv in given) if (!Defaults().ContainsKey(kv.Key)) throw new ApiError(400, $"unknown setting '{J.Cut(kv.Key, 40)}'");
        JsonObject result;
        lock (gate)
        {
            bool first = !Enabled;
            Store.Tx(db =>
            {
                var today = Today();
                if (first && db.Meta("start_date") == null)
                {
                    if (b["valid_from"] != null) throw new ApiError(400, "valid_from is for later versions of the settings");
                    var start = b["start_date"] == null ? today : ParseDate(J.Str(b["start_date"]), "start_date");
                    if (start > today) throw new ApiError(400, "start_date can't be in the future");
                    Opening(db, b["opening"]);
                    db.SetMeta("start_date", WorktimeStore.D(start));
                    db.SetSettings(start, Merged(Defaults(), given));
                    return;
                }
                if (b["start_date"] != null || b["opening"] != null) throw new ApiError(409, "start_date and opening are set when the module is turned on");
                var from = b["valid_from"] == null ? today : ParseDate(J.Str(b["valid_from"]), "valid_from");
                if (from < ParseDate(db.Meta("start_date"), "start_date")) throw new ApiError(400, "valid_from is before the start date");
                db.SetSettings(from, Merged(Filled(db.Settings().Last().Json), given));
            });
            if (first) S.Set((EnabledKey, JsonValue.Create(true)));
            Log.I("worktime", first ? "turned on" : "settings changed");
            result = StatusLocked();
        }
        Changed();
        return result;
    }

    /// <summary>What the settings page changes: on and off, the salary, its currency. Only through the page's messages, never the HTTP API; a test hub has
    /// a route to this. Turning it on again keeps the start date and all the data. Off with a session open is refused: nothing could finish it.</summary>
    public void PageSet(JsonObject c)
    {
        var given = new JsonObject();
        if (c.ContainsKey("worktime_salary"))
        {
            if (J.Num(c["worktime_salary"]) is not double salary || salary < 0 || salary > 1e12) throw new ApiError(400, "bad salary") { Ui = L.T("wt_bad_salary") };
            given["salary"] = salary;
        }
        if (c.ContainsKey("worktime_currency"))
        {
            var code = (J.IsStr(c["worktime_currency"]) ? J.Str(c["worktime_currency"]) : "").Trim().ToUpperInvariant();
            if (!CurrencyRx.IsMatch(code)) throw new ApiError(400, "bad currency") { Ui = L.T("wt_bad_currency") };
            given["salary_currency"] = code;
        }
        if (c["worktime_settings"] is JsonObject more)
            foreach (var kv in more) given[kv.Key] = J.C(kv.Value);
        lock (gate)
        {
            if (c.ContainsKey("worktime"))
            {
                bool on = J.Truthy(c["worktime"]);
                if (on && !Enabled)
                {
                    bool used;
                    try { used = Store.Tx(db => db.Meta("start_date") != null); }
                    catch (SqliteException e)
                    {
                        Log.E("worktime", "file unreadable: " + e.Message);
                        throw new ApiError(500, "the work hours file can't be read") { Ui = L.T("wt_file_broken") };   // it stays as it is: it may be restored
                    }
                    if (used) { S.Set((EnabledKey, JsonValue.Create(true))); Log.I("worktime", "turned on again"); }
                    else Asked(() => Setup(c["worktime_setup"] is JsonObject first ? (JsonObject)first.DeepClone() : new JsonObject()));
                }
                else if (!on && Enabled)
                {
                    bool open = false;
                    try { open = Store.Tx(db => db.OpenSession() != null); }
                    catch (SqliteException) { }   // an unreadable file holds no session anyone could finish: off must still work
                    if (open) throw new ApiError(409, "a session is open") { Ui = L.T("wt_open_session") };
                    S.Set((EnabledKey, JsonValue.Create(false)));
                    Log.I("worktime", "turned off");
                }
            }
            if (given.Count > 0)
            {
                On();
                Asked(() => Setup(new JsonObject { ["settings"] = given }));
            }
        }
        Changed();
    }

    /// <summary>A refusal the settings page can show: one translated sentence; the API's English reason goes only to the log (the page checks ranges itself first).</summary>
    static void Asked(Action a)
    {
        try { a(); }
        catch (ApiError e) when (e.Ui == null) { Log.I("worktime", "settings refused: " + e.Message); throw new ApiError(e.Code, e.Message) { Ui = L.T("wt_bad_settings") }; }
    }

    /// <summary>What the settings page shows.</summary>
    public JsonObject PageView()
    {
        lock (gate)
        {
            if (!Enabled) return OffView();
            PageSeen();
            try
            {
                return Store.Tx(db =>
                {
                    if (db.Settings().Count == 0) return Broken(null);
                    var versions = db.Settings();
                    var s = Filled(versions.Last().Json);
                    return new JsonObject
                    {
                        ["settings"] = s.DeepClone(),
                        ["versions"] = new JsonArray(versions.AsEnumerable().Reverse().Select(x => (JsonNode)new JsonObject { ["valid_from"] = WorktimeStore.D(x.From), ["settings"] = J.Parse(x.Json) }).ToArray()),
                        ["enabled"] = true, ["start_date"] = db.Meta("start_date"), ["salary"] = J.C(s["salary"]),
                        ["salary_currency"] = J.C(s["salary_currency"]), ["weekly_norm_hours"] = J.C(s["weekly_norm_hours"]),
                    };
                });
            }
            catch (SqliteException e) { return Broken(e.Message); }
        }
    }

    /// <summary>Off: if the module was used before, its start date (turning it on again continues from it and asks nothing). Looking doesn't create the file.</summary>
    JsonObject OffView()
    {
        var off = new JsonObject { ["enabled"] = false };
        if (!File.Exists(S.File_("worktime.db"))) return off;
        try { var start = Store.Tx(db => db.Meta("start_date")); if (start != null) off["start_date"] = start; }
        catch (SqliteException) { }   // unreadable: turning it on says so
        return off;
    }

    bool brokenLogged;

    /// <summary>The file is gone or damaged while the module is on: said on the panel and the settings page, once in the log, and the switch still turns it off.</summary>
    JsonObject Broken(string why)
    {
        if (!brokenLogged) { brokenLogged = true; Log.E("worktime", "file unreadable: " + (why ?? "no settings in it")); }
        return new JsonObject { ["enabled"] = true, ["broken"] = true };
    }

    static string Merged(JsonObject baseOn, JsonObject given)
    {
        foreach (var kv in given) baseOn[kv.Key] = J.C(kv.Value);
        CheckSettings(baseOn);
        return baseOn.ToJsonString();
    }

    static void Opening(WorktimeStore.Db db, JsonNode n)
    {
        if (n == null) return;
        if (n is not JsonObject o) throw new ApiError(400, "opening must be an object");
        Only(o, "deficit_hours", "weekday_hours", "weekend_hours", "holiday_hours");
        foreach (var kv in o) db.SetMeta("opening_" + kv.Key, Hours(kv.Value, "opening " + kv.Key, MaxOpeningSec, zeroOk: true).ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ the buttons
    public JsonObject Start(string source) => StartAt(long.MaxValue, source);

    /// <summary>Start with the answer to "today is a vacation day": "unmark" removes the mark (logged like any day change), "keep" or none keeps it and
    /// the hours count as overtime by the day's type.</summary>
    public JsonObject Start(JsonObject b)
    {
        var source = Source(b, "vacation");
        var v = b["vacation"] == null ? null : J.IsStr(b["vacation"]) ? J.Str(b["vacation"]) : "";
        if (v is not (null or "unmark" or "keep")) throw new ApiError(400, "vacation must be unmark or keep");
        return StartAt(long.MaxValue, source, v == "unmark");
    }

    /// <summary>Starts a session at <paramref name="at"/> (not later than now): a reminder's "Start from".</summary>
    JsonObject StartAt(long at, string source, bool unmarkVacation = false) => Act(db =>
    {
        if (db.OpenSession() != null) throw new ApiError(409, "a session is already open");
        var now = Math.Min(at, Now());
        var day = LocalDate(now);
        if (unmarkVacation && db.Marks(day, day).Any(x => x.Kind == "vacation")) Logged(db, day, "day_normal", () => db.ClearMark(day));
        var id = db.AddSession(LocalDate(now), now, null, true);
        db.AddInterval(id, now, null, null, source, "");
    });

    public JsonObject Pause() => Act(db =>
    {
        var s = db.OpenSession() ?? throw new ApiError(409, "no session is open");
        var running = db.IntervalsOfSession(s.Id).FirstOrDefault(x => x.Running) ?? throw new ApiError(409, "the session is already paused");
        db.SetInterval(running.Id, running.Start, Now(), null, running.Note);
    });

    public JsonObject Resume(string source) => Act(db =>
    {
        var s = db.OpenSession() ?? throw new ApiError(409, "no session is open");
        if (db.IntervalsOfSession(s.Id).Any(x => x.Running)) throw new ApiError(409, "the session isn't paused");
        db.AddInterval(s.Id, Now(), null, null, source, "");
    });

    public JsonObject Finish() => FinishAt(long.MaxValue);

    /// <summary>Finishes the open session at <paramref name="at"/>: not later than now, not before the running part began or the last part ended
    /// (a reminder's "Finish at").</summary>
    JsonObject FinishAt(long at) => Act(db =>
    {
        var s = db.OpenSession() ?? throw new ApiError(409, "no session is open");
        var end = Math.Min(at, Now());
        var ints = db.IntervalsOfSession(s.Id);
        var running = ints.FirstOrDefault(x => x.Running);
        if (running != null)
        {
            end = Math.Max(end, running.Start.Value);
            db.SetInterval(running.Id, running.Start, end, null, running.Note);
        }
        else end = Math.Max(end, ints.Where(x => x.End != null).Select(x => x.End.Value).DefaultIfEmpty(end).Max());
        var all = db.IntervalsOfSession(s.Id);
        db.SetSessionTimes(s.Id, all.Where(x => x.Start != null).Select(x => x.Start).Min(), end, false);
    });

    JsonObject Act(Action<WorktimeStore.Db> a)
    {
        JsonObject result;
        lock (gate)
        {
            On();
            Store.Tx(a);
            result = StatusLocked();
        }
        Changed();
        return result;
    }

    sealed class Rollback : Exception { public JsonObject Result; }

    /// <summary>A manual change, or with <paramref name="preview"/> what it would do to the totals: the same code in a transaction that never commits.</summary>
    JsonObject Apply(bool preview, Action<WorktimeStore.Db> a)
    {
        if (!preview) return Act(a);
        lock (gate)
        {
            On();
            try
            {
                Store.Tx(db =>
                {
                    var before = Totals(StatusIn(db));
                    a(db);
                    throw new Rollback { Result = new JsonObject { ["before"] = before, ["after"] = Totals(StatusIn(db)) } };
                });
            }
            catch (Rollback r) { return r.Result; }
            throw new InvalidOperationException("a preview must not commit");
        }
    }

    static JsonObject Totals(JsonObject s) => new()
    {
        ["deficit_seconds"] = J.C(s["deficit_seconds"]), ["pools"] = J.C(s["pools"]), ["money"] = J.C(s["money"]),
    };

    /// <summary>Who pressed the button, as the intervals remember it: the panel says so, anything else is the API.</summary>
    public static string Source(JsonObject b, params string[] more)
    {
        Only(b, more.Prepend("source").ToArray());
        if (b["source"] == null) return "api";
        var s = J.IsStr(b["source"]) ? J.Str(b["source"]) : "";
        return s is "api" or "panel" ? s : throw new ApiError(400, "source must be panel or api");
    }

    // ------------------------------------------------------------ corrections: all of them logged, day by day
    static void Writable(WorktimeStore.Db db, DateOnly d)
    {
        if (db.MonthClosed(d)) throw new ApiError(409, $"{d:yyyy-MM} is closed; reopen the month to change it");
    }

    static void InRange(WorktimeStore.Db db, DateOnly d)
    {
        if (d > Today()) throw new ApiError(400, "work_date can't be in the future");
        if (d < ParseDate(db.Meta("start_date"), "start_date")) throw new ApiError(400, "work_date is before the start date");
        Writable(db, d);
    }

    /// <summary>An interval with times: the end after the start and not ahead of the clock, starting on its session's date or, for a session past
    /// midnight, the next one, and not over another interval, which would count the same time twice.</summary>
    static void CheckPair(WorktimeStore.Db db, DateOnly workDate, long start, long end, long now, long exceptId = 0)
    {
        if (end <= start) throw new ApiError(400, "end must be after start");
        if (end - start > MaxPairSec) throw new ApiError(400, $"an interval can be at most {MaxPairSec / 3600} hours");
        if (end > now) throw new ApiError(400, "end can't be in the future");
        var d = LocalDate(start);
        if (d != workDate && d != workDate.AddDays(1)) throw new ApiError(400, $"start must be on the session's date, {WorktimeStore.D(workDate)}");
        if (db.Overlapping(start, end, now, exceptId) is { } other) throw new ApiError(409, $"it overlaps interval {other.Id} of {WorktimeStore.D(other.WorkDate)}");
    }

    static JsonObject Snapshot(WorktimeStore.Db db, DateOnly d)
    {
        var mark = db.Marks(d, d).Select(x => (JsonNode)new JsonObject { ["kind"] = x.Kind, ["source"] = x.Source }).FirstOrDefault();
        var sessions = new JsonArray();
        foreach (var s in db.SessionsOf(d))
            sessions.Add(new JsonObject
            {
                ["id"] = s.Id, ["open"] = s.Open,
                ["intervals"] = new JsonArray(db.IntervalsOfSession(s.Id).Select(x => (JsonNode)IntervalJson(x, Now())).ToArray()),
            });
        return new JsonObject { ["date"] = WorktimeStore.D(d), ["mark"] = mark, ["sessions"] = sessions };
    }

    static void Logged(WorktimeStore.Db db, DateOnly d, string action, Action change)
    {
        var before = Snapshot(db, d);
        change();
        db.LogChange(d, Now(), action, before, Snapshot(db, d));
    }

    /// <summary>The session's own times follow its intervals; a closed session left without any is gone.</summary>
    static void Resync(WorktimeStore.Db db, long sessionId)
    {
        var s = db.GetSession(sessionId);
        var all = db.IntervalsOfSession(sessionId);
        if (all.Count == 0 && !s.Open) { db.DeleteSession(sessionId); return; }
        db.SetSessionTimes(sessionId, all.Where(x => x.Start != null).Select(x => x.Start).Min(), s.Open ? null : all.Where(x => x.End != null).Select(x => x.End).Max(), s.Open);
    }

    public JsonObject AddSession(JsonObject b, bool preview = false) => Apply(preview, db =>
    {
        Only(b, "work_date", "intervals", "total_hours", "note");
        var d = ParseDate(J.Str(b["work_date"]), "work_date");
        InRange(db, d);
        if ((b["intervals"] != null) == (b["total_hours"] != null)) throw new ApiError(400, "give either intervals or total_hours");
        var note = Note(b["note"]);
        var now = Now();
        Logged(db, d, "session_add", () =>
        {
            var id = db.AddSession(d, null, null, false);
            if (b["total_hours"] != null)
                db.AddInterval(id, null, null, Hours(b["total_hours"], "total_hours", MaxTotalSec), "manual", note);
            else
            {
                if (b["intervals"] is not JsonArray list || list.Count == 0 || list.Count > MaxIntervalsAtOnce) throw new ApiError(400, $"intervals must be a list of 1 to {MaxIntervalsAtOnce}");
                foreach (var n in list)
                {
                    if (n is not JsonObject o) throw new ApiError(400, "an interval must be an object");
                    Only(o, "start", "end", "note");
                    long start = ParseTime(o["start"], "start"), end = ParseTime(o["end"], "end");
                    CheckPair(db, d, start, end, now);
                    db.AddInterval(id, start, end, null, "manual", o["note"] != null ? Note(o["note"]) : note);
                }
            }
            Resync(db, id);
        });
    });

    public JsonObject AddInterval(JsonObject b, bool preview = false) => Apply(preview, db =>
    {
        Only(b, "session_id", "start", "end", "duration_hours", "note");
        if (J.Num(b["session_id"]) is not double sid || sid != Math.Floor(sid) || sid < 1 || sid > 1e15) throw new ApiError(400, "session_id must be a number");
        var s = db.GetSession((long)sid) ?? throw new ApiError(404, "no such session");
        InRange(db, s.WorkDate);
        var note = Note(b["note"]);
        Logged(db, s.WorkDate, "interval_add", () =>
        {
            if (b["duration_hours"] != null)
            {
                if (b["start"] != null || b["end"] != null) throw new ApiError(400, "give either start and end or duration_hours");
                db.AddInterval(s.Id, null, null, Hours(b["duration_hours"], "duration_hours", MaxTotalSec), "manual", note);
            }
            else
            {
                long start = ParseTime(b["start"], "start"), end = ParseTime(b["end"], "end");
                CheckPair(db, s.WorkDate, start, end, Now());
                db.AddInterval(s.Id, start, end, null, "manual", note);
            }
            Resync(db, s.Id);
        });
    });

    public JsonObject EditInterval(long id, JsonObject b, bool preview = false) => Apply(preview, db =>
    {
        Only(b, "start", "end", "duration_hours", "note");
        if (b.Count == 0) throw new ApiError(400, "nothing to change");
        var x = db.GetInterval(id) ?? throw new ApiError(404, "no such interval");
        InRange(db, x.WorkDate);
        var note = b.ContainsKey("note") ? Note(b["note"]) : x.Note;
        var now = Now();
        long? start = x.Start, end = x.End, duration = x.Duration;
        if (b["duration_hours"] != null)
        {
            if (b["start"] != null || b["end"] != null) throw new ApiError(400, "give either start and end or duration_hours");
            if (x.Running) throw new ApiError(409, "the interval is running: pause or finish first");
            (start, end, duration) = (null, null, Hours(b["duration_hours"], "duration_hours", MaxTotalSec));
        }
        else if (b["start"] != null || b["end"] != null)
        {
            if (x.Running && b["end"] != null) throw new ApiError(409, "the interval is running: pause or finish first");
            if (b["start"] != null) start = ParseTime(b["start"], "start");
            if (b["end"] != null) end = ParseTime(b["end"], "end");
            if (start == null || (end == null && !x.Running)) throw new ApiError(400, "a duration-only interval needs both start and end");
            duration = null;
            if (x.Running)
            {
                if (start > now) throw new ApiError(400, "start can't be in the future");
                CheckPair(db, x.WorkDate, start.Value, Math.Max(now, start.Value + 1), Math.Max(now, start.Value + 1), id);
            }
            else CheckPair(db, x.WorkDate, start.Value, end.Value, now, id);
        }
        Logged(db, x.WorkDate, "interval_edit", () =>
        {
            db.SetInterval(id, start, end, duration, note);
            Resync(db, x.SessionId);
        });
    });

    public JsonObject DeleteInterval(long id, bool preview = false) => Apply(preview, db =>
    {
        var x = db.GetInterval(id) ?? throw new ApiError(404, "no such interval");
        InRange(db, x.WorkDate);
        if (x.Running) throw new ApiError(409, "the interval is running: pause or finish first");
        Logged(db, x.WorkDate, "interval_delete", () =>
        {
            db.DeleteInterval(id);
            Resync(db, x.SessionId);
        });
    });

    static (DateOnly From, DateOnly To) Span(Dictionary<string, string> q)
    {
        var today = Today();
        var from = q.TryGetValue("from", out var f) ? ParseDate(f, "from") : today;
        var to = q.TryGetValue("to", out var t) ? ParseDate(t, "to") : from;
        if (to < from) throw new ApiError(400, "to is before from");
        if (to.DayNumber - from.DayNumber >= MaxDays) throw new ApiError(400, $"at most {MaxDays} days at a time");
        return (from, to);
    }

    static JsonObject IntervalJson(WorktimeStore.Interval x, long now) => new()
    {
        ["id"] = x.Id, ["session_id"] = x.SessionId, ["work_date"] = WorktimeStore.D(x.WorkDate), ["start"] = Iso(x.Start), ["end"] = Iso(x.End),
        ["duration_seconds"] = x.Seconds(now), ["running"] = x.Running, ["source"] = x.Source, ["note"] = x.Note,
    };

    public JsonObject ListIntervals(Dictionary<string, string> q)
    {
        var (from, to) = Span(q);
        lock (gate)
        {
            On();
            var now = Now();
            var list = Store.Tx(db => db.IntervalsBetween(from, to));
            return new JsonObject { ["intervals"] = new JsonArray(list.Select(x => (JsonNode)IntervalJson(x, now)).ToArray()) };
        }
    }

    public JsonObject ListDays(Dictionary<string, string> q)
    {
        var (from, to) = Span(q);
        lock (gate)
        {
            On();
            var list = Store.Tx(db => db.Marks(from, to));
            return new JsonObject { ["days"] = new JsonArray(list.Select(x => (JsonNode)new JsonObject { ["date"] = WorktimeStore.D(x.Date), ["kind"] = x.Kind, ["source"] = x.Source }).ToArray()) };
        }
    }

    /// <summary>Marks a range of days as holiday or vacation, or back to normal. Days ahead are fine: a vacation is known in advance.</summary>
    public JsonObject SetDays(Dictionary<string, string> q, JsonObject b, bool preview = false)
    {
        var (from, to) = Span(q);
        Only(b, "kind");
        var kind = J.IsStr(b["kind"]) ? J.Str(b["kind"]) : "";
        if (kind is not ("holiday" or "vacation" or "normal")) throw new ApiError(400, "kind must be holiday, vacation or normal");
        return Apply(preview, db =>
        {
            for (var d = from; d <= to; d = d.AddDays(1)) Writable(db, d);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var day = d;
                var had = db.Marks(day, day).Select(x => x.Kind).FirstOrDefault();
                if (had == (kind == "normal" ? null : kind)) continue;
                Logged(db, day, "day_" + kind, () =>
                {
                    if (kind == "normal") db.ClearMark(day);
                    else db.SetMark(day, kind, "manual");
                });
            }
        });
    }

    static readonly DateOnly EarliestStart = new(2000, 1, 1);

    /// <summary>Moves the start of the count to an earlier day, so the hours of the days before it can be filled in; the first version of the settings
    /// moves with it. Never later (hours already entered would drop out), and not while a month is closed: its carry-over would change under it.</summary>
    public JsonObject MoveStart(JsonObject b, bool preview = false) => Apply(preview, db =>
    {
        Only(b, "start_date");
        var to = ParseDate(J.IsStr(b["start_date"]) ? J.Str(b["start_date"]) : "", "start_date");
        var from = ParseDate(db.Meta("start_date"), "start_date");
        if (to > Today()) throw new ApiError(400, "start_date can't be in the future");
        if (to >= from) throw new ApiError(400, "start_date can only move earlier than " + WorktimeStore.D(from));
        if (to < EarliestStart) throw new ApiError(400, "start_date can't be before " + WorktimeStore.D(EarliestStart));
        if (db.Closes().Count > 0) throw new ApiError(409, "a month is closed: reopen it before moving the start date");
        var first = db.Settings()[0].From;
        if (first == from) db.MoveSettings(from, to);
        db.SetMeta("start_date", WorktimeStore.D(to));
        if (!preview) Log.I("worktime", "start date moved earlier");
    });

    static void ClearDay(WorktimeStore.Db db, DateOnly d)
    {
        foreach (var s in db.SessionsOf(d))
        {
            foreach (var x in db.IntervalsOfSession(s.Id)) db.DeleteInterval(x.Id);
            db.DeleteSession(s.Id);
        }
    }

    /// <summary>The same hours for one day or several, without times: what the days had is replaced, each day logged on its own; 0 empties them. An open session stays out of this.</summary>
    public JsonObject SetHours(JsonObject b, bool preview = false) => Apply(preview, db =>
    {
        Only(b, "dates", "total_hours", "note");
        if (b["dates"] is not JsonArray list || list.Count == 0 || list.Count > MaxDays) throw new ApiError(400, $"dates must be a list of 1 to {MaxDays} dates");
        var dates = list.Select(x => ParseDate(J.IsStr(x) ? J.Str(x) : "", "a date")).Distinct().OrderBy(x => x).ToList();
        var seconds = Hours(b["total_hours"], "total_hours", MaxTotalSec, zeroOk: true);
        var note = Note(b["note"]);
        foreach (var d in dates)
        {
            InRange(db, d);
            if (db.SessionsOf(d).Any(s => s.Open)) throw new ApiError(409, $"a session of {WorktimeStore.D(d)} is open: finish it first");
        }
        foreach (var d in dates)
            Logged(db, d, "day_hours", () =>
            {
                ClearDay(db, d);
                if (seconds > 0) db.AddInterval(db.AddSession(d, null, null, false), null, null, seconds, "manual", note);
            });
    });

    /// <summary>Takes back a day's last manual change. Refused if the day has moved on since, as with hours from the buttons: going back would lose them.</summary>
    public JsonObject Undo(string date, bool preview = false)
    {
        var d = ParseDate(date, "date");
        return Apply(preview, db =>
        {
            Writable(db, d);
            var rows = db.History(d);
            if (rows.Count == 0) throw new ApiError(404, "this day has no change to undo");
            if (rows[0].After != Snapshot(db, d).ToJsonString()) throw new ApiError(409, "the day has changed since its last manual change");
            var before = (JsonObject)J.Parse(rows[0].Before);
            if (db.SessionsOf(d).Any(s => s.Open) || ((JsonArray)before["sessions"]).Any(s => J.Truthy(s["open"]))) throw new ApiError(409, "a session of this day is open: finish it first");
            Logged(db, d, "undo", () => Restore(db, d, before));
        });
    }

    static void Restore(WorktimeStore.Db db, DateOnly d, JsonObject day)
    {
        ClearDay(db, d);
        db.ClearMark(d);
        if (day["mark"] is JsonObject m) db.SetMark(d, J.Str(m["kind"]), J.Str(m["source"]));
        foreach (var s in (JsonArray)day["sessions"])
        {
            var id = db.AddSession(d, null, null, false);
            foreach (var x in (JsonArray)s["intervals"])
            {
                string source = J.Str(x["source"]), note = J.Str(x["note"]);
                if (x["start"] == null) db.AddInterval(id, null, null, (long)J.Num(x["duration_seconds"]).Value, source, note);
                else
                {
                    long start = ParseTime(x["start"], "start"), end = ParseTime(x["end"], "end");
                    if (db.Overlapping(start, end, Now()) is { } other) throw new ApiError(409, $"it overlaps interval {other.Id} of {WorktimeStore.D(other.WorkDate)}");
                    db.AddInterval(id, start, end, null, source, note);
                }
            }
            Resync(db, id);
        }
    }

    static JsonObject RowJson(WorktimeCalc.DayRow r) => new()
    {
        ["date"] = WorktimeStore.D(r.Date), ["kind"] = r.Kind.ToString().ToLowerInvariant(), ["worked_seconds"] = r.Worked, ["norm_seconds"] = r.Norm, ["closed"] = r.Closed,
        ["to_deficit_seconds"] = r.ToDeficit, ["paid_off_seconds"] = r.PaidOff, ["to_pool_seconds"] = r.ToPool, ["pool"] = r.Pool.ToString().ToLowerInvariant(), ["deficit_after_seconds"] = r.DeficitAfter,
    };

    /// <summary>Day by day: hours, norm, what went to the deficit and to overtime. Days ahead and before the start have no row, only their marks.</summary>
    public JsonObject Breakdown(Dictionary<string, string> q)
    {
        var (from, to) = Span(q);
        lock (gate)
        {
            On();
            return Store.Tx(db =>
            {
                var now = Now();
                var c = CalcOf(db, Today(), now);
                var r = WorktimeCalc.Compute(c.Input);
                return new JsonObject
                {
                    ["today"] = WorktimeStore.D(Today()), ["start_date"] = WorktimeStore.D(c.Input.Start),
                    ["days"] = new JsonArray(r.Days.Where(x => x.Date >= from && x.Date <= to).Select(x => (JsonNode)RowJson(x)).ToArray()),
                    ["marks"] = new JsonArray(db.Marks(from, to).Select(x => (JsonNode)new JsonObject { ["date"] = WorktimeStore.D(x.Date), ["kind"] = x.Kind, ["name"] = NameOf(db, c.Settings, x.Date, x.Kind) }).ToArray()),
                    ["closed_months"] = J.StrArray(db.Closes().Select(x => $"{x.Year:D4}-{x.Month:D2}")),
                };
            });
        }
    }

    /// <summary>One day as the day window shows it.</summary>
    public JsonObject Day(string date)
    {
        var d = ParseDate(date, "date");
        lock (gate)
        {
            On();
            return Store.Tx(db =>
            {
                var c = CalcOf(db, Today(), Now());
                var row = WorktimeCalc.Compute(c.Input).Days.FirstOrDefault(x => x.Date == d);
                var day = Snapshot(db, d);
                var last = db.History(d);
                bool open = db.SessionsOf(d).Any(s => s.Open);
                return new JsonObject
                {
                    ["date"] = date, ["row"] = row == null ? null : RowJson(row), ["mark"] = J.C(day["mark"]), ["name"] = NameOf(db, c.Settings, d, J.Str(day["mark"]?["kind"])), ["sessions"] = J.C(day["sessions"]),
                    ["closed_month"] = db.MonthClosed(d), ["editable"] = d <= Today() && d >= c.Input.Start && !db.MonthClosed(d),
                    ["can_undo"] = !open && !db.MonthClosed(d) && last.Count > 0 && last[0].After == day.ToJsonString(),
                    // a past day before the count: the start can move to it (PUT /api/worktime/start-date), unless a month is closed or it's too early
                    ["before_start"] = d < c.Input.Start && d <= Today(), ["can_move_start"] = d < c.Input.Start && d <= Today() && d >= EarliestStart && db.Closes().Count == 0,
                    ["move_start_why"] = !(d < c.Input.Start && d <= Today()) ? null : d < EarliestStart ? "too_early" : db.Closes().Count > 0 ? "closed" : null,
                };
            });
        }
    }

    public JsonObject History(string date)
    {
        var d = ParseDate(date, "date");
        lock (gate)
        {
            On();
            var rows = Store.Tx(db => db.History(d));
            return new JsonObject
            {
                ["date"] = date,
                ["changes"] = new JsonArray(rows.Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = x.Id, ["at"] = Iso(x.At), ["action"] = x.Action, ["before"] = J.Parse(x.Before), ["after"] = J.Parse(x.After),
                }).ToArray()),
            };
        }
    }

    // ------------------------------------------------------------ the state
    public JsonObject Status()
    {
        lock (gate)
        {
            if (!Enabled) return new JsonObject { ["enabled"] = false };
            try
            {
                var r = StatusLocked();
                brokenLogged = false;
                return r;
            }
            catch (SqliteException e) { return Broken(e.Message); }
            catch (ApiError) { return Broken(null); }   // no start date: a new, empty file
        }
    }

    static JsonObject BarJson(WorktimeCalc.Bar b) => new()
    {
        ["worked_seconds"] = b.Worked, ["full_seconds"] = b.Full, ["expected_seconds"] = b.Expected, ["balance_seconds"] = b.Balance,
        ["tolerance_seconds"] = b.Tolerance, ["above_seconds"] = b.Above, ["fill"] = Math.Round(b.Fill, 4), ["state"] = b.State.ToString().ToLowerInvariant(),
    };

    JsonObject StatusLocked() => Store.Tx(StatusIn);

    /// <summary>A holiday's name as shown, or null.</summary>
    static string NameOf(WorktimeStore.Db db, JsonObject settings, DateOnly d, string kind)
    {
        if (kind != "holiday" || !db.Names(d, d).TryGetValue(d, out var n)) return null;
        return n.Own ? n.Name : HolidayNames.Local(PlaceOf(settings).Country, n.Name, L.Lang) ?? n.Name;
    }

    sealed record Calc(WorktimeCalc.Input Input, JsonObject Settings, WorktimeStore.Session Open);

    static Calc CalcOf(WorktimeStore.Db db, DateOnly today, long now)
    {
        var startText = db.Meta("start_date") ?? throw new ApiError(409, "the work hours module has no start date");
        var start = ParseDate(startText, "start_date");
        var versions = db.Settings();
        var rules = versions.Select(x => RulesOf(x.From, x.Json)).ToList();
        var settings = Filled(versions.Last(x => x.From <= today || x.From == versions[0].From).Json);
        var open = db.OpenSession();
        var days = new Dictionary<DateOnly, WorktimeCalc.DayWork>();
        foreach (var x in db.IntervalsBetween(start, today))
        {
            days.TryGetValue(x.WorkDate, out var had);
            days[x.WorkDate] = new((had?.Seconds ?? 0) + x.Seconds(now));
        }
        if (open != null)
        {
            days.TryGetValue(open.WorkDate, out var had);
            days[open.WorkDate] = new(had?.Seconds ?? 0, true);
        }
        var marks = db.Marks(DateOnly.MinValue, DateOnly.MaxValue);
        long Opening(string k) => long.TryParse(db.Meta("opening_" + k), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var input = new WorktimeCalc.Input(rules, start, today, days,
            marks.Where(x => x.Kind == "holiday").Select(x => x.Date).ToHashSet(),
            marks.Where(x => x.Kind == "vacation").Select(x => x.Date).ToHashSet(),
            db.Closes().ToDictionary(x => (x.Year, x.Month), x => x.Close),
            Opening("deficit_hours"), Opening("weekday_hours"), Opening("weekend_hours"), Opening("holiday_hours"));
        return new Calc(input, settings, open);
    }

    JsonObject StatusIn(WorktimeStore.Db db)
    {
        var now = Now();
        var today = Today();
        var c = CalcOf(db, today, now);
        var (input, settings, open) = (c.Input, c.Settings, c.Open);
        var r = WorktimeCalc.Compute(input);

        var ofToday = db.SessionsOf(today).Where(x => x.Started != null).Select(x => x.Started.Value).ToList();
        long? elapsed = ofToday.Count > 0 ? now - ofToday.Min() : null;
        bool countToday = J.Truthy(settings["count_today"]);
        double tolerance = J.Num(settings["tolerance_pct"]) ?? 2;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var first = new DateOnly(today.Year, today.Month, 1);
        var week = WorktimeCalc.BarOf(input, monday, monday.AddDays(6), false, elapsed, countToday, tolerance);
        var month = WorktimeCalc.BarOf(input, first, first.AddMonths(1).AddDays(-1), true, elapsed, countToday, tolerance);

        JsonObject session = null;
        if (open != null)
        {
            var ints = db.IntervalsOfSession(open.Id);
            session = new JsonObject
            {
                ["id"] = open.Id, ["work_date"] = WorktimeStore.D(open.WorkDate), ["state"] = ints.Any(x => x.Running) ? "running" : "paused",
                ["started"] = Iso(open.Started), ["worked_seconds"] = ints.Sum(x => x.Seconds(now)),
            };
        }
        return new JsonObject
        {
            ["enabled"] = true, ["today"] = WorktimeStore.D(today), ["start_date"] = WorktimeStore.D(input.Start), ["session"] = session,
            ["today_vacation"] = input.Vacations.Contains(today),
            ["week"] = BarJson(week), ["month"] = BarJson(month), ["deficit_seconds"] = r.Deficit,
            ["pools"] = new JsonObject { ["weekday_seconds"] = r.Weekday, ["weekend_seconds"] = r.Weekend, ["holiday_seconds"] = r.Holiday },
            ["money"] = new JsonObject
            {
                ["set"] = (J.Num(settings["salary"]) ?? 0) > 0 || settings["manual_rate"] != null,
                ["currency"] = J.Str(settings["salary_currency"]), ["earned"] = Math.Round(r.Earned, 2),
                ["overtime"] = Math.Round(r.Current?.OvertimeMoney ?? 0, 2),
                ["display"] = Display(db, settings, today, r.Earned, r.Current?.OvertimeMoney ?? 0),
            },
            ["unclosed_months"] = J.StrArray(r.Months.Where(m => m.Paid && !m.Closed).Select(m => $"{m.Year:D4}-{m.Month:D2}")),
            ["holidays_to_confirm"] = HolidaysToConfirm(db, settings, today),
        };
    }
}
