using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Monitoring;
using Shouldly;

namespace FcTelecom.Domain.UnitTests;

/// <summary>
/// The availability maths, tested against the cases that actually occur.
/// </summary>
/// <remarks>
/// The happy path here is trivial and uninteresting. What matters is the handling of
/// unknown time, partial coverage, and overlapping maintenance — because those are the
/// cases where a plausible-looking implementation quietly produces a number that flatters
/// the carrier, and an availability figure nobody can trust is worse than none at all.
/// </remarks>
public sealed class AvailabilityCalculatorTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private const int DaySeconds = 86_400;

    [Fact]
    public void FullyUp_IsOneHundredPercent()
    {
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
            [new AvailabilityInterval(Start, End, IntervalKind.Up)]);

        result.AvailabilityPercent.ShouldBe(100m);
        result.CoveragePercent.ShouldBe(100m);
        result.LowConfidence.ShouldBeFalse();
        result.EligibleSeconds.ShouldBe(DaySeconds);
    }

    [Fact]
    public void OneHourDown_InADay_IsCorrectToFourPlaces()
    {
        DateTime outageStart = Start.AddHours(10);

        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
        [
            new AvailabilityInterval(Start, outageStart, IntervalKind.Up),
            new AvailabilityInterval(outageStart, outageStart.AddHours(1), IntervalKind.UnplannedDown),
            new AvailabilityInterval(outageStart.AddHours(1), End, IntervalKind.Up),
        ]);

        // 23/24 hours = 95.8333%
        result.AvailabilityPercent.ShouldBe(95.8333m);
        result.UnplannedDownSeconds.ShouldBe(3_600);
    }

    /// <summary>
    /// The single most important behaviour in this class.
    /// </summary>
    /// <remarks>
    /// If unknown time were counted as up, a monitoring stack that was blind for a quarter
    /// of the month would still report near-perfect availability, and the number would be
    /// worse than useless — it would be actively misleading in front of an executive.
    /// </remarks>
    [Fact]
    public void UnknownTime_IsExcludedFromTheDenominator_NotCountedAsUp()
    {
        // Six hours blind, eighteen hours up, nothing down.
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
        [
            new AvailabilityInterval(Start, Start.AddHours(6), IntervalKind.Unknown),
            new AvailabilityInterval(Start.AddHours(6), End, IntervalKind.Up),
        ]);

        result.EligibleSeconds.ShouldBe(18 * 3_600);
        result.UnknownSeconds.ShouldBe(6 * 3_600);
        result.AvailabilityPercent.ShouldBe(100m); // Of the time we could see, all of it was up.
        result.CoveragePercent.ShouldBe(75m);      // But we only saw three quarters of the period.
        result.LowConfidence.ShouldBeTrue();
    }

    [Fact]
    public void UnaccountedTime_BecomesUnknown_NotUp()
    {
        // Only six hours of the day is described. The rest is silence — and silence must
        // mean "we did not know", never "everything was fine".
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
            [new AvailabilityInterval(Start, Start.AddHours(6), IntervalKind.Up)]);

        result.UnknownSeconds.ShouldBe(18 * 3_600);
        result.EligibleSeconds.ShouldBe(6 * 3_600);
        result.CoveragePercent.ShouldBe(25m);
        result.LowConfidence.ShouldBeTrue();
    }

    [Fact]
    public void PlannedDowntime_IsExcludedFromTheDenominator()
    {
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
        [
            new AvailabilityInterval(Start, Start.AddHours(2), IntervalKind.PlannedDown),
            new AvailabilityInterval(Start.AddHours(2), End, IntervalKind.Up),
        ]);

        result.PlannedDownSeconds.ShouldBe(2 * 3_600);
        result.EligibleSeconds.ShouldBe(22 * 3_600);
        result.AvailabilityPercent.ShouldBe(100m);

        // Planned downtime is excluded from availability but is NOT unknown — we knew
        // exactly what was happening, so coverage stays complete.
        result.CoveragePercent.ShouldBe(100m);
        result.LowConfidence.ShouldBeFalse();
    }

    [Fact]
    public void EntirelyUnknownPeriod_ReportsZeroEligible_AndDoesNotDivideByZero()
    {
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
            [new AvailabilityInterval(Start, End, IntervalKind.Unknown)]);

        result.EligibleSeconds.ShouldBe(0);
        result.AvailabilityPercent.ShouldBe(0m);
        result.CoveragePercent.ShouldBe(0m);
        result.LowConfidence.ShouldBeTrue();
    }

    [Fact]
    public void IntervalsAreClippedToThePeriod()
    {
        // An outage that started the previous day and ended inside this one contributes
        // only its overlapping hour.
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
        [
            new AvailabilityInterval(Start.AddHours(-5), Start.AddHours(1), IntervalKind.UnplannedDown),
            new AvailabilityInterval(Start.AddHours(1), End.AddHours(5), IntervalKind.Up),
        ]);

        result.UnplannedDownSeconds.ShouldBe(3_600);
        result.EligibleSeconds.ShouldBe(DaySeconds);
    }

    [Fact]
    public void OverlappingIntervals_CannotProduceMoreThanOneHundredPercent()
    {
        // Pathological input: double-counted "up" time summing past the period length.
        // A report forwarded to an executive must never read 103% uptime.
        AvailabilityResult result = AvailabilityCalculator.Calculate(Start, End,
        [
            new AvailabilityInterval(Start, End, IntervalKind.Up),
            new AvailabilityInterval(Start, End, IntervalKind.Up),
        ]);

        result.AvailabilityPercent.ShouldBeLessThanOrEqualTo(100m);
        result.AvailabilityPercent.ShouldBeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public void EndBeforeStart_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            AvailabilityCalculator.Calculate(End, Start, []));
    }

    [Fact]
    public void Combine_IsWeightedByEligibleTime_NotAMeanOfPercentages()
    {
        // A circuit measured for one hour at 0% and one measured for 99 hours at 100%.
        // A naive mean says 50%. The truth is 99%.
        var rollups = new[]
        {
            new AvailabilityRollup { EligibleSeconds = 3_600, UnplannedDownSeconds = 3_600 },
            new AvailabilityRollup { EligibleSeconds = 356_400, UnplannedDownSeconds = 0 },
        };

        AvailabilityResult result = AvailabilityCalculator.Combine(rollups);

        result.AvailabilityPercent.ShouldBe(99m);
    }

    [Fact]
    public void Combine_PropagatesLowConfidence_WhenCoverageIsPoor()
    {
        var rollups = new[]
        {
            new AvailabilityRollup { EligibleSeconds = 3_600, UnknownSeconds = 82_800 },
        };

        AvailabilityResult result = AvailabilityCalculator.Combine(rollups);

        result.LowConfidence.ShouldBeTrue();
        result.CoveragePercent.ShouldBeLessThan(10m);
    }

    [Theory]
    [InlineData(99.99, 99.98, true)]   // Below SLA — a credit candidate.
    [InlineData(99.99, 99.99, false)]  // Exactly at SLA — met.
    [InlineData(99.99, 100.00, false)] // Above SLA.
    public void SlaCreditCandidate_IsFlagged_WhenBelowTarget(
        decimal sla, decimal actual, bool expected)
    {
        var result = new AvailabilityResult(
            EligibleSeconds: DaySeconds,
            UnplannedDownSeconds: 0,
            PlannedDownSeconds: 0,
            UnknownSeconds: 0,
            AvailabilityPercent: actual,
            CoveragePercent: 100m,
            LowConfidence: false);

        AvailabilityCalculator.IsSlaCreditCandidate(result, sla).ShouldBe(expected);
    }

    [Fact]
    public void SlaCreditCandidate_IsNeverFlagged_OnLowConfidenceData()
    {
        // Presenting a carrier with an SLA breach calculated over 40% coverage is a good
        // way to lose the argument and some credibility with it.
        var result = new AvailabilityResult(
            EligibleSeconds: 34_560, UnplannedDownSeconds: 3_456,
            PlannedDownSeconds: 0, UnknownSeconds: 51_840,
            AvailabilityPercent: 90m, CoveragePercent: 40m, LowConfidence: true);

        AvailabilityCalculator.IsSlaCreditCandidate(result, 99.99m).ShouldBeFalse();
    }

    [Fact]
    public void NoSlaOnFile_IsNeverACreditCandidate()
    {
        var result = new AvailabilityResult(DaySeconds, 8_640, 0, 0, 90m, 100m, false);

        AvailabilityCalculator.IsSlaCreditCandidate(result, slaAvailabilityPercent: null).ShouldBeFalse();
    }
}
