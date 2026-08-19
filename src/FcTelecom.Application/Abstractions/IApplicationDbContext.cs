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

namespace FcTelecom.Application.Abstractions;

/// <summary>
/// The database, as the Application layer sees it.
/// </summary>
/// <remarks>
/// This is deliberately a <c>DbContext</c>-shaped interface rather than a set of
/// repositories. The queries this application needs — spend grouped by carrier with an
/// effective-dated cost join, a location page pulling six related collections in one
/// round trip — are expressible in LINQ and painful through a repository. A repository
/// interface wide enough to express them is an abstraction that hides nothing and costs
/// a layer of indirection to read through.
/// <para>
/// The tradeoff accepted: the Application layer knows it is talking to something
/// EF Core-shaped. It does not know it is SQL Server, which is the part that matters
/// for testing.
/// </para>
/// </remarks>
public interface IApplicationDbContext
{
    // Organization
    DbSet<Location> Locations { get; }
    DbSet<Region> Regions { get; }
    DbSet<BusinessUnit> BusinessUnits { get; }
    DbSet<CostCenter> CostCenters { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<LocationContact> LocationContacts { get; }
    DbSet<LocationExternalIdentifier> LocationExternalIdentifiers { get; }

    // Vendors
    DbSet<Vendor> Vendors { get; }
    DbSet<VendorAccount> VendorAccounts { get; }
    DbSet<VendorTicketProcedure> VendorTicketProcedures { get; }

    // Services
    DbSet<TelecomService> TelecomServices { get; }
    DbSet<ServiceIdentifier> ServiceIdentifiers { get; }
    DbSet<ServiceBandwidth> ServiceBandwidths { get; }
    DbSet<ServiceIpAssignment> ServiceIpAssignments { get; }
    DbSet<ServicePhoneNumber> ServicePhoneNumbers { get; }
    DbSet<VoiceServiceDetail> VoiceServiceDetails { get; }
    DbSet<ServiceDependency> ServiceDependencies { get; }

    // Financials
    DbSet<ServiceCost> ServiceCosts { get; }
    DbSet<CostAllocation> CostAllocations { get; }
    DbSet<OneTimeCharge> OneTimeCharges { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<ImportBatch> ImportBatches { get; }
    DbSet<ImportRow> ImportRows { get; }

    // Contracts
    DbSet<Contract> Contracts { get; }
    DbSet<ContractService> ContractServices { get; }
    DbSet<ContractAmendment> ContractAmendments { get; }
    DbSet<ContractAlert> ContractAlerts { get; }

    // Monitoring
    DbSet<ServiceMonitor> Monitors { get; }
    DbSet<Probe> Probes { get; }
    DbSet<MonitorProbeAssignment> MonitorProbeAssignments { get; }
    DbSet<CheckResult> CheckResults { get; }
    DbSet<OutageEvent> OutageEvents { get; }
    DbSet<MaintenanceWindow> MaintenanceWindows { get; }
    DbSet<CoverageGap> CoverageGaps { get; }
    DbSet<AvailabilityRollup> AvailabilityRollups { get; }

    // Platform
    DbSet<AppUser> Users { get; }
    DbSet<AppRole> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RoleAssignment> RoleAssignments { get; }
    DbSet<UserPermissionGrant> UserPermissionGrants { get; }
    DbSet<EntraGroupRoleMap> EntraGroupRoleMaps { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<SecurityEvent> SecurityEvents { get; }
    DbSet<Document> Documents { get; }
    DbSet<SavedView> SavedViews { get; }

    // Integrations and notifications
    DbSet<IntegrationConnection> IntegrationConnections { get; }
    DbSet<ExternalRecordLink> ExternalRecordLinks { get; }
    DbSet<FieldMapping> FieldMappings { get; }
    DbSet<SyncRun> SyncRuns { get; }
    DbSet<SyncLogEntry> SyncLogEntries { get; }
    DbSet<NotificationRule> NotificationRules { get; }
    DbSet<NotificationEscalationStep> NotificationEscalationSteps { get; }
    DbSet<NotificationOutboxMessage> NotificationOutbox { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
