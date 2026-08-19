using System.Globalization;
using System.Text.RegularExpressions;
using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Application.Platform;

public enum SearchResultKind { Location, Service, Vendor, VendorAccount, Contract, PhoneNumber, IpAddress, Contact }

public sealed record SearchResult(
    SearchResultKind Kind, string Title, string Subtitle, string Url, string MatchedOn);

/// <summary>
/// Cross-entity search: location, circuit ID, account number, carrier, static IP,
/// phone number, contract number.
/// </summary>
/// <remarks>
/// Two things worth knowing about how this is built.
/// <para>
/// <b>Results are scoped at the query, not filtered at render.</b> A user without
/// <c>Costs.Read</c> searching an invoice number gets "no results", not "access denied" —
/// the second answer confirms the record exists, which is information they did not have
/// a moment ago.
/// </para>
/// <para>
/// <b>IP search uses the deterministic hash, not decryption.</b> The term is normalised
/// and hashed with the same HMAC key used at write time, and the hash is matched against
/// an index. This is exact-match only, which is what an engineer actually needs when they
/// are holding a firewall config and want to know which circuit an address belongs to.
/// </para>
/// </remarks>
public sealed partial class GlobalSearchService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IFieldEncryptor encryptor)
{
    private const int PerCategoryLimit = 8;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string term, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
        {
            return [];
        }

        term = term.Trim();
        string like = $"%{term}%";
        var results = new List<SearchResult>();

        if (currentUser.Has(Permissions.LocationsRead))
        {
            results.AddRange(await db.Locations
                .AsNoTracking()
                .Where(location => EF.Functions.Like(location.Name, like) ||
                                   EF.Functions.Like(location.LocationCode, like) ||
                                   EF.Functions.Like(location.PhysicalAddress.City, like) ||
                                   (location.MainPhone != null && EF.Functions.Like(location.MainPhone, like)))
                .OrderBy(location => location.LocationCode)
                .Take(PerCategoryLimit)
                .Select(location => new SearchResult(
                    SearchResultKind.Location,
                    location.LocationCode + " · " + location.Name,
                    location.PhysicalAddress.City + ", " + (location.PhysicalAddress.StateOrProvince ?? ""),
                    "/locations/" + location.Id,
                    "Location"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        if (currentUser.Has(Permissions.ServicesRead))
        {
            results.AddRange(await db.TelecomServices
                .AsNoTracking()
                .Where(service =>
                    (service.CircuitId != null && EF.Functions.Like(service.CircuitId, like)) ||
                    (service.CarrierServiceId != null && EF.Functions.Like(service.CarrierServiceId, like)) ||
                    service.Identifiers.Any(identifier => EF.Functions.Like(identifier.Value, like)))
                .OrderBy(service => service.CircuitId)
                .Take(PerCategoryLimit)
                .Select(service => new SearchResult(
                    SearchResultKind.Service,
                    service.CircuitId ?? service.ServiceType.ToString(),
                    service.CarrierVendor.DisplayName + " · " + service.Location.LocationCode + " " + service.Location.Name,
                    "/services/" + service.Id,
                    "Circuit ID"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));

            results.AddRange(await db.VendorAccounts
                .AsNoTracking()
                .Where(account => EF.Functions.Like(account.AccountNumber, like) ||
                                  (account.BillingAccountNumber != null &&
                                   EF.Functions.Like(account.BillingAccountNumber, like)))
                .Take(PerCategoryLimit)
                .Select(account => new SearchResult(
                    SearchResultKind.VendorAccount,
                    account.AccountNumber,
                    account.Vendor.DisplayName + (account.Description != null ? " · " + account.Description : ""),
                    "/vendors/" + account.VendorId,
                    "Account number"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));

            results.AddRange(await db.ServicePhoneNumbers
                .AsNoTracking()
                .Where(number => EF.Functions.Like(number.NumberOrRangeStart, like) ||
                                 (number.RangeEnd != null && EF.Functions.Like(number.RangeEnd, like)))
                .Take(PerCategoryLimit)
                .Select(number => new SearchResult(
                    SearchResultKind.PhoneNumber,
                    number.NumberOrRangeStart,
                    number.Service.Location.LocationCode + " · " + number.Service.CarrierVendor.DisplayName,
                    "/services/" + number.ServiceId,
                    "Phone number"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        if (currentUser.Has(Permissions.VendorsRead))
        {
            results.AddRange(await db.Vendors
                .AsNoTracking()
                .Where(vendor => EF.Functions.Like(vendor.DisplayName, like) ||
                                 EF.Functions.Like(vendor.LegalName, like))
                .Take(PerCategoryLimit)
                .Select(vendor => new SearchResult(
                    SearchResultKind.Vendor,
                    vendor.DisplayName,
                    vendor.LegalName,
                    "/vendors/" + vendor.Id,
                    "Vendor"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        if (currentUser.Has(Permissions.ContractsRead))
        {
            results.AddRange(await db.Contracts
                .AsNoTracking()
                .Where(contract => EF.Functions.Like(contract.ContractNumber, like))
                .Take(PerCategoryLimit)
                .Select(contract => new SearchResult(
                    SearchResultKind.Contract,
                    contract.ContractNumber,
                    contract.Vendor.DisplayName + (contract.Description != null ? " · " + contract.Description : ""),
                    "/contracts/" + contract.Id,
                    "Contract number"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        // Static IP search comes last and is gated on its own permission, independent of
        // Services.Read. Everything above is ordinary business data; this is not.
        if (currentUser.Has(Permissions.ServiceIpDataRead) && LooksLikeIpOrCidr(term))
        {
            byte[] hash = encryptor.ComputeSearchHash(NormalizeCidr(term));

            results.AddRange(await db.ServiceIpAssignments
                .AsNoTracking()
                .Where(assignment => assignment.CidrSearchHash != null && assignment.CidrSearchHash == hash)
                .Take(PerCategoryLimit)
                .Select(assignment => new SearchResult(
                    SearchResultKind.IpAddress,
                    term,
                    assignment.Service.Location.LocationCode + " · " +
                    assignment.Service.CarrierVendor.DisplayName + " · " +
                    (assignment.Service.CircuitId ?? ""),
                    "/services/" + assignment.ServiceId,
                    "Static IP block"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Normalises a CIDR or bare address so the same block hashes identically whichever
    /// way it was typed. Without this, <c>203.0.113.8/29</c> and <c>203.0.113.008/29</c>
    /// are different search terms and one of them silently finds nothing.
    /// </summary>
    internal static string NormalizeCidr(string value)
    {
        value = value.Trim();

        string[] parts = value.Split('/', 2);
        string address = parts[0];

        if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? parsed))
        {
            return value.ToUpperInvariant();
        }

        string normalized = parsed.ToString();

        if (parts.Length == 2 &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefix))
        {
            return $"{normalized}/{prefix}";
        }

        // A bare address is treated as a /32 or /128 so it hashes the same way a stored
        // single-address assignment would.
        int hostPrefix = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{normalized}/{hostPrefix}";
    }

    private static bool LooksLikeIpOrCidr(string term) => IpLikePattern().IsMatch(term);

    [GeneratedRegex(@"^[0-9a-fA-F:.]+(/\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex IpLikePattern();
}
