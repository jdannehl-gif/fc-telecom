using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Application.Common;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Platform;
using FcTelecom.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FcTelecom.Application.Services;

/// <summary>
/// Read-side operations for services and circuits.
/// </summary>
/// <remarks>
/// Plain injected class rather than a mediator pipeline. The indirection a mediator buys
/// is worth it when there are cross-cutting behaviours to compose; here there are two
/// (authorization and audit) and both live somewhere better — policies at the endpoint,
/// an interceptor at the DbContext.
/// </remarks>
public sealed class TelecomServiceQueries(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IFieldEncryptor encryptor,
    ISecurityEventLogger securityLog,
    ILogger<TelecomServiceQueries> logger)
{
    public async Task<PagedResult<ServiceListItemDto>> ListAsync(
        ServiceListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Require(Permissions.ServicesRead);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        IQueryable<TelecomService> query = db.TelecomServices.AsNoTracking();

        if (filter.IncludeArchived)
        {
            query = query.IgnoreQueryFilters();
        }

        if (filter.LocationId is { } locationId)
        {
            query = query.Where(service => service.LocationId == locationId);
        }

        if (filter.CarrierVendorId is { } carrierId)
        {
            query = query.Where(service => service.CarrierVendorId == carrierId);
        }

        if (filter.ServiceType is { } serviceType)
        {
            query = query.Where(service => service.ServiceType == serviceType);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(service => service.Status == status);
        }

        if (filter.ServiceRole is { } role)
        {
            query = query.Where(service => service.ServiceRole == role);
        }

        if (filter.RegionId is { } regionId)
        {
            query = query.Where(service => service.Location.RegionId == regionId);
        }

        if (filter.MissingCircuitId == true)
        {
            query = query.Where(service => service.CircuitId == null || service.CircuitId == "");
        }

        if (filter.MissingContract == true)
        {
            query = query.Where(service => !service.ContractLinks.Any());
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            string term = filter.SearchText.Trim();

            // Deliberately covers the identifier aliases too. An engineer pastes whatever
            // string the carrier read out to them; which of their several naming schemes
            // it came from is not their problem to solve.
            query = query.Where(service =>
                (service.CircuitId != null && EF.Functions.Like(service.CircuitId, $"%{term}%")) ||
                (service.CarrierServiceId != null && EF.Functions.Like(service.CarrierServiceId, $"%{term}%")) ||
                EF.Functions.Like(service.Location.Name, $"%{term}%") ||
                EF.Functions.Like(service.Location.LocationCode, $"%{term}%") ||
                EF.Functions.Like(service.CarrierVendor.DisplayName, $"%{term}%") ||
                service.Identifiers.Any(identifier => EF.Functions.Like(identifier.Value, $"%{term}%")));
        }

        query = ApplySort(query, filter.SortBy, filter.SortDescending);

        // Note what is NOT selected here: no IP assignment columns at all. The list view
        // has no use for them, so they never leave the database regardless of permission.
        IQueryable<ServiceListItemDto> projected = query.Select(service => new ServiceListItemDto(
            service.Id,
            service.ServiceType,
            service.Status,
            service.ServiceRole,
            service.LocationId,
            service.Location.LocationCode,
            service.Location.Name,
            service.CarrierVendor.DisplayName,
            service.CircuitId,
            service.Bandwidth != null ? service.Bandwidth.DownloadKbps : null,
            service.Bandwidth != null ? service.Bandwidth.UploadKbps : null,
            service.CostHistory
                .Where(cost => cost.EffectiveFrom <= today &&
                               (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                .Select(cost => (decimal?)(cost.MonthlyRecurringCharge + cost.TaxesAndFees + cost.EquipmentRental))
                .FirstOrDefault(),
            service.CostHistory
                .Where(cost => cost.EffectiveFrom <= today &&
                               (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                .Select(cost => cost.CurrencyCode)
                .FirstOrDefault(),
            service.Monitors
                .OrderByDescending(monitor => monitor.LastCheckedUtc)
                .Select(monitor => monitor.CurrentState)
                .FirstOrDefault(),
            service.ContractLinks.Any(),
            service.IpAssignments.Any()));

        PagedResult<ServiceListItemDto> result =
            await projected.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken)
                .ConfigureAwait(false);

        // Cost columns are stripped for callers without Costs.Read. Doing it here rather
        // than in the Razor markup means the figure is absent from the object graph, not
        // merely absent from the screen.
        if (!currentUser.Has(Permissions.CostsRead))
        {
            return result with
            {
                Items = [.. result.Items.Select(item => item with { MonthlyCost = null, CurrencyCode = null })],
            };
        }

        return result;
    }

    public async Task<ServiceDetailDto> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Require(Permissions.ServicesRead);

        bool canViewIp = currentUser.Has(Permissions.ServiceIpDataRead);

        TelecomService? service = await db.TelecomServices
            .AsNoTracking()
            .Include(item => item.Location)
            .Include(item => item.CarrierVendor)
            .Include(item => item.ResellerVendor)
            .Include(item => item.LastMileVendor)
            .Include(item => item.UnderlyingNetworkOwnerVendor)
            .Include(item => item.VendorAccount)
            .Include(item => item.Bandwidth)
            .Include(item => item.Identifiers)
            .Include(item => item.PhoneNumbers)
            .Include(item => item.Dependencies).ThenInclude(dependency => dependency.DependsOnService)
                .ThenInclude(other => other!.CarrierVendor)
            .Include(item => item.Monitors)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (service is null)
        {
            throw new RecordNotFoundException(nameof(TelecomService), id);
        }

        // Two separate queries against the IP table on purpose. The unauthorized path asks
        // only "does any row exist" and never reads a column; the authorized path reads
        // and decrypts. There is no code path where ciphertext is fetched and then
        // discarded at render time.
        List<ServiceIpAssignmentDto> ipAssignments = [];
        bool hasHiddenIpData = false;

        if (canViewIp)
        {
            List<ServiceIpAssignment> rows = await db.ServiceIpAssignments
                .AsNoTracking()
                .Where(assignment => assignment.ServiceId == id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            ipAssignments = [.. rows.Select(Decrypt)];

            if (rows.Count > 0)
            {
                // Revealing restricted data is attributable. This is the read that gets
                // logged; ordinary record views are not.
                await securityLog.LogAsync(
                    SecurityEventType.SensitiveFieldRevealed,
                    $"Viewed {rows.Count} IP assignment(s) for service {id} ({service.CircuitId}).",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            hasHiddenIpData = await db.ServiceIpAssignments
                .AsNoTracking()
                .AnyAsync(assignment => assignment.ServiceId == id, cancellationToken)
                .ConfigureAwait(false);
        }

        ServiceMonitor? primaryMonitor = service.Monitors
            .OrderByDescending(monitor => monitor.LastCheckedUtc)
            .FirstOrDefault();

        return new ServiceDetailDto(
            service.Id,
            service.ServiceType,
            service.Status,
            service.ServiceRole,
            service.LocationId,
            service.Location.LocationCode,
            service.Location.Name,
            service.Location.TimeZoneId,
            service.CarrierVendorId,
            service.CarrierVendor.DisplayName,
            service.CarrierVendor.MainSupportPhone,
            service.CarrierVendor.PortalUrl,
            service.ResellerVendor?.DisplayName,
            service.LastMileVendor?.DisplayName,
            service.UnderlyingNetworkOwnerVendor?.DisplayName,
            service.VendorAccount?.AccountNumber,
            service.VendorAccount?.BillingAccountNumber,
            service.CircuitId,
            service.CarrierServiceId,
            service.InstallDate,
            service.ActivationDate,
            service.DisconnectEffectiveDate,
            service.DemarcLocation,
            service.HandoffType,
            service.Media,
            service.CpeMake,
            service.CpeModel,
            service.CpeSerial,
            service.CpeManagedByCarrier,
            service.WanInterface,
            service.SupportPriority,
            service.TechnicalNotes,
            service.Bandwidth is null ? null : new ServiceBandwidthDto(
                service.Bandwidth.DownloadKbps,
                service.Bandwidth.UploadKbps,
                service.Bandwidth.CommittedInformationRateKbps,
                service.Bandwidth.DataCapGb,
                service.Bandwidth.SlaLatencyMs,
                service.Bandwidth.SlaPacketLossPercent,
                service.Bandwidth.SlaAvailabilityPercent,
                service.Bandwidth.AssignedBandwidthKbps),
            [.. service.Identifiers.Select(identifier =>
                new ServiceIdentifierDto(identifier.Id, identifier.IdentifierType, identifier.Value, identifier.Notes))],
            ipAssignments,
            [.. service.PhoneNumbers.Select(number =>
                new ServicePhoneNumberDto(number.Id, number.Display, number.Kind, number.Description))],
            [.. service.Dependencies.Select(dependency => new ServiceDependencyDto(
                dependency.Id,
                dependency.DependsOnServiceId,
                DescribeService(dependency.DependsOnService),
                dependency.DependencyType,
                dependency.Confidence,
                dependency.Evidence,
                dependency.AssessedOn))],
            canViewIp,
            hasHiddenIpData,
            primaryMonitor?.CurrentState ?? MonitorState.Unknown,
            primaryMonitor?.StateChangedUtc);
    }

    private ServiceIpAssignmentDto Decrypt(ServiceIpAssignment assignment) =>
        new(assignment.Id,
            assignment.AddressFamily,
            encryptor.Decrypt(assignment.CidrEncrypted),
            DecryptOptional(assignment.GatewayEncrypted),
            DecryptOptional(assignment.UsableFirstEncrypted),
            DecryptOptional(assignment.UsableLastEncrypted),
            DecryptOptional(assignment.DnsPrimaryEncrypted),
            DecryptOptional(assignment.DnsSecondaryEncrypted),
            assignment.IsRoutedBlock,
            assignment.AssignmentNotes);

    private string? DecryptOptional(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return encryptor.Decrypt(ciphertext);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException
                                              or System.Security.Cryptography.CryptographicException)
        {
            // A row that will not decrypt means a key rotation went wrong. Surface it as a
            // visible placeholder rather than throwing: one bad row should not take out the
            // whole circuit page during an outage, which is exactly when it would be noticed.
            logger.LogError(exception, "Failed to decrypt an IP assignment field. Check data-protection key rotation.");
            return "[decryption failed]";
        }
    }

    private static string DescribeService(TelecomService? service) =>
        service is null
            ? "(unknown service)"
            : $"{service.CarrierVendor?.DisplayName ?? "?"} · {service.CircuitId ?? service.ServiceType.ToString()}";

    private static IQueryable<TelecomService> ApplySort(
        IQueryable<TelecomService> query, string sortBy, bool descending) =>
        (sortBy, descending) switch
        {
            ("CircuitId", false) => query.OrderBy(service => service.CircuitId),
            ("CircuitId", true) => query.OrderByDescending(service => service.CircuitId),
            ("Carrier", false) => query.OrderBy(service => service.CarrierVendor.DisplayName),
            ("Carrier", true) => query.OrderByDescending(service => service.CarrierVendor.DisplayName),
            ("ServiceType", false) => query.OrderBy(service => service.ServiceType),
            ("ServiceType", true) => query.OrderByDescending(service => service.ServiceType),
            ("Status", false) => query.OrderBy(service => service.Status),
            ("Status", true) => query.OrderByDescending(service => service.Status),
            (_, true) => query.OrderByDescending(service => service.Location.LocationCode)
                              .ThenByDescending(service => service.ServiceRole),
            _ => query.OrderBy(service => service.Location.LocationCode)
                      .ThenBy(service => service.ServiceRole),
        };

    private void Require(string permission)
    {
        if (!currentUser.Has(permission))
        {
            throw new PermissionDeniedException(permission);
        }
    }
}
