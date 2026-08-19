using System.Linq.Expressions;
using FcTelecom.Application.Abstractions;
using FcTelecom.Domain.Common;
using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Integrations;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Notifications;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Platform;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    // Organization
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<LocationContact> LocationContacts => Set<LocationContact>();
    public DbSet<LocationExternalIdentifier> LocationExternalIdentifiers => Set<LocationExternalIdentifier>();

    // Vendors
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorAccount> VendorAccounts => Set<VendorAccount>();
    public DbSet<VendorTicketProcedure> VendorTicketProcedures => Set<VendorTicketProcedure>();

    // Services
    public DbSet<TelecomService> TelecomServices => Set<TelecomService>();
    public DbSet<ServiceIdentifier> ServiceIdentifiers => Set<ServiceIdentifier>();
    public DbSet<ServiceBandwidth> ServiceBandwidths => Set<ServiceBandwidth>();
    public DbSet<ServiceIpAssignment> ServiceIpAssignments => Set<ServiceIpAssignment>();
    public DbSet<ServicePhoneNumber> ServicePhoneNumbers => Set<ServicePhoneNumber>();
    public DbSet<VoiceServiceDetail> VoiceServiceDetails => Set<VoiceServiceDetail>();
    public DbSet<ServiceDependency> ServiceDependencies => Set<ServiceDependency>();

    // Financials
    public DbSet<ServiceCost> ServiceCosts => Set<ServiceCost>();
    public DbSet<CostAllocation> CostAllocations => Set<CostAllocation>();
    public DbSet<OneTimeCharge> OneTimeCharges => Set<OneTimeCharge>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();

    // Contracts
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractService> ContractServices => Set<ContractService>();
    public DbSet<ContractAmendment> ContractAmendments => Set<ContractAmendment>();
    public DbSet<ContractAlert> ContractAlerts => Set<ContractAlert>();

    // Monitoring
    public DbSet<ServiceMonitor> Monitors => Set<ServiceMonitor>();
    public DbSet<Probe> Probes => Set<Probe>();
    public DbSet<MonitorProbeAssignment> MonitorProbeAssignments => Set<MonitorProbeAssignment>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<OutageEvent> OutageEvents => Set<OutageEvent>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<CoverageGap> CoverageGaps => Set<CoverageGap>();
    public DbSet<AvailabilityRollup> AvailabilityRollups => Set<AvailabilityRollup>();

    // Platform
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppRole> Roles => Set<AppRole>();
    public DbSet<RolePermissionGrant> RolePermissions => Set<RolePermissionGrant>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<UserPermissionGrant> UserPermissionGrants => Set<UserPermissionGrant>();
    public DbSet<EntraGroupRoleMap> EntraGroupRoleMaps => Set<EntraGroupRoleMap>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();

    // Integrations and notifications
    public DbSet<IntegrationConnection> IntegrationConnections => Set<IntegrationConnection>();
    public DbSet<ExternalRecordLink> ExternalRecordLinks => Set<ExternalRecordLink>();
    public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncLogEntry> SyncLogEntries => Set<SyncLogEntry>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<NotificationEscalationStep> NotificationEscalationSteps => Set<NotificationEscalationStep>();
    public DbSet<NotificationOutboxMessage> NotificationOutbox => Set<NotificationOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // One IEntityTypeConfiguration per entity, discovered from this assembly. Nothing
        // else belongs in this method — a 900-line OnModelCreating is where schema
        // decisions go to die.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        ApplySoftDeleteFilters(modelBuilder);
        ApplyRowVersionConvention(modelBuilder);
        ApplyMoneyPrecision(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Marks every inherited <c>RowVersion</c> property as a SQL Server concurrency token.
    /// </summary>
    /// <remarks>
    /// Every entity deriving from <c>BaseEntity</c> has the property, but marking it is a
    /// per-configuration call that is easy to forget — and forgetting is silent. The column
    /// is still created, just as a nullable <c>varbinary(max)</c> that nobody checks, so two
    /// people editing the same circuit simply overwrite each other with no error.
    /// <para>
    /// Applied by convention for the same reason as the soft-delete filter: the failure
    /// mode of the per-entity approach is invisible until it costs someone their work.
    /// </para>
    /// </remarks>
    private static void ApplyRowVersionConvention(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is not null || !typeof(BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var property = entityType.FindProperty(nameof(BaseEntity.RowVersion));
            if (property is null)
            {
                continue;
            }

            property.SetIsConcurrencyToken(true);
            property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
            property.SetColumnType("rowversion");
        }
    }

    /// <summary>
    /// Adds <c>WHERE IsArchived = 0</c> to every soft-deletable entity.
    /// </summary>
    /// <remarks>
    /// Applied by convention rather than per-entity, because the failure mode of the
    /// per-entity approach is that somebody adds an entity, forgets the filter, and
    /// archived records quietly reappear in one list view for six months before anyone
    /// notices.
    /// </remarks>
    private static void ApplySoftDeleteFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType) || entityType.BaseType is not null)
            {
                continue;
            }

            ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "entity");
            MemberExpression property = Expression.Property(parameter, nameof(ISoftDeletable.IsArchived));
            LambdaExpression filter = Expression.Lambda(Expression.Not(property), parameter);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
        }
    }

    /// <summary>
    /// Forces <c>decimal(19,4)</c> on every decimal property that has not declared its own
    /// precision.
    /// </summary>
    /// <remarks>
    /// Without this, EF Core defaults to <c>decimal(18,2)</c> and emits a warning that
    /// nobody reads. Two decimal places is fine for an invoice total and wrong for a
    /// cost-per-Mbps figure, and discovering that after a year of stored data is not a
    /// pleasant migration.
    /// </remarks>
    private static void ApplyMoneyPrecision(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(entityType => entityType.GetProperties())
                     .Where(property => property.ClrType == typeof(decimal) ||
                                        property.ClrType == typeof(decimal?)))
        {
            if (property.GetColumnType() is null && property.GetPrecision() is null)
            {
                property.SetPrecision(19);
                property.SetScale(4);
            }
        }
    }
}
