using TaskHub;
using Xunit;
using static TaskHub.WorktimeCalc;

// The spec's examples first (.ai/worktime-spec.md is private; each case states its numbers): norm 40 h, Mon-Fri, 8 h a day.
// November 2026 starts on a Sunday: 21 workdays, 168 h; salary 3360 makes the rate 20 an hour.
public class WorktimeCalcTests
{
    const long H = 3600;
    static readonly DateOnly Mon = new(2026, 11, 2), Tue = Mon.AddDays(1), Wed = Mon.AddDays(2), Thu = Mon.AddDays(3), Fri = Mon.AddDays(4), Sat = Mon.AddDays(5), Sun = Mon.AddDays(6);
    static readonly DayOfWeek[] MonFri = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    static Rules R(DateOnly? from = null, decimal salary = 3360, decimal? manual = null, bool reduce = true, bool deduct = true, decimal? holidayMult = null, long weekly = 40 * H) =>
        new(from ?? new DateOnly(2026, 1, 1), weekly, MonFri, salary, manual, 1.5m, 2m, holidayMult, reduce, deduct);

    sealed class Case
    {
        public List<Rules> Rules = new() { R() };
        public DateOnly Start = Mon, Today = Mon.AddDays(7);
        public Dictionary<DateOnly, DayWork> Days = new();
        public HashSet<DateOnly> Holidays = new(), Vacations = new();
        public Dictionary<(int, int), MonthClose> Closes = new();
        public long OpenDeficit, OpenWeekday;
        public Case W(DateOnly d, double hours, bool open = false) { Days[d] = new DayWork((long)(hours * H), open); return this; }
        public Input In => new(Rules, Start, Today, Days, Holidays, Vacations, Closes, OpenDeficit, OpenWeekday);
        public Result Run => Compute(In);
    }

    static Case FullWeek(double fri) => new Case().W(Mon, 8).W(Tue, 8).W(Wed, 8).W(Thu, 8).W(Fri, fri);

    // ---- the spec's table
    [Fact] public void Spec_FridayShort_SaturdayPaysOff_RestToWeekendPool()
    {
        var c = FullWeek(7).W(Sat, 3);
        c.Today = Sat;
        var r = c.Run;
        Assert.Equal(1 * H, r.Days.Single(d => d.Date == Fri).ToDeficit);
        Assert.Equal(0, r.Deficit);
        Assert.Equal(2 * H, r.Weekend);
        Assert.Equal(0, r.Weekday);
    }

    [Fact] public void Spec_FourShortDays_LongFriday()
    {
        var c = new Case().W(Mon, 7).W(Tue, 7).W(Wed, 7).W(Thu, 7).W(Fri, 13);
        c.Today = Fri;
        var r = c.Run;
        Assert.Equal(4 * H, r.Days.Single(d => d.Date == Thu).DeficitAfter);
        Assert.Equal(0, r.Deficit);
        Assert.Equal(1 * H, r.Weekday);
    }

    [Fact] public void Spec_ShortTuesday_DrawsOnTheWeekdayPoolAtOnce()
    {
        var c = new Case().W(Mon, 10).W(Tue, 6);
        c.Today = Tue;
        var r = c.Run;
        Assert.Equal(0, r.Weekday);
        Assert.Equal(0, r.Deficit);
    }

    [Fact] public void Spec_ShortMonday_LeavesTheWeekendPoolAlone()
    {
        var c = new Case { Start = new DateOnly(2026, 11, 7) }.W(new DateOnly(2026, 11, 7), 5).W(new DateOnly(2026, 11, 9), 6);
        c.Today = new DateOnly(2026, 11, 9);
        var r = c.Run;
        Assert.Equal(2 * H, r.Deficit);
        Assert.Equal(5 * H, r.Weekend);
    }

    [Fact] public void Spec_NightShift_AllSixHoursBelongToFriday()
    {
        var c = FullWeek(6);   // a session 20:00 to 02:00 is handed in under its start date
        c.Today = Sat;
        var r = c.Run;
        Assert.Equal(6 * H, r.Days.Single(d => d.Date == Fri).Worked);
        Assert.Equal(0, r.Days.Single(d => d.Date == Sat).Worked);
        Assert.Equal(2 * H, r.Deficit);
        Assert.Equal(0, r.Weekend);
    }

    [Fact] public void Spec_MonthEnd_ClosingWithTheWeekend_PaysTheRest()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(3 * H, 0, Carry: false);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(0, oct.DeficitAfter);
        Assert.Equal(2 * H, oct.Weekend);
        Assert.Equal(0, oct.WrittenOff);
    }

    [Fact] public void Spec_AWeekEndingWithADeficit_GoesOnAndIsPaidOffFirst()
    {
        var c = FullWeek(5).W(new DateOnly(2026, 11, 9), 12);
        c.Today = new DateOnly(2026, 11, 9);
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(1 * H, r.Weekday);
    }

    // ---- days
    [Fact] public void Today_NotOverYet_IsNoDeficit()
    {
        var c = new Case();
        c.Today = Mon;
        Assert.Equal(0, c.Run.Deficit);
    }

    [Fact] public void Workday_WithoutASession_ClosesWithTheFullNorm()
    {
        var c = new Case();
        c.Today = Tue;
        Assert.Equal(8 * H, c.Run.Deficit);
    }

    [Fact] public void Today_FinishedShort_CountsAtOnce()
    {
        var c = new Case().W(Mon, 5);
        c.Today = Mon;
        Assert.Equal(3 * H, c.Run.Deficit);
    }

    [Fact] public void ASessionStillOpen_ClosesNothing_EvenDaysLater()
    {
        var c = new Case().W(Mon, 2, open: true);
        c.Today = Wed;
        var r = c.Run;
        Assert.False(r.Days.Single(d => d.Date == Mon).Closed);
        Assert.Equal(8 * H, r.Deficit);   // Tuesday only
    }

    [Fact] public void ASecondSessionAfterFinishing_TakesTheShortfallBack()
    {
        var c = new Case().W(Mon, 5);
        c.Today = Mon;
        Assert.Equal(3 * H, c.Run.Deficit);
        c.W(Mon, 9);
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(1 * H, r.Weekday);
    }

    [Fact] public void AnEditInThePast_ComesOutRight()
    {
        var c = new Case().W(Mon, 10).W(Tue, 6);
        c.Today = Tue;
        Assert.Equal(0, c.Run.Deficit);
        c.W(Mon, 8);
        Assert.Equal(2 * H, c.Run.Deficit);
    }

    [Fact] public void DaysBeforeTheStart_DontExist()
    {
        var c = new Case { Start = Wed };
        c.Today = Thu;
        Assert.Equal(8 * H, c.Run.Deficit);   // Wednesday only
    }

    [Fact] public void AnOpeningDeficit_IsPaidOffFirst_AndEarnsNothing()
    {
        var c = new Case { OpenDeficit = 2 * H }.W(Mon, 10);
        c.Today = Mon;
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(0, r.Weekday);
        Assert.Equal(160m, Math.Round(r.Current.SalaryPart, 6));   // 8 h at 20, the other 2 h were paid before
    }

    // ---- holidays and vacation
    [Fact] public void AHoliday_HasNoNorm_AndItsHoursGoToItsOwnPool()
    {
        var c = new Case().W(Mon, 8).W(Tue, 8).W(Wed, 3);
        c.Holidays.Add(Wed);
        c.Today = Wed;
        var r = c.Run;
        Assert.Equal(DayKind.Holiday, r.Days.Single(d => d.Date == Wed).Kind);
        Assert.Equal(3 * H, r.Holiday);
        Assert.Equal(0, r.Deficit);
    }

    [Fact] public void AHoliday_PaysOffTheDeficitFirst()
    {
        var c = new Case().W(Mon, 7).W(Tue, 8).W(Wed, 3);
        c.Holidays.Add(Wed);
        c.Today = Wed;
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(2 * H, r.Holiday);
    }

    [Fact] public void Vacation_MakesNoDeficit_AndEarnsNothing()
    {
        var c = new Case();
        c.Vacations.Add(Mon);
        c.Today = Tue;
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(0, r.Current.SalaryPart);
        Assert.Equal(168 * H, r.Current.NormNoVacationSec);   // the rate doesn't move
        Assert.Equal(20m, Math.Round(RateAt(c.In, c.Rules[0], Mon), 6));
    }

    [Fact] public void WorkOnAVacationDay_IsOvertimeOfTheDaysType()
    {
        var c = new Case().W(Mon, 4);
        c.Vacations.Add(Mon);
        c.Today = Mon;
        Assert.Equal(4 * H, c.Run.Weekday);
    }

    [Fact] public void HolidaysReduceTheNorm_RaisesTheRate()
    {
        var c = new Case();
        c.Holidays.Add(Mon);
        Assert.Equal(160 * H, Compute(c.In).Current.NormNoVacationSec);
        Assert.Equal(21m, Math.Round(RateAt(c.In, c.Rules[0], Mon), 6));
    }

    [Fact] public void AHolidayThatDoesntReduceTheNorm_IsPaidWithoutWork_SoAFullMonthGivesTheFullSalary()
    {
        var c = NovemberFull(reduce: false, holiday: Wed);
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(3360m, Math.Round(r.Current.SalaryPart, 6));
        Assert.Equal(168 * H, r.Current.NormSec);
    }

    [Fact] public void AHolidayThatReducesTheNorm_AFullMonthStillGivesTheFullSalary()
    {
        var c = NovemberFull(reduce: true, holiday: Wed);
        var r = c.Run;
        Assert.Equal(3360m, Math.Round(r.Current.SalaryPart, 6));
        Assert.Equal(160 * H, r.Current.NormSec);
    }

    // ---- money
    [Fact] public void Money_TheSpecsSum_SalaryGrowsWithTheHours_OvertimeByItsMultiplier()
    {
        var c = new Case().W(Mon, 10).W(Sat, 2);
        c.Today = Sat;
        c.Days[Tue] = new DayWork(8 * H);
        c.Days[Wed] = new DayWork(8 * H);
        c.Days[Thu] = new DayWork(8 * H);
        c.Days[Fri] = new DayWork(8 * H);
        var r = c.Run;
        Assert.Equal(800m, Math.Round(r.Current.SalaryPart, 6));                // 40 h at 20
        Assert.Equal(2 * 20 * 1.5m + 2 * 20 * 2m, Math.Round(r.Current.OvertimeMoney, 6));
        Assert.Equal(940m, Math.Round(r.Earned, 6));
    }

    [Fact] public void Money_AManualRate_IsForOvertime_NotForTheSalary()
    {
        var c = new Case().W(Mon, 10);
        c.Rules = new() { R(manual: 30) };
        c.Today = Mon;
        var r = c.Run;
        Assert.Equal(160m, Math.Round(r.Current.SalaryPart, 6));
        Assert.Equal(2 * 30 * 1.5m, Math.Round(r.Current.OvertimeMoney, 6));
    }

    [Fact] public void Money_TheHolidayMultiplier_FollowsTheWeekend_UntilSet()
    {
        Assert.Equal(2m, R().Mult(PoolKind.Holiday));
        Assert.Equal(3m, R(holidayMult: 3).Mult(PoolKind.Holiday));
    }

    [Fact] public void Money_DrawingOnThePool_TakesThePaidOvertimeBack()
    {
        var c = new Case().W(Mon, 10);
        c.Today = Mon;
        Assert.Equal(60m, Math.Round(c.Run.Current.OvertimeMoney, 6));
        c.W(Tue, 6);
        c.Today = Tue;
        var r = c.Run;
        Assert.Equal(0m, Math.Round(r.Current.OvertimeMoney, 6));
        Assert.Equal(320m, Math.Round(r.Current.SalaryPart, 6));                // the pool's 2 h now fill the norm
    }

    [Fact] public void Rules_ApplyFromTheirDate_HistoryKeepsItsOwn()
    {
        var c = new Case().W(Mon, 8).W(Tue, 8);
        c.Today = Tue;
        c.Rules = new() { R(), R(from: Tue, salary: 6720) };
        var r = c.Run;
        Assert.Equal(160m + 320m, Math.Round(r.Current.SalaryPart, 6));         // 8 h at 20, 8 h at 40
    }

    [Fact] public void Rules_ADayNormChange_ChangesTheDayFromItsDate()
    {
        var c = new Case().W(Mon, 8).W(Tue, 8);
        c.Today = Tue;
        c.Rules = new() { R(), R(from: Tue, weekly: 30 * H) };
        var r = c.Run;
        Assert.Equal(2 * H, r.Weekday);                                         // Tuesday's norm is 6 h
    }

    [Fact] public void Start_MidMonth_TheSalaryCountsOnlyFromThere()
    {
        var c = new Case { Start = new DateOnly(2026, 11, 16) };
        for (var d = c.Start; d.Month == 11; d = d.AddDays(1))
            if (Array.IndexOf(MonFri, d.DayOfWeek) >= 0) c.W(d, 8);
        c.Today = new DateOnly(2026, 11, 30);
        var r = c.Run;
        Assert.Equal(88 * H, r.Current.NormSec);
        Assert.Equal(88 * 20m, Math.Round(r.Current.SalaryPart, 6));            // 11 days of 21: the rate stays that of the whole month
    }

    // ---- the month's closing
    static Case OctoberDeficit3Weekend5()
    {
        // October 2026: 22 workdays; each full, the last one short by 3, a Saturday of 5 h before it
        var c = new Case { Start = new DateOnly(2026, 10, 1) };
        for (var d = c.Start; d.Month == 10; d = d.AddDays(1))
            if (Array.IndexOf(MonFri, d.DayOfWeek) >= 0) c.W(d, 8);
        c.W(new DateOnly(2026, 10, 24), 5);
        c.W(new DateOnly(2026, 10, 30), 5);
        c.Today = new DateOnly(2026, 11, 1);
        return c;
    }

    static Case NovemberFull(bool reduce, DateOnly holiday)
    {
        var c = new Case { Start = new DateOnly(2026, 11, 1) };
        c.Rules = new() { R(reduce: reduce) };
        c.Holidays.Add(holiday);
        for (var d = c.Start; d.Month == 11; d = d.AddDays(1))
            if (Array.IndexOf(MonFri, d.DayOfWeek) >= 0 && d != holiday) c.W(d, 8);
        c.Today = new DateOnly(2026, 11, 30);
        return c;
    }

    [Fact] public void Close_Refused_Carried_ThisMonthIsPaidInFull_TheNextOwesIt()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(0, 0, Carry: true);
        var r = c.Run;
        var oct = r.Months.Single(m => m.Month == 10);
        Assert.Equal(3 * H, oct.Carried);
        Assert.Equal(3360m, Math.Round(oct.SalaryPart, 6));
        Assert.Equal(5 * H, oct.Weekend);                                       // paid as overtime
        Assert.Equal(3 * H, r.Deficit);
        Assert.Equal(0, r.Weekend);                                             // the pools start empty
    }

    [Fact] public void Close_Partly_ThenCarried()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(1 * H, 0, Carry: true);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(2 * H, oct.Carried);
        Assert.Equal(4 * H, oct.Weekend);
    }

    [Fact] public void Close_Refused_NotCarried_Deducted()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(0, 0, Carry: false);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(3 * H, oct.WrittenOff);
        Assert.Equal(Math.Round(3360m * 173 / 176, 6), Math.Round(oct.SalaryPart, 6));
        Assert.Equal(0, c.Run.Deficit);
    }

    [Fact] public void Close_Refused_NotCarried_WrittenOff_SalaryStaysWhole()
    {
        var c = OctoberDeficit3Weekend5();
        c.Rules = new() { R(deduct: false) };
        c.Closes[(2026, 10)] = new MonthClose(0, 0, Carry: false);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(3 * H, oct.WrittenOff);
        Assert.Equal(3360m, Math.Round(oct.SalaryPart, 6));
    }

    [Fact] public void Close_Deducted_CoversADeficitCarriedIn_Too()
    {
        var c = OctoberDeficit3Weekend5();
        c.OpenDeficit = 8 * H;   // the Saturday's 5 h pay part of it, 3 h are left, and the last day's 3 h come on top
        c.Closes[(2026, 10)] = new MonthClose(0, 0, Carry: false);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(6 * H, oct.WrittenOff);
        Assert.Equal(Math.Round(3360m * 170 / 176, 6), Math.Round(oct.SalaryPart, 6));
    }

    [Fact] public void Close_Unanswered_ShowsAsCarried_AndIsMarked()
    {
        var c = OctoberDeficit3Weekend5();
        var r = c.Run;
        var oct = r.Months.Single(m => m.Month == 10);
        Assert.False(oct.Closed);
        Assert.Equal(3 * H, r.Deficit);
    }

    [Fact] public void Close_Asks_NoMoreThanThereIs()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(50 * H, 0, Carry: true);
        var oct = c.Run.Months.Single(m => m.Month == 10);
        Assert.Equal(0, oct.Carried);
        Assert.Equal(2 * H, oct.Weekend);
    }

    [Fact] public void Close_CarriedDeficit_PaidOffNextMonth_EarnsNothingTwice()
    {
        var c = OctoberDeficit3Weekend5();
        c.Closes[(2026, 10)] = new MonthClose(0, 0, Carry: true);
        c.W(new DateOnly(2026, 11, 2), 11);
        c.Today = new DateOnly(2026, 11, 2);
        var r = c.Run;
        Assert.Equal(0, r.Deficit);
        Assert.Equal(0, r.Weekday);
        Assert.Equal(160m, Math.Round(r.Current.SalaryPart, 6));
    }

    [Fact] public void SplitClosing_TheLowerMultiplierGoesFirst()
    {
        Assert.Equal((3 * H, 0), SplitClosing(R(), 3 * H, 5 * H, 5 * H));              // equal: the weekend
        Assert.Equal((1 * H, 2 * H), SplitClosing(R(), 3 * H, 1 * H, 5 * H));
        Assert.Equal((0, 3 * H), SplitClosing(R(holidayMult: 1.8m), 3 * H, 5 * H, 5 * H));
        Assert.Equal((3 * H, 5 * H), SplitClosing(R(holidayMult: 3), 8 * H, 3 * H, 5 * H));
    }

    // ---- the bars
    static Bar Week(Case c, long? elapsed = null, bool countToday = true) => BarOf(c.In, Mon, Sun, false, elapsed, countToday, 2);

    [Fact] public void Bar_OnMondayMorning_IsNotOrange()
    {
        var c = new Case();
        c.Today = Mon;
        var b = Week(c);
        Assert.Equal(40 * H, b.Full);
        Assert.Equal(BarState.Normal, b.State);
    }

    [Fact] public void Bar_TodaysShare_GrowsWithTheSession_AtMostADay()
    {
        var c = new Case().W(Mon, 2, open: true);
        c.Today = Mon;
        Assert.Equal(4 * H, Week(c, elapsed: 4 * H).Expected);
        Assert.Equal(BarState.Under, Week(c, elapsed: 4 * H).State);            // 2 h worked, a pause in the session
        Assert.Equal(8 * H, Week(c, elapsed: 20 * H).Expected);
    }

    [Fact] public void Bar_WithoutTodaysShare_OnlyPastDaysCount()
    {
        var c = new Case().W(Mon, 8).W(Tue, 2, open: true);
        c.Today = Tue;
        var b = Week(c, elapsed: 4 * H, countToday: false);
        Assert.Equal(8 * H, b.Expected);
        Assert.Equal(BarState.Over, b.State);
    }

    [Fact] public void Bar_ToleranceBand_IsBlue_OutsideOrangeOrGreen()
    {
        var c = new Case().W(Mon, 8).W(Tue, 8).W(Wed, 8).W(Thu, 8);
        c.Today = Fri;
        Assert.Equal(BarState.Normal, Week(c).State);
        Assert.Equal(32 * H * 2 / 100, Week(c).Tolerance);
        c.W(Thu, 7);
        Assert.Equal(BarState.Under, Week(c).State);
        c.W(Thu, 9);
        Assert.Equal(BarState.Over, Week(c).State);
    }

    [Fact] public void Bar_ToleranceHasAFloor()
    {
        var c = new Case().W(Mon, 0.5, open: true);
        c.Today = Mon;
        Assert.Equal(MinToleranceSec, Week(c, elapsed: (long)(0.5 * H)).Tolerance);
    }

    [Fact] public void Bar_AboveTheNorm_IsFull_AndShowsTheRest()
    {
        var c = new Case().W(Mon, 12).W(Tue, 12).W(Wed, 12).W(Thu, 12);
        c.Today = Thu;
        var b = Week(c);
        Assert.Equal(1, b.Fill);
        Assert.Equal(8 * H, b.Above);
    }

    [Fact] public void Bar_Week_HolidayAndVacationLowerItsNorm()
    {
        var c = new Case();
        c.Holidays.Add(Mon);
        c.Vacations.Add(Tue);
        c.Today = Mon;
        Assert.Equal(24 * H, Week(c).Full);
    }

    [Fact] public void Bar_Month_AHolidayThatDoesntReduce_CountsAsWorked()
    {
        var c = new Case { Start = new DateOnly(2026, 11, 1) };
        c.Rules = new() { R(reduce: false) };
        c.Holidays.Add(Mon);
        c.Today = Mon;
        var b = BarOf(c.In, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), true, null, true, 2);
        Assert.Equal(168 * H, b.Full);
        Assert.Equal(8 * H, b.Worked);
        Assert.Equal(8 * H, b.Expected);
        Assert.Equal(BarState.Normal, b.State);
    }
}
