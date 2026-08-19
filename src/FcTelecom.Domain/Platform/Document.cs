using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Platform;

public enum DocumentType
{
    Contract = 1, Amendment = 2, Invoice = 3, LetterOfAgency = 4,
    NetworkDiagram = 5, InstallDocument = 6, Photo = 7,
    Correspondence = 8, Quote = 9, DisconnectNotice = 10, Other = 99,
}

public enum DocumentSensitivity
{
    Normal = 1,

    /// <summary>Requires elevated permission to download, and the download is logged.</summary>
    Restricted = 2,
}

/// <summary>
/// A file in Blob Storage, attached to some entity.
/// </summary>
/// <remarks>
/// The owner is a polymorphic pair rather than a foreign key per entity type, because
/// documents attach to locations, services, contracts, vendors, and invoices, and five
/// nullable FK columns on one table is worse than a discriminator.
/// <para>
/// There is no URL column. Downloads are served through a per-request user-delegation SAS
/// with a short TTL, so there is no permanent link that can be shared or leaked, and every
/// download writes a <c>SecurityEvent</c>.
/// </para>
/// </remarks>
public class Document : AuditableEntity
{
    /// <summary>Entity type name: <c>Location</c>, <c>TelecomService</c>, <c>Contract</c>, …</summary>
    public required string OwnerEntityType { get; set; }

    public required string OwnerEntityId { get; set; }

    public DocumentType DocumentType { get; set; } = DocumentType.Other;
    public required string FileName { get; set; }

    /// <summary>Container-relative path. Not a URL.</summary>
    public required string BlobPath { get; set; }

    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Content hash, for integrity checking and for detecting duplicate uploads.</summary>
    public string? Sha256 { get; set; }

    public DocumentSensitivity Sensitivity { get; set; } = DocumentSensitivity.Normal;
    public string? Description { get; set; }

    public Guid UploadedByUserId { get; set; }
    public DateTime UploadedUtc { get; set; }
}

/// <summary>
/// A saved combination of filters and columns for a list view.
/// </summary>
public class SavedView : AuditableEntity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>Which list this applies to: <c>Services</c>, <c>Contracts</c>, …</summary>
    public required string EntityType { get; set; }

    public required string Name { get; set; }
    public string FilterJson { get; set; } = "{}";
    public string ColumnsJson { get; set; } = "[]";

    /// <summary>Shared views are visible to everyone but editable only by their owner.</summary>
    public bool IsShared { get; set; }

    public bool IsDefault { get; set; }
}
