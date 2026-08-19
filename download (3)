using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Application.Common;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Application.Vendors;

public sealed record VendorListItemDto(
    Guid Id, string DisplayName, string LegalName, VendorKind Kind,
    string? MainSupportPhone, string? PortalUrl,
    int ServiceCount, int AccountCount, decimal? MonthlySpend);

public sealed record VendorDetailDto(
    Guid Id, string DisplayName, string LegalName, VendorKind Kind,
    string? PortalUrl, string? MainSupportPhone, string? SupportHours,
    string? CredentialReference, string? ItGluePasswordRecordId, string? Notes,
    IReadOnlyList<VendorAccountDto> Accounts,
    IReadOnlyList<VendorContactDto> Contacts,
    IReadOnlyList<TicketProcedureDto> TicketProcedures,
    int ServiceCount,
    decimal? MonthlySpend,
    IReadOnlyList<VendorRoleUsageDto> RoleUsage);

public sealed record VendorAccountDto(
    Guid Id, string AccountNumber, string? BillingAccountNumber, string? Description,
    string? BillingContactName, int ServiceCount);

public sealed record VendorContactDto(
    Guid Id, string FullName, string? JobTitle, ContactKind Kind,
    string? Email, string? PhoneNumber, string? MobileNumber, int? EscalationLevel);

public sealed record TicketProcedureDto(
    Guid Id, string ScenarioName, string? PhoneNumber, string? PortalUrl, string? EmailAddress,
    string? HoursOfOperation, string? Procedure, string? RequiredInformation, string? ExpectedResponseTime);

/// <summary>
/// How this vendor shows up across the estate — as carrier, reseller, last-mile provider,
/// or backbone owner.
/// </summary>
/// <remarks>
/// Worth surfacing on the vendor page because a company appearing as last-mile provider
/// for eleven circuits sold by three different carriers is a concentration risk that is
/// otherwise invisible: nothing on any individual circuit record looks unusual.
/// </remarks>
public sealed record VendorRoleUsageDto(string Role, int ServiceCount);

public sealed class VendorQueries(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<PagedResult<VendorListItemDto>> ListAsync(
        string? searchText, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        Require(Permissions.VendorsRead);

        DateOnly today = clock.Today;
        bool canSeeCosts = currentUser.Has(Permissions.CostsRead);

        IQueryable<Vendor> query = db.Vendors.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string like = $"%{searchText.Trim()}%";
            query = query.Where(vendor => EF.Functions.Like(vendor.DisplayName, like) ||
                                          EF.Functions.Like(vendor.LegalName, like));
        }

        return await query
            .OrderBy(vendor => vendor.DisplayName)
            .Select(vendor => new VendorListItemDto(
                vendor.Id,
                vendor.DisplayName,
                vendor.LegalName,
                vendor.Kind,
                vendor.MainSupportPhone,
                vendor.PortalUrl,
                db.TelecomServices.Count(service => service.CarrierVendorId == vendor.Id &&
                                                    service.Status == ServiceStatus.Active),
                vendor.Accounts.Count,
                canSeeCosts
                    ? db.TelecomServices
                        .Where(service => service.CarrierVendorId == vendor.Id &&
                                          service.Status == ServiceStatus.Active)
                        .SelectMany(service => service.CostHistory)
                        .Where(cost => cost.EffectiveFrom <= today &&
                                       (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                        .Sum(cost => (decimal?)(cost.MonthlyRecurringCharge + cost.TaxesAndFees + cost.EquipmentRental))
                    : null))
            .ToPagedResultAsync(page, pageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VendorDetailDto> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Require(Permissions.VendorsRead);

        DateOnly today = clock.Today;
        bool canSeeCosts = currentUser.Has(Permissions.CostsRead);

        Vendor? vendor = await db.Vendors
            .AsNoTracking()
            .Include(item => item.Accounts).ThenInclude(account => account.BillingContact)
            .Include(item => item.Contacts)
            .Include(item => item.TicketProcedures)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (vendor is null)
        {
            throw new RecordNotFoundException(nameof(Vendor), id);
        }

        var accountServiceCounts = await db.TelecomServices
            .AsNoTracking()
            .Where(service => service.VendorAccountId != null &&
                              service.VendorAccount!.VendorId == id)
            .GroupBy(service => service.VendorAccountId!.Value)
            .Select(group => new { AccountId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.AccountId, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        int serviceCount = await db.TelecomServices
            .AsNoTracking()
            .CountAsync(service => service.CarrierVendorId == id && service.Status == ServiceStatus.Active,
                        cancellationToken)
            .ConfigureAwait(false);

        decimal? monthlySpend = null;
        if (canSeeCosts)
        {
            monthlySpend = await db.TelecomServices
                .AsNoTracking()
                .Where(service => service.CarrierVendorId == id && service.Status == ServiceStatus.Active)
                .SelectMany(service => service.CostHistory)
                .Where(cost => cost.EffectiveFrom <= today &&
                               (cost.EffectiveTo == null || cost.EffectiveTo >= today))
                .SumAsync(cost => (decimal?)(cost.MonthlyRecurringCharge + cost.TaxesAndFees + cost.EquipmentRental),
                          cancellationToken)
                .ConfigureAwait(false);
        }

        var roleUsage = new List<VendorRoleUsageDto>
        {
            new("Carrier", serviceCount),
            new("Reseller", await db.TelecomServices.CountAsync(
                service => service.ResellerVendorId == id, cancellationToken).ConfigureAwait(false)),
            new("Last-mile provider", await db.TelecomServices.CountAsync(
                service => service.LastMileVendorId == id, cancellationToken).ConfigureAwait(false)),
            new("Underlying network owner", await db.TelecomServices.CountAsync(
                service => service.UnderlyingNetworkOwnerVendorId == id, cancellationToken).ConfigureAwait(false)),
        };

        return new VendorDetailDto(
            vendor.Id,
            vendor.DisplayName,
            vendor.LegalName,
            vendor.Kind,
            vendor.PortalUrl,
            vendor.MainSupportPhone,
            vendor.SupportHours,
            vendor.CredentialReference,
            vendor.ItGluePasswordRecordId,
            vendor.Notes,
            [.. vendor.Accounts.Select(account => new VendorAccountDto(
                account.Id, account.AccountNumber, account.BillingAccountNumber, account.Description,
                account.BillingContact?.FullName,
                accountServiceCounts.TryGetValue(account.Id, out int count) ? count : 0))],
            [.. vendor.Contacts
                .OrderBy(contact => contact.EscalationLevel ?? int.MaxValue)
                .ThenBy(contact => contact.FullName)
                .Select(contact => new VendorContactDto(
                    contact.Id, contact.FullName, contact.JobTitle, contact.Kind,
                    contact.Email, contact.PhoneNumber, contact.MobileNumber, contact.EscalationLevel))],
            [.. vendor.TicketProcedures.Select(procedure => new TicketProcedureDto(
                procedure.Id, procedure.ScenarioName, procedure.PhoneNumber, procedure.PortalUrl,
                procedure.EmailAddress, procedure.HoursOfOperation, procedure.Procedure,
                procedure.RequiredInformation, procedure.ExpectedResponseTime))],
            serviceCount,
            monthlySpend,
            [.. roleUsage.Where(usage => usage.ServiceCount > 0)]);
    }

    private void Require(string permission)
    {
        if (!currentUser.Has(permission))
        {
            throw new PermissionDeniedException(permission);
        }
    }
}
