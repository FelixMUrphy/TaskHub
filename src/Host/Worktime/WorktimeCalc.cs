namespace TaskHub;

/// <summary>The work-hours module's arithmetic: the pure part, no clock, no files, no state of its own, tested in tests/unit. Everything is
/// worked out again from the raw hours of each day, so an edit of any interval comes out right. Time in whole seconds, money in the salary's
/// currency. Rules that change apply from their date on: history keeps the rules of its own day.
/// A deficit is hours missing from the norm; it never expires. Hours above a day's norm first pay it off, oldest first, the rest goes to the
/// overtime pool of that day's type. A short day draws at once only on the weekday pool; the weekend and holiday pools wait for the month's
/// closing. A deficit carried from an earlier month was paid then, so closing it earns nothing again.</summary>
static class WorktimeCalc
{
    public enum DayKind { Workday, Weekend, Holiday, Vacation }
    public enum PoolKind { Weekday, Weekend, Holiday }
    public enum BarState { Under, Normal, Over }

    /// <summary>Tolerance of a bar's colour never goes below this: in the morning a percentage of almost nothing would flip it at once.</summary>
    public const long MinToleranceSec = 300;

    /// <summary>The settings from a date on. <paramref name="HolidayMult"/> null: follows <paramref name="WeekendMult"/>.
    /// <paramref name="ManualRate"/> null: salary / the month's norm.</summary>
    public sealed record Rules(DateOnly ValidFrom, long WeeklyNormSec, DayOfWeek[] WorkDays, decimal Salary, decimal? ManualRate,
        decimal WeekdayMult, decimal WeekendMult, decimal? HolidayMult, bool HolidaysReduceNorm, bool DeductUnclosed)
    {
        public long DailyNormSec => WorkDays.Length > 0 ? WeeklyNormSec / WorkDays.Length : 0;
        public decimal Mult(PoolKind k) => k switch { PoolKind.Weekday => WeekdayMult, PoolKind.Weekend => WeekendMult, _ => HolidayMult ?? WeekendMult };
    }

    /// <summary>A date's work. <paramref name="Open"/>: a session of that date is still running or paused, so the day isn't closed.</summary>
    public sealed record DayWork(long Seconds, bool Open = false);

    /// <summary>The user's answers when a month was closed. <paramref name="Carry"/>: what's left of the deficit goes on to the next month;
    /// no: it's deducted from the salary or written off, as the rules say.</summary>
    public sealed record MonthClose(long ClosedByWeekend, long ClosedByHoliday, bool Carry);

    public sealed record Input(IReadOnlyList<Rules> Rules, DateOnly Start, DateOnly Today, IReadOnlyDictionary<DateOnly, DayWork> Days,
        IReadOnlySet<DateOnly> Holidays, IReadOnlySet<DateOnly> Vacations, IReadOnlyDictionary<(int Year, int Month), MonthClose> Closes,
        long OpeningDeficit = 0, long OpeningWeekday = 0, long OpeningWeekend = 0, long OpeningHoliday = 0);

    public sealed record DayRow(DateOnly Date, DayKind Kind, long Worked, long Norm, bool Closed, long ToDeficit, long PaidOff, long ToPool, PoolKind Pool, long DeficitAfter);

    /// <summary>A month's result. <paramref name="Paid"/>: its overtime was fixed by the closing (or, while the closing's unanswered, is shown
    /// as if it had been); the pools then start empty.</summary>
    public sealed record MonthResult(int Year, int Month, decimal SalaryPart, decimal OvertimeMoney, long NormSec, long NormNoVacationSec,
        long Weekday, long Weekend, long Holiday, long DeficitAfter, long Carried, long WrittenOff, bool Closed, bool Paid);

    public sealed record Result(IReadOnlyList<DayRow> Days, IReadOnlyList<MonthResult> Months, long Deficit, long Weekday, long Weekend, long Holiday)
    {
        public MonthResult Current => Months.Count > 0 ? Months[^1] : null;
        public decimal Earned => Current == null ? 0 : Current.SalaryPart + Current.OvertimeMoney;
    }

    public sealed record Bar(long Worked, long Full, long Expected, long Balance, long Tolerance, BarState State)
    {
        public long Above => Math.Max(0, Worked - Full);
        public double Fill => Full > 0 ? Math.Min(1, (double)Worked / Full) : Worked > 0 ? 1 : 0;
    }

    public static Rules RulesAt(IReadOnlyList<Rules> all, DateOnly d)
    {
        Rules found = all[0];   // a date before the first version goes by the first
        foreach (var r in all) if (r.ValidFrom <= d && r.ValidFrom >= found.ValidFrom) found = r;
        return found;
    }

    public static bool IsWorkWeekday(Rules r, DateOnly d) => Array.IndexOf(r.WorkDays, d.DayOfWeek) >= 0;

    public static DayKind KindOf(Input i, Rules r, DateOnly d) =>
        i.Holidays.Contains(d) ? DayKind.Holiday : i.Vacations.Contains(d) && IsWorkWeekday(r, d) ? DayKind.Vacation : IsWorkWeekday(r, d) ? DayKind.Workday : DayKind.Weekend;

    static PoolKind PoolOf(DayKind kind, Rules r, DateOnly d) =>
        kind == DayKind.Holiday ? PoolKind.Holiday : IsWorkWeekday(r, d) ? PoolKind.Weekday : PoolKind.Weekend;

    static IEnumerable<DateOnly> DaysOf(int y, int m)
    {
        for (int n = 1, last = DateTime.DaysInMonth(y, m); n <= last; n++) yield return new DateOnly(y, m, n);
    }

    /// <summary>What the salary pays for: the whole calendar month with the vacations left in, whenever counting started.</summary>
    public static long NormNoVacation(Input i, int y, int m)
    {
        long sum = 0;
        foreach (var d in DaysOf(y, m))
        {
            var r = RulesAt(i.Rules, d);
            if (IsWorkWeekday(r, d) && !(i.Holidays.Contains(d) && r.HolidaysReduceNorm)) sum += r.DailyNormSec;
        }
        return sum;
    }

    /// <summary>The month's norm for its bar: from the start date on, vacations out; a holiday stays in where it doesn't reduce the norm.</summary>
    public static long NormOfMonth(Input i, int y, int m)
    {
        long sum = 0;
        foreach (var d in DaysOf(y, m)) if (d >= i.Start) sum += MonthNormOfDay(i, d);
        return sum;
    }

    static long MonthNormOfDay(Input i, DateOnly d)
    {
        var r = RulesAt(i.Rules, d);
        return KindOf(i, r, d) switch
        {
            DayKind.Workday => r.DailyNormSec,
            DayKind.Holiday when IsWorkWeekday(r, d) && !r.HolidaysReduceNorm => r.DailyNormSec,
            _ => 0,
        };
    }

    public static decimal RateAt(Input i, Rules r, DateOnly d)
    {
        if (r.ManualRate is decimal manual) return manual;
        long norm = NormNoVacation(i, d.Year, d.Month);
        return norm > 0 ? r.Salary * 3600 / norm : 0;
    }

    /// <summary>Which closing order the money asks for: the lower multiplier first, the weekend's when they're equal.</summary>
    public static (long Weekend, long Holiday) SplitClosing(Rules r, long hours, long weekendPool, long holidayPool)
    {
        hours = Math.Max(0, hours);
        if (r.Mult(PoolKind.Holiday) < r.Mult(PoolKind.Weekend))
        {
            long h = Math.Min(hours, holidayPool);
            return (Math.Min(hours - h, weekendPool), h);
        }
        long w = Math.Min(hours, weekendPool);
        return (w, Math.Min(hours - w, holidayPool));
    }

    sealed class Pool
    {
        public long Sec;
        public decimal Money;
        public void Add(long s, decimal perHour) { Sec += s; Money += s * perHour / 3600; }
        public void Take(long s)
        {
            if (s <= 0 || Sec <= 0) return;
            Money -= Money * s / Sec;   // at the mean value: the spec's sum wherever the rules didn't change
            Sec -= s;
        }
        public void Clear() { Sec = 0; Money = 0; }
    }

    public static Result Compute(Input i)
    {
        var days = new List<DayRow>();
        var months = new List<MonthResult>();
        var pools = new Dictionary<PoolKind, Pool> { [PoolKind.Weekday] = new(), [PoolKind.Weekend] = new(), [PoolKind.Holiday] = new() };
        long carried = i.OpeningDeficit, current = 0;   // from earlier months (paid then), and from this one (not paid unless closed)
        var open = RulesAt(i.Rules, i.Start);
        var rate0 = RateAt(i, open, i.Start);
        pools[PoolKind.Weekday].Add(i.OpeningWeekday, rate0 * open.Mult(PoolKind.Weekday));
        pools[PoolKind.Weekend].Add(i.OpeningWeekend, rate0 * open.Mult(PoolKind.Weekend));
        pools[PoolKind.Holiday].Add(i.OpeningHoliday, rate0 * open.Mult(PoolKind.Holiday));

        var first = new DateOnly(i.Start.Year, i.Start.Month, 1);
        for (var mon = first; mon <= i.Today; mon = mon.AddMonths(1))
        {
            int y = mon.Year, m = mon.Month;
            long noVac = NormNoVacation(i, y, m);
            decimal salary = 0;
            decimal PerSec(Rules r) => noVac > 0 ? r.Salary / noVac : 0;

            void PayOff(long x, Rules r)
            {
                long a = Math.Min(x, carried);
                carried -= a;
                long b = Math.Min(x - a, current);
                current -= b;
                salary += b * PerSec(r);   // the hours of this month's norm are worked after all
            }

            foreach (var d in DaysOf(y, m))
            {
                if (d < i.Start || d > i.Today) continue;
                var r = RulesAt(i.Rules, d);
                var kind = KindOf(i, r, d);
                var pk = PoolOf(kind, r, d);
                long norm = kind == DayKind.Workday ? r.DailyNormSec : 0;
                i.Days.TryGetValue(d, out var work);
                long worked = work?.Seconds ?? 0;
                bool hasOpen = work?.Open == true;
                bool closed = !hasOpen && (d < i.Today || work != null);
                salary += Math.Min(worked, norm) * PerSec(r);
                if (kind == DayKind.Holiday && IsWorkWeekday(r, d) && !r.HolidaysReduceNorm) salary += r.DailyNormSec * PerSec(r);   // paid without work
                long over = worked - norm, toDeficit = 0, paidOff = 0, toPool = 0;
                if (over < 0 && closed)
                {
                    toDeficit = -over;
                    current += toDeficit;
                    paidOff = Math.Min(carried + current, pools[PoolKind.Weekday].Sec);
                    pools[PoolKind.Weekday].Take(paidOff);
                    PayOff(paidOff, r);
                }
                else if (over > 0)
                {
                    paidOff = Math.Min(over, carried + current);
                    PayOff(paidOff, r);
                    toPool = over - paidOff;
                    pools[pk].Add(toPool, RateAt(i, r, d) * r.Mult(pk));
                }
                days.Add(new DayRow(d, kind, worked, norm, closed, toDeficit, paidOff, toPool, pk, carried + current));
            }

            long wd = pools[PoolKind.Weekday].Sec, we = pools[PoolKind.Weekend].Sec, ho = pools[PoolKind.Holiday].Sec;
            decimal overtime = pools.Values.Sum(p => p.Money);
            bool past = new DateOnly(y, m, 1).AddMonths(1) <= new DateOnly(i.Today.Year, i.Today.Month, 1);
            long carriedOn = 0, writtenOff = 0;
            bool answered = false;
            if (past)
            {
                var last = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
                var r = RulesAt(i.Rules, last);
                i.Closes.TryGetValue((y, m), out var c);
                answered = c != null;
                long byW = Math.Min(Math.Min(c?.ClosedByWeekend ?? 0, carried + current), pools[PoolKind.Weekend].Sec);
                pools[PoolKind.Weekend].Take(byW);
                PayOff(byW, r);
                long byH = Math.Min(Math.Min(c?.ClosedByHoliday ?? 0, carried + current), pools[PoolKind.Holiday].Sec);
                pools[PoolKind.Holiday].Take(byH);
                PayOff(byH, r);
                wd = pools[PoolKind.Weekday].Sec;
                we = pools[PoolKind.Weekend].Sec;
                ho = pools[PoolKind.Holiday].Sec;
                overtime = pools.Values.Sum(p => p.Money);
                if (c?.Carry ?? true)   // unanswered counts as carried, until the dialog is answered
                {
                    salary += current * PerSec(r);   // this month is paid in full; the next pays it off
                    carriedOn = carried + current;
                    carried = carriedOn;
                    current = 0;
                }
                else
                {
                    if (r.DeductUnclosed) salary = Math.Max(0, salary - carried * PerSec(r));
                    else salary += current * PerSec(r);
                    writtenOff = carried + current;
                    carried = current = 0;
                }
                foreach (var p in pools.Values) p.Clear();
            }
            months.Add(new MonthResult(y, m, salary, overtime, NormOfMonth(i, y, m), noVac, wd, we, ho, carried + current, carriedOn, writtenOff, answered, past));
        }
        return new Result(days, months, carried + current, pools[PoolKind.Weekday].Sec, pools[PoolKind.Weekend].Sec, pools[PoolKind.Holiday].Sec);
    }

    /// <summary>A bar for the days from..to. The length compares with the period's whole norm, the colour with the norm so far.
    /// <paramref name="todayElapsed"/>: time since today's session started, null when there's none. <paramref name="countToday"/>: the setting
    /// that counts today's share. A holiday that doesn't reduce the norm counts as worked in a month's bar; a week's norm goes down with it.</summary>
    public static Bar BarOf(Input i, DateOnly from, DateOnly to, bool monthBar, long? todayElapsed, bool countToday, double tolerancePct)
    {
        long worked = 0, full = 0, expected = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d < i.Start) continue;
            var r = RulesAt(i.Rules, d);
            long norm = monthBar ? MonthNormOfDay(i, d) : KindOf(i, r, d) == DayKind.Workday ? r.DailyNormSec : 0;
            bool credit = monthBar && KindOf(i, r, d) == DayKind.Holiday && norm > 0;
            full += norm;
            if (d > i.Today) continue;
            i.Days.TryGetValue(d, out var work);
            worked += (work?.Seconds ?? 0) + (credit ? norm : 0);
            if (d < i.Today || credit) expected += norm;
            else if (countToday && norm > 0)
            {
                bool closed = work != null && !work.Open;
                double share = closed ? 1 : todayElapsed is long e ? Math.Min(1, (double)e / norm) : 0;
                expected += (long)Math.Round(norm * share);
            }
        }
        long balance = worked - expected;
        long tol = Math.Max((long)Math.Round(expected * tolerancePct / 100), MinToleranceSec);
        return new Bar(worked, full, expected, balance, tol, balance < -tol ? BarState.Under : balance > tol ? BarState.Over : BarState.Normal);
    }
}
