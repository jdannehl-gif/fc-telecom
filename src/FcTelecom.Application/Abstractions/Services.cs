using FcTelecom.Domain.Platform;

namespace FcTelecom.Application.Abstractions;

/// <summary>
/// Who is making the current request, and what they are allowed to do.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? UserPrincipalName { get; }
    string? DisplayName { get; }
    bool IsAuthenticated { get; }
    IReadOnlySet<string> Permissions { get; }
    Guid CorrelationId { get; }
    string? IpAddress { get; }

    bool Has(string permission) => Permissions.Contains(permission);
}

/// <summary>
/// The clock, injected rather than called statically.
/// </summary>
/// <remarks>
/// Every calculation that depends on "now" — notice deadlines, outage durations,
/// availability windows, staleness — takes time as an input. That is what makes those
/// calculations testable at a month boundary, across a DST transition, and at the exact
/// instant a threshold is crossed, instead of only at whatever moment the test happened
/// to run.
/// </remarks>
public interface IClock
{
    DateTime UtcNow { get; }

    DateOnly Today => DateOnly.FromDateTime(UtcNow);
}

/// <summary>
/// Encrypts and decrypts the static IP fields, and produces the deterministic search hash.
/// </summary>
/// <remarks>
/// Backed by a Key Vault key in Azure. The point of application-level encryption on this
/// one table is that a database backup, a read-replica, or the reporting SQL principal
/// yields ciphertext — a copy of the database is not a copy of the organisation's public
/// address map.
/// </remarks>
public interface IFieldEncryptor
{
    string Encrypt(string plaintext);

    string Decrypt(string ciphertext);

    /// <summary>
    /// Deterministic HMAC over a normalised value, for exact-match search without decryption.
    /// </summary>
    /// <remarks>
    /// Deterministic hashing leaks equality — someone with database access can tell that
    /// two rows hold the same block. That is an accepted, bounded compromise: it is what
    /// makes "find the circuit with this IP" work during an outage, and range queries are
    /// resolved in memory over rows the caller may already see.
    /// </remarks>
    byte[] ComputeSearchHash(string value);
}

public sealed record DocumentUploadRequest(
    string FileName,
    string ContentType,
    Stream Content,
    string OwnerEntityType,
    string OwnerEntityId);

/// <summary>Blob-backed document storage.</summary>
public interface IDocumentStore
{
    Task<string> UploadAsync(DocumentUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A short-lived, single-use-ish download URL.
    /// </summary>
    /// <remarks>
    /// There is no permanent URL anywhere in this system. A user-delegation SAS with a
    /// few minutes of life means a link that leaks out of an email thread is already dead,
    /// and every issuance is attributable.
    /// </remarks>
    Task<Uri> GetDownloadUriAsync(string blobPath, TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default);
}

public sealed record NotificationMessage(
    string Subject,
    string BodyMarkdown,
    IReadOnlyList<string> Recipients,
    string? DeepLinkUrl = null);

/// <summary>Sends an outbound message. Implemented per channel: Graph mail, Teams, webhook.</summary>
public interface INotificationSender
{
    string Channel { get; }

    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Writes a security event. Separate from audit: audit is "what changed", this is "who looked".</summary>
public interface ISecurityEventLogger
{
    Task LogAsync(
        SecurityEventType eventType,
        string? detail,
        CancellationToken cancellationToken = default);
}

/// <summary>Produces .xlsx from tabular data, with CSV-injection escaping applied.</summary>
public interface IExcelExporter
{
    /// <summary>
    /// Builds a workbook from rows of values.
    /// </summary>
    /// <remarks>
    /// Cell values beginning <c>=</c>, <c>+</c>, <c>-</c>, or <c>@</c> are prefixed with an
    /// apostrophe. A contract note containing <c>=HYPERLINK(...)</c> should not execute
    /// when someone in Finance opens the export.
    /// </remarks>
    byte[] Build(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows);
}
