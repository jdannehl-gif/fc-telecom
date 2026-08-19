using FcTelecom.Domain.Monitoring;

namespace FcTelecom.Domain.Calculations;

/// <summary>
/// A period of time in a known state, used as input to the availability maths.
/// </summary>
public readonly record struct AvailabilityInterval(DateTime StartUtc, DateTime EndUtc, IntervalKind Kind)
{
    public double TotalSeconds => (EndUtc - StartUtc).TotalSeconds;
}

public enum IntervalKind
{
    Up = 1,

    /// <summary>Down, and it counts against availability.</summary>
    UnplannedDown = 2,

    /// <summary>Down inside a maintenance window. Excluded from the denominator, still recorded.</summary>
    PlannedDown = 3,

    /// <summary>No usable coverage. Excluded from the denominator. <b>Never counted as up.</b></summary>
    Unknown = 4,
}

public readonly record struct AvailabilityResult(
    int EligibleSeconds,
    int UnplannedDownSeconds,
    int PlannedDownSeconds,
    int UnknownSeconds,
    decimal AvailabilityPercent,
    decimal CoveragePercent,
    bool LowConfidence)
{
    public int TotalPeriodSeconds =>
        EligibleSeconds + PlannedDownSeconds + UnknownSeconds;
}

/// <summary>
/// Computes availability from a set of state intervals.
/// </summary>
/// <remarks>
/// <para>
/// The formula:
/// </para>
/// <code>
/// EligibleSeconds     = PeriodSeconds − PlannedDownSeconds − UnknownSeconds
/// AvailabilityPercent = (EligibleSeconds − UnplannedDownSeconds) / EligibleSeconds × 100
/// </code>
/// <para>
/// Three properties, each deliberate:
/// </para>
/// <list type="number">
/// <item><b>Unknown time is removed from the denominator, not counted as up.</b> If
/// monitoring was blind for 10% of a month, availability is computed over the 90% that
/// was measured and the coverage figure is reported alongside it. Counting unknown time
/// as available is the standard way uptime reports quietly flatter themselves.</item>
/// <item><b>Planned maintenance is excluded but preserved.</b> The underlying outage is
/// still recorded and linked to its window, so total downtime including planned remains
/// answerable.</item>
/// <item><b>Low confidence is flagged, not hidden.</b> Below the coverage floor, the
/// result carries a flag and every UI surface shows coverage next to availability.</item>
/// </list>
/// <para>
/// This class is a pure function of its inputs — no clock, no database, no configuration
/// lookup. That is what makes the edge cases (zero eligible time, entirely unknown
/// periods, overlapping windows) testable rather than aspirational.
/// </para>
/// </remarks>
public static class AvailabilityCalculator
{
    /// <summary>Default floor below which a result is flagged as low confidence.</summary>
    public const decimal DefaultMinimumCoveragePercent = 90m;

    /// <summary>
    /// Computes availability over <paramref name="periodStartUtc"/> to
    /// <paramref name="periodEndUtc"/> from a set of intervals.
    /// </summary>
    /// <remarks>
    /// Intervals are clipped to the period, so a maintenance window that starts before the
    /// period or an outage that runs past its end contribute only their overlapping part.
    /// Any part of the period not covered by an interval is treated as
    /// <see cref="IntervalKind.Unknown"/> — silence means we did not know, never that
    /// everything was fine.
    /// </remarks>
    public static AvailabilityResult Calculate(
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        IEnumerable<AvailabilityInterval> intervals,
        decimal minimumCoveragePercent = DefaultMinimumCoveragePercent)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        if (periodEndUtc <= periodStartUtc)
        {
            throw new ArgumentException(
                "The period end must be after the period start.", nameof(periodEndUtc));
        }

        double totalSeconds = (periodEndUtc - periodStartUtc).TotalSeconds;

        double up = 0, unplanned = 0, planned = 0, unknown = 0;

        foreach (AvailabilityInterval interval in intervals)
        {
            DateTime start = interval.StartUtc < periodStartUtc ? periodStartUtc : interval.StartUtc;
            DateTime end = interval.EndUtc > periodEndUtc ? periodEndUtc : interval.EndUtc;

            if (end <= start)
            {
                continue; // No overlap with the period.
            }

            double seconds = (end - start).TotalSeconds;

            switch (interval.Kind)
            {
                case IntervalKind.Up: up += seconds; break;
                case IntervalKind.UnplannedDown: unplanned += seconds; break;
                case IntervalKind.PlannedDown: planned += seconds; break;
                case IntervalKind.Unknown: unknown += seconds; break;
                default: throw new ArgumentOutOfRangeException(
                    nameof(intervals), interval.Kind, "Unrecognised interval kind.");
            }
        }

        // Anything the intervals did not account for is unknown, not up. This is the
        // single most important line in the class: a gap in the data is a gap in our
        // knowledge, and pretending otherwise is how availability figures become fiction.
        double accounted = up + unplanned + planned + unknown;
        if (accounted < totalSeconds)
        {
            unknown += totalSeconds - accounted;
        }

        double eligible = totalSeconds - planned - unknown;

        // Guard against floating-point dust producing a tiny negative eligible window.
        if (eligible < 0)
        {
            eligible = 0;
        }

        decimal availability = eligible <= 0
            ? 0m
            : Math.Round((decimal)((eligible - unplanned) / eligible) * 100m, 4);

        // Clamp: pathological input (overlapping intervals summing past the period) must
        // not produce 103% uptime in a report someone forwards to an executive.
        availability = Math.Clamp(availability, 0m, 100m);

        decimal coverage = totalSeconds <= 0
            ? 0m
            : Math.Round((decimal)((totalSeconds - unknown) / totalSeconds) * 100m, 2);

        return new AvailabilityResult(
            EligibleSeconds: (int)Math.Round(eligible),
            UnplannedDownSeconds: (int)Math.Round(unplanned),
            PlannedDownSeconds: (int)Math.Round(planned),
            UnknownSeconds: (int)Math.Round(unknown),
            AvailabilityPercent: availability,
            CoveragePercent: coverage,
            LowConfidence: coverage < minimumCoveragePercent);
    }

    /// <summary>
    /// Combines finer-grained rollups into a coarser one — hourly into daily, daily into
    /// monthly, or many circuits into a carrier-level figure.
    /// </summary>
    /// <remarks>
    /// Weighted by eligible seconds, <b>not</b> a mean of the percentages. Averaging
    /// percentages across periods or circuits with different coverage produces a number
    /// that does not mean anything: a circuit measured for one hour at 0% and one measured
    /// for a month at 100% do not average to 50%.
    /// </remarks>
    public static AvailabilityResult Combine(
        IEnumerable<AvailabilityRollup> rollups,
        decimal minimumCoveragePercent = DefaultMinimumCoveragePercent)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        long eligible = 0, unplanned = 0, planned = 0, unknown = 0;

        foreach (AvailabilityRollup rollup in rollups)
        {
            eligible += rollup.EligibleSeconds;
            unplanned += rollup.UnplannedDownSeconds;
            planned += rollup.PlannedDownSeconds;
            unknown += rollup.UnknownSeconds;
        }

        long total = eligible + planned + unknown;

        decimal availability = eligible <= 0
            ? 0m
            : Math.Round((decimal)(eligible - unplanned) / eligible * 100m, 4);

        availability = Math.Clamp(availability, 0m, 100m);

        decimal coverage = total <= 0
            ? 0m
            : Math.Round((decimal)(total - unknown) / total * 100m, 2);

        return new AvailabilityResult(
            EligibleSeconds: (int)Math.Min(eligible, int.MaxValue),
            UnplannedDownSeconds: (int)Math.Min(unplanned, int.MaxValue),
            PlannedDownSeconds: (int)Math.Min(planned, int.MaxValue),
            UnknownSeconds: (int)Math.Min(unknown, int.MaxValue),
            AvailabilityPercent: availability,
            CoveragePercent: coverage,
            LowConfidence: coverage < minimumCoveragePercent);
    }

    /// <summary>
    /// Whether measured availability fell short of the contractual commitment by enough
    /// to be worth investigating a service credit.
    /// </summary>
    /// <remarks>
    /// A low-confidence result never produces a credit candidate. Presenting a carrier
    /// with an SLA breach calculated over 40% coverage is a good way to lose the argument
    /// and some credibility with it.
    /// </remarks>
    public static bool IsSlaCreditCandidate(AvailabilityResult result, decimal? slaAvailabilityPercent) =>
        slaAvailabilityPercent is { } sla &&
        !result.LowConfidence &&
        result.EligibleSeconds > 0 &&
        result.AvailabilityPercent < sla;
}
