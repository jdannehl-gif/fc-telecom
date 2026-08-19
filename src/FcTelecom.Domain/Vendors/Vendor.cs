using FcTelecom.Domain.Common;
using FcTelecom.Domain.Organization;

namespace FcTelecom.Domain.Vendors;

/// <summary>
/// What role a company plays. A single company frequently plays several — Lumen is a
/// carrier for one circuit and the last-mile provider for a competitor's circuit at the
/// same address — which is why this is a flags enum rather than a single value.
/// </summary>
[Flags]
public enum VendorKind
{
    None = 0,
    Carrier = 1,
    Reseller = 2,
    LastMileProvider = 4,
    EquipmentSupplier = 8,
    ManagedServiceProvider = 16,
    Other = 32,
}

/// <summary>
/// A company you buy from, escalate to, or depend on.
/// </summary>
public class Vendor : AuditableEntity
{
    public required string LegalName { get; set; }

    /// <summary>What people actually call them. Used everywhere in the UI.</summary>
    public required string DisplayName { get; set; }

    public VendorKind Kind { get; set; } = VendorKind.Carrier;

    public string? PortalUrl { get; set; }
    public string? MainSupportPhone { get; set; }
    public string? SupportHours { get; set; }

    /// <summary>
    /// A pointer to where the portal credentials live — "1Password → Carriers → Lumen"
    /// or an IT Glue password record ID.
    /// <para>
    /// This is a reference, never a credential. Storing vendor portal passwords here is
    /// an explicit guardrail violation: this database is read by reporting principals,
    /// exported to Excel, and backed up to places a password should never reach.
    /// </para>
    /// </summary>
    public string? CredentialReference { get; set; }

    /// <summary>Optional IT Glue password record ID, so the UI can deep-link there.</summary>
    public string? ItGluePasswordRecordId { get; set; }

    public string? Notes { get; set; }

    public ICollection<VendorAccount> Accounts { get; set; } = [];
    public ICollection<Contact> Contacts { get; set; } = [];
    public ICollection<VendorTicketProcedure> TicketProcedures { get; set; } = [];

    public bool IsCarrier => Kind.HasFlag(VendorKind.Carrier);
}

/// <summary>
/// A billing account with a vendor. Account numbers live here, not on the service,
/// because one account covers many circuits and carriers bill at the account level.
/// </summary>
public class VendorAccount : AuditableEntity
{
    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public required string AccountNumber { get; set; }

    /// <summary>
    /// Some carriers use a separate billing account number (BAN) alongside the
    /// customer account number, and support asks for one while Finance asks for the other.
    /// </summary>
    public string? BillingAccountNumber { get; set; }

    public string? Description { get; set; }

    public Guid? BillingContactId { get; set; }
    public Contact? BillingContact { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// How to open a ticket with this vendor, and what they will ask for.
/// </summary>
/// <remarks>
/// This exists because every carrier's process differs and the knowledge otherwise lives
/// in one engineer's head. Written down, it means whoever is on call can open a ticket
/// correctly the first time.
/// </remarks>
public class VendorTicketProcedure : AuditableEntity
{
    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    /// <summary>"Fiber outage", "Billing dispute", "Move/add/change" — scopes the procedure.</summary>
    public required string ScenarioName { get; set; }

    public string? PhoneNumber { get; set; }
    public string? PortalUrl { get; set; }
    public string? EmailAddress { get; set; }
    public string? HoursOfOperation { get; set; }

    /// <summary>Step-by-step. Markdown is rendered in the UI.</summary>
    public string? Procedure { get; set; }

    /// <summary>
    /// What they will ask for before they will help — circuit ID, account number, a PIN,
    /// the site contact's name. Populating this is what turns a fifteen-minute call
    /// into a five-minute one.
    /// </summary>
    public string? RequiredInformation { get; set; }

    /// <summary>Typical first-response commitment, for setting expectations during an outage.</summary>
    public string? ExpectedResponseTime { get; set; }
}
