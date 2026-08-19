using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Integrations;

public enum SyncDirection
{
    /// <summary>The only supported value in Phase 4. This application is the system of record.</summary>
    OutboundOnly = 1,

    InboundOnly = 2,

    /// <summary>Not enabled until ownership and conflict rules are written down and approved.</summary>
    Bidirectional = 3,
}

/// <summary>
/// A configured connection to an external system.
/// </summary>
public class IntegrationConnection : AuditableEntity
{
    /// <summary>Stable key: <c>ITGlue</c>, <c>TheDudeSyslog</c>. Not the display name.</summary>
    public required string SystemKey { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>
    /// Configurable because IT Glue has regional endpoints — <c>api.itglue.com</c>,
    /// <c>api.eu.itglue.com</c>, <c>api.au.itglue.com</c> — and hardcoding one is a bug
    /// waiting for an EU subsidiary.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The <b>name</b> of the Key Vault secret holding the API token. Never the token.
    /// This database is backed up, replicated, and read by a reporting principal; a token
    /// stored here would be in all three places.
    /// </summary>
    public string? ApiKeySecretName { get; set; }

    public bool Enabled { get; set; }
    public SyncDirection SyncDirection { get; set; } = SyncDirection.OutboundOnly;
    public string? ScheduleCron { get; set; }

    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public string? ErrorState { get; set; }
    public int ConsecutiveFailures { get; set; }

    public ICollection<FieldMapping> FieldMappings { get; set; } = [];
    public ICollection<ExternalRecordLink> RecordLinks { get; set; } = [];

    public bool IsUnhealthy => ConsecutiveFailures >= 3;
}

public enum SyncState { Pending = 1, Synced = 2, Conflict = 3, Failed = 4, Orphaned = 5 }

/// <summary>
/// Ties a local record to its counterpart in an external system.
/// </summary>
/// <remarks>
/// Unique on (ConnectionId, LocalEntityType, LocalEntityId) and on
/// (ConnectionId, ExternalType, ExternalId). Those two indexes are what make sync
/// idempotent — re-running it updates rather than duplicating.
/// <para>
/// Note that the key is an ID on both sides. Names are never used as integration keys:
/// a location renamed from "Northgate Clinic" to "Northgate Medical" would otherwise
/// create a second IT Glue record and orphan the first.
/// </para>
/// </remarks>
public class ExternalRecordLink : BaseEntity
{
    public Guid ConnectionId { get; set; }
    public IntegrationConnection Connection { get; set; } = null!;

    public required string LocalEntityType { get; set; }
    public required string LocalEntityId { get; set; }

    public string? ExternalId { get; set; }

    /// <summary>IT Glue resource type: <c>flexible_assets</c>, <c>configurations</c>, …</summary>
    public string? ExternalType { get; set; }

    public DateTime? LastSyncedUtc { get; set; }

    /// <summary>
    /// Hash over the <b>mapped fields only</b>. A change to a field that is not mapped
    /// does not trigger a write, which is what keeps request volume comfortably under
    /// IT Glue's 3000-per-5-minute ceiling.
    /// </summary>
    public string? LocalVersionHash { get; set; }

    public string? ExternalVersionHash { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Maps a local field to an external one.</summary>
public class FieldMapping : AuditableEntity
{
    public Guid ConnectionId { get; set; }
    public IntegrationConnection Connection { get; set; } = null!;

    public required string LocalEntityType { get; set; }
    public required string LocalField { get; set; }
    public required string ExternalField { get; set; }

    /// <summary>Optional transform. Kept simple deliberately — this is not a scripting engine.</summary>
    public string? TransformExpression { get; set; }

    /// <summary>
    /// Sensitive fields are excluded from sync by default. Enabling one requires
    /// <c>Integrations.Manage</c> and writes a <c>SecurityEvent</c>, because the effect is
    /// to copy restricted data into a system with a different access model.
    /// </summary>
    public bool IsSensitive { get; set; }

    public bool IncludeInSync { get; set; } = true;

    /// <summary>The gate that keeps IP data out of IT Glue unless somebody deliberately opens it.</summary>
    public bool EffectivelyIncluded => IncludeInSync && !IsSensitive;
}

public enum SyncMode { DryRun = 1, Manual = 2, Scheduled = 3 }

public class SyncRun : BaseEntity
{
    public Guid ConnectionId { get; set; }
    public IntegrationConnection Connection { get; set; } = null!;

    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public SyncMode Mode { get; set; }

    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }

    public Guid? TriggeredByUserId { get; set; }
    public string? Summary { get; set; }

    public ICollection<SyncLogEntry> LogEntries { get; set; } = [];

    public bool Succeeded => CompletedUtc is not null && FailedCount == 0;
}

public enum SyncAction { Create = 1, Update = 2, Skip = 3, Fail = 4 }

public class SyncLogEntry
{
    public long Id { get; set; }

    public Guid SyncRunId { get; set; }
    public SyncRun SyncRun { get; set; } = null!;

    public required string EntityType { get; set; }
    public string? LocalId { get; set; }
    public string? ExternalId { get; set; }
    public SyncAction Action { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }

    /// <summary>Field-level diff for the dry-run preview.</summary>
    public string? ChangesJson { get; set; }
}
