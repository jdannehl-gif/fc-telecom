using FcTelecom.Domain.Common;
using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Vendors;

namespace FcTelecom.Domain.Services;

// Naming note: the design document calls this entity "Service". In code it is
// "TelecomService", because a type named Service in a codebase full of
// IServiceProvider / IServiceCollection / application services reads ambiguously in
// every single file that touches it.

public enum ServiceType
{
    Internet = 1,
    MplsVpn = 2,
    PointToPoint = 3,
    SdWanUnderlay = 4,
    CellularBackup = 5,
    FixedWireless = 6,
    SipTrunk = 10,
    Pri = 11,
    Pots = 12,
    HostedVoice = 13,
    AlarmLine = 20,
    ElevatorLine = 21,
    FaxLine = 22,
    EmergencyLine = 23,
    Other = 99,
}

public enum ServiceStatus
{
    Ordered = 1,
    Installing = 2,
    Active = 3,
    Suspended = 4,
    PendingDisconnect = 5,
    Disconnected = 6,
}

public enum ServiceRole { Primary = 1, Secondary = 2, Tertiary = 3, Standalone = 4 }

public enum HandoffType
{
    Rj45 = 1, SingleModeFiberLc = 2, MultiModeFiberLc = 3, Coax = 4,
    T1Rj48 = 5, Sfp = 6, Wireless = 7, Unknown = 98, Other = 99,
}

public enum TransportMedia
{
    Fiber = 1, Coax = 2, Copper = 3, FixedWireless = 4,
    Cellular = 5, Satellite = 6, Unknown = 98, Other = 99,
}

public enum SupportPriority { P1 = 1, P2 = 2, P3 = 3 }

/// <summary>
/// A connectivity or telecom service at a location: a circuit, a SIP trunk, a POTS
/// line for an elevator. One generic entity with type-specific detail hanging off it,
/// so the application supports more than internet circuits without a table per type.
/// </summary>
public class TelecomService : AuditableEntity
{
    public ServiceType ServiceType { get; set; }

    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    // ── The four vendor roles ────────────────────────────────────────────────────────
    //
    // These are separate on purpose, and it is the single most important modelling
    // decision in this schema.
    //
    // You buy from a reseller, who resells a carrier, who leases last-mile fibre from
    // the incumbent, whose backbone belongs to someone else again. Two circuits sold by
    // two different carriers routinely share the same last-mile provider and the same
    // building entrance. Collapse these into one VendorId and the question "is our
    // backup real?" becomes unanswerable — which is one of the questions this product
    // exists to answer.

    /// <summary>Who you buy the service from and whose name is on the bill.</summary>
    public Guid CarrierVendorId { get; set; }
    public Vendor CarrierVendor { get; set; } = null!;

    /// <summary>The agent or VAR in the middle, if there is one.</summary>
    public Guid? ResellerVendorId { get; set; }
    public Vendor? ResellerVendor { get; set; }

    /// <summary>Who owns the physical path into the building. The diversity question.</summary>
    public Guid? LastMileVendorId { get; set; }
    public Vendor? LastMileVendor { get; set; }

    /// <summary>Whose backbone the traffic actually rides.</summary>
    public Guid? UnderlyingNetworkOwnerVendorId { get; set; }
    public Vendor? UnderlyingNetworkOwnerVendor { get; set; }

    public Guid? VendorAccountId { get; set; }
    public VendorAccount? VendorAccount { get; set; }

    // ── Identity ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The identifier you search on and read to the carrier. Whatever they call it —
    /// ECCKT, Circuit ID, Service ID — this is the one that matters. Additional
    /// identifiers go in <see cref="Identifiers"/>.
    /// </summary>
    public string? CircuitId { get; set; }

    public string? CarrierServiceId { get; set; }

    public ServiceStatus Status { get; set; } = ServiceStatus.Active;
    public ServiceRole ServiceRole { get; set; } = ServiceRole.Standalone;

    // ── Lifecycle ────────────────────────────────────────────────────────────────────

    public DateOnly? OrderDate { get; set; }
    public DateOnly? InstallDate { get; set; }
    public DateOnly? ActivationDate { get; set; }
    public DateOnly? DisconnectRequestedDate { get; set; }
    public DateOnly? DisconnectEffectiveDate { get; set; }

    // ── Physical ─────────────────────────────────────────────────────────────────────

    /// <summary>Building, room, rack, panel, port. The detail that saves an hour on site.</summary>
    public string? DemarcLocation { get; set; }

    public HandoffType HandoffType { get; set; } = HandoffType.Unknown;
    public TransportMedia Media { get; set; } = TransportMedia.Unknown;

    public string? CpeMake { get; set; }
    public string? CpeModel { get; set; }
    public string? CpeSerial { get; set; }
    public bool CpeManagedByCarrier { get; set; }

    /// <summary>Which interface on your equipment this lands on: <c>ether1</c>, <c>Gi0/0/1</c>.</summary>
    public string? WanInterface { get; set; }

    public SupportPriority SupportPriority { get; set; } = SupportPriority.P2;

    public string? TechnicalNotes { get; set; }

    // ── Children ─────────────────────────────────────────────────────────────────────

    public ServiceBandwidth? Bandwidth { get; set; }
    public VoiceServiceDetail? VoiceDetail { get; set; }

    public ICollection<ServiceIdentifier> Identifiers { get; set; } = [];
    public ICollection<ServiceIpAssignment> IpAssignments { get; set; } = [];
    public ICollection<ServicePhoneNumber> PhoneNumbers { get; set; } = [];
    public ICollection<ServiceDependency> Dependencies { get; set; } = [];
    public ICollection<ServiceCost> CostHistory { get; set; } = [];
    public ICollection<ContractService> ContractLinks { get; set; } = [];
    public ICollection<ServiceMonitor> Monitors { get; set; } = [];

    // ── Derived ──────────────────────────────────────────────────────────────────────

    public bool IsLive => Status is ServiceStatus.Active or ServiceStatus.Installing;

    public bool IsBillable => Status is not ServiceStatus.Disconnected;

    /// <summary>True for the WAN-ish types where bandwidth and cost-per-Mbps are meaningful.</summary>
    public bool IsDataService => ServiceType is
        ServiceType.Internet or ServiceType.MplsVpn or ServiceType.PointToPoint or
        ServiceType.SdWanUnderlay or ServiceType.CellularBackup or ServiceType.FixedWireless;

    /// <summary>True for voice types, where channel counts and phone numbers matter instead.</summary>
    public bool IsVoiceService => ServiceType is
        ServiceType.SipTrunk or ServiceType.Pri or ServiceType.Pots or
        ServiceType.HostedVoice or ServiceType.FaxLine;

    /// <summary>
    /// The cost record in force on a given date. Null if the service was not priced then,
    /// which is a real state — an ordered-but-not-activated circuit has no cost yet.
    /// </summary>
    public ServiceCost? CostOn(DateOnly date) =>
        CostHistory.SingleOrDefault(cost =>
            cost.EffectiveFrom <= date && (cost.EffectiveTo is null || cost.EffectiveTo >= date));

    /// <summary>
    /// True when this service is billed but no longer in service — the "disconnected but
    /// still paying" case that quietly costs organisations real money for years.
    /// </summary>
    public bool IsDisconnectedButStillPriced(DateOnly asOf) =>
        Status == ServiceStatus.Disconnected &&
        DisconnectEffectiveDate is not null &&
        DisconnectEffectiveDate < asOf &&
        CostOn(asOf) is not null;
}
