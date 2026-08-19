using FcTelecom.Domain.Common;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Services;

namespace FcTelecom.Domain.Calculations;

/// <summary>
/// Spend maths. Pure functions over cost records — no clock, no database.
/// </summary>
/// <remarks>
/// Nothing here is stored. Annualized spend is computed on read every time, because a
/// stored total drifts the moment someone backdates a price change, and a drifted total
/// is worse than no total: people act on it.
/// </remarks>
public static class SpendCalculator
{
    /// <summary>
    /// Monthly-equivalent spend for a set of services on a given date.
    /// </summary>
    /// <remarks>
    /// Services on non-monthly billing cycles are normalised, so an annually-billed
    /// $12,000 circuit contributes $1,000. Services with no cost record on that date
    /// contribute nothing rather than zero-with-a-shrug — an ordered-but-not-activated
    /// circuit genuinely has no cost yet, and that is different from costing nothing.
    /// </remarks>
    public static Money MonthlySpend(
        IEnumerable<TelecomService> services,
        DateOnly asOf,
        string currencyCode = Money.DefaultCurrency)
    {
        ArgumentNullException.ThrowIfNull(services);

        return Money.Sum(
            services
                .Select(service => service.CostOn(asOf))
                .Where(cost => cost is not null)
                .Select(cost => cost!.MonthlyEquivalent),
            currencyCode);
    }

    /// <summary>Monthly spend × 12. The run-rate figure, not a forecast.</summary>
    /// <remarks>
    /// This is deliberately a naive annualization. It does not attempt to model
    /// contractual escalators, planned disconnections, or circuits mid-install, because a
    /// number that quietly incorporates assumptions is a number nobody can reconcile
    /// against an invoice. Forecasting is a separate, clearly-labelled report.
    /// </remarks>
    public static Money AnnualizedSpend(
        IEnumerable<TelecomService> services,
        DateOnly asOf,
        string currencyCode = Money.DefaultCurrency) =>
        MonthlySpend(services, asOf, currencyCode) * 12m;

    /// <summary>Groups monthly spend by an arbitrary key — carrier, region, service type.</summary>
    public static IReadOnlyDictionary<TKey, Money> MonthlySpendBy<TKey>(
        IEnumerable<TelecomService> services,
        Func<TelecomService, TKey> keySelector,
        DateOnly asOf,
        string currencyCode = Money.DefaultCurrency)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keySelector);

        return services
            .Select(service => (Key: keySelector(service), Cost: service.CostOn(asOf)))
            .Where(pair => pair.Cost is not null)
            .GroupBy(pair => pair.Key)
            .ToDictionary(
                group => group.Key,
                group => Money.Sum(group.Select(pair => pair.Cost!.MonthlyEquivalent), currencyCode));
    }

    /// <summary>
    /// Cost per Mbps for a data service, or null when the figure would be meaningless.
    /// </summary>
    /// <remarks>
    /// Uses the committed rate where one exists rather than the advertised rate. Comparing
    /// a 1 Gbps best-effort coax service against a 1 Gbps CIR fibre service at the
    /// advertised rate makes the coax look like a bargain, which is precisely the
    /// conclusion that leads someone to put a clinic's primary circuit on it.
    /// <para>
    /// Returns null for voice services and for anything with no bandwidth on record —
    /// a cost-per-Mbps figure for a POTS alarm line is noise in a report that is supposed
    /// to surface outliers.
    /// </para>
    /// </remarks>
    public static decimal? CostPerMbps(TelecomService service, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!service.IsDataService || service.Bandwidth is not { } bandwidth)
        {
            return null;
        }

        int kbps = bandwidth.BillableKbps;
        if (kbps <= 0)
        {
            return null;
        }

        ServiceCost? cost = service.CostOn(asOf);
        if (cost is null)
        {
            return null;
        }

        decimal mbps = kbps / 1000m;
        return Math.Round(cost.MonthlyEquivalent.Amount / mbps, 4);
    }

    /// <summary>
    /// Finds services whose cost per Mbps is unusually high relative to their peers.
    /// </summary>
    /// <remarks>
    /// Compares against the <b>median</b> rather than the mean. Telecom pricing has a long
    /// right tail — a handful of legacy T1s at $600/Mbps drag a mean so far up that
    /// genuinely overpriced circuits look reasonable next to it. The median is not fooled.
    /// <para>
    /// Comparison is within a peer group (same service type and similar media), because
    /// cellular backup costing more per Mbps than fibre is a fact about cellular, not a
    /// finding.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CostOutlier> FindCostOutliers(
        IEnumerable<TelecomService> services,
        DateOnly asOf,
        decimal thresholdMultiplier = 1.5m,
        int minimumPeerGroupSize = 3)
    {
        ArgumentNullException.ThrowIfNull(services);

        var priced = services
            .Select(service => (Service: service, PerMbps: CostPerMbps(service, asOf)))
            .Where(pair => pair.PerMbps is > 0)
            .Select(pair => (pair.Service, PerMbps: pair.PerMbps!.Value))
            .ToList();

        var outliers = new List<CostOutlier>();

        foreach (var peerGroup in priced.GroupBy(pair => (pair.Service.ServiceType, pair.Service.Media)))
        {
            var group = peerGroup.ToList();
            if (group.Count < minimumPeerGroupSize)
            {
                continue; // Too small a sample to call anything an outlier honestly.
            }

            decimal median = Median(group.Select(pair => pair.PerMbps));
            if (median <= 0)
            {
                continue;
            }

            decimal threshold = median * thresholdMultiplier;

            outliers.AddRange(
                group.Where(pair => pair.PerMbps > threshold)
                     .Select(pair => new CostOutlier(
                         pair.Service.Id,
                         pair.PerMbps,
                         median,
                         Math.Round(pair.PerMbps / median, 2))));
        }

        return [.. outliers.OrderByDescending(outlier => outlier.RatioToMedian)];
    }

    internal static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        if (sorted.Count == 0)
        {
            return 0m;
        }

        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}

/// <summary>A service costing materially more per Mbps than comparable services.</summary>
public readonly record struct CostOutlier(
    Guid ServiceId,
    decimal CostPerMbps,
    decimal PeerMedianCostPerMbps,
    decimal RatioToMedian);
