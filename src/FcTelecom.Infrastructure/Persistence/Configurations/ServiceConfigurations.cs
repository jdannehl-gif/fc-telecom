using FcTelecom.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FcTelecom.Infrastructure.Persistence.Configurations;

public sealed class TelecomServiceConfiguration : IEntityTypeConfiguration<TelecomService>
{
    public void Configure(EntityTypeBuilder<TelecomService> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Services");
        builder.HasKey(service => service.Id);
        builder.Property(service => service.RowVersion).IsRowVersion();

        builder.Property(service => service.CircuitId).HasMaxLength(120);
        builder.Property(service => service.CarrierServiceId).HasMaxLength(120);
        builder.Property(service => service.DemarcLocation).HasMaxLength(300);
        builder.Property(service => service.CpeMake).HasMaxLength(100);
        builder.Property(service => service.CpeModel).HasMaxLength(100);
        builder.Property(service => service.CpeSerial).HasMaxLength(100);
        builder.Property(service => service.WanInterface).HasMaxLength(100);

        builder.HasOne(service => service.Location)
            .WithMany(location => location.Services)
            .HasForeignKey(service => service.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // All four vendor relationships are Restrict. Cascading a vendor deletion into the
        // circuit inventory would be spectacular, and nothing is hard-deleted through the
        // application anyway — the restriction is a second lock on the same door.
        builder.HasOne(service => service.CarrierVendor)
            .WithMany()
            .HasForeignKey(service => service.CarrierVendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(service => service.ResellerVendor)
            .WithMany()
            .HasForeignKey(service => service.ResellerVendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(service => service.LastMileVendor)
            .WithMany()
            .HasForeignKey(service => service.LastMileVendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(service => service.UnderlyingNetworkOwnerVendor)
            .WithMany()
            .HasForeignKey(service => service.UnderlyingNetworkOwnerVendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(service => service.VendorAccount)
            .WithMany()
            .HasForeignKey(service => service.VendorAccountId)
            .OnDelete(DeleteBehavior.SetNull);

        // The outage-time lookup index, and simultaneously the duplicate detector for
        // imports. Filtered so archived and disconnected records do not block a reuse
        // of the same circuit ID by the carrier years later.
        builder.HasIndex(service => service.CircuitId)
            .IsUnique()
            .HasFilter("[CircuitId] IS NOT NULL AND [IsArchived] = 0")
            .HasDatabaseName("UX_Services_CircuitId_Active");

        builder.HasIndex(service => new { service.LocationId, service.Status })
            .HasDatabaseName("IX_Services_Location_Status");

        builder.HasIndex(service => new { service.CarrierVendorId, service.ServiceType })
            .HasDatabaseName("IX_Services_Carrier_Type");

        builder.HasIndex(service => service.Status);
        builder.HasIndex(service => service.LastMileVendorId);
    }
}

public sealed class ServiceIdentifierConfiguration : IEntityTypeConfiguration<ServiceIdentifier>
{
    public void Configure(EntityTypeBuilder<ServiceIdentifier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServiceIdentifiers");
        builder.Property(identifier => identifier.RowVersion).IsRowVersion();
        builder.Property(identifier => identifier.IdentifierType).HasMaxLength(60).IsRequired();
        builder.Property(identifier => identifier.Value).HasMaxLength(200).IsRequired();

        builder.HasOne(identifier => identifier.Service)
            .WithMany(service => service.Identifiers)
            .HasForeignKey(identifier => identifier.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Global search hits this. Non-unique, because two carriers can legitimately use
        // the same string for different things.
        builder.HasIndex(identifier => identifier.Value).HasDatabaseName("IX_ServiceIdentifiers_Value");
        builder.HasIndex(identifier => new { identifier.ServiceId, identifier.IdentifierType });
    }
}

public sealed class ServiceBandwidthConfiguration : IEntityTypeConfiguration<ServiceBandwidth>
{
    public void Configure(EntityTypeBuilder<ServiceBandwidth> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServiceBandwidths", table => table.HasCheckConstraint(
            "CK_ServiceBandwidth_NonNegative",
            "[DownloadKbps] >= 0 AND [UploadKbps] >= 0 AND [CommittedInformationRateKbps] >= 0"));
        builder.HasKey(bandwidth => bandwidth.ServiceId);

        builder.Property(bandwidth => bandwidth.SlaPacketLossPercent).HasPrecision(5, 3);
        builder.Property(bandwidth => bandwidth.SlaJitterMs).HasPrecision(7, 3);

        // 99.999 needs five significant figures; decimal(19,4) would store it but the
        // narrower type documents the intent and rejects nonsense like 150%.
        builder.Property(bandwidth => bandwidth.SlaAvailabilityPercent).HasPrecision(6, 3);

        builder.HasOne(bandwidth => bandwidth.Service)
            .WithOne(service => service.Bandwidth)
            .HasForeignKey<ServiceBandwidth>(bandwidth => bandwidth.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ServiceIpAssignmentConfiguration : IEntityTypeConfiguration<ServiceIpAssignment>
{
    public void Configure(EntityTypeBuilder<ServiceIpAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServiceIpAssignments");
        builder.Property(assignment => assignment.RowVersion).IsRowVersion();

        // Ciphertext is longer than plaintext and base64 inflates it further. 512 is
        // generous for an IPv6 CIDR wrapped in an AES envelope, and keeps the row narrow
        // enough to stay off-page.
        builder.Property(assignment => assignment.CidrEncrypted).HasMaxLength(512).IsRequired();
        builder.Property(assignment => assignment.GatewayEncrypted).HasMaxLength(512);
        builder.Property(assignment => assignment.UsableFirstEncrypted).HasMaxLength(512);
        builder.Property(assignment => assignment.UsableLastEncrypted).HasMaxLength(512);
        builder.Property(assignment => assignment.DnsPrimaryEncrypted).HasMaxLength(512);
        builder.Property(assignment => assignment.DnsSecondaryEncrypted).HasMaxLength(512);

        builder.Property(assignment => assignment.CidrSearchHash).HasMaxLength(32);

        builder.HasOne(assignment => assignment.Service)
            .WithMany(service => service.IpAssignments)
            .HasForeignKey(assignment => assignment.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Exact-match IP lookup without decrypting anything. This index is the entire
        // reason the deterministic hash exists.
        builder.HasIndex(assignment => assignment.CidrSearchHash)
            .HasDatabaseName("IX_ServiceIpAssignments_SearchHash");

        builder.HasIndex(assignment => assignment.ServiceId);
    }
}

public sealed class ServicePhoneNumberConfiguration : IEntityTypeConfiguration<ServicePhoneNumber>
{
    public void Configure(EntityTypeBuilder<ServicePhoneNumber> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServicePhoneNumbers");
        builder.Property(number => number.RowVersion).IsRowVersion();
        builder.Property(number => number.NumberOrRangeStart).HasMaxLength(40).IsRequired();
        builder.Property(number => number.RangeEnd).HasMaxLength(40);
        builder.Property(number => number.E911Address).HasMaxLength(400);

        builder.HasOne(number => number.Service)
            .WithMany(service => service.PhoneNumbers)
            .HasForeignKey(number => number.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(number => number.NumberOrRangeStart);
    }
}

public sealed class VoiceServiceDetailConfiguration : IEntityTypeConfiguration<VoiceServiceDetail>
{
    public void Configure(EntityTypeBuilder<VoiceServiceDetail> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("VoiceServiceDetails");
        builder.HasKey(detail => detail.ServiceId);
        builder.Property(detail => detail.BillingTelephoneNumber).HasMaxLength(40);
        builder.Property(detail => detail.E911RegisteredAddress).HasMaxLength(400);

        builder.HasOne(detail => detail.Service)
            .WithOne(service => service.VoiceDetail)
            .HasForeignKey<VoiceServiceDetail>(detail => detail.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ServiceDependencyConfiguration : IEntityTypeConfiguration<ServiceDependency>
{
    public void Configure(EntityTypeBuilder<ServiceDependency> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServiceDependencies", table => table.HasCheckConstraint(
            "CK_ServiceDependencies_NotSelf",
            "[ServiceId] <> [DependsOnServiceId]"));
        builder.Property(dependency => dependency.RowVersion).IsRowVersion();
        builder.Property(dependency => dependency.Evidence).HasMaxLength(1000);

        builder.HasOne(dependency => dependency.Service)
            .WithMany(service => service.Dependencies)
            .HasForeignKey(dependency => dependency.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction on the far side: SQL Server rejects multiple cascade paths into the
        // same table, and a self-referencing many-to-many is exactly that case.
        builder.HasOne(dependency => dependency.DependsOnService)
            .WithMany()
            .HasForeignKey(dependency => dependency.DependsOnServiceId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(dependency => new { dependency.ServiceId, dependency.DependsOnServiceId, dependency.DependencyType })
            .IsUnique()
            .HasDatabaseName("UX_ServiceDependencies_Pair_Type");
    }
}
