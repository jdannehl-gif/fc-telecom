using FcTelecom.Application.Authorization;
using FcTelecom.Domain.Integrations;
using FcTelecom.Domain.Notifications;
using FcTelecom.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FcTelecom.Infrastructure.Persistence.Configurations;

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users");
        builder.Property(user => user.RowVersion).IsRowVersion();
        builder.Property(user => user.EntraObjectId).HasMaxLength(100).IsRequired();
        builder.Property(user => user.UserPrincipalName).HasMaxLength(320).IsRequired();
        builder.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.Email).HasMaxLength(320);

        // The Entra object ID is the identity key, and it is the only unique constraint
        // here. The UPN is deliberately NOT unique: it changes when someone marries, and
        // it gets reassigned when someone leaves.
        builder.HasIndex(user => user.EntraObjectId)
            .IsUnique()
            .HasDatabaseName("UX_Users_EntraObjectId");

        builder.HasIndex(user => user.UserPrincipalName);
    }
}

public sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles");
        builder.Property(role => role.Name).HasMaxLength(60).IsRequired();
        builder.Property(role => role.Description).HasMaxLength(300);
        builder.HasIndex(role => role.Name).IsUnique();

        // The five shipped roles are seeded here, in the model, so a fresh database is
        // usable immediately and the IDs are stable across environments. Their permissions
        // are seeded separately and are editable afterwards; these rows are not.
        builder.HasData(
            new AppRole { Id = 1, Name = Roles.AppAdministrator, IsSystemRole = true, Description = "Full configuration, user, and role administration." },
            new AppRole { Id = 2, Name = Roles.NetworkEngineer, IsSystemRole = true, Description = "Locations, services, IP data, outages, monitoring, technical documentation." },
            new AppRole { Id = 3, Name = Roles.Procurement, IsSystemRole = true, Description = "Vendors, spend, invoices, contracts, renewals, cost allocation." },
            new AppRole { Id = 4, Name = Roles.HelpDesk, IsSystemRole = true, Description = "Circuit and escalation detail, incident recording, service status." },
            new AppRole { Id = 5, Name = Roles.ReadOnly, IsSystemRole = true, Description = "Dashboards and reports only." });
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermissionGrant>
{
    public void Configure(EntityTypeBuilder<RolePermissionGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RolePermissions");
        builder.HasKey(permission => new { permission.RoleId, permission.Permission });
        builder.Property(permission => permission.Permission).HasMaxLength(60).IsRequired();

        builder.HasOne(permission => permission.Role)
            .WithMany(role => role.Permissions)
            .HasForeignKey(permission => permission.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RoleAssignments");
        builder.HasKey(assignment => new { assignment.UserId, assignment.RoleId });

        builder.HasOne(assignment => assignment.User)
            .WithMany(user => user.RoleAssignments)
            .HasForeignKey(assignment => assignment.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(assignment => assignment.Role)
            .WithMany()
            .HasForeignKey(assignment => assignment.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserPermissionGrantConfiguration : IEntityTypeConfiguration<UserPermissionGrant>
{
    public void Configure(EntityTypeBuilder<UserPermissionGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserPermissionGrants");
        builder.Property(grant => grant.Permission).HasMaxLength(60).IsRequired();

        // Required, not optional. An unexplained standing grant of ServiceIpData.Read to
        // one person is exactly the finding an access review is meant to surface, and it
        // cannot be reviewed if nobody wrote down why it exists.
        builder.Property(grant => grant.Justification).HasMaxLength(500).IsRequired();

        builder.HasOne(grant => grant.User)
            .WithMany(user => user.DirectPermissions)
            .HasForeignKey(grant => grant.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(grant => new { grant.UserId, grant.Permission }).IsUnique();
        builder.HasIndex(grant => grant.ExpiresUtc);
    }
}

public sealed class EntraGroupRoleMapConfiguration : IEntityTypeConfiguration<EntraGroupRoleMap>
{
    public void Configure(EntityTypeBuilder<EntraGroupRoleMap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EntraGroupRoleMaps");
        builder.Property(map => map.RowVersion).IsRowVersion();
        builder.Property(map => map.EntraGroupObjectId).HasMaxLength(100).IsRequired();
        builder.Property(map => map.EntraGroupDisplayName).HasMaxLength(300);

        builder.HasOne(map => map.Role)
            .WithMany(role => role.GroupMappings)
            .HasForeignKey(map => map.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique on the object ID, not the display name. A group rename must not silently
        // revoke everyone's access — or, worse, grant it to whichever different group
        // inherits the old name.
        builder.HasIndex(map => map.EntraGroupObjectId)
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_EntraGroupRoleMaps_GroupObjectId");
    }
}

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditEntries");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.ActorUpn).HasMaxLength(320);
        builder.Property(entry => entry.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.EntityId).HasMaxLength(64).IsRequired();
        builder.Property(entry => entry.IpAddress).HasMaxLength(64);

        // Per-record history panel: "show me everything that ever happened to this circuit".
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId, entry.OccurredUtc })
            .HasDatabaseName("IX_AuditEntries_Entity_OccurredAt");

        builder.HasIndex(entry => entry.OccurredUtc);
        builder.HasIndex(entry => entry.ActorUserId);
        builder.HasIndex(entry => entry.CorrelationId);
    }
}

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SecurityEvents");
        builder.HasKey(securityEvent => securityEvent.Id);
        builder.Property(securityEvent => securityEvent.ActorUpn).HasMaxLength(320);
        builder.Property(securityEvent => securityEvent.Detail).HasMaxLength(2000);
        builder.Property(securityEvent => securityEvent.IpAddress).HasMaxLength(64);

        builder.HasIndex(securityEvent => new { securityEvent.EventType, securityEvent.OccurredUtc });
        builder.HasIndex(securityEvent => securityEvent.ActorUserId);
    }
}

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Documents");
        builder.Property(document => document.RowVersion).IsRowVersion();
        builder.Property(document => document.OwnerEntityType).HasMaxLength(100).IsRequired();
        builder.Property(document => document.OwnerEntityId).HasMaxLength(64).IsRequired();
        builder.Property(document => document.FileName).HasMaxLength(400).IsRequired();
        builder.Property(document => document.BlobPath).HasMaxLength(600).IsRequired();
        builder.Property(document => document.ContentType).HasMaxLength(150);
        builder.Property(document => document.Sha256).HasMaxLength(64);
        builder.Property(document => document.Description).HasMaxLength(500);

        builder.HasIndex(document => new { document.OwnerEntityType, document.OwnerEntityId })
            .HasDatabaseName("IX_Documents_Owner");

        // Duplicate-upload detection. Not unique: the same PDF legitimately attaches to a
        // contract and to the three circuits it covers.
        builder.HasIndex(document => document.Sha256);
    }
}

public sealed class SavedViewConfiguration : IEntityTypeConfiguration<SavedView>
{
    public void Configure(EntityTypeBuilder<SavedView> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SavedViews");
        builder.Property(view => view.RowVersion).IsRowVersion();
        builder.Property(view => view.EntityType).HasMaxLength(60).IsRequired();
        builder.Property(view => view.Name).HasMaxLength(150).IsRequired();

        builder.HasOne(view => view.User)
            .WithMany()
            .HasForeignKey(view => view.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(view => new { view.UserId, view.EntityType, view.Name })
            .IsUnique()
            .HasFilter("[IsArchived] = 0");
    }
}

public sealed class IntegrationConnectionConfiguration : IEntityTypeConfiguration<IntegrationConnection>
{
    public void Configure(EntityTypeBuilder<IntegrationConnection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IntegrationConnections");
        builder.Property(connection => connection.RowVersion).IsRowVersion();
        builder.Property(connection => connection.SystemKey).HasMaxLength(60).IsRequired();
        builder.Property(connection => connection.DisplayName).HasMaxLength(150).IsRequired();
        builder.Property(connection => connection.BaseUrl).HasMaxLength(500);

        // The NAME of the Key Vault secret. Never the token.
        builder.Property(connection => connection.ApiKeySecretName).HasMaxLength(150);

        builder.Property(connection => connection.ScheduleCron).HasMaxLength(100);
        builder.Property(connection => connection.ErrorState).HasMaxLength(2000);

        builder.HasIndex(connection => connection.SystemKey)
            .IsUnique()
            .HasFilter("[IsArchived] = 0");
    }
}

public sealed class ExternalRecordLinkConfiguration : IEntityTypeConfiguration<ExternalRecordLink>
{
    public void Configure(EntityTypeBuilder<ExternalRecordLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ExternalRecordLinks");
        builder.Property(link => link.LocalEntityType).HasMaxLength(100).IsRequired();
        builder.Property(link => link.LocalEntityId).HasMaxLength(64).IsRequired();
        builder.Property(link => link.ExternalId).HasMaxLength(100);
        builder.Property(link => link.ExternalType).HasMaxLength(60);
        builder.Property(link => link.LocalVersionHash).HasMaxLength(64);
        builder.Property(link => link.ExternalVersionHash).HasMaxLength(64);
        builder.Property(link => link.LastError).HasMaxLength(2000);

        builder.HasOne(link => link.Connection)
            .WithMany(connection => connection.RecordLinks)
            .HasForeignKey(link => link.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        // These two unique indexes are what make sync idempotent in both directions of
        // lookup. Re-running a sync updates; it never duplicates. Note that both key on
        // IDs — names are never integration keys, because a location rename would
        // otherwise create a second IT Glue record and orphan the first.
        builder.HasIndex(link => new { link.ConnectionId, link.LocalEntityType, link.LocalEntityId })
            .IsUnique()
            .HasDatabaseName("UX_ExternalRecordLinks_Local");

        builder.HasIndex(link => new { link.ConnectionId, link.ExternalType, link.ExternalId })
            .IsUnique()
            .HasFilter("[ExternalId] IS NOT NULL")
            .HasDatabaseName("UX_ExternalRecordLinks_External");

        builder.HasIndex(link => link.SyncState);
    }
}

public sealed class FieldMappingConfiguration : IEntityTypeConfiguration<FieldMapping>
{
    public void Configure(EntityTypeBuilder<FieldMapping> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FieldMappings");
        builder.Property(mapping => mapping.RowVersion).IsRowVersion();
        builder.Property(mapping => mapping.LocalEntityType).HasMaxLength(100).IsRequired();
        builder.Property(mapping => mapping.LocalField).HasMaxLength(100).IsRequired();
        builder.Property(mapping => mapping.ExternalField).HasMaxLength(150).IsRequired();
        builder.Property(mapping => mapping.TransformExpression).HasMaxLength(500);

        builder.HasOne(mapping => mapping.Connection)
            .WithMany(connection => connection.FieldMappings)
            .HasForeignKey(mapping => mapping.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(mapping => new { mapping.ConnectionId, mapping.LocalEntityType, mapping.LocalField })
            .IsUnique()
            .HasFilter("[IsArchived] = 0");
    }
}

public sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SyncRuns");
        builder.Property(run => run.Summary).HasMaxLength(2000);

        builder.HasOne(run => run.Connection)
            .WithMany()
            .HasForeignKey(run => run.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(run => new { run.ConnectionId, run.StartedUtc });
    }
}

public sealed class SyncLogEntryConfiguration : IEntityTypeConfiguration<SyncLogEntry>
{
    public void Configure(EntityTypeBuilder<SyncLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SyncLogEntries");
        builder.Property(entry => entry.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.LocalId).HasMaxLength(64);
        builder.Property(entry => entry.ExternalId).HasMaxLength(100);
        builder.Property(entry => entry.Message).HasMaxLength(2000);

        builder.HasOne(entry => entry.SyncRun)
            .WithMany(run => run.LogEntries)
            .HasForeignKey(entry => entry.SyncRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entry => new { entry.SyncRunId, entry.Success });
    }
}

public sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationRules");
        builder.Property(rule => rule.RowVersion).IsRowVersion();
        builder.Property(rule => rule.Name).HasMaxLength(150).IsRequired();
        builder.Property(rule => rule.EventType).HasMaxLength(80).IsRequired();
        builder.Property(rule => rule.Recipients).HasMaxLength(2000);
        builder.Property(rule => rule.SharedMailbox).HasMaxLength(320);
        builder.Property(rule => rule.TeamsChannelReference).HasMaxLength(400);
        builder.Property(rule => rule.WebhookUrl).HasMaxLength(1000);
        builder.Property(rule => rule.RoleScope).HasMaxLength(60);
        builder.Property(rule => rule.ThresholdDaysCsv).HasMaxLength(200);

        // Derived helpers, not columns.
        builder.Ignore(rule => rule.HasNoPossibleRecipient);

        builder.HasIndex(rule => new { rule.EventType, rule.Enabled });
    }
}

public sealed class NotificationEscalationStepConfiguration
    : IEntityTypeConfiguration<NotificationEscalationStep>
{
    public void Configure(EntityTypeBuilder<NotificationEscalationStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationEscalationSteps");
        builder.Property(step => step.RowVersion).IsRowVersion();
        builder.Property(step => step.Recipients).HasMaxLength(2000);
        builder.Property(step => step.RoleScope).HasMaxLength(60);
        builder.Property(step => step.Description).HasMaxLength(500);

        builder.HasOne(step => step.Rule)
            .WithMany(rule => rule.EscalationSteps)
            .HasForeignKey(step => step.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // One step per threshold per rule. Two steps at 60 days is always a mistake, and
        // the resulting double-escalation is exactly the noise that trains people to
        // ignore escalations.
        builder.HasIndex(step => new { step.RuleId, step.ThresholdDays })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_NotificationEscalationSteps_Rule_Threshold");
    }
}

public sealed class NotificationOutboxConfiguration : IEntityTypeConfiguration<NotificationOutboxMessage>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("NotificationOutbox");
        builder.Property(message => message.RowVersion).IsRowVersion();
        builder.Property(message => message.EventType).HasMaxLength(80).IsRequired();
        builder.Property(message => message.DedupeKey).HasMaxLength(300).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(2000);

        builder.HasOne(message => message.Rule)
            .WithMany()
            .HasForeignKey(message => message.RuleId)
            .OnDelete(DeleteBehavior.SetNull);

        // The single most important constraint in the notification path. A redeploy
        // mid-drain, a retry storm, or a duplicated timer fire all try to insert the same
        // dedupe key, and exactly one of them wins.
        builder.HasIndex(message => message.DedupeKey)
            .IsUnique()
            .HasDatabaseName("UX_NotificationOutbox_DedupeKey");

        // The drain query: pending, scheduled at or before now, oldest first.
        builder.HasIndex(message => new { message.Status, message.ScheduledUtc })
            .HasDatabaseName("IX_NotificationOutbox_Drain");
    }
}
