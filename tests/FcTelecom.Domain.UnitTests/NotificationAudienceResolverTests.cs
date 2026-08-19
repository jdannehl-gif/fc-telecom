using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Notifications;
using Shouldly;

namespace FcTelecom.Domain.UnitTests;

/// <summary>
/// The notification preview — "who would actually receive this?"
/// </summary>
/// <remarks>
/// The failure this guards against is not a delivery bug. It is a rule that is switched on
/// believing it reaches the right people and reaches nobody, or reaches four hundred. Both
/// are silent until they matter.
/// </remarks>
public sealed class NotificationAudienceResolverTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Roles =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["Procurement"] = ["proc1@example.org", "proc2@example.org"],
            ["HelpDesk"] = ["hd@example.org"],
            ["EmptyRole"] = [],
        };

    private static NotificationContext Context(
        int? thresholdDays = null, bool? confirmed = null,
        bool? actionRecorded = null, string? owner = "owner@example.org") =>
        new(thresholdDays, confirmed, actionRecorded, owner, Roles);

    /// <summary>The renewal rule exactly as seeded, so the tests describe the real policy.</summary>
    private static NotificationRule RenewalRule(bool enabled = true) => new()
    {
        Name = "Contract renewal and notice deadline",
        EventType = NotificationEventTypes.ContractNoticeDeadline,
        Channels = NotificationChannel.Email | NotificationChannel.Teams,
        NotifyRecordOwner = true,
        SharedMailbox = "telecom-procurement@example.org",
        TeamsChannelReference = "Telecom / Contracts",
        ThresholdDaysCsv = "180,120,90,60,30",
        Enabled = enabled,
        EscalationSteps =
        [
            new NotificationEscalationStep
            {
                ThresholdDays = 60,
                Condition = EscalationCondition.IfUnconfirmedOrNoAction,
                RoleScope = "Procurement",
            },
            new NotificationEscalationStep
            {
                ThresholdDays = 30,
                Condition = EscalationCondition.Always,
                RoleScope = "Procurement",
                Recipients = "it-leadership@example.org",
            },
        ],
    };

    [Fact]
    public void At180Days_TheOwnerAndSharedMailboxAreNotified_AndNobodyIsEscalatedTo()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: 180, confirmed: false, actionRecorded: false));

        audience.EmailRecipients.ShouldBe(
            new[] { "owner@example.org", "telecom-procurement@example.org" }, ignoreOrder: true);
        audience.EscalationEmailRecipients.ShouldBeEmpty();
        audience.TeamsChannel.ShouldBe("Telecom / Contracts");
        audience.WouldSend.ShouldBeTrue();
    }

    [Fact]
    public void At60Days_WithAnUnconfirmedDeadline_ProcurementIsEscalatedTo()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: 60, confirmed: false, actionRecorded: true));

        audience.EscalationEmailRecipients.ShouldBe(
            new[] { "proc1@example.org", "proc2@example.org" }, ignoreOrder: true);
        audience.Explanations.ShouldContain(e => e.Contains("still unconfirmed"));
    }

    /// <summary>
    /// The whole point of a conditional escalation. A contract whose deadline has been
    /// confirmed and whose decision has been recorded does not need chasing at 60 days,
    /// and chasing it anyway is how people learn to ignore the 30-day one.
    /// </summary>
    [Fact]
    public void At60Days_WithAConfirmedDeadlineAndRecordedAction_NobodyIsEscalatedTo()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: 60, confirmed: true, actionRecorded: true));

        audience.EscalationEmailRecipients.ShouldBeEmpty();
        audience.Explanations.ShouldContain(e => e.Contains("did not fire"));
    }

    [Fact]
    public void At30Days_EscalationIsUnconditional_AndReachesLeadership()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: 30, confirmed: true, actionRecorded: true));

        audience.EscalationEmailRecipients.ShouldContain("it-leadership@example.org");
        audience.EscalationEmailRecipients.ShouldContain("proc1@example.org");
    }

    [Fact]
    public void SomebodyOnThePrimaryListIsNotAlsoEscalatedTo()
    {
        NotificationRule rule = RenewalRule();
        rule.Recipients = "proc1@example.org";

        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            rule, Context(thresholdDays: 30, confirmed: true, actionRecorded: true));

        audience.EmailRecipients.ShouldContain("proc1@example.org");
        audience.EscalationEmailRecipients.ShouldNotContain("proc1@example.org");
    }

    [Fact]
    public void ADisabledRuleWouldNotSend_ButStillPreviewsItsAudience()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(enabled: false), Context(thresholdDays: 90, confirmed: false, actionRecorded: false));

        audience.WouldSend.ShouldBeFalse();
        // The preview is the whole reason to look at a disabled rule.
        audience.EmailRecipients.ShouldNotBeEmpty();
        audience.Explanations.ShouldContain(e => e.Contains("disabled"));
    }

    [Fact]
    public void AnEnabledRuleThatReachesNobody_IsWarnedAbout()
    {
        var rule = new NotificationRule
        {
            Name = "Broken", EventType = NotificationEventTypes.OutageConfirmed,
            Channels = NotificationChannel.Email, Enabled = true,
        };

        NotificationAudience audience = NotificationAudienceResolver.Resolve(rule, Context());

        audience.WouldSend.ShouldBeFalse();
        audience.Warnings.ShouldContain(w => w.Contains("would reach nobody"));
        rule.HasNoPossibleRecipient.ShouldBeTrue();
    }

    [Fact]
    public void ARoleWithNoMembers_IsWarnedAbout_RatherThanSilentlyReachingNobody()
    {
        var rule = new NotificationRule
        {
            Name = "Empty role", EventType = NotificationEventTypes.ProbeOffline,
            Channels = NotificationChannel.Email, RoleScope = "EmptyRole", Enabled = true,
        };

        NotificationAudience audience = NotificationAudienceResolver.Resolve(rule, Context());

        audience.Warnings.ShouldContain(w => w.Contains("nobody currently holds it"));
    }

    [Fact]
    public void NotifyingTheRecordOwner_WhenThereIsNoOwner_IsWarnedAbout()
    {
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: 180, owner: null));

        audience.Warnings.ShouldContain(w => w.Contains("no owner assigned"));

        // The shared mailbox still gets it — a missing owner degrades the audience rather
        // than silencing the alert.
        audience.EmailRecipients.ShouldBe(new[] { "telecom-procurement@example.org" });
    }

    [Fact]
    public void SelectingTeamsWithoutAChannelReference_IsWarnedAbout()
    {
        NotificationRule rule = RenewalRule();
        rule.TeamsChannelReference = null;

        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            rule, Context(thresholdDays: 180));

        audience.TeamsChannel.ShouldBeNull();
        audience.Warnings.ShouldContain(w => w.Contains("no channel reference"));
    }

    [Fact]
    public void ALargeRecipientList_IsWarnedAbout()
    {
        var rule = new NotificationRule
        {
            Name = "Too many", EventType = NotificationEventTypes.OutageConfirmed,
            Channels = NotificationChannel.Email, Enabled = true,
            Recipients = string.Join(";", Enumerable.Range(1, 30).Select(i => $"user{i}@example.org")),
        };

        NotificationAudience audience = NotificationAudienceResolver.Resolve(rule, Context());

        audience.Warnings.ShouldContain(w => w.Contains("Consider a shared mailbox"));
    }

    [Fact]
    public void AnImmediateEvent_WithNoThreshold_NeverEscalates()
    {
        // An outage fires on confirmation. Escalation steps are a renewal-timeline concept
        // and must not leak into it.
        NotificationAudience audience = NotificationAudienceResolver.Resolve(
            RenewalRule(), Context(thresholdDays: null));

        audience.EscalationEmailRecipients.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("180,120,90,60,30", new[] { 180, 120, 90, 60, 30 })]
    [InlineData(" 30 , 60 ,30 ", new[] { 60, 30 })]
    [InlineData("", new int[0])]
    [InlineData("not-a-number,90", new[] { 90 })]
    public void ThresholdDaysParsing_IsForgivingOfBadInput(string csv, int[] expected)
    {
        // A typo in one rule's threshold list must not take out the nightly job for every
        // other rule, so parsing drops what it cannot read rather than throwing.
        var rule = new NotificationRule
        {
            Name = "T", EventType = NotificationEventTypes.ContractExpiring, ThresholdDaysCsv = csv,
        };

        rule.ThresholdDays().ShouldBe(expected);
    }
}
