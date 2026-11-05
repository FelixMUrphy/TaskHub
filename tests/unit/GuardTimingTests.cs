using TaskHub;
using Xunit;

// At the production constants: billing 600 s (the default), a fast reading every 60 s, carried for at most 900 s.
public class GuardTimingTests
{
    const double Billing = 600, Fast = GuardTiming.FastRead;

    static double Next(double? balance, double stop, double burn, bool stopping = false, bool canStop = true) =>
        GuardTiming.NextReading(Billing, Fast, balance, stop, burn, stopping, canStop);

    const double Stop = 2, Burn = 18;

    static GuardTiming.DeadlineCall At(double? fresh, double effective, double announced = 2.9, double burn = Burn) =>
        GuardTiming.AtDeadline(fresh, effective, announced, Stop, burn);

    [Fact] public void Deadline_FigureAtTheThreshold_Stops() => Assert.Equal(GuardTiming.DeadlineCall.Stop, At(2, 2));

    [Fact] public void Deadline_FigureStillAbove_Waits_HoweverLowTheEstimate() => Assert.Equal(GuardTiming.DeadlineCall.Wait, At(2.9, 0.5));

    [Fact] public void Deadline_StepAboveTheThreshold_Waits() => Assert.Equal(GuardTiming.DeadlineCall.Wait, At(2.5, 2.5));

    [Fact] public void Deadline_MoneyCame_Cancels() => Assert.Equal(GuardTiming.DeadlineCall.Cancel, At(50, 50));

    [Fact] public void Deadline_NothingSpent_Cancels() => Assert.Equal(GuardTiming.DeadlineCall.Cancel, At(2.9, 2.9, burn: 0));

    [Fact] public void Zero_GoesByTheLastFigure_AndTheTimeSinceIt() => Assert.Equal(1200, GuardTiming.SecondsToZero(2, 1000, 2, 3400)!.Value, 6);

    [Fact] public void Zero_IsNotCappedLikeTheCarry() => Assert.True(GuardTiming.SecondsToZero(1, 0, 1, GuardTiming.MaxCarry * 10) < 0);

    [Fact] public void Zero_NothingSpent_NoEstimate() => Assert.Null(GuardTiming.SecondsToZero(2, 0, 0, 100));

    [Fact] public void Deadline_ReadFails_OnTheEstimateAlone_Holds() => Assert.Equal(GuardTiming.DeadlineCall.Hold, At(null, 1.5));

    [Fact] public void Deadline_ReadFails_FigureAlreadyBelow_Holds() => Assert.Equal(GuardTiming.DeadlineCall.Hold, At(null, 1.5));

    [Fact] public void Deadline_ReadFails_EstimateNotThere_Holds() => Assert.Equal(GuardTiming.DeadlineCall.Hold, At(null, 2.4));

    [Fact] public void Deadline_ReadFails_NoFigureKnown_Holds() => Assert.Equal(GuardTiming.DeadlineCall.Hold, At(null, double.MaxValue));

    [Fact] public void Constants() { Assert.Equal(60, GuardTiming.FastRead); Assert.Equal(900, GuardTiming.MaxCarry); }

    [Fact] public void FarFromTheThreshold_TheConfiguredInterval() => Assert.Equal(Billing, Next(50, 2, 1));

    [Fact] public void Closer_ThreeLooksBeforeTheThreshold() => Assert.Equal(240, Next(2.2, 2, 1), 6);   // 720 s to go

    [Fact] public void VeryClose_NeverFasterThanAMinute() => Assert.Equal(Fast, Next(2.01, 2, 30));

    [Fact] public void BelowTheThreshold_EveryMinute() => Assert.Equal(Fast, Next(1, 2, 1));

    [Fact] public void NoBurn_TheConfiguredInterval() { Assert.Equal(Billing, Next(2.01, 2, 0)); Assert.Equal(Billing, Next(2.01, 2, -1)); }

    [Fact] public void UnknownBalance_TheConfiguredInterval() => Assert.Equal(Billing, Next(null, 2, 1));

    [Fact] public void PendingOrFailedStop_EveryMinute() => Assert.Equal(Fast, Next(50, 2, 0, stopping: true));

    [Fact] public void CantStopAnything_NoFasterReads() => Assert.Equal(Billing, Next(1, 2, 30, canStop: false));

    [Fact] public void ShortConfiguredInterval_IsNeverLengthened()
    {
        Assert.Equal(30, GuardTiming.NextReading(30, Fast, 50, 2, 1, false, true));
        Assert.Equal(30, GuardTiming.NextReading(30, Fast, 50, 2, 1, true, true));
    }

    [Fact] public void TheFigureMoving_StartsAgain()
    {
        var r = GuardTiming.Next(null, 10, 36, 1000);
        Assert.Equal(10, r.Effective);
        r = GuardTiming.Next(r, 10, 36, 1100);
        Assert.Equal(9, r.Effective, 6);    // 100 s at $36/h
        r = GuardTiming.Next(r, 9.5, 36, 1200);
        Assert.Equal(9.5, r.Effective);     // a step
        r = GuardTiming.Next(r, 30, 36, 1300);
        Assert.Equal(30, r.Effective);      // a top-up
    }

    [Fact] public void AStuckFigure_IsCarriedDownNoFurtherThanTheLimit()
    {
        var r = GuardTiming.Next(null, 10, 36, 0);
        r = GuardTiming.Next(r, 10, 36, 600);
        Assert.Equal(4, r.Effective, 6);
        r = GuardTiming.Next(r, 10, 36, 900);
        Assert.Equal(1, r.Effective, 6);
        r = GuardTiming.Next(r, 10, 36, 5000);
        Assert.Equal(1, r.Effective, 6);
    }

    [Fact] public void ABurnThatJustChanged_DoesntCountBackwards()
    {
        var r = GuardTiming.Next(null, 10, 0, 0);
        r = GuardTiming.Next(r, 10, 3600, 600);   // nothing was running for these 600 s
        Assert.Equal(10, r.Effective);
        r = GuardTiming.Next(r, 10, 3600, 601);
        Assert.Equal(9, r.Effective, 6);
    }

    [Fact] public void NothingBeingSpent_TheServicesFigureStands()
    {
        var r = GuardTiming.Next(null, 10, 36, 0);
        r = GuardTiming.Next(r, 10, 0, 100);
        Assert.Equal(10, r.Effective);
    }

    [Fact] public void BillingMode_AskedAtEachReadingUntilKnown_ThenHourly()
    {
        Assert.True(GuardTiming.AskMode(null, 0, 1, 3600));              // never answered, a failure a second ago doesn't count
        Assert.False(GuardTiming.AskMode("PREPAID", 1000, 4600, 3600));
        Assert.True(GuardTiming.AskMode("PREPAID", 1000, 4601, 3600));
    }

    [Fact] public void Cancel_OnlyWhenMoneyCame()
    {
        Assert.False(GuardTiming.Cancels(2.5, 2.5, 2.9, 2, 36));   // a step, above the threshold, but lower than announced
        Assert.False(GuardTiming.Cancels(1.5, 3.0, 2.9, 2, 36));   // still below by the estimate
        Assert.False(GuardTiming.Cancels(2.5, 2.9, 2.9, 2, 36));   // the same figure
        Assert.True(GuardTiming.Cancels(20, 20, 2.9, 2, 36));      // a top-up
        Assert.True(GuardTiming.Cancels(3, 3, 1.5, 2, 36));        // announced below the threshold, now above it
        Assert.True(GuardTiming.Cancels(2.5, 2.5, 2.9, 2, 0));     // nothing being spent
    }

    // Runpod's case at the default interval: $30/h, booked every 600 s, stop at $2: the guard is below the threshold before
    // the booked figure could say so, within one fast reading of the true crossing, and with money left.
    [Fact] public void BookedInSteps_TheThresholdIsSeenBeforeTheNextStep()
    {
        double burn = 30, stop = 2, booked = 5.5, t = 0, crossedAt = -1;
        GuardTiming.Reading r = null;
        while (t < 600)
        {
            r = GuardTiming.Next(r, booked, burn, t);
            if (r.Effective <= stop) { crossedAt = t; break; }
            t += GuardTiming.NextReading(Billing, Fast, r.Effective, stop, burn, false, true);
        }
        Assert.InRange(crossedAt, 0, 600);
        double trueBalance = booked - burn * crossedAt / 3600;
        Assert.True(trueBalance > 0, $"true balance {trueBalance} at {crossedAt} s");
        Assert.True(trueBalance <= stop, $"crossed early: {trueBalance} at {crossedAt} s");
    }
}
