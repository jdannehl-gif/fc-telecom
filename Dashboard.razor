using FcTelecom.Domain.Common;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;

namespace FcTelecom.Domain.Monitoring;

/// <summary>
/// What the correlation engine concluded was responsible, and why.
/// </summary>
/// <remarks>
/// Every classification is shown in the UI together with the reasoning that produced it.
/// A classification an engineer cannot argue with is a classification they will ignore.
/// </remarks>
public enum OutageClassification
{
    /// <summary>This carrier is down while the site demonstrably is not.</summary>
    CarrierFailure = 1,

    /// <summary>Everything at the location is failing — power, or a site-wide event.</summary>
    SiteFailure = 2,

    /// <summary>
    /// The probe is down, not the circuits. No outage is opened in this case; a coverage
    /// gap is recorded instead. The enum value exists so an operator can classify a
    /// manually-created event this way.
    /// </summary>
    MonitoringFailure = 3,

    /// <summary>The carrier's edge answers but nothing behind it does.</summary>
    CpeFailure = 4,

    /// <summary>Not enough evidence. We say so rather than guessing.</summary>
    Unknown = 99,
}

public enum BusinessImpact { None = 0, Low = 1, Moderate = 2, High = 3, Critical = 4 }

public enum SlaCreditStatus { NotEligible = 1, Eligible = 2, Claimed = 3, Received = 4, Denied = 5 }

/// <summary>
/// A correlated period of unavailability. Never deleted — this is the incident and SLA record.
/// </summary>
/// <remarks>
/// Outages are produced by the correlation state machine, not by individual failed checks.
/// A single dropped packet is not an incident, and treating it as one is how a monitoring
/// system loses its audience permanently.
/// </remarks>
public class OutageEvent : BaseEntity, IAuditable
{
    public Guid? MonitorId { get; set; }
    public ServiceMonitor? Monitor { get; set; }

    public Guid? ServiceId { get; set; }
    public TelecomService? Service { get; set; }

    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public DateTime StartUtc { get; set; }

    /// <summary>Null while the outage is ongoing. Indexed (filtered) for the dashboard tile.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>How many quorum-eligible probes agreed. Recorded so confidence is auditable.</summary>
    public int ConfirmingProbeCount { get; set; }

    public OutageClassification Classification { get; set; } = OutageClassification.Unknown;

    /// <summary>Why the engine classified it this way. Shown verbatim in the UI.</summary>
    public string? ClassificationReason { get; set; }

    public string? Cause { get; set; }
    public string? CarrierTicketNumber { get; set; }
    public string? InternalTicketNumber { get; set; }

    public bool IsPlanned { get; set; }

    public Guid? MaintenanceWindowId { get; set; }
    public MaintenanceWindow? MaintenanceWindow { get; set; }

    public BusinessImpact BusinessImpact { get; set; } = BusinessImpact.Moderate;

    public SlaCreditStatus SlaCreditStatus { get; set; } = SlaCreditStatus.NotEligible;
    public decimal? SlaCreditAmount { get; set; }

    /// <summary>When the carrier was first told. The clock MTTR is measured against.</summary>
    public DateTime? CarrierNotifiedUtc { get; set; }

    public DateTime? CarrierFirstResponseUtc { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsOngoing => EndUtc is null;

    public TimeSpan Duration(DateTime utcNow) => (EndUtc ?? utcNow) - StartUtc;

    /// <summary>How long the carrier took to acknowledge. Feeds the carrier scorecard.</summary>
    public TimeSpan? CarrierResponseTime =>
        CarrierNotifiedUtc is { } notified && CarrierFirstResponseUtc is { } responded
            ? responded - notified
            : null;

    /// <summary>Time to restore, measured from when the carrier was told.</summary>
    public TimeSpan? MeanTimeToRestore =>
        CarrierNotifiedUtc is { } notified && EndUtc is { } ended ? ended - notified : null;
}

public enum MaintenanceSource { Manual = 1, CarrierNotice = 2, Recurring = 3 }

/// <summary>
/// A planned window during which downtime does not count against availability.
/// </summary>
/// <remarks>
/// A window removes time from the eligible denominator, but the underlying
/// <see cref="OutageEvent"/> is still recorded and still linked here. Nothing is silently
/// deleted, so "how much total downtime, including planned?" remains answerable.
/// </remarks>
public class MaintenanceWindow : AuditableEntity
{
    /// <summary>Scope: set exactly one of service, location, or vendor.</summary>
    public Guid? ServiceId { get; set; }
    public TelecomService? Service { get; set; }

    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Carrier-wide maintenance affecting every service from that vendor.</summary>
    public Guid? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public MaintenanceSource Source { get; set; } = MaintenanceSource.Manual;

    /// <summary>RFC 5545 RRULE for recurring windows. Null for a one-off.</summary>
    public string? RecurrenceRule { get; set; }

    public string? Description { get; set; }

    public bool Covers(DateTime instantUtc) => instantUtc >= StartUtc && instantUtc <= EndUtc;

    public bool Overlaps(DateTime fromUtc, DateTime toUtc) => StartUtc < toUtc && EndUtc > fromUtc;
}

public enum CoverageGapReason
{
    AgentOffline = 1,
    NoProbesAssigned = 2,
    MonitorPaused = 3,
    SystemOutage = 4,
    Deploying = 5,
    NeverConfigured = 6,
}

/// <summary>
/// A period during which we could not observe a monitor. Time recorded here becomes
/// <c>UnknownSeconds</c> and is removed from the availability denominator.
/// </summary>
/// <remarks>
/// This table is the honesty mechanism of the whole monitoring module. Without it, an
/// offline probe produces either a fabricated outage or a silently inflated uptime figure,
/// and both destroy trust in the numbers.
/// </remarks>
public class CoverageGap : BaseEntity, IImmutableRecord
{
    public Guid MonitorId { get; set; }
    public ServiceMonitor Monitor { get; set; } = null!;

    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public CoverageGapReason Reason { get; set; }
    public string? Detail { get; set; }

    public bool IsOpen => EndUtc is null;

    public TimeSpan Duration(DateTime utcNow) => (EndUtc ?? utcNow) - StartUtc;
}

public enum RollupGrain { Hourly = 1, Daily = 2, Monthly = 3 }

/// <summary>
/// Pre-aggregated availability for a period. Everything that reports on uptime reads
/// these, never raw <see cref="CheckResult"/> rows.
/// </summary>
/// <remarks>
/// Rollup jobs are idempotent and re-runnable for any period, which matters more than it
/// sounds: the alternative is an availability history you can never correct after finding
/// a bug in the maths.
/// </remarks>
public class AvailabilityRollup
{
    // Deliberately NOT IImmutableRecord. Rollups are upserted: re-running a period
    // overwrites the row. That is the property that lets a bug in the availability maths
    // be fixed and the affected history recomputed, rather than leaving a permanent wrong
    // number in the record.

    public long Id { get; set; }

    public Guid MonitorId { get; set; }
    public ServiceMonitor Monitor { get; set; } = null!;

    public Guid? ServiceId { get; set; }

    public RollupGrain Grain { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>Period length minus planned downtime minus unknown time.</summary>
    public int EligibleSeconds { get; set; }

    public int UnplannedDownSeconds { get; set; }
    public int PlannedDownSeconds { get; set; }

    /// <summary>Time with no usable coverage. Excluded from the denominator, never counted as up.</summary>
    public int UnknownSeconds { get; set; }

    public decimal AvailabilityPercent { get; set; }

    /// <summary>
    /// True when coverage fell below the configured confidence floor. The UI shows the
    /// coverage figure next to the availability figure wherever this is set — because
    /// 99.94% over 96% coverage and 99.94% over 40% coverage are completely different
    /// statements and presenting them identically is a lie of omission.
    /// </summary>
    public bool LowConfidence { get; set; }

    public int? AvgLatencyMs { get; set; }
    public int? MaxLatencyMs { get; set; }
    public decimal? AvgPacketLossPercent { get; set; }

    public int TotalPeriodSeconds => (int)(PeriodEndUtc - PeriodStartUtc).TotalSeconds;

    /// <summary>What fraction of the period we could actually see. 0–100.</summary>
    public decimal CoveragePercent => TotalPeriodSeconds == 0
        ? 0m
        : Math.Round((decimal)(TotalPeriodSeconds - UnknownSeconds) / TotalPeriodSeconds * 100m, 2);
}
