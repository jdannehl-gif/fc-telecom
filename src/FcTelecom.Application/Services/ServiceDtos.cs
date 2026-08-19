using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Services;

namespace FcTelecom.Application.Services;

/// <summary>Row shape for the services list view.</summary>
public sealed record ServiceListItemDto(
    Guid Id,
    ServiceType ServiceType,
    ServiceStatus Status,
    ServiceRole ServiceRole,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    string CarrierName,
    string? CircuitId,
    int? DownloadKbps,
    int? UploadKbps,
    decimal? MonthlyCost,
    string? CurrencyCode,
    MonitorState MonitorState,
    bool HasContract,
    bool HasIpData);

/// <summary>
/// The full circuit record.
/// </summary>
/// <remarks>
/// <see cref="IpAssignments"/> is empty when the caller lacks <c>ServiceIpData.Read</c> —
/// the query handler never selects the values, so they do not reach this object, the
/// render tree, or a JSON response. Masking in the UI would leave the value one
/// developer-tools inspection away from being visible, which is not a control.
/// </remarks>
public sealed record ServiceDetailDto(
    Guid Id,
    ServiceType ServiceType,
    ServiceStatus Status,
    ServiceRole ServiceRole,
    Guid LocationId,
    string LocationCode,
    string LocationName,
    string LocationTimeZoneId,

    // Vendor roles — four of them, because that is what makes the diversity question answerable.
    Guid CarrierVendorId,
    string CarrierName,
    string? CarrierSupportPhone,
    string? CarrierPortalUrl,
    string? ResellerName,
    string? LastMileVendorName,
    string? UnderlyingNetworkOwnerName,

    string? AccountNumber,
    string? BillingAccountNumber,
    string? CircuitId,
    string? CarrierServiceId,

    DateOnly? InstallDate,
    DateOnly? ActivationDate,
    DateOnly? DisconnectEffectiveDate,

    string? DemarcLocation,
    HandoffType HandoffType,
    TransportMedia Media,
    string? CpeMake,
    string? CpeModel,
    string? CpeSerial,
    bool CpeManagedByCarrier,
    string? WanInterface,
    SupportPriority SupportPriority,
    string? TechnicalNotes,

    ServiceBandwidthDto? Bandwidth,
    IReadOnlyList<ServiceIdentifierDto> Identifiers,
    IReadOnlyList<ServiceIpAssignmentDto> IpAssignments,
    IReadOnlyList<ServicePhoneNumberDto> PhoneNumbers,
    IReadOnlyList<ServiceDependencyDto> Dependencies,

    // CanViewIpData reflects the ServiceIpData.Read permission. When false, IpAssignments
    // is empty because the handler never selected the values — they are not withheld at
    // render time, they were never fetched.
    bool CanViewIpData,

    // HasHiddenIpData lets the UI say "addressing is recorded but restricted" instead of
    // "no addressing recorded". Existence is disclosed deliberately: every user here is
    // an authenticated employee, and letting a help-desk agent believe a circuit has no
    // static IPs sends them down the wrong diagnostic path. The values stay hidden; only
    // the fact that some exist does not.
    bool HasHiddenIpData,

    MonitorState MonitorState,
    DateTime? MonitorStateChangedUtc);

public sealed record ServiceBandwidthDto(
    int DownloadKbps,
    int UploadKbps,
    int CommittedInformationRateKbps,
    int? DataCapGb,
    int? SlaLatencyMs,
    decimal? SlaPacketLossPercent,
    decimal? SlaAvailabilityPercent,
    int? AssignedBandwidthKbps);

public sealed record ServiceIdentifierDto(Guid Id, string IdentifierType, string Value, string? Notes);

/// <summary>
/// A decrypted IP assignment. Only ever constructed after the permission check has passed.
/// </summary>
public sealed record ServiceIpAssignmentDto(
    Guid Id,
    AddressFamily AddressFamily,
    string Cidr,
    string? Gateway,
    string? UsableFirst,
    string? UsableLast,
    string? DnsPrimary,
    string? DnsSecondary,
    bool IsRoutedBlock,
    string? Notes);

public sealed record ServicePhoneNumberDto(
    Guid Id, string Display, PhoneNumberKind Kind, string? Description);

public sealed record ServiceDependencyDto(
    Guid Id,
    Guid DependsOnServiceId,
    string DependsOnServiceLabel,
    DependencyType DependencyType,
    DependencyConfidence Confidence,
    string? Evidence,
    DateOnly? AssessedOn);

/// <summary>Filters for the services list.</summary>
public sealed record ServiceListFilter
{
    public Guid? LocationId { get; init; }
    public Guid? CarrierVendorId { get; init; }
    public ServiceType? ServiceType { get; init; }
    public ServiceStatus? Status { get; init; }
    public ServiceRole? ServiceRole { get; init; }
    public int? RegionId { get; init; }
    public string? SearchText { get; init; }
    public bool? MissingCircuitId { get; init; }
    public bool? MissingContract { get; init; }
    public bool IncludeArchived { get; init; }
    public string SortBy { get; init; } = "LocationCode";
    public bool SortDescending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Everything the outage view needs, in one query.
/// </summary>
/// <remarks>
/// Assembled server-side as a single object because the page it feeds is opened by
/// someone standing in a wiring closet on a phone with one bar. Every additional round
/// trip is a chance for it not to load.
/// </remarks>
public sealed record OutageContextDto(
    Guid ServiceId,
    string LocationCode,
    string LocationName,
    string LocationAddress,
    string? SiteContactName,
    string? SiteContactPhone,
    string CarrierName,
    string? CarrierSupportPhone,
    string? CarrierPortalUrl,
    SupportPriority SupportPriority,
    string? CircuitId,
    string? AccountNumber,
    string? DemarcLocation,
    string? CpeSummary,
    string? WanInterface,
    string ServiceSummary,
    MonitorState MonitorState,
    DateTime? DownSinceUtc,
    DateTime? LastGoodCheckUtc,
    int ConfirmingProbeCount,
    OutageClassification? Classification,
    string? ClassificationReason,
    IReadOnlyList<SiblingServiceDto> OtherServicesAtLocation,
    IReadOnlyList<string> DiversityWarnings,
    IReadOnlyList<RecentOutageDto> RecentOutages)
{
    /// <summary>
    /// The paste-ready block behind the "copy support summary" button.
    /// </summary>
    /// <remarks>
    /// One small feature with disproportionate daily value: it turns a fifteen-minute
    /// call into a five-minute one and removes the transcription errors that send a
    /// carrier looking at the wrong circuit.
    /// </remarks>
    public string BuildSupportSummary(TimeZoneInfo displayTimeZone)
    {
        ArgumentNullException.ThrowIfNull(displayTimeZone);

        var lines = new List<string>
        {
            $"Circuit ID: {CircuitId ?? "(not recorded)"}",
            $"Account: {AccountNumber ?? "(not recorded)"}",
            $"Location: {LocationName}, {LocationAddress}",
        };

        if (!string.IsNullOrWhiteSpace(SiteContactName))
        {
            lines.Add($"Site contact: {SiteContactName} {SiteContactPhone}".TrimEnd());
        }

        lines.Add($"Service: {ServiceSummary}");
        lines.Add($"Demarc: {DemarcLocation ?? "(not recorded)"}");

        if (!string.IsNullOrWhiteSpace(CpeSummary))
        {
            lines.Add($"CPE: {CpeSummary}");
        }

        if (DownSinceUtc is { } down)
        {
            lines.Add($"Down since: {Format(down, displayTimeZone)}");
            lines.Add($"Confirmed from: {ConfirmingProbeCount} probe(s)");
        }

        if (LastGoodCheckUtc is { } lastGood)
        {
            lines.Add($"Last good check: {Format(lastGood, displayTimeZone)}");
        }

        foreach (SiblingServiceDto sibling in OtherServicesAtLocation)
        {
            lines.Add($"Other service at site: {sibling.Summary} — {sibling.MonitorState}");
        }

        lines.AddRange(DiversityWarnings.Select(warning => $"WARNING: {warning}"));

        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        + $" {(tz.IsDaylightSavingTime(utc) ? tz.DaylightName : tz.StandardName)}";
}

public sealed record SiblingServiceDto(
    Guid ServiceId, string Summary, ServiceRole Role, MonitorState MonitorState, string? CircuitId);

public sealed record RecentOutageDto(
    Guid Id, DateTime StartUtc, DateTime? EndUtc, string? Cause, OutageClassification Classification)
{
    public TimeSpan? Duration => EndUtc is { } end ? end - StartUtc : null;
}
