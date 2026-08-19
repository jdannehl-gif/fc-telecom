using FcTelecom.Domain.Contracts;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FcTelecom.Infrastructure.Persistence.Configurations;

public sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Vendors");
        builder.Property(vendor => vendor.RowVersion).IsRowVersion();
        builder.Property(vendor => vendor.LegalName).HasMaxLength(250).IsRequired();
        builder.Property(vendor => vendor.DisplayName).HasMaxLength(150).IsRequired();
        builder.Property(vendor => vendor.PortalUrl).HasMaxLength(500);
        builder.Property(vendor => vendor.MainSupportPhone).HasMaxLength(50);
        builder.Property(vendor => vendor.SupportHours).HasMaxLength(200);

        // A pointer to where credentials live, never a credential. Sized for a vault path
        // and a note, not for a secret.
        builder.Property(vendor => vendor.CredentialReference).HasMaxLength(300);
        builder.Property(vendor => vendor.ItGluePasswordRecordId).HasMaxLength(100);

        builder.HasIndex(vendor => vendor.DisplayName)
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_Vendors_DisplayName_Active");
    }
}

public sealed class VendorAccountConfiguration : IEntityTypeConfiguration<VendorAccount>
{
    public void Configure(EntityTypeBuilder<VendorAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("VendorAccounts");
        builder.Property(account => account.RowVersion).IsRowVersion();
        builder.Property(account => account.AccountNumber).HasMaxLength(100).IsRequired();
        builder.Property(account => account.BillingAccountNumber).HasMaxLength(100);
        builder.Property(account => account.Description).HasMaxLength(300);

        builder.HasOne(account => account.Vendor)
            .WithMany(vendor => vendor.Accounts)
            .HasForeignKey(account => account.VendorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(account => account.BillingContact)
            .WithMany()
            .HasForeignKey(account => account.BillingContactId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(account => new { account.VendorId, account.AccountNumber })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_VendorAccounts_Vendor_Number");

        // Global search matches on account number alone, without knowing the vendor.
        builder.HasIndex(account => account.AccountNumber);
    }
}

public sealed class VendorTicketProcedureConfiguration : IEntityTypeConfiguration<VendorTicketProcedure>
{
    public void Configure(EntityTypeBuilder<VendorTicketProcedure> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("VendorTicketProcedures");
        builder.Property(procedure => procedure.RowVersion).IsRowVersion();
        builder.Property(procedure => procedure.ScenarioName).HasMaxLength(150).IsRequired();
        builder.Property(procedure => procedure.PhoneNumber).HasMaxLength(50);
        builder.Property(procedure => procedure.PortalUrl).HasMaxLength(500);
        builder.Property(procedure => procedure.EmailAddress).HasMaxLength(320);
        builder.Property(procedure => procedure.HoursOfOperation).HasMaxLength(200);
        builder.Property(procedure => procedure.ExpectedResponseTime).HasMaxLength(150);

        builder.HasOne(procedure => procedure.Vendor)
            .WithMany(vendor => vendor.TicketProcedures)
            .HasForeignKey(procedure => procedure.VendorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ServiceCostConfiguration : IEntityTypeConfiguration<ServiceCost>
{
    public void Configure(EntityTypeBuilder<ServiceCost> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServiceCosts", table => table.HasCheckConstraint(
            "CK_ServiceCosts_EffectiveRange",
            "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]"));

        builder.Property(cost => cost.RowVersion).IsRowVersion();
        builder.Property(cost => cost.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(cost => cost.GlCode).HasMaxLength(50);

        builder.HasOne(cost => cost.Service)
            .WithMany(service => service.CostHistory)
            .HasForeignKey(cost => cost.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(cost => cost.CostCenter)
            .WithMany()
            .HasForeignKey(cost => cost.CostCenterId)
            .OnDelete(DeleteBehavior.SetNull);

        // At most one open cost row per service. This is what enforces "append-only,
        // effective-dated" at the database rather than trusting the application to behave.
        // A bug that tries to leave two current prices on one circuit fails loudly here
        // instead of silently doubling that circuit's contribution to every spend report.
        builder.HasIndex(cost => cost.ServiceId)
            .IsUnique()
            .HasFilter("[EffectiveTo] IS NULL")
            .HasDatabaseName("UX_ServiceCosts_OneOpenPerService");

        builder.HasIndex(cost => new { cost.ServiceId, cost.EffectiveFrom })
            .HasDatabaseName("IX_ServiceCosts_Service_EffectiveFrom");
    }
}

public sealed class CostAllocationConfiguration : IEntityTypeConfiguration<CostAllocation>
{
    public void Configure(EntityTypeBuilder<CostAllocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CostAllocations", table => table.HasCheckConstraint(
            "CK_CostAllocations_Percent", "[Percent] > 0 AND [Percent] <= 100"));

        builder.Property(allocation => allocation.Percent).HasPrecision(6, 3);

        builder.HasOne(allocation => allocation.ServiceCost)
            .WithMany(cost => cost.Allocations)
            .HasForeignKey(allocation => allocation.ServiceCostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(allocation => allocation.CostCenter)
            .WithMany()
            .HasForeignKey(allocation => allocation.CostCenterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(allocation => new { allocation.ServiceCostId, allocation.CostCenterId }).IsUnique();
    }
}

public sealed class OneTimeChargeConfiguration : IEntityTypeConfiguration<OneTimeCharge>
{
    public void Configure(EntityTypeBuilder<OneTimeCharge> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OneTimeCharges");
        builder.Property(charge => charge.RowVersion).IsRowVersion();
        builder.Property(charge => charge.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(charge => charge.Description).HasMaxLength(500);

        builder.HasOne(charge => charge.Service)
            .WithMany()
            .HasForeignKey(charge => charge.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(charge => charge.Invoice)
            .WithMany()
            .HasForeignKey(charge => charge.InvoiceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(charge => new { charge.ServiceId, charge.IncurredOn });
    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Invoices");
        builder.Property(invoice => invoice.RowVersion).IsRowVersion();
        builder.Property(invoice => invoice.InvoiceNumber).HasMaxLength(100).IsRequired();
        builder.Property(invoice => invoice.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasOne(invoice => invoice.Vendor)
            .WithMany()
            .HasForeignKey(invoice => invoice.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(invoice => invoice.VendorAccount)
            .WithMany()
            .HasForeignKey(invoice => invoice.VendorAccountId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(invoice => invoice.ImportBatch)
            .WithMany()
            .HasForeignKey(invoice => invoice.ImportBatchId)
            .OnDelete(DeleteBehavior.SetNull);

        // Duplicate-import protection: the same invoice number from the same vendor is
        // almost always somebody importing the same file twice.
        builder.HasIndex(invoice => new { invoice.VendorId, invoice.InvoiceNumber })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_Invoices_Vendor_Number");

        builder.HasIndex(invoice => invoice.InvoiceDate);
    }
}

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InvoiceLines");
        builder.Property(line => line.RowVersion).IsRowVersion();
        builder.Property(line => line.RawCircuitReference).HasMaxLength(200);
        builder.Property(line => line.Description).HasMaxLength(500);
        builder.Property(line => line.VariancePercent).HasPrecision(9, 2);

        builder.HasOne(line => line.Invoice)
            .WithMany(invoice => invoice.Lines)
            .HasForeignKey(line => line.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(line => line.Service)
            .WithMany()
            .HasForeignKey(line => line.ServiceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(line => line.MatchStatus);
        builder.HasIndex(line => line.RawCircuitReference);
    }
}

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ImportBatches");
        builder.Property(batch => batch.FileName).HasMaxLength(400).IsRequired();
        builder.Property(batch => batch.BlobPath).HasMaxLength(600);
        builder.HasIndex(batch => new { batch.BatchType, batch.UploadedUtc });
    }
}

public sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ImportRows");
        builder.HasKey(row => row.Id);

        builder.HasOne(row => row.ImportBatch)
            .WithMany(batch => batch.Rows)
            .HasForeignKey(row => row.ImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(row => new { row.ImportBatchId, row.Status });
    }
}

public sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Contracts");
        builder.Property(contract => contract.RowVersion).IsRowVersion();
        builder.Property(contract => contract.ContractNumber).HasMaxLength(100).IsRequired();
        builder.Property(contract => contract.Description).HasMaxLength(500);
        builder.Property(contract => contract.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(contract => contract.PriceEscalatorPercent).HasPrecision(6, 3);
        builder.Property(contract => contract.EarlyTerminationFormula).HasMaxLength(1000);

        builder.HasOne(contract => contract.Vendor)
            .WithMany()
            .HasForeignKey(contract => contract.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(contract => contract.ContractOwner)
            .WithMany()
            .HasForeignKey(contract => contract.ContractOwnerUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(contract => new { contract.VendorId, contract.ContractNumber })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_Contracts_Vendor_Number");

        // Filtered to active contracts: the nightly renewal scan reads only these, and a
        // narrow index over a few hundred rows keeps that job trivial regardless of how
        // many expired contracts accumulate over the years.
        builder.HasIndex(contract => contract.NoticeDeadlineDate)
            .HasFilter("[Status] IN (2, 3) AND [IsArchived] = 0")
            .HasDatabaseName("IX_Contracts_NoticeDeadline_Active");

        builder.HasIndex(contract => contract.EndDate);
    }
}

public sealed class ContractServiceConfiguration : IEntityTypeConfiguration<ContractService>
{
    public void Configure(EntityTypeBuilder<ContractService> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContractServices");
        builder.HasKey(link => new { link.ContractId, link.ServiceId });

        builder.HasOne(link => link.Contract)
            .WithMany(contract => contract.Services)
            .HasForeignKey(link => link.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(link => link.Service)
            .WithMany(service => service.ContractLinks)
            .HasForeignKey(link => link.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(link => link.ServiceEndDate);
    }
}

public sealed class ContractAmendmentConfiguration : IEntityTypeConfiguration<ContractAmendment>
{
    public void Configure(EntityTypeBuilder<ContractAmendment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContractAmendments");
        builder.Property(amendment => amendment.RowVersion).IsRowVersion();
        builder.Property(amendment => amendment.AmendmentNumber).HasMaxLength(50).IsRequired();
        builder.Property(amendment => amendment.Summary).HasMaxLength(1000);

        builder.HasOne(amendment => amendment.Contract)
            .WithMany(contract => contract.Amendments)
            .HasForeignKey(amendment => amendment.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(amendment => amendment.Document)
            .WithMany()
            .HasForeignKey(amendment => amendment.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ContractAlertConfiguration : IEntityTypeConfiguration<ContractAlert>
{
    public void Configure(EntityTypeBuilder<ContractAlert> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContractAlerts");
        builder.Property(alert => alert.Recipients).HasMaxLength(2000);
        builder.Property(alert => alert.FailureReason).HasMaxLength(1000);

        builder.HasOne(alert => alert.Contract)
            .WithMany(contract => contract.Alerts)
            .HasForeignKey(alert => alert.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // One alert per contract, per kind, per threshold — ever. This unique index is
        // what stops the nightly job re-sending the same 90-day warning for thirty nights
        // running, which is the fastest way to teach people to filter your alerts to a folder.
        builder.HasIndex(alert => new { alert.ContractId, alert.AlertKind, alert.ThresholdDays })
            .IsUnique()
            .HasDatabaseName("UX_ContractAlerts_Contract_Kind_Threshold");
    }
}
