using System.Text.Json;
using FcTelecom.Application.Abstractions;
using FcTelecom.Domain.Common;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Notifications;
using FcTelecom.Domain.Platform;
using FcTelecom.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FcTelecom.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps audit fields, writes the audit trail, applies soft delete, and drains domain
/// events to the outbox — all inside the caller's transaction.
/// </summary>
/// <remarks>
/// Doing this in an interceptor rather than in each handler means it cannot be forgotten.
/// A new feature that saves an entity gets audit and outbox behaviour for free, and a
/// developer who does not know the audit log exists cannot bypass it.
/// <para>
/// Because it runs in the same <c>SaveChanges</c>, the audit row and the change it
/// describes commit or roll back together. There is no window in which the data changed
/// and the audit trail says otherwise.
/// </para>
/// </remarks>
public sealed class AuditSaveChangesInterceptor(ICurrentUser currentUser, IClock clock) : SaveChangesInterceptor
{
    /// <summary>
    /// Properties whose values are never written to the audit log.
    /// </summary>
    /// <remarks>
    /// The audit trail must record that the static IP block changed without becoming a
    /// second, unencrypted copy of it. These properties log as <c>"[redacted]"</c> with a
    /// changed flag, which preserves the forensic value and discards the disclosure.
    /// </remarks>
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.Ordinal)
    {
        nameof(ServiceIpAssignment.CidrEncrypted),
        nameof(ServiceIpAssignment.GatewayEncrypted),
        nameof(ServiceIpAssignment.UsableFirstEncrypted),
        nameof(ServiceIpAssignment.UsableLastEncrypted),
        nameof(ServiceIpAssignment.DnsPrimaryEncrypted),
        nameof(ServiceIpAssignment.DnsSecondaryEncrypted),
        nameof(ServiceIpAssignment.CidrSearchHash),
        "ApiKeySecretName",
        "HmacKeyVaultSecretName",
        "CredentialReference",
        "PayloadJson",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            Apply(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            Apply(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContext context)
    {
        DateTime now = clock.UtcNow;
        Guid? actorId = currentUser.UserId;
        string? actorUpn = currentUser.UserPrincipalName;

        context.ChangeTracker.DetectChanges();

        var auditEntries = new List<AuditEntry>();
        var outboxMessages = new List<NotificationOutboxMessage>();

        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditEntry or SecurityEvent or NotificationOutboxMessage)
            {
                continue; // Never audit the audit trail. That way lies infinite regress.
            }

            if (entry.Entity is IImmutableRecord)
            {
                EnforceAppendOnly(entry);
            }

            if (entry.Entity is IAuditable auditable)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        auditable.CreatedUtc = now;
                        auditable.CreatedByUserId = actorId;
                        break;
                    case EntityState.Modified:
                        auditable.ModifiedUtc = now;
                        auditable.ModifiedByUserId = actorId;
                        break;
                    default:
                        break;
                }
            }

            // Convert a hard delete into an archive. Nothing is hard-deleted through the
            // application; a purge is a separate, explicitly-authorised operation.
            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable softDeletable })
            {
                entry.State = EntityState.Modified;
                softDeletable.IsArchived = true;
                softDeletable.ArchivedUtc = now;
                softDeletable.ArchivedByUserId = actorId;
            }

            AuditEntry? audit = BuildAuditEntry(entry, now, actorId, actorUpn);
            if (audit is not null)
            {
                auditEntries.Add(audit);
            }

            if (entry.Entity is BaseEntity baseEntity && baseEntity.DomainEvents.Count > 0)
            {
                outboxMessages.AddRange(baseEntity.DomainEvents.Select(domainEvent =>
                    new NotificationOutboxMessage
                    {
                        EventType = domainEvent.GetType().Name,
                        PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                        DedupeKey = domainEvent.DedupeKey,
                        ScheduledUtc = now,
                        CorrelationId = currentUser.CorrelationId,
                    }));

                baseEntity.ClearDomainEvents();
            }
        }

        if (auditEntries.Count > 0)
        {
            context.Set<AuditEntry>().AddRange(auditEntries);
        }

        if (outboxMessages.Count > 0)
        {
            context.Set<NotificationOutboxMessage>().AddRange(outboxMessages);
        }
    }

    /// <summary>
    /// Properties an append-only record is nonetheless allowed to have written once, to
    /// close it out.
    /// </summary>
    /// <remarks>
    /// "Append-only" in this domain does not mean "no column ever changes" — it means
    /// history is never rewritten. Closing a cost period by stamping
    /// <c>ServiceCost.EffectiveTo</c> does not alter what was charged; it records when the
    /// price stopped applying. Ending a coverage gap is the same shape of operation.
    /// <para>
    /// Everything not listed here is genuinely frozen. A type with no entry (audit
    /// entries, security events, raw check results) can only ever be inserted.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<Type, HashSet<string>> AppendOnlyClosableProperties = new()
    {
        [typeof(ServiceCost)] = new(StringComparer.Ordinal)
        {
            nameof(ServiceCost.EffectiveTo),
            nameof(IAuditable.ModifiedUtc),
            nameof(IAuditable.ModifiedByUserId),
        },
        [typeof(CoverageGap)] = new(StringComparer.Ordinal)
        {
            nameof(CoverageGap.EndUtc),
            nameof(CoverageGap.Detail),
        },
    };

    private static void EnforceAppendOnly(EntityEntry entry)
    {
        Type type = entry.Entity.GetType();

        if (entry.State == EntityState.Deleted)
        {
            throw new InvalidOperationException(
                $"{type.Name} is an append-only record and cannot be deleted. Retention is " +
                "handled by an explicit, dated purge job — not by the application deleting " +
                "history it finds inconvenient.");
        }

        if (entry.State != EntityState.Modified)
        {
            return;
        }

        AppendOnlyClosableProperties.TryGetValue(type, out HashSet<string>? closable);

        List<string> illegal =
        [
            .. entry.Properties
                .Where(property => property.IsModified)
                .Select(property => property.Metadata.Name)
                .Where(name => closable is null || !closable.Contains(name))
        ];

        if (illegal.Count > 0)
        {
            throw new InvalidOperationException(
                $"{type.Name} is an append-only record. Cannot modify: {string.Join(", ", illegal)}. " +
                "Insert a new row instead — this is what keeps cost, monitoring, and audit " +
                "history reproducible after the fact.");
        }
    }

    private static AuditEntry? BuildAuditEntry(
        EntityEntry entry, DateTime now, Guid? actorId, string? actorUpn)
    {
        AuditAction action;

        switch (entry.State)
        {
            case EntityState.Added:
                action = AuditAction.Create;
                break;
            case EntityState.Modified:
                action = entry.Entity is ISoftDeletable { IsArchived: true } &&
                         entry.Property(nameof(ISoftDeletable.IsArchived)).IsModified
                    ? AuditAction.Archive
                    : AuditAction.Update;
                break;
            case EntityState.Deleted:
                action = AuditAction.Archive;
                break;
            default:
                return null;
        }

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (PropertyEntry property in entry.Properties)
        {
            string name = property.Metadata.Name;

            if (name is nameof(BaseEntity.RowVersion) or nameof(IAuditable.CreatedUtc)
                or nameof(IAuditable.ModifiedUtc) or nameof(IAuditable.CreatedByUserId)
                or nameof(IAuditable.ModifiedByUserId))
            {
                continue;
            }

            bool changed = entry.State == EntityState.Added || property.IsModified;
            if (!changed)
            {
                continue;
            }

            if (RedactedProperties.Contains(name))
            {
                changes[name] = new { changed = true, value = "[redacted]" };
                continue;
            }

            changes[name] = entry.State == EntityState.Added
                ? new { to = property.CurrentValue }
                : new { from = property.OriginalValue, to = property.CurrentValue };
        }

        if (changes.Count == 0)
        {
            return null; // A save that changed nothing is not worth a row.
        }

        object? key = entry.Metadata.FindPrimaryKey()?.Properties
            .Select(property => entry.Property(property.Name).CurrentValue)
            .FirstOrDefault();

        return new AuditEntry
        {
            OccurredUtc = now,
            ActorUserId = actorId,
            ActorUpn = actorUpn,
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = key?.ToString() ?? "(composite)",
            Action = action,
            ChangesJson = JsonSerializer.Serialize(changes, JsonOptions),
        };
    }
}
