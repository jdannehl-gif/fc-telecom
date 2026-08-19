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

/// <summary>
/// Delivery channels. A flags enum because one event legitimately goes to several places
/// at once — a renewal deadline is emailed to the contract owner <i>and</i> posted to a
/// Teams channel, and modelling that as two rules means two things to keep in step.
/// </summary>
[Flags]
public enum NotificationChannel
{
    None = 0,
    Email = 1,
    Teams = 2,
    Webhook = 4,
}

/// <summary>
/// When an escalation step fires, beyond simply reaching its threshold.
/// </summary>
public enum EscalationCondition
{
    /// <summary>Fires whenever the threshold is reached.</summary>
    Always = 1,

    /// <summary>Fires only if the notice deadline has still not been confirmed by a person.</summary>
    IfDeadlineUnconfirmed = 2,

    /// <summary>Fires only if nobody has recorded a decision or action against the item.</summary>
    IfNoActionRecorded = 3,

    /// <summary>Fires if either of the above is true.</summary>
    IfUnconfirmedOrNoAction = 4,
}

/// <summary>
/// Who gets told about what, through which channels.
/// </summary>
/// <remarks>
/// Every field here is editable in the application. That is a requirement rather than a
/// nicety: a notification configuration that needs a deployment to change is one that
/// stops matching reality within a quarter and then gets ignored.
/// </remarks>
public class NotificationRule : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>A value from <see cref="NotificationEventTypes"/>.</summary>
    public required string EventType { get; set; }

    public NotificationChannel Channels { get; set; } = NotificationChannel.Email;

    /// <summary>Semicolon-separated addresses. Explicit named recipients.</summary>
    public string? Recipients { get; set; }

    /// <summary>
    /// A shared team mailbox — telecom, procurement, help desk. Kept separate from
    /// <see cref="Recipients"/> so it survives individual people joining and leaving.
    /// </summary>
    public string? SharedMailbox { get; set; }

    /// <summary>Teams channel reference. Configurable per rule.</summary>
    public string? TeamsChannelReference { get; set; }

    /// <summary>Power Automate–compatible webhook, used when Graph channel posting is unavailable.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Also notify whoever owns the record that raised the event — the contract owner, the
    /// location's IT owner. Resolved at send time rather than copied into the rule.
    /// </summary>
    public bool NotifyRecordOwner { get; set; }

    /// <summary>Also notify everyone holding this role. Null for named recipients only.</summary>
    public string? RoleScope { get; set; }

    /// <summary>
    /// Ships <c>false</c>, and stays false until the initial data import has been reviewed
    /// and a test notification has been sent. A demo or first import that fires four hundred
    /// emails is how a rollout becomes an incident.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Day thresholds this rule fires at, e.g. <c>180,120,90,60,30</c>.</summary>
    public string? ThresholdDaysCsv { get; set; }

    /// <summary>Any remaining event-specific configuration, such as a variance percentage.</summary>
    public string? ThresholdConfigJson { get; set; }

    public DateTime? LastTestedUtc { get; set; }
    public Guid? LastTestedByUserId { get; set; }

    public ICollection<NotificationEscalationStep> EscalationSteps { get; set; } = [];

    /// <summary>
    /// Parses <see cref="ThresholdDaysCsv"/>, descending. Returns empty rather than throwing
    /// on malformed input — a typo in a threshold list must not take out the nightly job for
    /// every other rule.
    /// </summary>
    public IReadOnlyList<int> ThresholdDays()
    {
        if (string.IsNullOrWhiteSpace(ThresholdDaysCsv))
        {
            return [];
        }

        var days = new List<int>();
        foreach (string part in ThresholdDaysCsv.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                            StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= 0)
            {
                days.Add(value);
            }
        }

        return [.. days.Distinct().OrderByDescending(day => day)];
    }

    /// <summary>
    /// A rule that would reach nobody. Surfaced in the admin UI, because an enabled rule
    /// with no recipients looks like it is working and is not.
    /// </summary>
    public bool HasNoPossibleRecipient =>
        !NotifyRecordOwner &&
        string.IsNullOrWhiteSpace(Recipients) &&
        string.IsNullOrWhiteSpace(SharedMailbox) &&
        string.IsNullOrWhiteSpace(RoleScope) &&
        string.IsNullOrWhiteSpace(TeamsChannelReference) &&
        string.IsNullOrWhiteSpace(WebhookUrl);
}

/// <summary>
/// One escalation step: at this threshold, under this condition, also tell these people.
/// </summary>
/// <remarks>
/// A child collection rather than a pair of fields on the rule, because real escalation
/// policies differ per threshold — "at 60 days, chase if still unconfirmed; at 30 days,
/// tell the owner, procurement, and IT leadership" is two different audiences under two
/// different conditions, and flattening it loses one of them.
/// </remarks>
public class NotificationEscalationStep : AuditableEntity
{
    public Guid RuleId { get; set; }
    public NotificationRule Rule { get; set; } = null!;

    /// <summary>Days remaining at which this step fires.</summary>
    public int ThresholdDays { get; set; }

    public EscalationCondition Condition { get; set; } = EscalationCondition.Always;

    /// <summary>Semicolon-separated addresses, in addition to the rule's own recipients.</summary>
    public string? Recipients { get; set; }

    /// <summary>Also escalate to everyone holding this role.</summary>
    public string? RoleScope { get; set; }

    /// <summary>Channels for this step. Falls back to the rule's channels when None.</summary>
    public NotificationChannel Channels { get; set; } = NotificationChannel.None;

    /// <summary>Shown in the preview so a reviewer can see why a step exists.</summary>
    public string? Description { get; set; }
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

    /// <summary>
    /// True when this message was produced by the "send a test notification" action rather
    /// than by a real event. Test sends are recorded so that a rule cannot be enabled on the
    /// strength of a test nobody can find afterwards.
    /// </summary>
    public bool IsTest { get; set; }

    /// <summary>Ties the message back to the request that produced it, across process boundaries.</summary>
    public Guid? CorrelationId { get; set; }

    public bool IsExhausted(int maxAttempts) => Attempts >= maxAttempts;

    /// <summary>Exponential backoff with a one-hour ceiling: 1, 2, 4, 8, 16, 32, 60, 60 minutes.</summary>
    public DateTime NextAttemptUtc(DateTime utcNow) =>
        utcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(Attempts, 6))));
}
