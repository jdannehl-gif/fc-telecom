using FcTelecom.Domain.Common;
using FcTelecom.Domain.Vendors;

namespace FcTelecom.Domain.Organization;

public enum ContactKind
{
    /// <summary>Someone at one of your locations.</summary>
    Internal = 1,

    /// <summary>Vendor-side: billing.</summary>
    VendorBilling = 2,

    /// <summary>Vendor-side: sales or account management.</summary>
    VendorSales = 3,

    /// <summary>Vendor-side: front-line technical support.</summary>
    VendorSupport = 4,

    /// <summary>Vendor-side: NOC or escalation. The number you want at 2am.</summary>
    VendorNocEscalation = 5,

    Other = 99,
}

/// <summary>
/// A person. Shared between locations and vendors rather than duplicated per context,
/// because the same account manager covers eleven sites and nobody wants to update
/// eleven records when their mobile number changes.
/// </summary>
public class Contact : AuditableEntity
{
    public required string FullName { get; set; }
    public string? JobTitle { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? MobileNumber { get; set; }
    public ContactKind Kind { get; set; } = ContactKind.Internal;

    /// <summary>Set for vendor-side contacts; null for internal ones.</summary>
    public Guid? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>
    /// 1 = first call, 2 = second, and so on. Null means "not part of an escalation path".
    /// Kept as a plain integer because carrier escalation paths are simple lists, and
    /// modelling them as a graph would be effort spent on a problem nobody has.
    /// </summary>
    public int? EscalationLevel { get; set; }

    public string? Notes { get; set; }

    public ICollection<LocationContact> Locations { get; set; } = [];

    public string Display => string.IsNullOrWhiteSpace(JobTitle) ? FullName : $"{FullName} ({JobTitle})";
}

/// <summary>Join between a location and a contact, carrying the role they play there.</summary>
public class LocationContact
{
    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    /// <summary>"Site manager", "Facilities", "After-hours key holder" — free text on purpose.</summary>
    public required string RoleAtLocation { get; set; }

    public bool IsPrimary { get; set; }
}
