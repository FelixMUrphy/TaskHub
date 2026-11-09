using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>Reminders: a session left open (paused too long, or still open late), work not started by a set time on a working day, and a month to
/// close or holidays to confirm while the Work hours page isn't opened. A reminder changes nothing by itself: its buttons do, through the same
/// actions as the panel. The host shows it (a toast at the PC, else the messengers) and checks it about once a minute.</summary>
sealed partial class Worktime
{
    /// <summary>A reminder to show. "open" and "start" carry buttons (id, label); "need" is a plain notice. AtPc: as a toast, else to the messengers
    /// when <see cref="Messengers"/> allows it.</summary>
    public sealed record Reminder(string Kind, string Title, string Message, IReadOnlyList<KeyValuePair<string, string>> Buttons, bool AtPc, string To);

    /// <summary>Set by the host: shows a reminder, or with null takes away the one shown.</summary>
    public Action<Reminder> Remind;

    const double AtPcIdleSec = 300;          // "at the PC": input within 5 minutes and the session not locked
    const long SnoozeSec = 2 * 3600;         // "Still working": quiet for 2 hours
    const int NoticeHour = 10;               // a month to close or holidays to confirm: said once a day from 10:00
    const long ShowFinishAtSec = 300;
    const long LateGraceSec = 3600;          // "still open after": not within the first hour of a session        // "Finish at" only when the last activity is at least 5 minutes ago

    // the reminder on screen, under gate
    string remindKind;                       // "open", "start" or null
    long remindSent, remindSession, snoozeSession, snoozeUntil, finishAt, startFrom;
    DateOnly startAsked, noticeDay, pageDay;

    static void CheckReminders(JsonObject s)
    {
        foreach (var k in new[] { "remind_start", "remind_open_after" })
            if (s[k] != null && !(J.IsStr(s[k]) && TimeOfDayRx.IsMatch(J.Str(s[k])))) throw new ApiError(400, k + " must be a time like 09:00, or null for off");
        if (s["remind_paused_hours"] != null && Range(s, "remind_paused_hours", 0, 24) <= 0) throw new ApiError(400, "remind_paused_hours must be above 0, or null for off");
        Range(s, "remind_repeat_minutes", 5, 1440);
        if (!J.IsBool(s["remind_messengers"])) throw new ApiError(400, "remind_messengers must be true or false");
        if (!(J.IsStr(s["remind_to"]) && J.Str(s["remind_to"]) is "none" or "telegram" or "discord" or "all")) throw new ApiError(400, "remind_to must be none, telegram, discord or all");
    }

    /// <summary>The clock the reminders run on (the test clock included).</summary>
    internal static long Clock => Now();

    static int MinuteOf(string hhmm) => int.Parse(hhmm[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(hhmm[3..], CultureInfo.InvariantCulture);
    static int MinuteOf(long utc) { var t = DateTimeOffset.FromUnixTimeSeconds(utc).LocalDateTime; return t.Hour * 60 + t.Minute; }

    /// <summary>The local moment of a time of day on a date.</summary>
    static long At(DateOnly d, string hhmm) =>
        new DateTimeOffset(d.ToDateTime(new TimeOnly(MinuteOf(hhmm) / 60, MinuteOf(hhmm) % 60), DateTimeKind.Local)).ToUnixTimeSeconds();

    /// <summary>The page's locale for the UI language (ui/i18n.js LOCALE), so a time looks the same in a reminder and on the panel.</summary>
    static readonly Dictionary<string, string> Locale = new()
    {
        ["en"] = "en-GB", ["uk"] = "uk-UA", ["es"] = "es-ES", ["de"] = "de-DE", ["fr"] = "fr-FR", ["it"] = "it-IT", ["pt"] = "pt-BR", ["pl"] = "pl-PL", ["zh"] = "zh-CN", ["ja"] = "ja-JP",
    };

    static CultureInfo Ui() { try { return CultureInfo.GetCultureInfo(Locale.GetValueOrDefault(L.Lang, L.Lang)); } catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; } }
    static string LocalTime(long utc) => DateTimeOffset.FromUnixTimeSeconds(utc).LocalDateTime.ToString("t", Ui());

    /// <summary>One check: what is due at <paramref name="now"/> is shown (or sent again after the repeat time), what no longer is goes.
    /// <paramref name="idle"/>: seconds since the last input on this PC; <paramref name="awayOn"/>: the last input of a day before the PC was locked or
    /// went to sleep (the input that wakes it says nothing about when work stopped).</summary>
    public JsonObject RemindTick(long now, double idle, bool locked, Func<DateOnly, long?> awayOn = null)
    {
        var show = new List<Reminder>();
        bool hide = false;
        lock (gate)
        {
            string due = null;
            Reminder r = null, notice = null;
            if (Enabled)
                try { (due, r, notice) = Store.Tx(db => Due(db, now, idle, locked, awayOn)); }
                catch (Exception e) when (e is SqliteException or ApiError) { }   // a damaged or empty file reminds of nothing; the panel says so
            if (due == null) { hide = remindKind != null; remindKind = null; }
            else if (r != null) { show.Add(r); remindKind = due; remindSent = now; }
            if (notice != null) show.Add(notice);
        }
        var sent = new JsonArray();
        if (hide) Try(null);
        foreach (var x in show)
        {
            Try(x);
            sent.Add(new JsonObject
            {
                ["kind"] = x.Kind, ["title"] = x.Title, ["message"] = x.Message, ["at_pc"] = x.AtPc, ["to"] = x.To,
                ["buttons"] = new JsonArray(x.Buttons.Select(b => (JsonNode)new JsonObject { ["id"] = b.Key, ["label"] = b.Value }).ToArray()),
            });
        }
        string shown;
        lock (gate) shown = remindKind;
        return new JsonObject { ["shown"] = shown, ["hidden"] = hide, ["sent"] = sent };

        void Try(Reminder x) { try { Remind?.Invoke(x); } catch (Exception e) { Log.W("worktime", "reminder: " + e.Message); } }
    }

    /// <summary>Under gate: what is due. The kind on screen, the reminder to send now (null: not now), a notice to send.</summary>
    (string Due, Reminder Send, Reminder Notice) Due(WorktimeStore.Db db, long now, double idle, bool locked, Func<DateOnly, long?> awayOn)
    {
        var today = LocalDate(now);
        var versions = db.Settings();
        if (versions.Count == 0 || db.Meta("start_date") is not { } startText) return (null, null, null);
        var current = versions.Last(x => x.From <= today || x.From == versions[0].From);
        var s = Filled(current.Json);
        bool atPc = idle < AtPcIdleSec && !locked;
        var to = J.Str(s["remind_to"]);
        double repeat = (J.Num(s["remind_repeat_minutes"]) ?? 30) * 60;
        Reminder Make(string kind, string title, string msg, params KeyValuePair<string, string>[] b) => new(kind, title, msg, b, atPc, to);
        static KeyValuePair<string, string> B(string id, string label) => new(id, label);

        string due = null;
        Reminder send = null;
        var open = db.OpenSession();
        if (open != null)
        {
            var ints = db.IntervalsOfSession(open.Id);
            var running = ints.FirstOrDefault(x => x.Running);
            long? pausedSince = running == null ? ints.Where(x => x.End != null).Select(x => x.End).Max() : null;
            double? pausedH = J.Num(s["remind_paused_hours"]);
            var after = s["remind_open_after"] == null ? null : J.Str(s["remind_open_after"]);
            bool paused = pausedH != null && pausedSince != null && now - pausedSince.Value >= pausedH.Value * 3600;
            // late: from the set time on the session's day, and not within an hour of its start (a session begun at 23:30 isn't "still open" at once)
            bool late = after != null && now >= Math.Max(At(open.WorkDate, after), (open.Started ?? 0) + LateGraceSec);
            bool snoozed = snoozeSession == open.Id && now < snoozeUntil;
            if ((paused || late) && !snoozed)
            {
                due = "open";
                if (remindKind != "open" || remindSession != open.Id || now - remindSent >= repeat)
                {
                    long last = pausedSince ?? Math.Max(running?.Start ?? now, now - (long)idle);
                    // a session over midnight with the PC used again since: the last input of its own day, before the lock or sleep
                    if (running != null && open.WorkDate < today && LocalDate(now - (long)idle) > open.WorkDate && awayOn?.Invoke(open.WorkDate) is long away && away > running.Start)
                        last = away;
                    finishAt = Math.Min(last, now);
                    remindSession = open.Id;
                    var msg = paused ? L.T("wt_r_paused", LocalTime(pausedSince.Value))
                        : L.T("wt_r_late", open.Started is long st ? (LocalDate(st) == today ? LocalTime(st) : DateTimeOffset.FromUnixTimeSeconds(st).LocalDateTime.ToString("g", Ui())) : "");
                    // each button carries its session (and time): one pressed in an older message acts on what it says, or is refused
                    var sid = open.Id.ToString(CultureInfo.InvariantCulture);
                    var buttons = new List<KeyValuePair<string, string>> { B("finish_now." + sid, L.T("wt_r_finish_now")) };
                    if (now - finishAt >= ShowFinishAtSec) buttons.Add(B($"finish_at.{sid}.{finishAt}", L.T("wt_r_finish_at", LocalTime(finishAt))));
                    buttons.Add(B("still." + sid, L.T("wt_r_still")));
                    send = Make("open", L.T("wt_r_open_t"), msg, buttons.ToArray());
                }
            }
        }
        else if (s["remind_start"] != null && J.Str(s["remind_start"]) is { } start && MinuteOf(now) >= MinuteOf(start) && today >= ParseDate(startText, "start_date")
                 && WorktimeCalc.IsWorkWeekday(RulesOf(current.From, current.Json), today) && db.Marks(today, today).Count == 0
                 && db.SessionsOf(today).Count == 0 && db.Meta("remind_skip_day") != WorktimeStore.D(today))
        {
            due = "start";
            if (remindKind != "start" || startAsked != today)
            {
                startAsked = today;
                startFrom = At(today, start);
                var day = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                send = Make("start", L.T("wt_r_start_t"), L.T("wt_r_start", LocalTime(startFrom)),
                    B("start_now." + day, L.T("wt_r_start_now")), B($"start_from.{day}.{startFrom}", L.T("wt_r_start_from", LocalTime(startFrom))), B("not_today." + day, L.T("wt_r_not_today")));
            }
        }

        Reminder notice = null;
        if (noticeDay != today && pageDay != today && MinuteOf(now) >= NoticeHour * 60 && db.Meta("remind_notice_day") != WorktimeStore.D(today))
        {
            var c = CalcOf(db, today, now);
            var lines = WorktimeCalc.Compute(c.Input).Months.Where(m => m.Paid && !m.Closed)
                .Select(m => L.T("wt_r_close", new DateTime(m.Year, m.Month, 1).ToString("Y", Ui()))).ToList();
            if (HolidaysToConfirm(db, c.Settings, today) != null) lines.Add(L.T("wt_r_hol"));
            if (lines.Count > 0)
            {
                noticeDay = today;
                db.SetMeta("remind_notice_day", WorktimeStore.D(today));   // once a day, also over a restart
                notice = Make("need", L.T("wt_r_need_t"), string.Join("\n", lines));
            }
        }
        return (due, send, notice);
    }

    /// <summary>A reminder's button (panel, toast, Telegram, Discord): the same action as the panel's. Refused when that reminder is no longer due.</summary>
    public JsonObject Answer(string button)
    {
        // name.session[.time] for a session left open, name.yyyyMMdd[.time] for a day not started
        var part = (button ?? "").Split('.');
        string name = part[0];
        long at = part.Length > 2 && long.TryParse(part[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0;
        bool ofOpen = name is "finish_now" or "finish_at" or "still";
        if (!ofOpen && name is not ("start_now" or "start_from" or "not_today")) throw new ApiError(404, $"no button '{J.Cut(button, 40)}'");
        string kind;
        long session;
        lock (gate) { kind = remindKind; session = remindSession; }
        bool same = part.Length > 1 && (ofOpen ? part[1] == session.ToString(CultureInfo.InvariantCulture) : part[1] == Today().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        if (kind != (ofOpen ? "open" : "start") || !same || (name is "finish_at" or "start_from" && at == 0))
            throw new ApiError(409, "that reminder is no longer due") { Stale = true };
        JsonObject r;
        switch (name)
        {
            case "finish_now": r = Finish(); break;
            case "finish_at": r = FinishAt(at); break;
            case "start_now": r = Start("reminder"); break;
            case "start_from": r = StartAt(at, "reminder"); break;
            case "still":
                lock (gate) { snoozeSession = session; snoozeUntil = Math.Max(Now(), remindSent) + SnoozeSec; }   // from the check that asked (a test checks ahead of the clock)
                r = Status();
                break;
            case "not_today":
                lock (gate) { On(); Store.Tx(db => db.SetMeta("remind_skip_day", WorktimeStore.D(Today()))); }
                r = Status();
                break;
            default: throw new ApiError(404, $"no button '{J.Cut(button, 40)}'");
        }
        lock (gate) remindKind = null;
        Log.I("worktime", "reminder answered: " + button);
        return r;
    }

    /// <summary>The Work hours page was looked at today: the month's notices aren't sent today.</summary>
    void PageSeen() => pageDay = Today();
}
