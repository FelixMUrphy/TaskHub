using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TaskHub;

/// <summary>The work-hours module's file, worktime.db in the data folder: raw intervals and what the user decided. Norm, deficit, overtime and
/// money are never stored, they're worked out from this (<see cref="WorktimeCalc"/>). Created when the module is turned on, not before.
/// Times are whole UTC seconds. An interval has a start and an end, a start alone (it's running), or a duration alone (the day's total, without times).
/// Not thread-safe: <see cref="Worktime"/> holds its lock around a whole call.</summary>
sealed class WorktimeStore
{
    const int SchemaVersion = 1;
    readonly string connection;

    public WorktimeStore(string path) =>
        connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();   // no pool: the file isn't held between calls

    public sealed record Session(long Id, DateOnly WorkDate, long? Started, long? Finished, bool Open);
    public sealed record Interval(long Id, long SessionId, DateOnly WorkDate, long? Start, long? End, long? Duration, string Source, string Note)
    {
        public bool Running => Start != null && End == null && Duration == null;
        public long Seconds(long now) => Duration ?? (Start is long s ? Math.Max(0, (End ?? now) - s) : 0);
    }

    public static string D(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    static DateOnly ParseD(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One connection and one transaction: all of it happens, or none.</summary>
    public T Tx<T>(Func<Db, T> f)
    {
        using var c = new SqliteConnection(connection);
        c.Open();
        Migrate(c);
        using var t = c.BeginTransaction();
        var r = f(new Db(c, t));
        t.Commit();
        return r;
    }

    public void Tx(Action<Db> f) => Tx<object>(db => { f(db); return null; });

    static void Migrate(SqliteConnection c)
    {
        using var ask = c.CreateCommand();
        ask.CommandText = "PRAGMA user_version";
        var have = Convert.ToInt32(ask.ExecuteScalar());
        if (have > SchemaVersion) throw new ApiError(409, "worktime.db comes from a newer TaskHub");
        if (have == SchemaVersion) { Extras(c); return; }
        using var t = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = $"""
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE sessions(id INTEGER PRIMARY KEY, work_date TEXT NOT NULL, started_utc INTEGER, finished_utc INTEGER, open INTEGER NOT NULL);
            CREATE INDEX sessions_date ON sessions(work_date);
            CREATE TABLE intervals(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id), start_utc INTEGER, end_utc INTEGER,
                duration_sec INTEGER, source TEXT NOT NULL, note TEXT NOT NULL DEFAULT '');
            CREATE INDEX intervals_session ON intervals(session_id);
            CREATE TABLE day_marks(date TEXT PRIMARY KEY, kind TEXT NOT NULL, source TEXT NOT NULL);
            CREATE TABLE settings_history(valid_from TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE month_close(month TEXT PRIMARY KEY, deficit_closed_by_weekend INTEGER NOT NULL, deficit_closed_by_holiday INTEGER NOT NULL,
                carry INTEGER NOT NULL, deficit_carried INTEGER NOT NULL, deficit_written_off INTEGER NOT NULL, paid_overtime_weekday INTEGER NOT NULL,
                paid_overtime_weekend INTEGER NOT NULL, paid_overtime_holiday INTEGER NOT NULL, salary_part TEXT NOT NULL, rate TEXT NOT NULL,
                currency TEXT NOT NULL, fx_date TEXT);
            CREATE TABLE rates(date TEXT NOT NULL, source TEXT NOT NULL, currency TEXT NOT NULL, rate TEXT NOT NULL, PRIMARY KEY(date, source, currency));
            CREATE TABLE change_log(id INTEGER PRIMARY KEY, work_date TEXT NOT NULL, at_utc INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL);
            CREATE INDEX change_log_date ON change_log(work_date, id);
            PRAGMA user_version = {SchemaVersion};
            """;
        cmd.ExecuteNonQuery();
        t.Commit();
        Extras(c);
    }

    /// <summary>Tables added after the first schema. They come with "if not exists" and leave user_version alone, so an older TaskHub still opens the
    /// file and only doesn't see them: holidays fetched (a year of a country, as the source gave it), the months whose holidays were confirmed, and
    /// holiday names (from the source, or the user's own).</summary>
    static void Extras(SqliteConnection c)
    {
        using var ask = c.CreateCommand();
        ask.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('holiday_cache','holiday_review','day_names')";
        if (Convert.ToInt32(ask.ExecuteScalar()) == 3) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS holiday_cache(country TEXT NOT NULL, year INTEGER NOT NULL, fetched_utc INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(country, year));
            CREATE TABLE IF NOT EXISTS holiday_review(month TEXT PRIMARY KEY, at_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS day_names(date TEXT PRIMARY KEY, name TEXT NOT NULL, own INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public sealed class Db
    {
        readonly SqliteConnection c;
        readonly SqliteTransaction t;
        public Db(SqliteConnection c, SqliteTransaction t) { this.c = c; this.t = t; }

        SqliteCommand Cmd(string sql, params (string k, object v)[] args)
        {
            var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd;
        }

        void Run(string sql, params (string k, object v)[] args) { using var cmd = Cmd(sql, args); cmd.ExecuteNonQuery(); }

        long Insert(string sql, params (string k, object v)[] args)
        {
            Run(sql, args);
            using var cmd = Cmd("SELECT last_insert_rowid()");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        List<T> Rows<T>(string sql, Func<SqliteDataReader, T> row, params (string k, object v)[] args)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(row(r));
            return list;
        }

        static long? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

        // ---- meta
        public string Meta(string key) => Rows("SELECT value FROM meta WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();
        public void SetMeta(string key, string value) => Run("INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)", ("$k", key), ("$v", value));

        // ---- settings
        public List<(DateOnly From, string Json)> Settings() =>
            Rows("SELECT valid_from, json FROM settings_history ORDER BY valid_from", r => (ParseD(r.GetString(0)), r.GetString(1)));
        public void MoveSettings(DateOnly from, DateOnly to) =>
            Run("UPDATE settings_history SET valid_from = $t WHERE valid_from = $f", ("$f", D(from)), ("$t", D(to)));
        public void SetSettings(DateOnly from, string json) =>
            Run("INSERT OR REPLACE INTO settings_history(valid_from, json) VALUES($d, $j)", ("$d", D(from)), ("$j", json));

        // ---- sessions
        static Session AsSession(SqliteDataReader r) => new(r.GetInt64(0), ParseD(r.GetString(1)), N(r, 2), N(r, 3), r.GetInt64(4) != 0);
        const string SessionCols = "id, work_date, started_utc, finished_utc, open";

        public Session GetSession(long id) => Rows($"SELECT {SessionCols} FROM sessions WHERE id=$i", AsSession, ("$i", id)).FirstOrDefault();
        public Session OpenSession() => Rows($"SELECT {SessionCols} FROM sessions WHERE open=1 ORDER BY id DESC LIMIT 1", AsSession).FirstOrDefault();
        public List<Session> SessionsOf(DateOnly d) => Rows($"SELECT {SessionCols} FROM sessions WHERE work_date=$d ORDER BY id", AsSession, ("$d", D(d)));
        public long AddSession(DateOnly d, long? started, long? finished, bool open) =>
            Insert("INSERT INTO sessions(work_date, started_utc, finished_utc, open) VALUES($d, $s, $f, $o)", ("$d", D(d)), ("$s", started), ("$f", finished), ("$o", open ? 1 : 0));
        public void SetSessionTimes(long id, long? started, long? finished, bool open) =>
            Run("UPDATE sessions SET started_utc=$s, finished_utc=$f, open=$o WHERE id=$i", ("$s", started), ("$f", finished), ("$o", open ? 1 : 0), ("$i", id));
        public void DeleteSession(long id) => Run("DELETE FROM sessions WHERE id=$i", ("$i", id));

        // ---- intervals
        static Interval AsInterval(SqliteDataReader r) =>
            new(r.GetInt64(0), r.GetInt64(1), ParseD(r.GetString(2)), N(r, 3), N(r, 4), N(r, 5), r.GetString(6), r.GetString(7));
        const string IntervalCols = "i.id, i.session_id, s.work_date, i.start_utc, i.end_utc, i.duration_sec, i.source, i.note";
        const string IntervalFrom = "FROM intervals i JOIN sessions s ON s.id = i.session_id";

        public Interval GetInterval(long id) => Rows($"SELECT {IntervalCols} {IntervalFrom} WHERE i.id=$i", AsInterval, ("$i", id)).FirstOrDefault();
        public List<Interval> IntervalsOfSession(long id) => Rows($"SELECT {IntervalCols} {IntervalFrom} WHERE i.session_id=$i ORDER BY i.id", AsInterval, ("$i", id));
        public List<Interval> IntervalsBetween(DateOnly from, DateOnly to) =>
            Rows($"SELECT {IntervalCols} {IntervalFrom} WHERE s.work_date BETWEEN $a AND $b ORDER BY s.work_date, i.id", AsInterval, ("$a", D(from)), ("$b", D(to)));
        public long AddInterval(long session, long? start, long? end, long? duration, string source, string note) =>
            Insert("INSERT INTO intervals(session_id, start_utc, end_utc, duration_sec, source, note) VALUES($s, $a, $b, $d, $o, $n)",
                ("$s", session), ("$a", start), ("$b", end), ("$d", duration), ("$o", source), ("$n", note ?? ""));
        public void SetInterval(long id, long? start, long? end, long? duration, string note) =>
            Run("UPDATE intervals SET start_utc=$a, end_utc=$b, duration_sec=$d, note=$n WHERE id=$i",
                ("$a", start), ("$b", end), ("$d", duration), ("$n", note ?? ""), ("$i", id));
        public void DeleteInterval(long id) => Run("DELETE FROM intervals WHERE id=$i", ("$i", id));

        /// <summary>An interval with times that lies over this one; a running one reaches until <paramref name="now"/>. Duration-only ones have no place in time.</summary>
        public Interval Overlapping(long start, long end, long now, long exceptId = 0) =>
            Rows($"SELECT {IntervalCols} {IntervalFrom} WHERE i.id<>$x AND i.start_utc IS NOT NULL AND i.start_utc < $e AND COALESCE(i.end_utc, $n) > $s LIMIT 1",
                AsInterval, ("$x", exceptId), ("$e", end), ("$s", start), ("$n", now)).FirstOrDefault();

        // ---- what a day is
        public List<(DateOnly Date, string Kind, string Source)> Marks(DateOnly from, DateOnly to) =>
            Rows("SELECT date, kind, source FROM day_marks WHERE date BETWEEN $a AND $b ORDER BY date", r => (ParseD(r.GetString(0)), r.GetString(1), r.GetString(2)), ("$a", D(from)), ("$b", D(to)));
        public void SetMark(DateOnly d, string kind, string source) =>
            Run("INSERT OR REPLACE INTO day_marks(date, kind, source) VALUES($d, $k, $s)", ("$d", D(d)), ("$k", kind), ("$s", source));
        public void ClearMark(DateOnly d) => Run("DELETE FROM day_marks WHERE date=$d", ("$d", D(d)));

        // ---- closed months
        public List<(int Year, int Month, WorktimeCalc.MonthClose Close)> Closes() =>
            Rows("SELECT month, deficit_closed_by_weekend, deficit_closed_by_holiday, carry FROM month_close", r =>
            {
                var m = r.GetString(0);
                return (int.Parse(m[..4]), int.Parse(m[5..7]), new WorktimeCalc.MonthClose(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3) != 0));
            });
        public bool MonthClosed(DateOnly d) =>
            Rows("SELECT 1 FROM month_close WHERE month=$m", r => 1, ("$m", d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))).Count > 0;

        public (long Fetched, string Json)? HolidayCache(string country, int year) =>
            Rows("SELECT fetched_utc, json FROM holiday_cache WHERE country=$c AND year=$y", r => ((long, string)?)(r.GetInt64(0), r.GetString(1)), ("$c", country), ("$y", year)).FirstOrDefault();
        public void SetHolidayCache(string country, int year, long fetched, string json) =>
            Run("INSERT OR REPLACE INTO holiday_cache(country, year, fetched_utc, json) VALUES($c, $y, $f, $j)", ("$c", country), ("$y", year), ("$f", fetched), ("$j", json));
        public bool Reviewed(string month) => Rows("SELECT 1 FROM holiday_review WHERE month=$m", r => 1, ("$m", month)).Count > 0;
        public void SetReviewed(string month, long at) => Run("INSERT OR REPLACE INTO holiday_review(month, at_utc) VALUES($m, $t)", ("$m", month), ("$t", at));
        public Dictionary<DateOnly, (string Name, bool Own)> Names(DateOnly from, DateOnly to) =>
            Rows("SELECT date, name, own FROM day_names WHERE date BETWEEN $a AND $b", r => (ParseD(r.GetString(0)), r.GetString(1), r.GetInt64(2) != 0), ("$a", D(from)), ("$b", D(to)))
                .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));
        public void SetName(DateOnly d, string name, bool own) =>
            Run("INSERT OR REPLACE INTO day_names(date, name, own) VALUES($d, $n, $o)", ("$d", D(d)), ("$n", name), ("$o", own ? 1 : 0));
        public void ClearName(DateOnly d) => Run("DELETE FROM day_names WHERE date=$d", ("$d", D(d)));

        public void AddClose(string month, WorktimeCalc.MonthClose c, WorktimeCalc.MonthResult r, decimal rate, string currency) =>
            Run("""
                INSERT INTO month_close(month, deficit_closed_by_weekend, deficit_closed_by_holiday, carry, deficit_carried, deficit_written_off,
                    paid_overtime_weekday, paid_overtime_weekend, paid_overtime_holiday, salary_part, rate, currency, fx_date)
                VALUES($m, $w, $h, $c, $dc, $dw, $pw, $pe, $ph, $s, $r, $cu, NULL)
                """, ("$m", month), ("$w", c.ClosedByWeekend), ("$h", c.ClosedByHoliday), ("$c", c.Carry ? 1 : 0), ("$dc", r.Carried), ("$dw", r.WrittenOff),
                ("$pw", r.Weekday), ("$pe", r.Weekend), ("$ph", r.Holiday), ("$s", r.SalaryPart.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("$r", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$cu", currency));
        // ---- exchange rates: units of the currency for one unit of the source's base, on the rate's own date; the history is kept
        public (DateOnly Date, decimal Rate)? Rate(string source, string currency, DateOnly onOrBefore) =>
            Rows("SELECT date, rate FROM rates WHERE source=$s AND currency=$c AND date<=$d ORDER BY date DESC LIMIT 1",
                r => ((DateOnly, decimal)?)(ParseD(r.GetString(0)), decimal.Parse(r.GetString(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)),
                ("$s", source), ("$c", currency), ("$d", D(onOrBefore))).FirstOrDefault();
        public void SetRate(DateOnly d, string source, string currency, decimal rate) =>
            Run("INSERT OR REPLACE INTO rates(date, source, currency, rate) VALUES($d, $s, $c, $r)", ("$d", D(d)), ("$s", source), ("$c", currency),
                ("$r", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public string LastRateDate(string source) => Rows("SELECT max(date) FROM rates WHERE source=$s", r => r.IsDBNull(0) ? null : r.GetString(0), ("$s", source)).FirstOrDefault();

        public void DeleteClose(string month) => Run("DELETE FROM month_close WHERE month=$m", ("$m", month));
        public JsonObject CloseRow(string month) =>
            Rows("""
                SELECT deficit_closed_by_weekend, deficit_closed_by_holiday, carry, deficit_carried, deficit_written_off, paid_overtime_weekday,
                    paid_overtime_weekend, paid_overtime_holiday, salary_part, currency FROM month_close WHERE month=$m
                """, r => new JsonObject
                {
                    ["closed_by_weekend_seconds"] = r.GetInt64(0), ["closed_by_holiday_seconds"] = r.GetInt64(1), ["carry"] = r.GetInt64(2) != 0,
                    ["carried_seconds"] = r.GetInt64(3), ["written_off_seconds"] = r.GetInt64(4), ["paid_weekday_seconds"] = r.GetInt64(5),
                    ["paid_weekend_seconds"] = r.GetInt64(6), ["paid_holiday_seconds"] = r.GetInt64(7),
                    ["salary_part"] = decimal.Parse(r.GetString(8), System.Globalization.CultureInfo.InvariantCulture), ["currency"] = r.GetString(9),
                }, ("$m", month)).FirstOrDefault();

        // ---- the log of manual changes
        public void LogChange(DateOnly d, long at, string action, JsonNode before, JsonNode after) =>
            Run("INSERT INTO change_log(work_date, at_utc, action, before_json, after_json) VALUES($d, $t, $a, $b, $c)",
                ("$d", D(d)), ("$t", at), ("$a", action), ("$b", before.ToJsonString()), ("$c", after.ToJsonString()));
        public List<(long Id, long At, string Action, string Before, string After)> History(DateOnly d) =>
            Rows("SELECT id, at_utc, action, before_json, after_json FROM change_log WHERE work_date=$d ORDER BY id DESC LIMIT 200",
                r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$d", D(d)));
    }
}
