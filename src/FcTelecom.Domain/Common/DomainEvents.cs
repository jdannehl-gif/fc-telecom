using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Notifications;

namespace FcTelecom.Domain.Common;

/// <summary>
/// Raised when the correlation engine confirms an outage.
/// </summary>
/// <remarks>
/// The dedupe key is the monitor plus the outage start instant. A redeploy mid-drain, a
/// retry storm, or a duplicated timer fire therefore cannot produce two "site is down"
/// messages for the same event — which matters, because the second one arrives while
/// somebody is already on the phone to the carrier.
/// </remarks>
public sealed record OutageConfirmedEvent(
    Guid OutageId,
    Guid LocationId,
    Guid? ServiceId,
    Guid? MonitorId,
    DateTime StartUtc,
    OutageClassification Classification,
    string ClassificationReason) : DomainEvent
{
    public override string DedupeKey =>
        $"{NotificationEventTypes.OutageConfirmed}:{MonitorId}:{StartUtc:O}";
}

public sealed record OutageResolvedEvent(
    Guid OutageId,
    Guid LocationId,
    Guid? ServiceId,
    DateTime StartUtc,
    DateTime EndUtc) : DomainEvent
{
    public override string DedupeKey =>
        $"{NotificationEventTypes.OutageResolved}:{OutageId}";
}

/// <summary>
/// Raised when a contract crosses an alert threshold for the first time.
/// </summary>
/// <remarks>
/// Keyed on contract plus threshold, which is what stops the nightly job re-sending the
/// same 90-day warning every night for thirty nights.
/// </remarks>
public sealed record ContractNoticeDeadlineApproachingEvent(
    Guid ContractId,
    string ContractNumber,
    int ThresholdDays,
    DateOnly NoticeDeadline,
    bool DeadlineConfirmed,
    Guid? ContractOwnerUserId) : DomainEvent
{
    public override string DedupeKey =>
        $"{NotificationEventTypes.ContractNoticeDeadline}:{ContractId}:{ThresholdDays}";
}

public sealed record InvoiceVarianceDetectedEvent(
    Guid InvoiceId,
    string InvoiceNumber,
    Guid VendorId,
    decimal VarianceAmount,
    decimal VariancePercent,
    int AffectedLineCount) : DomainEvent
{
    public override string DedupeKey =>
        $"{NotificationEventTypes.InvoiceVarianceDetected}:{InvoiceId}";
}

public sealed record IntegrationSyncFailedEvent(
    Guid ConnectionId,
    string SystemKey,
    int ConsecutiveFailures,
    string Error) : DomainEvent
{
    // Keyed on the failure count so the third, sixth, and ninth failures each notify
    // once, rather than every run producing a fresh alert about the same broken token.
    public override string DedupeKey =>
        $"{NotificationEventTypes.IntegrationSyncFailed}:{ConnectionId}:{ConsecutiveFailures}";
}

public sealed record ProbeWentOfflineEvent(
    Guid ProbeId,
    string ProbeName,
    DateTime? LastHeartbeatUtc,
    int AffectedMonitorCount) : DomainEvent
{
    public override string DedupeKey =>
        $"{NotificationEventTypes.ProbeOffline}:{ProbeId}:{LastHeartbeatUtc:O}";
}
