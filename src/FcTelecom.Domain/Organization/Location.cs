using FcTelecom.Domain.Common;
using FcTelecom.Domain.Services;

namespace FcTelecom.Domain.Organization;

// Namespace note: the design document calls this module "Directory". In code it is
// "Organization", because a namespace named Directory shadows System.IO.Directory and
// turns every File/Directory call in the assembly into a puzzle.

public enum LocationStatus { Active = 1, Planned = 2, Closing = 3, Closed = 4 }

public enum LocationType
{
    Office = 1, Retail = 2, Warehouse = 3, DataCenter = 4,
    Clinic = 5, Manufacturing = 6, Remote = 7, Other = 99,
}

/// <summary>How badly an outage here hurts. Drives alert routing and reporting weight.</summary>
public enum Criticality { Low = 1, Standard = 2, High = 3, Critical = 4 }

/// <summary>
/// A physical place with one or more telecom services.
/// </summary>
/// <remarks>
/// There is deliberately no <c>PrimaryServiceId</c> on this entity. Primacy is a
/// property of the service (<see cref="TelecomService.ServiceRole"/>), which lets a
/// location have two active primaries (dual-active SD-WAN), a primary plus three
/// standalone voice lines, or any other real-world shape without the schema arguing.
/// </remarks>
public class Location : AuditableEntity
{
    /// <summary>
    /// Your existing location number. This is the natural key that must match whatever
    /// system is authoritative today (ERP, AD sites, the store master). Imports match on
    /// it; integrations key on it. Never on the name.
    /// </summary>
    public required string LocationCode { get; set; }

    public required string Name { get; set; }
    public LocationStatus Status { get; set; } = LocationStatus.Active;
    public LocationType LocationType { get; set; } = LocationType.Office;

    public required Address PhysicalAddress { get; set; }

    /// <summary>Null means "same as physical", which is the common case.</summary>
    public Address? MailingAddress { get; set; }

    /// <summary>
    /// IANA identifier, e.g. <c>America/Chicago</c>. Not a Windows time zone name —
    /// this application runs on Linux App Service, and the IANA form also survives being
    /// handed to Power BI or any non-.NET consumer.
    /// </summary>
    public required string TimeZoneId { get; set; }

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public int? BusinessUnitId { get; set; }
    public BusinessUnit? BusinessUnit { get; set; }

    public string? MainPhone { get; set; }

    public Guid? ItOwnerContactId { get; set; }
    public Contact? ItOwnerContact { get; set; }

    /// <summary>Free text — "M–F 07:00–18:00, Sat 08:00–12:00". Deliberately not modelled.</summary>
    public string? OperatingHours { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public Criticality Criticality { get; set; } = Criticality.Standard;

    /// <summary>
    /// How long this site can be down before it is a business problem. Used to weight
    /// outage severity and to sort the outage queue — a 15-minute clinic outranks a
    /// four-hour warehouse.
    /// </summary>
    public int AcceptableOutageMinutes { get; set; } = 240;

    public string? Notes { get; set; }

    public ICollection<TelecomService> Services { get; set; } = [];
    public ICollection<LocationContact> Contacts { get; set; } = [];

    /// <summary>
    /// Identifiers this location carries in other systems.
    /// </summary>
    /// <remarks>
    /// Deliberately a child collection rather than an <c>AgrisLocationCode</c> column.
    /// Not every monitored facility exists as a conventional Agris location — a tower site,
    /// a leased closet, or a warehouse annexe may be a real telecom location with no
    /// counterpart in the facility master — and a nullable column named after one system
    /// invites exactly the conflation this design is trying to avoid.
    /// <para>
    /// <see cref="LocationCode"/> remains the permanent enterprise key and is never
    /// synonymous with an external value.
    /// </para>
    /// </remarks>
    public ICollection<LocationExternalIdentifier> ExternalIdentifiers { get; set; } = [];

    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;

    public string DisplayName => $"{LocationCode} · {Name}";

    /// <summary>The identifier this location carries in a named external system, if any.</summary>
    public string? ExternalCodeFor(string systemKey) =>
        ExternalIdentifiers.FirstOrDefault(identifier =>
            string.Equals(identifier.SystemKey, systemKey, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>Well-known external systems that carry their own location identifiers.</summary>
public static class ExternalLocationSystems
{
    public const string Agris = "Agris";

    public static readonly IReadOnlyList<string> Known = [Agris];
}

/// <summary>
/// A location's identifier in some other system of record.
/// </summary>
/// <remarks>
/// This application is the system of record for telecom-specific location detail. These
/// identifiers exist so a future read-only integration with a facility master can match
/// deterministically on an ID rather than on a name — and so that a location which has no
/// counterpart in that system is representable rather than awkward.
/// </remarks>
public class LocationExternalIdentifier : AuditableEntity
{
    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    /// <summary>A value from <see cref="ExternalLocationSystems"/>, or any other system name.</summary>
    public required string SystemKey { get; set; }

    public required string Value { get; set; }

    public string? Notes { get; set; }
}

/// <summary>A postal address. Owned by its parent — no table of its own.</summary>
public class Address
{
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }

    /// <summary>State, province, or region. Not validated against a list — this has to
    /// work for a Canadian province and a UK county without a schema change.</summary>
    public string? StateOrProvince { get; set; }

    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = "US";

    public string SingleLine =>
        string.Join(", ", new[] { Line1, Line2, City, StateOrProvince, PostalCode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}

public class Region
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public ICollection<Location> Locations { get; set; } = [];
}

public class BusinessUnit
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public ICollection<Location> Locations { get; set; } = [];
}

public class CostCenter
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? GlAccount { get; set; }
    public bool IsActive { get; set; } = true;
}
