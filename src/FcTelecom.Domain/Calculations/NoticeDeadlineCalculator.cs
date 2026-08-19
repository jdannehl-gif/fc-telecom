using System.Globalization;
using FcTelecom.Domain.Contracts;

namespace FcTelecom.Domain.Calculations;

/// <summary>
/// Why a contract needs attention, and how urgently.
/// </summary>
public enum RenewalUrgency
{
    /// <summary>Nothing due within the alert horizon.</summary>
    None = 0,

    /// <summary>Inside the alert horizon but not the closest threshold.</summary>
    Upcoming = 1,

    /// <summary>Inside the tightest threshold. Act now.</summary>
    Urgent = 2,

    /// <summary>The notice deadline has passed. Depending on renewal type, you may be committed.</summary>
    Missed = 3,

    /// <summary>
    /// The terms needed to answer the question are not on file. This is its own state
    /// rather than an empty cell, because "we do not know when we can cancel this" is a
    /// finding, not an absence of one.
    /// </summary>
    TermsUnknown = 4,
}

public readonly record struct RenewalAssessment(
    RenewalUrgency Urgency,
    DateOnly? NoticeDeadline,
    int? DaysRemaining,
    int? TriggeredThreshold,
    bool DeadlineConfirmed,
    string Explanation);

/// <summary>
/// Works out when notice must be given to prevent a contract renewing, and how close
/// that is.
/// </summary>
/// <remarks>
/// The system <b>proposes</b> a deadline; a person <b>confirms</b> it. Real telecom
/// contracts say things like "ninety days prior to the end of the then-current term",
/// where the then-current term is itself disputed after an auto-renewal has already
/// happened once. Computing that silently produces a date nobody trusts, and a date
/// nobody trusts is a date nobody acts on.
/// <para>
/// Unconfirmed deadlines still raise alerts, labelled as unconfirmed. Suppressing an
/// alert on a technicality is worse than sending an uncertain one.
/// </para>
/// </remarks>
public static class NoticeDeadlineCalculator
{
    /// <summary>Default alert horizon, in days before the deadline.</summary>
    public static readonly IReadOnlyList<int> DefaultThresholdDays = [180, 120, 90, 60, 30];

    /// <summary>
    /// Computes the proposed deadline from the contract's own terms.
    /// Returns null when the terms are insufficient — which is information, not a failure.
    /// </summary>
    public static DateOnly? ProposeNoticeDeadline(DateOnly? endDate, int? noticePeriodDays)
    {
        if (endDate is not { } end || noticePeriodDays is not { } days || days < 0)
        {
            return null;
        }

        return end.AddDays(-days);
    }

    /// <summary>
    /// Where a contract stands relative to its notice deadline, with an explanation
    /// suitable for showing directly to a user.
    /// </summary>
    public static RenewalAssessment Assess(
        Contract contract,
        DateOnly today,
        IReadOnlyList<int>? thresholdDays = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        thresholdDays ??= DefaultThresholdDays;

        if (contract.HasIncompleteTerms && contract.EffectiveNoticeDeadline is null)
        {
            return new RenewalAssessment(
                RenewalUrgency.TermsUnknown,
                NoticeDeadline: null,
                DaysRemaining: null,
                TriggeredThreshold: null,
                DeadlineConfirmed: false,
                Explanation: DescribeMissingTerms(contract));
        }

        if (contract.EffectiveNoticeDeadline is not { } deadline)
        {
            return new RenewalAssessment(
                RenewalUrgency.TermsUnknown,
                null, null, null, false,
                "No notice deadline is recorded and one cannot be computed from the terms on file.");
        }

        int daysRemaining = deadline.DayNumber - today.DayNumber;
        bool confirmed = contract.NoticeDeadlineConfirmed;

        string confirmationNote = confirmed
            ? string.Empty
            : " This date was computed, not confirmed against the agreement — verify before relying on it.";

        if (daysRemaining < 0)
        {
            string consequence = contract.RenewalType switch
            {
                RenewalType.AutoRenew =>
                    // InvariantCulture, not the ambient culture: these assessment strings are
                    // asserted on directly by the calculator's unit tests, and a month count
                    // that formats differently on a differently-configured CI runner would
                    // make those tests fail for a reason that has nothing to do with the
                    // renewal logic (CA1305).
                    $"The contract has likely auto-renewed for a further {contract.RenewalTermMonths?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} months.",
                RenewalType.EvergreenMonthToMonth =>
                    "The contract has moved to month-to-month; cancellation is likely still possible on shorter notice.",
                RenewalType.None =>
                    "The contract is set to end rather than renew, so the missed date may not be costly.",
                _ =>
                    "The consequence depends on renewal terms that are not recorded.",
            };

            return new RenewalAssessment(
                RenewalUrgency.Missed, deadline, daysRemaining, null, confirmed,
                $"The notice deadline passed {-daysRemaining} day(s) ago. {consequence}{confirmationNote}");
        }

        // The tightest threshold this contract has crossed.
        int? triggered = thresholdDays
            .Where(threshold => daysRemaining <= threshold)
            .OrderBy(threshold => threshold)
            .Select(threshold => (int?)threshold)
            .FirstOrDefault();

        if (triggered is null)
        {
            return new RenewalAssessment(
                RenewalUrgency.None, deadline, daysRemaining, null, confirmed,
                $"Notice is due in {daysRemaining} day(s), outside the alert horizon.{confirmationNote}");
        }

        int tightest = thresholdDays.Min();
        RenewalUrgency urgency = triggered.Value <= tightest
            ? RenewalUrgency.Urgent
            : RenewalUrgency.Upcoming;

        return new RenewalAssessment(
            urgency, deadline, daysRemaining, triggered, confirmed,
            $"Notice is due in {daysRemaining} day(s) — inside the {triggered} day threshold.{confirmationNote}");
    }

    /// <summary>
    /// Which alert thresholds a contract has crossed as of today, given those already sent.
    /// </summary>
    /// <remarks>
    /// Returns only newly-crossed thresholds, so the nightly job cannot re-send the same
    /// 90-day warning every night for a month. Crossed-but-unsent thresholds are included
    /// even if a tighter one is also due, so a contract that was added to the system late
    /// still produces its 90-day notice rather than silently skipping it.
    /// </remarks>
    public static IReadOnlyList<int> ThresholdsToRaise(
        Contract contract,
        DateOnly today,
        IReadOnlySet<int> alreadySentThresholds,
        IReadOnlyList<int>? thresholdDays = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(alreadySentThresholds);
        thresholdDays ??= DefaultThresholdDays;

        if (!contract.IsActionable || contract.EffectiveNoticeDeadline is not { } deadline)
        {
            return [];
        }

        int daysRemaining = deadline.DayNumber - today.DayNumber;
        if (daysRemaining < 0)
        {
            return [];
        }

        return
        [
            .. thresholdDays
                .Where(threshold => daysRemaining <= threshold)
                .Where(threshold => !alreadySentThresholds.Contains(threshold))
                .OrderByDescending(threshold => threshold)
        ];
    }

    private static string DescribeMissingTerms(Contract contract)
    {
        var missing = new List<string>(3);

        if (contract.EndDate is null)
        {
            missing.Add("end date");
        }

        if (contract.NoticePeriodDays is null)
        {
            missing.Add("notice period");
        }

        if (contract.RenewalType == RenewalType.Unknown)
        {
            missing.Add("renewal type");
        }

        return missing.Count == 0
            ? "Contract terms are incomplete."
            : $"Missing {string.Join(", ", missing)}. Until these are recorded, there is no way to know " +
              "whether this agreement auto-renews or when notice would have to be given.";
    }
}
