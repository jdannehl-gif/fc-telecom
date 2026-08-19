using FcTelecom.Domain.Monitoring;

namespace FcTelecom.Domain.Calculations;

/// <summary>One probe's recent verdict on one monitor, as input to correlation.</summary>
public readonly record struct ProbeObservation(
    Guid ProbeId,
    bool CountsTowardQuorum,
    CheckOutcome LatestOutcome,
    int ConsecutiveFailures,
    int ConsecutiveSuccesses,
    DateTime LatestObservedUtc,
    bool IsStale);

/// <summary>
/// Context about the surrounding estate, used to classify a failure.
/// </summary>
/// <param name="SiblingMonitorsAtLocation">
/// Monitors at the same location, excluding this one, with each one's current state.
/// </param>
/// <param name="ProbeWideFailureMap">
/// Per probe, whether every monitor that probe covers is currently failing. Used to spot
/// a dead probe before it is mistaken for a dead network.
/// </param>
/// <param name="InternalTargetReachable">
/// Whether an internal target at this location is reachable. Null when there is none,
/// which is itself a coverage gap worth reporting.
/// </param>
/// <param name="InMaintenanceWindow">Whether this instant falls inside a maintenance window.</param>
public readonly record struct CorrelationContext(
    IReadOnlyList<(Guid MonitorId, MonitorState State)> SiblingMonitorsAtLocation,
    IReadOnlyDictionary<Guid, bool> ProbeWideFailureMap,
    bool? InternalTargetReachable,
    bool InMaintenanceWindow);

public readonly record struct CorrelationDecision(
    MonitorState NewState,
    bool ShouldOpenOutage,
    bool ShouldCloseOutage,
    bool ShouldRecordCoverageGap,
    CoverageGapReason? CoverageGapReason,
    OutageClassification Classification,
    string Reason,
    int ConfirmingProbeCount);

/// <summary>
/// Turns raw check results into a state, and a state change into an outage — or,
/// importantly, into a decision not to open one.
/// </summary>
/// <remarks>
/// <para>
/// The organising principle: it is better to report "we don't know" than a confident wrong
/// answer. A monitoring system that cries wolf twice gets ignored forever, and an
/// availability number nobody trusts is worse than no number because it still ends up in
/// front of executives.
/// </para>
/// <para>
/// Three refusals to jump to conclusions are built in:
/// </para>
/// <list type="number">
/// <item><b>Debounce.</b> A single dropped packet is not an incident. It takes
/// <see cref="ServiceMonitor.FailureThreshold"/> consecutive failures to leave Up.</item>
/// <item><b>Quorum.</b> A single perspective cannot distinguish "the circuit is down"
/// from "the path to the observer is down" from "the observer is down". Two independent
/// perspectives distinguish all three in most cases.</item>
/// <item><b>Unknown.</b> When the probes are gone, the answer is Unknown and the time is
/// excluded from the availability denominator — not Down, and certainly not Up.</item>
/// </list>
/// <para>
/// Pure function. No clock, no database, no configuration lookup, which is what lets the
/// scenario suite cover flapping, single-probe, all-probes-offline, site-wide, and
/// maintenance-overlap cases deterministically.
/// </para>
/// </remarks>
public static class OutageCorrelator
{
    public static CorrelationDecision Evaluate(
        ServiceMonitor monitor,
        MonitorState currentState,
        IReadOnlyList<ProbeObservation> observations,
        CorrelationContext context)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(observations);

        var usable = observations.Where(observation => !observation.IsStale).ToList();

        // ── No usable observers ──────────────────────────────────────────────────────
        // We are blind. Not down — blind. Anything else here manufactures either a false
        // outage or a false 100%.
        if (usable.Count == 0)
        {
            return new CorrelationDecision(
                MonitorState.Unknown,
                ShouldOpenOutage: false,
                ShouldCloseOutage: currentState is MonitorState.Down or MonitorState.Recovering,
                ShouldRecordCoverageGap: true,
                CoverageGapReason: observations.Count == 0
                    ? Monitoring.CoverageGapReason.NoProbesAssigned
                    : Monitoring.CoverageGapReason.AgentOffline,
                OutageClassification.Unknown,
                observations.Count == 0
                    ? "No probes are assigned to this monitor, so its state is unknown."
                    : "Every assigned probe is stale or offline. The state is unknown, not down — " +
                      "this period is excluded from availability rather than counted either way.",
                ConfirmingProbeCount: 0);
        }

        var quorumEligible = usable.Where(observation => observation.CountsTowardQuorum).ToList();

        var failing = quorumEligible
            .Where(observation => observation.ConsecutiveFailures >= monitor.FailureThreshold)
            .ToList();

        var succeeding = quorumEligible
            .Where(observation => observation.ConsecutiveSuccesses >= monitor.SuccessThreshold)
            .ToList();

        bool anyFailing = usable.Any(observation => observation.LatestOutcome is CheckOutcome.Down or CheckOutcome.Timeout);

        // ── Is the probe the problem? ────────────────────────────────────────────────
        // If a probe reports every single monitor it covers as failing, the probe is what
        // broke, not every circuit in the estate simultaneously. Discount it entirely.
        var credibleFailing = failing
            .Where(observation => !IsProbeWideFailure(observation.ProbeId, context))
            .ToList();

        if (failing.Count > 0 && credibleFailing.Count == 0)
        {
            return new CorrelationDecision(
                MonitorState.Unknown,
                ShouldOpenOutage: false,
                ShouldCloseOutage: false,
                ShouldRecordCoverageGap: true,
                Monitoring.CoverageGapReason.AgentOffline,
                OutageClassification.MonitoringFailure,
                "Every monitor covered by the reporting probe is failing at once, so the probe " +
                "itself is the most likely fault. No outage opened; coverage recorded as a gap.",
                ConfirmingProbeCount: 0);
        }

        // ── Recovery ────────────────────────────────────────────────────────────────
        if (currentState is MonitorState.Down or MonitorState.Recovering)
        {
            if (succeeding.Count > 0 && !anyFailing)
            {
                return new CorrelationDecision(
                    MonitorState.Up,
                    false,
                    ShouldCloseOutage: true,
                    false, null,
                    OutageClassification.Unknown,
                    $"{succeeding.Count} probe(s) reported {monitor.SuccessThreshold} consecutive " +
                    "successful checks. Service restored.",
                    succeeding.Count);
            }

            // Recovering means "coming back", and it only applies when nothing is still
            // failing. With one probe up and another still down we stay Down — otherwise a
            // flapping circuit produces a resolved/reopened pair every few minutes and a
            // stream of notifications nobody can act on.
            if (!anyFailing && usable.Any(observation => observation.LatestOutcome == CheckOutcome.Up))
            {
                return new CorrelationDecision(
                    MonitorState.Recovering, false, false, false, null,
                    OutageClassification.Unknown,
                    "Checks are succeeding again but not yet for long enough to close the outage.",
                    0);
            }

            return new CorrelationDecision(
                MonitorState.Down, false, false, false, null,
                ClassifyFailure(monitor, context, out string ongoingReason),
                ongoingReason,
                credibleFailing.Count);
        }

        // ── Failure ─────────────────────────────────────────────────────────────────
        if (credibleFailing.Count > 0)
        {
            int required = Math.Min(monitor.RequiredProbeQuorum, Math.Max(1, quorumEligible.Count));

            if (credibleFailing.Count >= required)
            {
                OutageClassification classification = ClassifyFailure(monitor, context, out string reason);

                if (context.InMaintenanceWindow)
                {
                    // The outage is still opened and recorded — it is simply linked to the
                    // window and excluded from the availability denominator. Silently
                    // discarding it would make "total downtime including planned"
                    // unanswerable, which is a question people genuinely ask.
                    return new CorrelationDecision(
                        MonitorState.Down,
                        ShouldOpenOutage: true,
                        false, false, null,
                        classification,
                        $"{reason} This falls inside a maintenance window, so it is recorded but " +
                        "excluded from availability.",
                        credibleFailing.Count);
                }

                return new CorrelationDecision(
                    MonitorState.Down,
                    ShouldOpenOutage: true,
                    false, false, null,
                    classification,
                    reason,
                    credibleFailing.Count);
            }

            // Failing, but not enough independent agreement to call it.
            return new CorrelationDecision(
                MonitorState.Suspect, false, false, false, null,
                OutageClassification.Unknown,
                $"{credibleFailing.Count} of {required} required probe(s) report failure. " +
                "Not enough independent agreement to declare an outage — a single perspective " +
                "cannot tell a circuit fault from a path fault.",
                credibleFailing.Count);
        }

        if (anyFailing)
        {
            return new CorrelationDecision(
                MonitorState.Suspect, false, false, false, null,
                OutageClassification.Unknown,
                $"Checks are failing but have not yet reached the {monitor.FailureThreshold}-failure " +
                "threshold. A single missed check is not an incident.",
                0);
        }

        return new CorrelationDecision(
            MonitorState.Up, false,
            ShouldCloseOutage: currentState is MonitorState.Down or MonitorState.Recovering,
            false, null,
            OutageClassification.Unknown,
            "All reporting probes confirm the service is reachable.",
            usable.Count);
    }

    /// <summary>
    /// Works out what is most likely responsible, and produces the sentence shown to the
    /// engineer. The reasoning is surfaced deliberately: a classification somebody cannot
    /// argue with is a classification they will ignore.
    /// </summary>
    private static OutageClassification ClassifyFailure(
        ServiceMonitor monitor,
        CorrelationContext context,
        out string reason)
    {
        var siblings = context.SiblingMonitorsAtLocation ?? [];

        // Everything at the site is down → site event, not a carrier's fault.
        if (siblings.Count > 0 && siblings.All(sibling => sibling.State is MonitorState.Down))
        {
            reason = "Every monitored service at this location is down simultaneously, which points " +
                     "to a site event — power, or a total site disconnection — rather than one carrier.";
            return OutageClassification.SiteFailure;
        }

        // A sibling is up → the site has connectivity, so this carrier is the problem.
        if (siblings.Any(sibling => sibling.State == MonitorState.Up))
        {
            reason = "Another service at this location is up, so the site itself has connectivity. " +
                     "The fault is with this carrier or its path.";
            return OutageClassification.CarrierFailure;
        }

        // Public target unreachable, internal target fine → the site is alive, transport is not.
        if (context.InternalTargetReachable == true && !monitor.IsInternalTarget)
        {
            reason = "The circuit's public target is unreachable while an internal target at this " +
                     "location still responds. The site is alive; the transport is not.";
            return OutageClassification.CarrierFailure;
        }

        // The carrier's edge answers but nothing behind it does.
        if (context.InternalTargetReachable == false && monitor.IsInternalTarget)
        {
            reason = "The carrier's edge responds but the internal target behind it does not, " +
                     "which points at customer equipment or the LAN rather than the carrier.";
            return OutageClassification.CpeFailure;
        }

        reason = "There is not enough surrounding signal to attribute this outage. Recorded as " +
                 "unknown rather than guessed.";
        return OutageClassification.Unknown;
    }

    private static bool IsProbeWideFailure(Guid probeId, CorrelationContext context)
    {
        var map = context.ProbeWideFailureMap;

        // Needs a few monitors before "all of them are failing" means anything. With one
        // or two monitors on a probe, all of them failing is an ordinary small outage.
        if (map is null || map.Count < 3)
        {
            return false;
        }

        return map.TryGetValue(probeId, out bool allFailing) && allFailing;
    }
}
