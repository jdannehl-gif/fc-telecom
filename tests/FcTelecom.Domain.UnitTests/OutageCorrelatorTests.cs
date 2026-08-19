using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Monitoring;
using Shouldly;

namespace FcTelecom.Domain.UnitTests;

/// <summary>
/// The correlation engine — where check results become incidents, or deliberately do not.
/// </summary>
/// <remarks>
/// A monitoring system that cries wolf twice gets ignored forever. Most of these tests are
/// about the engine <i>refusing</i> to open an outage: below threshold, without quorum,
/// when the probe itself is the fault, and when nobody is watching at all.
/// </remarks>
public sealed class OutageCorrelatorTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ServiceMonitor Monitor(int quorum = 2, int failureThreshold = 3, bool internalTarget = false) =>
        new()
        {
            Name = "Test monitor",
            Target = "203.0.113.10",
            CheckType = CheckType.Icmp,
            FailureThreshold = failureThreshold,
            SuccessThreshold = 2,
            RequiredProbeQuorum = quorum,
            IsInternalTarget = internalTarget,
        };

    private static ProbeObservation Failing(Guid probeId, int consecutiveFailures = 3) =>
        new(probeId, CountsTowardQuorum: true, CheckOutcome.Down, consecutiveFailures, 0, Now, IsStale: false);

    private static ProbeObservation Succeeding(Guid probeId, int consecutiveSuccesses = 2) =>
        new(probeId, CountsTowardQuorum: true, CheckOutcome.Up, 0, consecutiveSuccesses, Now, IsStale: false);

    private static CorrelationContext Context(
        IReadOnlyList<(Guid, MonitorState)>? siblings = null,
        IReadOnlyDictionary<Guid, bool>? probeWide = null,
        bool? internalReachable = null,
        bool inMaintenance = false) =>
        new(siblings ?? [], probeWide ?? new Dictionary<Guid, bool>(), internalReachable, inMaintenance);

    // ── Refusals ────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoProbesAtAll_IsUnknown_NotDown()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(), MonitorState.Up, [], Context());

        decision.NewState.ShouldBe(MonitorState.Unknown);
        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.ShouldRecordCoverageGap.ShouldBeTrue();
        decision.CoverageGapReason.ShouldBe(CoverageGapReason.NoProbesAssigned);
    }

    [Fact]
    public void AllProbesStale_IsUnknown_NotDown()
    {
        Guid probe = Guid.NewGuid();
        var stale = new ProbeObservation(probe, true, CheckOutcome.Down, 5, 0, Now.AddHours(-3), IsStale: true);

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(), MonitorState.Up, [stale], Context());

        decision.NewState.ShouldBe(MonitorState.Unknown);
        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.CoverageGapReason.ShouldBe(CoverageGapReason.AgentOffline);
        decision.Reason.ShouldContain("unknown, not down");
    }

    [Fact]
    public void SingleFailureBelowThreshold_IsSuspect_NoOutage()
    {
        Guid probe = Guid.NewGuid();

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(), MonitorState.Up, [Failing(probe, consecutiveFailures: 1)], Context());

        decision.NewState.ShouldBe(MonitorState.Suspect);
        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.Reason.ShouldContain("single missed check is not an incident");
    }

    [Fact]
    public void OneProbeFailing_WithQuorumOfTwo_IsSuspect_NoOutage()
    {
        Guid failing = Guid.NewGuid();
        Guid healthy = Guid.NewGuid();

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 2),
            MonitorState.Up,
            [Failing(failing), Succeeding(healthy)],
            Context());

        decision.NewState.ShouldBe(MonitorState.Suspect);
        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.Reason.ShouldContain("cannot tell a circuit fault from a path fault");
    }

    /// <summary>
    /// A probe reporting every monitor it covers as failing is a broken probe, not an
    /// estate-wide outage. Opening dozens of outages here is how a monitoring system
    /// destroys its own credibility in a single afternoon.
    /// </summary>
    [Fact]
    public void ProbeWideFailure_OpensNoOutage_AndRecordsACoverageGap()
    {
        Guid probe = Guid.NewGuid();

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1),
            MonitorState.Up,
            [Failing(probe)],
            Context(probeWide: new Dictionary<Guid, bool>
            {
                [probe] = true,
                [Guid.NewGuid()] = false,
                [Guid.NewGuid()] = false,
            }));

        decision.NewState.ShouldBe(MonitorState.Unknown);
        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.Classification.ShouldBe(OutageClassification.MonitoringFailure);
        decision.ShouldRecordCoverageGap.ShouldBeTrue();
    }

    // ── Opening an outage ───────────────────────────────────────────────────────────

    [Fact]
    public void QuorumOfFailingProbes_OpensAnOutage()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 2),
            MonitorState.Up,
            [Failing(Guid.NewGuid()), Failing(Guid.NewGuid())],
            Context());

        decision.NewState.ShouldBe(MonitorState.Down);
        decision.ShouldOpenOutage.ShouldBeTrue();
        decision.ConfirmingProbeCount.ShouldBe(2);
    }

    [Fact]
    public void SiblingServiceUp_ClassifiesAsCarrierFailure_AndExplainsWhy()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1),
            MonitorState.Up,
            [Failing(Guid.NewGuid())],
            Context(siblings: [(Guid.NewGuid(), MonitorState.Up)]));

        decision.Classification.ShouldBe(OutageClassification.CarrierFailure);

        // The reasoning is surfaced deliberately. A classification an engineer cannot
        // argue with is a classification they will ignore.
        decision.Reason.ShouldContain("Another service at this location is up");
    }

    [Fact]
    public void EverythingAtTheSiteDown_ClassifiesAsSiteFailure()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1),
            MonitorState.Up,
            [Failing(Guid.NewGuid())],
            Context(siblings:
            [
                (Guid.NewGuid(), MonitorState.Down),
                (Guid.NewGuid(), MonitorState.Down),
            ]));

        decision.Classification.ShouldBe(OutageClassification.SiteFailure);
        decision.Reason.ShouldContain("site event");
    }

    [Fact]
    public void PublicTargetDown_InternalTargetUp_ClassifiesAsCarrierFailure()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1, internalTarget: false),
            MonitorState.Up,
            [Failing(Guid.NewGuid())],
            Context(internalReachable: true));

        decision.Classification.ShouldBe(OutageClassification.CarrierFailure);
        decision.Reason.ShouldContain("site is alive; the transport is not");
    }

    [Fact]
    public void InternalTargetDown_ClassifiesAsCpeFailure()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1, internalTarget: true),
            MonitorState.Up,
            [Failing(Guid.NewGuid())],
            Context(internalReachable: false));

        decision.Classification.ShouldBe(OutageClassification.CpeFailure);
    }

    [Fact]
    public void NoSurroundingSignal_ClassifiesAsUnknown_RatherThanGuessing()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1), MonitorState.Up, [Failing(Guid.NewGuid())], Context());

        decision.Classification.ShouldBe(OutageClassification.Unknown);
        decision.Reason.ShouldContain("Recorded as unknown rather than guessed");
    }

    /// <summary>
    /// Inside a maintenance window the outage is still recorded — it is simply linked to
    /// the window and excluded from availability. Discarding it would make "total downtime
    /// including planned" unanswerable, and people genuinely ask that.
    /// </summary>
    [Fact]
    public void MaintenanceWindow_StillOpensTheOutage_ButSaysSo()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1),
            MonitorState.Up,
            [Failing(Guid.NewGuid())],
            Context(inMaintenance: true));

        decision.ShouldOpenOutage.ShouldBeTrue();
        decision.Reason.ShouldContain("maintenance window");
        decision.Reason.ShouldContain("excluded from availability");
    }

    // ── Recovery ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SuccessesBelowThreshold_AreRecovering_NotYetUp()
    {
        Guid probe = Guid.NewGuid();
        var oneSuccess = new ProbeObservation(probe, true, CheckOutcome.Up, 0, 1, Now, false);

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(), MonitorState.Down, [oneSuccess], Context());

        decision.NewState.ShouldBe(MonitorState.Recovering);
        decision.ShouldCloseOutage.ShouldBeFalse();
    }

    [Fact]
    public void SustainedSuccess_ClosesTheOutage()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(), MonitorState.Down, [Succeeding(Guid.NewGuid())], Context());

        decision.NewState.ShouldBe(MonitorState.Up);
        decision.ShouldCloseOutage.ShouldBeTrue();
    }

    /// <summary>
    /// Flapping: one probe recovered, another is still failing. Closing the outage here
    /// would produce a resolved-then-reopened pair every few minutes and a stream of
    /// notifications nobody can act on.
    /// </summary>
    [Fact]
    public void MixedSignalsDuringRecovery_KeepTheOutageOpen()
    {
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(),
            MonitorState.Down,
            [Succeeding(Guid.NewGuid()), Failing(Guid.NewGuid())],
            Context());

        decision.ShouldCloseOutage.ShouldBeFalse();
        decision.NewState.ShouldBe(MonitorState.Down);
    }

    // ── Advisory probes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ingested signal (The Dude syslog, another platform's webhook) can raise suspicion
    /// but cannot alone confirm an outage.
    /// </summary>
    /// <remarks>
    /// Syslog from The Dude tells you The Dude thinks something changed. It does not say
    /// what was measured, from where, with what timeout, or whether The Dude itself was
    /// healthy. Treating that as authoritative would poison the availability figures the
    /// rest of the system works hard to keep honest.
    /// </remarks>
    [Fact]
    public void AdvisoryProbeAlone_CannotConfirmAnOutage()
    {
        var advisory = new ProbeObservation(
            Guid.NewGuid(), CountsTowardQuorum: false, CheckOutcome.Down, 10, 0, Now, false);

        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 1), MonitorState.Up, [advisory], Context());

        decision.ShouldOpenOutage.ShouldBeFalse();
        decision.NewState.ShouldBe(MonitorState.Suspect);
    }

    [Fact]
    public void QuorumIsCappedByTheNumberOfProbesActuallyAvailable()
    {
        // A monitor configured for quorum 2 but with only one probe assigned still opens
        // outages — it is simply flagged as reduced confidence elsewhere. Requiring an
        // impossible quorum would mean it never reports anything at all.
        CorrelationDecision decision = OutageCorrelator.Evaluate(
            Monitor(quorum: 2), MonitorState.Up, [Failing(Guid.NewGuid())], Context());

        decision.ShouldOpenOutage.ShouldBeTrue();
        decision.ConfirmingProbeCount.ShouldBe(1);
    }
}
