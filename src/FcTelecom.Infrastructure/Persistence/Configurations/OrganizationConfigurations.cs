using FcTelecom.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FcTelecom.Infrastructure.Persistence.Configurations;

public sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Locations");
        builder.HasKey(location => location.Id);
        builder.Property(location => location.RowVersion).IsRowVersion();

        builder.Property(location => location.LocationCode).HasMaxLength(50).IsRequired();
        builder.Property(location => location.Name).HasMaxLength(200).IsRequired();
        builder.Property(location => location.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(location => location.MainPhone).HasMaxLength(50);
        builder.Property(location => location.OperatingHours).HasMaxLength(500);

        // Coordinates need six decimal places (roughly 11 cm). The model-wide decimal(19,4)
        // default would round a location to about the nearest 11 metres, which is fine for
        // a map pin and wrong if anyone ever uses these for distance maths.
        builder.Property(location => location.Latitude).HasPrecision(9, 6);
        builder.Property(location => location.Longitude).HasPrecision(9, 6);

        builder.OwnsOne(location => location.PhysicalAddress, address =>
        {
            address.Property(item => item.Line1).HasColumnName("PhysicalLine1").HasMaxLength(200).IsRequired();
            address.Property(item => item.Line2).HasColumnName("PhysicalLine2").HasMaxLength(200);
            address.Property(item => item.City).HasColumnName("PhysicalCity").HasMaxLength(100).IsRequired();
            address.Property(item => item.StateOrProvince).HasColumnName("PhysicalState").HasMaxLength(100);
            address.Property(item => item.PostalCode).HasColumnName("PhysicalPostalCode").HasMaxLength(20);
            address.Property(item => item.CountryCode).HasColumnName("PhysicalCountry").HasMaxLength(2).IsRequired();
        });

        builder.OwnsOne(location => location.MailingAddress, address =>
        {
            address.Property(item => item.Line1).HasColumnName("MailingLine1").HasMaxLength(200);
            address.Property(item => item.Line2).HasColumnName("MailingLine2").HasMaxLength(200);
            address.Property(item => item.City).HasColumnName("MailingCity").HasMaxLength(100);
            address.Property(item => item.StateOrProvince).HasColumnName("MailingState").HasMaxLength(100);
            address.Property(item => item.PostalCode).HasColumnName("MailingPostalCode").HasMaxLength(20);
            address.Property(item => item.CountryCode).HasColumnName("MailingCountry").HasMaxLength(2);
        });

        builder.HasOne(location => location.Region)
            .WithMany(region => region.Locations)
            .HasForeignKey(location => location.RegionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(location => location.BusinessUnit)
            .WithMany(unit => unit.Locations)
            .HasForeignKey(location => location.BusinessUnitId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(location => location.CostCenter)
            .WithMany()
            .HasForeignKey(location => location.CostCenterId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(location => location.ItOwnerContact)
            .WithMany()
            .HasForeignKey(location => location.ItOwnerContactId)
            .OnDelete(DeleteBehavior.SetNull);

        // Filtered unique: a code may be reused once the old location is archived, which
        // happens when a site closes and a new one opens with the same store number.
        builder.HasIndex(location => location.LocationCode)
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_Locations_LocationCode_Active");

        builder.HasIndex(location => new { location.RegionId, location.Status });
        builder.HasIndex(location => location.Criticality);

        builder.Ignore(location => location.HasCoordinates);
        builder.Ignore(location => location.DisplayName);
    }
}

public sealed class LocationExternalIdentifierConfiguration
    : IEntityTypeConfiguration<LocationExternalIdentifier>
{
    public void Configure(EntityTypeBuilder<LocationExternalIdentifier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LocationExternalIdentifiers");
        builder.Property(identifier => identifier.RowVersion).IsRowVersion();
        builder.Property(identifier => identifier.SystemKey).HasMaxLength(60).IsRequired();
        builder.Property(identifier => identifier.Value).HasMaxLength(100).IsRequired();

        builder.HasOne(identifier => identifier.Location)
            .WithMany(location => location.ExternalIdentifiers)
            .HasForeignKey(identifier => identifier.LocationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One value per system per location, and one location per value within a system.
        // Two locations claiming the same Agris code means the facility master and this
        // inventory disagree about what a location is, which is worth failing an import over.
        builder.HasIndex(identifier => new { identifier.LocationId, identifier.SystemKey })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_LocationExternalIdentifiers_Location_System");

        builder.HasIndex(identifier => new { identifier.SystemKey, identifier.Value })
            .IsUnique()
            .HasFilter("[IsArchived] = 0")
            .HasDatabaseName("UX_LocationExternalIdentifiers_System_Value");
    }
}

public sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    public void Configure(EntityTypeBuilder<Region> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Regions");
        builder.Property(region => region.Name).HasMaxLength(100).IsRequired();
        builder.Property(region => region.Code).HasMaxLength(20);
        builder.HasIndex(region => region.Name).IsUnique();
    }
}

public sealed class BusinessUnitConfiguration : IEntityTypeConfiguration<BusinessUnit>
{
    public void Configure(EntityTypeBuilder<BusinessUnit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("BusinessUnits");
        builder.Property(unit => unit.Name).HasMaxLength(100).IsRequired();
        builder.Property(unit => unit.Code).HasMaxLength(20);
        builder.HasIndex(unit => unit.Name).IsUnique();
    }
}

public sealed class CostCenterConfiguration : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CostCenters");
        builder.Property(center => center.Code).HasMaxLength(50).IsRequired();
        builder.Property(center => center.Name).HasMaxLength(200).IsRequired();
        builder.Property(center => center.GlAccount).HasMaxLength(50);
        builder.HasIndex(center => center.Code).IsUnique();
    }
}

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Contacts");
        builder.Property(contact => contact.RowVersion).IsRowVersion();
        builder.Property(contact => contact.FullName).HasMaxLength(200).IsRequired();
        builder.Property(contact => contact.JobTitle).HasMaxLength(150);
        builder.Property(contact => contact.Email).HasMaxLength(320);
        builder.Property(contact => contact.PhoneNumber).HasMaxLength(50);
        builder.Property(contact => contact.MobileNumber).HasMaxLength(50);

        builder.HasOne(contact => contact.Vendor)
            .WithMany(vendor => vendor.Contacts)
            .HasForeignKey(contact => contact.VendorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(contact => contact.FullName);
        builder.HasIndex(contact => new { contact.VendorId, contact.Kind });
    }
}

public sealed class LocationContactConfiguration : IEntityTypeConfiguration<LocationContact>
{
    public void Configure(EntityTypeBuilder<LocationContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LocationContacts");
        builder.HasKey(link => new { link.LocationId, link.ContactId });
        builder.Property(link => link.RoleAtLocation).HasMaxLength(150).IsRequired();

        builder.HasOne(link => link.Location)
            .WithMany(location => location.Contacts)
            .HasForeignKey(link => link.LocationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(link => link.Contact)
            .WithMany(contact => contact.Locations)
            .HasForeignKey(link => link.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
