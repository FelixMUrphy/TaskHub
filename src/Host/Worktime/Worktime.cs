using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>The work-hours module: sessions and intervals, what the API may change, and the state worked out of them. Off until it's turned on;
/// while off it has no file. A session belongs to the local date it started on. Nothing here closes a session by itself, only the user does.</summary>
sealed class Worktime
{
    const string EnabledKey = "worktime_enabled";
    const long MaxPairSec = 48 * 3600, MaxTotalSec = 24 * 3600, MaxOpeningSec = 100_000 * 3600L;
    const int MaxDays = 366, MaxNote = 500, MaxIntervalsAtOnce = 100;
    static readonly Regex DateRx = new(@"^\d{4}-\d{2}-\d{2}$"), TimeRx = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$"), CurrencyRx = new("^[A-Z]{3}$");

    readonly Settings S;
    readonly object gate = new();
    WorktimeStore store;

    public Worktime(Settings s) => S = s;

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
        ["tolerance_pct"] = 2, ["count_today"] = true,
    };

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
                db.SetSettings(from, Merged((JsonObject)J.Parse(db.Settings().Last().Json), given));
            });
            if (first) S.Set((EnabledKey, JsonValue.Create(true)));
            Log.I("worktime", first ? "turned on" : "settings changed");
            return StatusLocked();
        }
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
    public JsonObject Start(string source) => Act(db =>
    {
        if (db.OpenSession() != null) throw new ApiError(409, "a session is already open");
        var now = Now();
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

    public JsonObject Finish() => Act(db =>
    {
        var s = db.OpenSession() ?? throw new ApiError(409, "no session is open");
        var now = Now();
        var running = db.IntervalsOfSession(s.Id).FirstOrDefault(x => x.Running);
        if (running != null) db.SetInterval(running.Id, running.Start, now, null, running.Note);
        var all = db.IntervalsOfSession(s.Id);
        db.SetSessionTimes(s.Id, all.Where(x => x.Start != null).Select(x => x.Start).Min(), now, false);
    });

    JsonObject Act(Action<WorktimeStore.Db> a)
    {
        lock (gate)
        {
            On();
            Store.Tx(a);
            return StatusLocked();
        }
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

    public JsonObject AddSession(JsonObject b) => Act(db =>
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

    public JsonObject AddInterval(JsonObject b) => Act(db =>
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

    public JsonObject EditInterval(long id, JsonObject b) => Act(db =>
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

    public JsonObject DeleteInterval(long id) => Act(db =>
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
    public JsonObject SetDays(Dictionary<string, string> q, JsonObject b)
    {
        var (from, to) = Span(q);
        Only(b, "kind");
        var kind = J.IsStr(b["kind"]) ? J.Str(b["kind"]) : "";
        if (kind is not ("holiday" or "vacation" or "normal")) throw new ApiError(400, "kind must be holiday, vacation or normal");
        return Act(db =>
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
            return StatusLocked();
        }
    }

    static JsonObject BarJson(WorktimeCalc.Bar b) => new()
    {
        ["worked_seconds"] = b.Worked, ["full_seconds"] = b.Full, ["expected_seconds"] = b.Expected, ["balance_seconds"] = b.Balance,
        ["tolerance_seconds"] = b.Tolerance, ["above_seconds"] = b.Above, ["fill"] = Math.Round(b.Fill, 4), ["state"] = b.State.ToString().ToLowerInvariant(),
    };

    JsonObject StatusLocked()
    {
        var now = Now();
        var today = Today();
        return Store.Tx(db =>
        {
            var startText = db.Meta("start_date") ?? throw new ApiError(409, "the work hours module has no start date");
            var start = ParseDate(startText, "start_date");
            var versions = db.Settings();
            var rules = versions.Select(x => RulesOf(x.From, x.Json)).ToList();
            var settings = (JsonObject)J.Parse(versions.Last(x => x.From <= today || x.From == versions[0].From).Json);

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
                ["enabled"] = true, ["today"] = WorktimeStore.D(today), ["start_date"] = startText, ["session"] = session,
                ["week"] = BarJson(week), ["month"] = BarJson(month), ["deficit_seconds"] = r.Deficit,
                ["pools"] = new JsonObject { ["weekday_seconds"] = r.Weekday, ["weekend_seconds"] = r.Weekend, ["holiday_seconds"] = r.Holiday },
                ["money"] = new JsonObject
                {
                    ["currency"] = J.Str(settings["salary_currency"]), ["earned"] = Math.Round(r.Earned, 2),
                    ["overtime"] = Math.Round(r.Current?.OvertimeMoney ?? 0, 2),
                },
                ["unclosed_months"] = J.StrArray(r.Months.Where(m => m.Paid && !m.Closed).Select(m => $"{m.Year:D4}-{m.Month:D2}")),
            };
        });
    }
}
