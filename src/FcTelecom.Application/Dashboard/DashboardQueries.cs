using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Application.Dashboard;

public sealed record PortfolioDashboardDto(
    int ActiveLocationCount,
    int ActiveServiceCount,
    decimal? MonthlyRecurringSpend,
    decimal? AnnualizedSpend,
    string? CurrencyCode,
    IReadOnlyList<AttentionItemDto> NeedsAttention,
    IReadOnlyList<CarrierSpendDto> SpendByCarrier,
    IReadOnlyList<CarrierAvailabilityDto> AvailabilityByCarrier,
    AvailabilityHeadlineDto? OverallAvailability,
    IReadOnlyList<RenewalPipelineItemDto> RenewalPipeline);

/// <summary>
/// A dashboard tile. Every one carries a <see cref="FilterUrl"/>.
/// </summary>
/// <remarks>
/// A number nobody can drill into is a decoration, and people stop trusting decorations
/// within a month. If a tile cannot be turned into a list of the specific records behind
/// it, it should not be on the dashboard.
/// </remarks>
public sealed record AttentionItemDto(
    string Key, string Icon, int Count, string Description, string FilterUrl, AttentionSeverity Severity);

public enum AttentionSeverity { Informational = 0, Warning = 1, Urgent = 2 }

public sealed record CarrierSpendDto(Guid VendorId, string CarrierName, decimal MonthlySpend, int ServiceCount);

public sealed record CarrierAvailabilityDto(
    Guid VendorId, string CarrierName, decimal AvailabilityPercent, decimal CoveragePercent,
    decimal? SlaTargetPercent, bool BelowSla, bool LowConfidence);

public sealed record AvailabilityHeadlineDto(decimal AvailabilityPercent, decimal CoveragePercent, bool LowConfidence);

public sealed record RenewalPipelineItemDto(
    Guid ContractId, string ContractNumber, string VendorName, int ServiceCount,
    decimal? AnnualValue, DateOnly? NoticeDeadline, int? DaysAway,
    RenewalUrgency Urgency, bool Confirmed, string Explanation);

/// <summary>Builds the portfolio dashboard, respecting what the caller may see.</summary>
public sealed class DashboardQueries(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
{
    private const int RenewalHorizonDays = 180;

    public async Task<PortfolioDashboardDto> GetAsync(int? regionId = null, CancellationToken cancellationToken = default)
    {
        DateOnly today = clock.Today;
        DateTime windowStart = clock.UtcNow.AddDays(-30);

        bool canSeeCosts = currentUser.Has(Permissions.CostsRead);
        bool canSeeContracts = currentUser.Has(Permissions.ContractsRead);
        bool canSeeIncidents = currentUser.Has(Permissions.IncidentsRead);

        IQueryable<Location> locations = db.Locations.AsNoTracking();
        IQueryable<TelecomService> services = db.TelecomServices.AsNoTracking();

        if (regionId is { } region)
        {
            locations = locations.Where(location => location.RegionId == region);
            services = services.Where(service => service.Location.RegionId == region);
        }

        int activeLocations = await locations
            .CountAsync(location => location.Status == LocationStatus.Active, cancellationToken)
            .ConfigureAwait(false);

        int activeServices = await services
            .CountAsync(service => service.Status == ServiceStatus.Active, cancellationToken)
            .ConfigureAwait(false);

        decimal? monthly = null;
        List<CarrierSpendDto> spendByCarrier = [];

        if (canSeeCosts)
        {
            var carrierRows = await services
                .Where(service => service.Status == ServiceStatus.Active)
                .Select(service => new
                {
                    service.CarrierVendorId,
                    CarrierName = service.CarrierVendor.DisplayName,
                    Cost = service.CostHistory
                        .Where(cost => cost.EffectiveFrom <= today &&
                                       (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                        .Select(cost => (decimal?)(cost.MonthlyRecurringCharge + cost.TaxesAndFees + cost.EquipmentRental))
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            monthly = carrierRows.Sum(row => row.Cost ?? 0m);

            spendByCarrier =
            [
                .. carrierRows
                    .GroupBy(row => (row.CarrierVendorId, row.CarrierName))
                    .Select(group => new CarrierSpendDto(
                        group.Key.CarrierVendorId,
                        group.Key.CarrierName,
                        group.Sum(row => row.Cost ?? 0m),
                        group.Count()))
                    .OrderByDescending(item => item.MonthlySpend)
            ];
        }

        var attention = new List<AttentionItemDto>();

        if (canSeeContracts)
        {
            List<Contract> upcoming = await db.Contracts
                .AsNoTracking()
                .Include(contract => contract.Vendor)
                .Include(contract => contract.Services)
                .Where(contract => contract.Status == ContractStatus.Active ||
                                   contract.Status == ContractStatus.InNoticePeriod)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var assessed = upcoming
                .Select(contract => (Contract: contract, Assessment: NoticeDeadlineCalculator.Assess(contract, today)))
                .ToList();

            int within90 = assessed.Count(pair =>
                pair.Assessment.DaysRemaining is >= 0 and <= 90);

            if (within90 > 0)
            {
                attention.Add(new AttentionItemDto(
                    "contracts-90", "⏰", within90,
                    "contracts with a cancellation-notice deadline within 90 days",
                    "/contracts?noticeWithin=90",
                    within90 > 0 ? AttentionSeverity.Urgent : AttentionSeverity.Warning));
            }

            int missingTerms = assessed.Count(pair => pair.Assessment.Urgency == RenewalUrgency.TermsUnknown);
            if (missingTerms > 0)
            {
                attention.Add(new AttentionItemDto(
                    "contracts-terms", "⛔", missingTerms,
                    "contracts with no end date, notice period, or renewal type on file",
                    "/contracts?missingTerms=true",
                    AttentionSeverity.Warning));
            }
        }

        if (canSeeIncidents)
        {
            int openOutages = await db.OutageEvents
                .AsNoTracking()
                .CountAsync(outage => outage.EndUtc == null && !outage.IsPlanned, cancellationToken)
                .ConfigureAwait(false);

            if (openOutages > 0)
            {
                attention.Add(new AttentionItemDto(
                    "outages-open", "🔴", openOutages, "services currently down",
                    "/outages?state=open", AttentionSeverity.Urgent));
            }
        }

        // Data-completeness tiles. These name exactly which records are missing which
        // fields — a generic "improve data quality" nudge gets ignored, a list of 23
        // circuits with no circuit ID gets fixed.
        int missingCircuitId = await services
            .CountAsync(service => service.Status == ServiceStatus.Active &&
                                   (service.CircuitId == null || service.CircuitId == ""), cancellationToken)
            .ConfigureAwait(false);

        if (missingCircuitId > 0)
        {
            attention.Add(new AttentionItemDto(
                "missing-circuit-id", "📄", missingCircuitId,
                "active services with no circuit ID recorded",
                "/services?missingCircuitId=true", AttentionSeverity.Warning));
        }

        int unmonitored = await services
            .CountAsync(service => service.Status == ServiceStatus.Active &&
                                   !service.Monitors.Any(), cancellationToken)
            .ConfigureAwait(false);

        if (unmonitored > 0)
        {
            attention.Add(new AttentionItemDto(
                "unmonitored", "❓", unmonitored,
                "active services with no monitoring coverage — their availability is unknown, not 100%",
                "/services?unmonitored=true", AttentionSeverity.Informational));
        }

        // Diversity has to be computed in memory: it walks dependency graphs and compares
        // vendor roles across services, and neither translates to SQL.
        int noDiversity = await CountLocationsWithoutDiversityAsync(regionId, cancellationToken)
            .ConfigureAwait(false);

        if (noDiversity > 0)
        {
            attention.Add(new AttentionItemDto(
                "no-diversity", "⚡", noDiversity,
                "locations with no backup or no true carrier diversity",
                "/reports/diversity", AttentionSeverity.Warning));
        }

        AvailabilityHeadlineDto? overall = null;
        List<CarrierAvailabilityDto> availabilityByCarrier = [];

        if (canSeeIncidents)
        {
            var rollupRows = await db.AvailabilityRollups
                .AsNoTracking()
                .Where(rollup => rollup.Grain == RollupGrain.Daily && rollup.PeriodStartUtc >= windowStart)
                // Both key expressions must have the same type. AvailabilityRollup.ServiceId
                // is Guid? (a monitor may watch a location-level internal target rather than
                // one circuit), so the right-hand key is lifted to Guid? rather than the
                // left-hand one being force-unwrapped — a rollup with no service must not
                // throw, it must simply not join.
                .Join(db.TelecomServices,
                      rollup => rollup.ServiceId,
                      service => (Guid?)service.Id,
                      (rollup, service) => new
                      {
                          Rollup = rollup,
                          service.CarrierVendorId,
                          CarrierName = service.CarrierVendor.DisplayName,
                          Sla = service.Bandwidth != null ? service.Bandwidth.SlaAvailabilityPercent : null,
                      })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (rollupRows.Count > 0)
            {
                AvailabilityResult combined = AvailabilityCalculator.Combine(rollupRows.Select(row => row.Rollup));
                overall = new AvailabilityHeadlineDto(
                    combined.AvailabilityPercent, combined.CoveragePercent, combined.LowConfidence);

                availabilityByCarrier =
                [
                    .. rollupRows
                        .GroupBy(row => (row.CarrierVendorId, row.CarrierName, row.Sla))
                        .Select(group =>
                        {
                            AvailabilityResult result = AvailabilityCalculator.Combine(group.Select(row => row.Rollup));
                            return new CarrierAvailabilityDto(
                                group.Key.CarrierVendorId,
                                group.Key.CarrierName,
                                result.AvailabilityPercent,
                                result.CoveragePercent,
                                group.Key.Sla,
                                AvailabilityCalculator.IsSlaCreditCandidate(result, group.Key.Sla),
                                result.LowConfidence);
                        })
                        .OrderBy(item => item.AvailabilityPercent)
                ];
            }
        }

        List<RenewalPipelineItemDto> pipeline = canSeeContracts
            ? await BuildRenewalPipelineAsync(today, canSeeCosts, cancellationToken).ConfigureAwait(false)
            : [];

        return new PortfolioDashboardDto(
            activeLocations,
            activeServices,
            monthly,
            monthly * 12m,
            canSeeCosts ? "USD" : null,
            [.. attention.OrderByDescending(item => item.Severity).ThenByDescending(item => item.Count)],
            spendByCarrier,
            availabilityByCarrier,
            overall,
            pipeline);
    }

    private async Task<int> CountLocationsWithoutDiversityAsync(int? regionId, CancellationToken cancellationToken)
    {
        IQueryable<Location> query = db.Locations
            .AsNoTracking()
            .Where(location => location.Status == LocationStatus.Active);

        if (regionId is { } region)
        {
            query = query.Where(location => location.RegionId == region);
        }

        List<Location> locations = await query
            .Include(location => location.Services).ThenInclude(service => service.Dependencies)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return locations.Count(location =>
        {
            DiversityAssessment assessment = DiversityAnalyzer.Assess(location, [.. location.Services]);
            return assessment.Verdict is DiversityVerdict.NoBackup or DiversityVerdict.SharedRisk;
        });
    }

    private async Task<List<RenewalPipelineItemDto>> BuildRenewalPipelineAsync(
        DateOnly today, bool canSeeCosts, CancellationToken cancellationToken)
    {
        List<Contract> contracts = await db.Contracts
            .AsNoTracking()
            .Include(contract => contract.Vendor)
            .Include(contract => contract.Services).ThenInclude(link => link.Service)
                .ThenInclude(service => service.CostHistory)
            .Where(contract => contract.Status == ContractStatus.Active ||
                               contract.Status == ContractStatus.InNoticePeriod)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. contracts
                .Select(contract => (Contract: contract, Assessment: NoticeDeadlineCalculator.Assess(contract, today)))
                .Where(pair => pair.Assessment.Urgency != RenewalUrgency.None)
                .Where(pair => pair.Assessment.DaysRemaining is null ||
                               pair.Assessment.DaysRemaining <= RenewalHorizonDays)
                .OrderBy(pair => pair.Assessment.NoticeDeadline ?? DateOnly.MaxValue)
                .Select(pair => new RenewalPipelineItemDto(
                    pair.Contract.Id,
                    pair.Contract.ContractNumber,
                    pair.Contract.Vendor?.DisplayName ?? "(unknown vendor)",
                    pair.Contract.Services.Count,
                    canSeeCosts
                        ? pair.Contract.Services
                            .Select(link => link.Service?.CostOn(today)?.MonthlyEquivalent.Amount ?? 0m)
                            .Sum() * 12m
                        : null,
                    pair.Assessment.NoticeDeadline,
                    pair.Assessment.DaysRemaining,
                    pair.Assessment.Urgency,
                    pair.Assessment.DeadlineConfirmed,
                    pair.Assessment.Explanation))
        ];
    }
}
