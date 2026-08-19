using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Application.Common;
using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Application.Organization;

public sealed record LocationListItemDto(
    Guid Id,
    string LocationCode,
    string Name,
    LocationStatus Status,
    LocationType LocationType,
    string City,
    string? StateOrProvince,
    string? RegionName,
    Criticality Criticality,
    int ServiceCount,
    decimal? MonthlyCost,
    string? CurrencyCode,
    bool HasOpenOutage);

public sealed record LocationFilter
{
    public int? RegionId { get; init; }
    public LocationStatus? Status { get; init; }
    public LocationType? LocationType { get; init; }
    public Criticality? Criticality { get; init; }
    public string? SearchText { get; init; }
    public bool IncludeArchived { get; init; }
    public string SortBy { get; init; } = "LocationCode";
    public bool SortDescending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Everything the location detail page needs, in one call.
/// </summary>
/// <remarks>
/// The page has to answer seven questions on one screen — what services are here, which
/// is primary, who is the carrier and how do we escalate, what are the circuit IDs and
/// handoff details, what are we paying, when do contracts renew and when is notice due,
/// and is it up. Assembling that from seven round trips would make the page slow exactly
/// when someone is in a hurry.
/// </remarks>
public sealed record LocationDetailDto(
    Guid Id,
    string LocationCode,
    string Name,
    LocationStatus Status,
    LocationType LocationType,
    string AddressSingleLine,
    string TimeZoneId,
    string? RegionName,
    string? CostCenterCode,
    string? BusinessUnitName,
    string? MainPhone,
    string? OperatingHours,
    Criticality Criticality,
    int AcceptableOutageMinutes,
    decimal? Latitude,
    decimal? Longitude,
    string? Notes,
    string? ItOwnerName,
    string? ItOwnerPhone,
    IReadOnlyList<LocationContactDto> Contacts,
    IReadOnlyList<LocationServiceSummaryDto> Services,
    IReadOnlyList<UpcomingDeadlineDto> UpcomingDeadlines,
    DiversityAssessment Diversity,
    decimal? TotalMonthlyCost,
    decimal? TotalAnnualizedCost,
    decimal? CostPerMbps,
    string? CurrencyCode,
    int MonitoredServiceCount);

public sealed record LocationContactDto(
    Guid Id, string FullName, string? JobTitle, string RoleAtLocation,
    string? PhoneNumber, string? Email, bool IsPrimary);

public sealed record LocationServiceSummaryDto(
    Guid ServiceId,
    ServiceType ServiceType,
    ServiceRole ServiceRole,
    ServiceStatus Status,
    string CarrierName,
    string? CarrierSupportPhone,
    SupportPriority SupportPriority,
    string? CircuitId,
    string? AccountNumber,
    int? DownloadKbps,
    int? UploadKbps,
    int? CommittedKbps,
    string? HandoffSummary,
    bool HasIpData,
    decimal? MonthlyCost,
    string? ContractNumber,
    DateOnly? NoticeDeadline,
    int? DaysUntilNotice,
    bool NoticeDeadlineConfirmed,
    MonitorState MonitorState,
    decimal? Availability30Day,
    decimal? Coverage30Day,
    IReadOnlyList<string> Warnings);

public sealed record UpcomingDeadlineDto(
    DateOnly Date, string Description, string? ContractNumber, int DaysAway, bool Confirmed);

public sealed class LocationQueries(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<PagedResult<LocationListItemDto>> ListAsync(
        LocationFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Require(Permissions.LocationsRead);

        DateOnly today = clock.Today;
        bool canSeeCosts = currentUser.Has(Permissions.CostsRead);

        IQueryable<Location> query = db.Locations.AsNoTracking();

        if (filter.IncludeArchived)
        {
            query = query.IgnoreQueryFilters();
        }

        if (filter.RegionId is { } regionId)
        {
            query = query.Where(location => location.RegionId == regionId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(location => location.Status == status);
        }

        if (filter.LocationType is { } locationType)
        {
            query = query.Where(location => location.LocationType == locationType);
        }

        if (filter.Criticality is { } criticality)
        {
            query = query.Where(location => location.Criticality == criticality);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            string term = filter.SearchText.Trim();
            query = query.Where(location =>
                EF.Functions.Like(location.Name, $"%{term}%") ||
                EF.Functions.Like(location.LocationCode, $"%{term}%") ||
                EF.Functions.Like(location.PhysicalAddress.City, $"%{term}%"));
        }

        query = (filter.SortBy, filter.SortDescending) switch
        {
            ("Name", false) => query.OrderBy(location => location.Name),
            ("Name", true) => query.OrderByDescending(location => location.Name),
            ("Criticality", false) => query.OrderBy(location => location.Criticality),
            ("Criticality", true) => query.OrderByDescending(location => location.Criticality),
            (_, true) => query.OrderByDescending(location => location.LocationCode),
            _ => query.OrderBy(location => location.LocationCode),
        };

        IQueryable<LocationListItemDto> projected = query.Select(location => new LocationListItemDto(
            location.Id,
            location.LocationCode,
            location.Name,
            location.Status,
            location.LocationType,
            location.PhysicalAddress.City,
            location.PhysicalAddress.StateOrProvince,
            location.Region != null ? location.Region.Name : null,
            location.Criticality,
            location.Services.Count(service => service.Status == ServiceStatus.Active),
            // Computed unconditionally, then stripped below for callers without Costs.Read.
            //
            // The obvious alternative — wrapping this aggregate in `canSeeCosts ? … : null`
            // — puts a captured bool inside the projection, which EF Core turns into a
            // parameterised CASE around a correlated aggregate subquery. That translates on
            // some provider versions and throws on others, and the failure surfaces at
            // runtime on a list page rather than at build time. One query shape is worth
            // more than one skipped SUM.
            location.Services
                .SelectMany(service => service.CostHistory)
                .Where(cost => cost.EffectiveFrom <= today &&
                               (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                .Sum(cost => (decimal?)(cost.MonthlyRecurringCharge + cost.TaxesAndFees + cost.EquipmentRental)),
            "USD",
            db.OutageEvents.Any(outage => outage.LocationId == location.Id && outage.EndUtc == null)));

        PagedResult<LocationListItemDto> result =
            await projected.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken)
                .ConfigureAwait(false);

        // The figure never reaches the caller's object graph, only the app tier's.
        return canSeeCosts
            ? result
            : result with
            {
                Items = [.. result.Items.Select(item => item with { MonthlyCost = null, CurrencyCode = null })],
            };
    }

    public async Task<LocationDetailDto> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Require(Permissions.LocationsRead);

        DateOnly today = clock.Today;
        DateTime windowStart = clock.UtcNow.AddDays(-30);
        bool canSeeCosts = currentUser.Has(Permissions.CostsRead);
        bool canSeeContracts = currentUser.Has(Permissions.ContractsRead);

        Location? location = await db.Locations
            .AsNoTracking()
            .Include(item => item.Region)
            .Include(item => item.CostCenter)
            .Include(item => item.BusinessUnit)
            .Include(item => item.ItOwnerContact)
            .Include(item => item.Contacts).ThenInclude(link => link.Contact)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            throw new RecordNotFoundException(nameof(Location), id);
        }

        List<TelecomService> services = await db.TelecomServices
            .AsNoTracking()
            .Where(service => service.LocationId == id)
            .Include(service => service.CarrierVendor)
            .Include(service => service.LastMileVendor)
            .Include(service => service.VendorAccount)
            .Include(service => service.Bandwidth)
            .Include(service => service.CostHistory)
            .Include(service => service.Dependencies)
            .Include(service => service.Monitors)
            .Include(service => service.ContractLinks)
                .ThenInclude(link => link.Contract)
                .ThenInclude(contract => contract.Vendor)
            .OrderBy(service => service.ServiceRole)
            .ThenBy(service => service.ServiceType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Which services have addressing on file, without reading a single encrypted
        // column. The location page needs the fact, not the values, and there is no
        // reason for ciphertext to travel just so it can be discarded.
        var serviceIds = services.Select(service => service.Id).ToList();

        HashSet<Guid> servicesWithIpData = serviceIds.Count == 0
            ? []
            : [.. await db.ServiceIpAssignments
                .AsNoTracking()
                .Where(assignment => serviceIds.Contains(assignment.ServiceId))
                .Select(assignment => assignment.ServiceId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)];

        // Diversity is computed in memory: the analyser walks the dependency graph and
        // infers shared vendors across services, neither of which translates to SQL.
        DiversityAssessment diversity = DiversityAnalyzer.Assess(location, services);

        var monitorIds = services.SelectMany(service => service.Monitors).Select(monitor => monitor.Id).ToList();

        Dictionary<Guid, AvailabilityRollup[]> rollupsByMonitor = monitorIds.Count == 0
            ? []
            : (await db.AvailabilityRollups
                .AsNoTracking()
                .Where(rollup => monitorIds.Contains(rollup.MonitorId) &&
                                 rollup.Grain == RollupGrain.Daily &&
                                 rollup.PeriodStartUtc >= windowStart)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
              .GroupBy(rollup => rollup.MonitorId)
              .ToDictionary(group => group.Key, group => group.ToArray());

        var serviceSummaries = new List<LocationServiceSummaryDto>(services.Count);

        foreach (TelecomService service in services)
        {
            var contractLink = service.ContractLinks.FirstOrDefault();
            Contract? contract = contractLink?.Contract;

            RenewalAssessment? renewal = contract is null
                ? null
                : NoticeDeadlineCalculator.Assess(contract, today);

            ServiceMonitor? monitor = service.Monitors
                .OrderByDescending(item => item.LastCheckedUtc)
                .FirstOrDefault();

            AvailabilityResult? availability = null;
            if (monitor is not null && rollupsByMonitor.TryGetValue(monitor.Id, out AvailabilityRollup[]? rollups))
            {
                availability = AvailabilityCalculator.Combine(rollups);
            }

            var warnings = new List<string>();

            warnings.AddRange(
                diversity.Risks
                    .Where(risk => risk.ServiceId == service.Id || risk.ConflictingServiceId == service.Id)
                    .Select(risk => risk.Description));

            if (string.IsNullOrWhiteSpace(service.CircuitId))
            {
                warnings.Add("No circuit ID recorded — this circuit cannot be looked up with the carrier.");
            }

            if (contract is null && service.Status == ServiceStatus.Active)
            {
                warnings.Add("No contract on file. Renewal and cancellation terms are unknown.");
            }

            if (monitor is null && service.IsDataService)
            {
                warnings.Add("No monitoring configured. Availability for this circuit is unknown, not 100%.");
            }

            serviceSummaries.Add(new LocationServiceSummaryDto(
                service.Id,
                service.ServiceType,
                service.ServiceRole,
                service.Status,
                service.CarrierVendor.DisplayName,
                service.CarrierVendor.MainSupportPhone,
                service.SupportPriority,
                service.CircuitId,
                service.VendorAccount?.AccountNumber,
                service.Bandwidth?.DownloadKbps,
                service.Bandwidth?.UploadKbps,
                service.Bandwidth?.CommittedInformationRateKbps,
                BuildHandoffSummary(service),
                servicesWithIpData.Contains(service.Id),
                canSeeCosts ? service.CostOn(today)?.MonthlyEquivalent.Amount : null,
                canSeeContracts ? contract?.ContractNumber : null,
                canSeeContracts ? renewal?.NoticeDeadline : null,
                canSeeContracts ? renewal?.DaysRemaining : null,
                renewal?.DeadlineConfirmed ?? false,
                monitor?.CurrentState ?? MonitorState.Unknown,
                availability?.AvailabilityPercent,
                availability?.CoveragePercent,
                warnings));
        }

        var deadlines = canSeeContracts
            ? services
                .SelectMany(service => service.ContractLinks)
                .Select(link => link.Contract)
                .Where(contract => contract is not null)
                .DistinctBy(contract => contract!.Id)
                .Select(contract => (Contract: contract!, Assessment: NoticeDeadlineCalculator.Assess(contract!, today)))
                .Where(pair => pair.Assessment.NoticeDeadline is not null)
                .OrderBy(pair => pair.Assessment.NoticeDeadline)
                .Select(pair => new UpcomingDeadlineDto(
                    pair.Assessment.NoticeDeadline!.Value,
                    $"{pair.Contract.Vendor?.DisplayName ?? "Vendor"} {pair.Contract.ContractNumber} — cancellation notice",
                    pair.Contract.ContractNumber,
                    pair.Assessment.DaysRemaining ?? 0,
                    pair.Assessment.DeadlineConfirmed))
                .ToList()
            : [];

        decimal? monthlyTotal = canSeeCosts
            ? services.Sum(service => service.CostOn(today)?.MonthlyEquivalent.Amount ?? 0m)
            : null;

        int wanKbps = services
            .Where(service => service.IsDataService && service.IsLive && service.Bandwidth is not null)
            .Sum(service => service.Bandwidth!.BillableKbps);

        return new LocationDetailDto(
            location.Id,
            location.LocationCode,
            location.Name,
            location.Status,
            location.LocationType,
            location.PhysicalAddress.SingleLine,
            location.TimeZoneId,
            location.Region?.Name,
            location.CostCenter?.Code,
            location.BusinessUnit?.Name,
            location.MainPhone,
            location.OperatingHours,
            location.Criticality,
            location.AcceptableOutageMinutes,
            location.Latitude,
            location.Longitude,
            location.Notes,
            location.ItOwnerContact?.FullName,
            location.ItOwnerContact?.PhoneNumber,
            [.. location.Contacts.Select(link => new LocationContactDto(
                link.Contact.Id, link.Contact.FullName, link.Contact.JobTitle, link.RoleAtLocation,
                link.Contact.PhoneNumber, link.Contact.Email, link.IsPrimary))],
            serviceSummaries,
            deadlines,
            diversity,
            monthlyTotal,
            monthlyTotal * 12m,
            monthlyTotal is { } total && wanKbps > 0
                ? Math.Round(total / (wanKbps / 1000m), 2)
                : null,
            canSeeCosts ? "USD" : null,
            services.Count(service => service.Monitors.Count > 0));
    }

    private static string? BuildHandoffSummary(TelecomService service)
    {
        var parts = new List<string>(2);

        if (service.HandoffType != HandoffType.Unknown)
        {
            parts.Add(service.HandoffType.ToString());
        }

        if (!string.IsNullOrWhiteSpace(service.DemarcLocation))
        {
            parts.Add(service.DemarcLocation);
        }

        return parts.Count == 0 ? null : string.Join(" → ", parts);
    }

    private void Require(string permission)
    {
        if (!currentUser.Has(permission))
        {
            throw new PermissionDeniedException(permission);
        }
    }
}
