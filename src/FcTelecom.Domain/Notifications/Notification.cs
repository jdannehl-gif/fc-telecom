using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Notifications;

/// <summary>Event types a notification rule can subscribe to.</summary>
public static class NotificationEventTypes
{
    public const string OutageConfirmed = "outage.confirmed";
    public const string OutageResolved = "outage.resolved";
    public const string ContractNoticeDeadline = "contract.notice-deadline";
    public const string ContractExpiring = "contract.expiring";
    public const string InvoiceVarianceDetected = "invoice.variance";
    public const string IntegrationSyncFailed = "integration.sync-failed";
    public const string ProbeOffline = "probe.offline";

    public static readonly IReadOnlyList<string> All =
    [
        OutageConfirmed, OutageResolved, ContractNoticeDeadline, ContractExpiring,
        InvoiceVarianceDetected, IntegrationSyncFailed, ProbeOffline,
    ];
}

public enum NotificationChannel { Email = 1, Teams = 2, Webhook = 3 }

/// <summary>Who gets told about what, and how.</summary>
public class NotificationRule : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>A value from <see cref="NotificationEventTypes"/>.</summary>
    public required string EventType { get; set; }

    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;

    /// <summary>Semicolon-separated addresses, or a Teams channel reference.</summary>
    public string? Recipients { get; set; }

    /// <summary>Also notify everyone holding this role. Null to use <see cref="Recipients"/> only.</summary>
    public string? RoleScope { get; set; }

    /// <summary>
    /// Ships <c>false</c>. A demo import that fires four hundred emails on day one is
    /// how a rollout becomes an incident, so every seeded rule starts switched off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Event-specific configuration — alert thresholds, variance percentages.</summary>
    public string? ThresholdConfigJson { get; set; }
}

public enum OutboxStatus { Pending = 1, Sending = 2, Sent = 3, Failed = 4, Suppressed = 5 }

/// <summary>
/// A message waiting to go out. Written in the same transaction as the state change that
/// caused it, then drained by a Functions timer trigger.
/// </summary>
/// <remarks>
/// The transactional outbox is why an alert is never lost and never duplicated. If the
/// database commit succeeds, the message exists; if it fails, neither the change nor the
/// message happened. The unique <see cref="DedupeKey"/> then makes a redeploy mid-drain,
/// a retry storm, or a duplicated timer fire safe.
/// </remarks>
public class NotificationOutboxMessage : BaseEntity
{
    public Guid? RuleId { get; set; }
    public NotificationRule? Rule { get; set; }

    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }

    /// <summary>Unique. Two messages with the same key produce at most one send.</summary>
    public required string DedupeKey { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }

    /// <summary>When it becomes eligible to send. Moved forward on retry for backoff.</summary>
    public DateTime ScheduledUtc { get; set; }

    public DateTime? SentUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Ties the message back to the request that produced it, across process boundaries.</summary>
    public Guid? CorrelationId { get; set; }

    public bool IsExhausted(int maxAttempts) => Attempts >= maxAttempts;

    /// <summary>Exponential backoff with a one-hour ceiling: 1, 2, 4, 8, 16, 32, 60, 60 minutes.</summary>
    public DateTime NextAttemptUtc(DateTime utcNow) =>
        utcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(Attempts, 6))));
}
