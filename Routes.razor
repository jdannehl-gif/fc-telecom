using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Services;

/// <summary>
/// An additional carrier-specific identifier for a service.
/// </summary>
/// <remarks>
/// This table exists because carriers do not agree on what to call anything. Lumen says
/// ECCKT, Spectrum says Circuit ID, AT&amp;T wants a BAN plus a separate service ID, and
/// the reseller invents a third label for the same physical circuit.
/// <para>
/// The alternative — a column per carrier — grows forever and is empty for most rows.
/// Global search covers <see cref="TelecomService.CircuitId"/> and every
/// <see cref="Value"/> here, so an engineer can paste whatever string the carrier gave
/// them and find the circuit.
/// </para>
/// </remarks>
public class ServiceIdentifier : AuditableEntity
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    /// <summary>Free text — "ECCKT", "BAN", "PON", "WTN", "Order #". Not an enum, deliberately.</summary>
    public required string IdentifierType { get; set; }

    public required string Value { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// Speeds and SLA terms. One-to-one with the service; separate table to keep the main
/// row narrow and because it is meaningless for POTS and alarm lines.
/// </summary>
public class ServiceBandwidth
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public int DownloadKbps { get; set; }
    public int UploadKbps { get; set; }

    /// <summary>
    /// Committed information rate — what the carrier guarantees rather than advertises.
    /// Zero means "best effort", which is the honest description of most coax and
    /// cellular services regardless of what the sales sheet said.
    /// </summary>
    public int CommittedInformationRateKbps { get; set; }

    /// <summary>Null means uncapped.</summary>
    public int? DataCapGb { get; set; }

    public int? SlaLatencyMs { get; set; }
    public decimal? SlaPacketLossPercent { get; set; }
    public decimal? SlaJitterMs { get; set; }

    /// <summary>The contractual availability commitment, e.g. 99.99. Drives credit detection.</summary>
    public decimal? SlaAvailabilityPercent { get; set; }

    /// <summary>What you actually provision to it, which is not always what you bought.</summary>
    public int? AssignedBandwidthKbps { get; set; }

    public Bandwidth Download => Bandwidth.FromKbps(DownloadKbps);
    public Bandwidth Upload => Bandwidth.FromKbps(UploadKbps);
    public Bandwidth Committed => Bandwidth.FromKbps(CommittedInformationRateKbps);

    public bool IsSymmetric => DownloadKbps == UploadKbps && DownloadKbps > 0;

    /// <summary>
    /// The rate to use for cost-per-Mbps. Prefers the committed rate, because paying
    /// for a "1 Gbps" best-effort coax service and a 1 Gbps CIR fibre service are not
    /// the same purchase and comparing them at the advertised rate flatters the coax.
    /// </summary>
    public int BillableKbps => CommittedInformationRateKbps > 0 ? CommittedInformationRateKbps : DownloadKbps;
}

/// <summary>Voice-specific detail, for SIP, PRI, POTS, and hosted voice services.</summary>
public class VoiceServiceDetail
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    /// <summary>Concurrent call paths. For POTS this is 1.</summary>
    public int? ChannelCount { get; set; }

    public int? DirectInwardDialNumberCount { get; set; }

    /// <summary>Billing telephone number — the one the carrier indexes the account by.</summary>
    public string? BillingTelephoneNumber { get; set; }

    /// <summary>
    /// Where emergency services will be sent. Worth its own field because it is a
    /// compliance obligation and because it is wrong surprisingly often after an office move.
    /// </summary>
    public string? E911RegisteredAddress { get; set; }

    public DateOnly? E911LastVerifiedDate { get; set; }
    public bool SupportsFax { get; set; }
    public string? Notes { get; set; }
}

public enum PhoneNumberKind { Main = 1, Did = 2, Fax = 3, Alarm = 4, Elevator = 5, Modem = 6, Other = 99 }

/// <summary>
/// A number or a contiguous block of numbers on a service. Ranges are stored as a range
/// rather than as a row per number, because a 100-DID block should not be 100 rows.
/// </summary>
public class ServicePhoneNumber : AuditableEntity
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public required string NumberOrRangeStart { get; set; }

    /// <summary>Null for a single number.</summary>
    public string? RangeEnd { get; set; }

    public PhoneNumberKind Kind { get; set; } = PhoneNumberKind.Did;
    public string? Description { get; set; }
    public string? E911Address { get; set; }

    public bool IsRange => !string.IsNullOrWhiteSpace(RangeEnd);

    public string Display => IsRange ? $"{NumberOrRangeStart} – {RangeEnd}" : NumberOrRangeStart;
}
