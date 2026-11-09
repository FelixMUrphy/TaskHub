namespace TaskHub;

/// <summary>When the balance guard reads the balance next and what it takes the balance to be: the pure part, no clock,
/// no state of its own, tested at the production constants in tests/unit. A service can book charges in steps (Runpod,
/// about every 10 minutes), so reading more often isn't enough: between steps the balance is carried down at the burn.</summary>
static class GuardTiming
{
    /// <summary>The shortest gap between readings: a pending or failed stop, or a threshold close by.</summary>
    public const double FastRead = 60;

    /// <summary>A figure that hasn't moved for this long is carried down no further.</summary>
    public const double MaxCarry = 900;

    /// <summary>What was last read. <see cref="Carried"/> is the money spent since the figure last moved.</summary>
    public sealed record Reading(double Amount, double Burn, double Carried, double MovedAt, double ReadAt)
    {
        /// <summary>The balance to decide by. Without a burn nothing is being spent, so the service's own figure stands.</summary>
        public double Effective => Burn > 0 ? Amount - Carried : Amount;
    }

    /// <summary>The next reading. Any change of the figure, a booked step or a top-up, starts from the new figure. The time
    /// since the previous reading counts at the burn known then, so a machine started a moment ago doesn't count backwards.</summary>
    public static Reading Next(Reading prev, double amount, double burn, double now)
    {
        if (prev == null || amount != prev.Amount) return new Reading(amount, burn, 0, now, now);
        double limit = prev.MovedAt + MaxCarry;
        double seconds = Math.Max(0, Math.Min(now, limit) - Math.Min(prev.ReadAt, limit));
        return prev with { Burn = burn, Carried = prev.Carried + Math.Max(0, prev.Burn) * seconds / 3600, ReadAt = now };
    }

    /// <summary>Seconds until the next reading: three looks before the stop threshold can be reached, within fast..billing.
    /// A guard that couldn't stop anything anyway (<paramref name="canStop"/> false) doesn't read faster.</summary>
    public static double NextReading(double billing, double fast, double? effective, double stop, double burn, bool stopInProgress, bool canStop)
    {
        double least = Math.Min(billing, fast);
        if (stopInProgress) return least;
        if (!canStop || effective is not double e || burn <= 0) return billing;
        double toStop = Math.Max(0, e - stop) / burn * 3600;
        return Math.Clamp(toStop / 3, least, billing);
    }

    /// <summary>Time to ask for the billing mode: at every reading while it isn't known, else every <paramref name="every"/> seconds.
    /// A failed question doesn't count as asked.</summary>
    public static bool AskMode(string known, double answeredAt, double now, double every) => known == null || now - answeredAt > every;

    /// <summary>A held stop warns again when the estimated $0 is this close (seconds): at $0 the service acts on its own.</summary>
    public const double ZeroWarnAhead = 900;

    /// <summary>Seconds until $0 going by the last figure known (it was <paramref name="amount"/> at <paramref name="at"/>), null when
    /// nothing is being spent. Not capped like <see cref="Next"/>: this is about the worst case while no reading comes in.</summary>
    public static double? SecondsToZero(double amount, double at, double burn, double now) =>
        burn > 0 ? amount / burn * 3600 - (now - at) : null;

    public enum DeadlineCall { Stop, Cancel, Hold, Wait }

    /// <summary>What a pending stop's deadline comes to. The estimate only brings the warning and the deadline forward: a stop
    /// needs a fresh reading at or below the threshold. A figure above the threshold is no confirmation, so the stop waits for
    /// the next reading. A failed reading holds the stop: the user is warned and decides.</summary>
    /// <param name="fresh">The figure read now, null when the reading failed.</param>
    /// <param name="effective">The balance to decide by after that reading.</param>
    public static DeadlineCall AtDeadline(double? fresh, double effective, double amountWhenAnnounced, double stop, double burn)
    {
        if (fresh is not double a) return DeadlineCall.Hold;
        if (Cancels(effective, a, amountWhenAnnounced, stop, burn)) return DeadlineCall.Cancel;
        return a <= stop ? DeadlineCall.Stop : DeadlineCall.Wait;
    }

    /// <summary>A pending stop goes away only when money came: the figure is above the threshold and it rose since the stop was
    /// announced, or nothing is being spent. A booked step that lands above the estimate is no top-up.</summary>
    public static bool Cancels(double effective, double amount, double amountWhenAnnounced, double stop, double burn) =>
        effective > stop && (amount > amountWhenAnnounced || burn <= 0);
}
