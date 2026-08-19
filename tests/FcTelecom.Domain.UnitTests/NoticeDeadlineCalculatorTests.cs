using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Contracts;
using Shouldly;

namespace FcTelecom.Domain.UnitTests;

/// <summary>
/// Notice-deadline logic — the calculation that stops a contract auto-renewing by accident.
/// </summary>
public sealed class NoticeDeadlineCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 19);

    private static Contract ContractWith(
        DateOnly? endDate, int? noticeDays, RenewalType renewalType = RenewalType.AutoRenew,
        bool confirmed = false, DateOnly? explicitDeadline = null) =>
        new()
        {
            ContractNumber = "TEST-001",
            StartDate = Today.AddYears(-2),
            InitialTermMonths = 24,
            EndDate = endDate,
            NoticePeriodDays = noticeDays,
            RenewalType = renewalType,
            Status = ContractStatus.Active,
            ProposedNoticeDeadlineDate = NoticeDeadlineCalculator.ProposeNoticeDeadline(endDate, noticeDays),
            NoticeDeadlineDate = explicitDeadline,
            NoticeDeadlineConfirmed = confirmed,
        };

    [Fact]
    public void ProposedDeadline_IsEndDateMinusNoticePeriod()
    {
        DateOnly? deadline = NoticeDeadlineCalculator.ProposeNoticeDeadline(
            new DateOnly(2026, 12, 31), 120);

        deadline.ShouldBe(new DateOnly(2026, 9, 2));
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("2026-12-31", null)]
    [InlineData(null, null)]
    public void ProposedDeadline_IsNull_WhenTermsAreIncomplete(string? endDate, int? noticeDays)
    {
        DateOnly? parsed = endDate is null ? null : DateOnly.Parse(endDate, null);

        NoticeDeadlineCalculator.ProposeNoticeDeadline(parsed, noticeDays).ShouldBeNull();
    }

    /// <summary>
    /// Missing terms are their own state, not an absence of one.
    /// </summary>
    /// <remarks>
    /// This is the case that quietly costs money for years: a legacy POTS group with no
    /// paperwork, auto-renewing forever because nobody knew there was a deadline. Rendering
    /// it as an empty cell is how it stays invisible.
    /// </remarks>
    [Fact]
    public void MissingTerms_ProduceTermsUnknown_WithAnExplanationNamingWhatIsMissing()
    {
        Contract contract = ContractWith(endDate: null, noticeDays: null, RenewalType.Unknown);

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.TermsUnknown);
        assessment.Explanation.ShouldContain("end date");
        assessment.Explanation.ShouldContain("notice period");
        assessment.Explanation.ShouldContain("renewal type");
    }

    [Fact]
    public void DeadlineInsideTheTightestThreshold_IsUrgent()
    {
        Contract contract = ContractWith(Today.AddDays(140), 120); // deadline in 20 days

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.Urgent);
        assessment.DaysRemaining.ShouldBe(20);
        assessment.TriggeredThreshold.ShouldBe(30);
    }

    [Fact]
    public void DeadlineInsideAWiderThreshold_IsUpcoming()
    {
        Contract contract = ContractWith(Today.AddDays(200), 120); // deadline in 80 days

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.Upcoming);
        assessment.TriggeredThreshold.ShouldBe(90);
    }

    [Fact]
    public void DeadlineOutsideTheHorizon_IsNotFlagged()
    {
        Contract contract = ContractWith(Today.AddDays(500), 120);

        NoticeDeadlineCalculator.Assess(contract, Today).Urgency.ShouldBe(RenewalUrgency.None);
    }

    [Fact]
    public void MissedDeadline_OnAnAutoRenewingContract_SaysWhatHappened()
    {
        Contract contract = ContractWith(Today.AddDays(10), 120, RenewalType.AutoRenew);
        contract.RenewalTermMonths = 12;

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.Missed);
        assessment.Explanation.ShouldContain("auto-renewed");
        assessment.Explanation.ShouldContain("12 months");
    }

    [Fact]
    public void MissedDeadline_OnAnEvergreenContract_SaysCancellationIsProbablyStillPossible()
    {
        Contract contract = ContractWith(Today.AddDays(10), 120, RenewalType.EvergreenMonthToMonth);

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.Missed);
        assessment.Explanation.ShouldContain("month-to-month");
    }

    /// <summary>
    /// An unconfirmed deadline still raises alerts — labelled as unconfirmed.
    /// </summary>
    /// <remarks>
    /// Suppressing an alert because the date was computed rather than confirmed would be a
    /// technicality that costs someone a renewal. The label goes on the alert; the alert
    /// still fires.
    /// </remarks>
    [Fact]
    public void UnconfirmedDeadline_StillAlerts_ButIsLabelledForReview()
    {
        Contract contract = ContractWith(Today.AddDays(140), 120, confirmed: false);

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.Urgency.ShouldBe(RenewalUrgency.Urgent);
        assessment.DeadlineConfirmed.ShouldBeFalse();
        assessment.Explanation.ShouldContain("computed, not confirmed");
    }

    [Fact]
    public void ConfirmedDeadline_TakesPrecedenceOverTheProposal()
    {
        // The paperwork turned out to say something different from the arithmetic, and a
        // person recorded the real date. That date is what alerts use.
        Contract contract = ContractWith(
            Today.AddDays(200), 120, confirmed: true,
            explicitDeadline: Today.AddDays(15));

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.NoticeDeadline.ShouldBe(Today.AddDays(15));
        assessment.Urgency.ShouldBe(RenewalUrgency.Urgent);
        assessment.DeadlineConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void ThresholdsToRaise_ReturnsOnlyNewlyCrossedThresholds()
    {
        Contract contract = ContractWith(Today.AddDays(200), 120); // 80 days remaining

        IReadOnlyList<int> raised = NoticeDeadlineCalculator.ThresholdsToRaise(
            contract, Today, alreadySentThresholds: new HashSet<int> { 180, 120 });

        raised.ShouldBe(new[] { 90 });
    }

    /// <summary>
    /// This is what stops the nightly job re-sending the same 90-day warning for thirty
    /// consecutive nights — the fastest way to teach people to filter your alerts away.
    /// </summary>
    [Fact]
    public void ThresholdsToRaise_ReturnsNothing_WhenEverythingCrossedIsAlreadySent()
    {
        Contract contract = ContractWith(Today.AddDays(200), 120);

        IReadOnlyList<int> raised = NoticeDeadlineCalculator.ThresholdsToRaise(
            contract, Today, new HashSet<int> { 180, 120, 90, 60, 30 });

        raised.ShouldBeEmpty();
    }

    /// <summary>
    /// A contract added to the system late has crossed several thresholds at once. It
    /// should still produce the wider warnings rather than silently skipping to the
    /// tightest one — the 90-day notice is the one someone can still act on calmly.
    /// </summary>
    [Fact]
    public void ThresholdsToRaise_ReturnsAllUnsentCrossedThresholds_ForALateAddition()
    {
        Contract contract = ContractWith(Today.AddDays(145), 120); // 25 days remaining

        IReadOnlyList<int> raised = NoticeDeadlineCalculator.ThresholdsToRaise(
            contract, Today, new HashSet<int>());

        raised.ShouldBe(new[] { 180, 120, 90, 60, 30 });
    }

    [Fact]
    public void ThresholdsToRaise_ReturnsNothing_ForAPassedDeadline()
    {
        Contract contract = ContractWith(Today.AddDays(10), 120); // deadline 110 days ago

        NoticeDeadlineCalculator.ThresholdsToRaise(contract, Today, new HashSet<int>())
            .ShouldBeEmpty();
    }

    [Fact]
    public void ThresholdsToRaise_ReturnsNothing_ForAnInactiveContract()
    {
        Contract contract = ContractWith(Today.AddDays(140), 120);
        contract.Status = ContractStatus.Expired;

        NoticeDeadlineCalculator.ThresholdsToRaise(contract, Today, new HashSet<int>())
            .ShouldBeEmpty();
    }

    [Fact]
    public void DeadlineExactlyToday_IsUrgentAndNotMissed()
    {
        // Boundary: zero days remaining still means action is possible today.
        Contract contract = ContractWith(Today.AddDays(120), 120);

        RenewalAssessment assessment = NoticeDeadlineCalculator.Assess(contract, Today);

        assessment.DaysRemaining.ShouldBe(0);
        assessment.Urgency.ShouldBe(RenewalUrgency.Urgent);
    }
}
