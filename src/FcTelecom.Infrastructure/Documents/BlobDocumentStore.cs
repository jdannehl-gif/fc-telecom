using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using FcTelecom.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FcTelecom.Infrastructure.Documents;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "Documents";

    public string ContainerName { get; set; } = "documents";

    /// <summary>How long a download link lives. Short on purpose — see the class remarks.</summary>
    public int SasLifetimeMinutes { get; set; } = 5;
}

/// <summary>
/// Documents in Azure Blob Storage, served through short-lived user-delegation SAS URLs.
/// </summary>
/// <remarks>
/// <para>
/// There is no permanent URL anywhere in this system, and the <c>Document</c> entity has
/// no URL column. Every download mints a fresh SAS with a few minutes of life. A link
/// forwarded out of an email thread, pasted into a ticket, or left in a browser history
/// is already dead by the time anyone tries it.
/// </para>
/// <para>
/// <b>User-delegation SAS, not account-key SAS.</b> A user-delegation SAS is signed with a
/// key obtained from Entra ID using the application's managed identity, so it inherits that
/// identity's permissions and can be revoked centrally. An account-key SAS requires the
/// storage account key to exist somewhere the application can read it, which is precisely
/// the thing we are trying not to have.
/// </para>
/// </remarks>
public sealed class BlobDocumentStore(
    BlobServiceClient blobServiceClient,
    IOptions<DocumentStorageOptions> options) : IDocumentStore
{
    private readonly DocumentStorageOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<string> UploadAsync(
        DocumentUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        BlobContainerClient container = blobServiceClient.GetBlobContainerClient(_options.ContainerName);

        // PublicAccessType.None explicitly. A container that is accidentally public is a
        // data breach with no attacker required, and the default is worth stating out loud.
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Path shape: owner-type/owner-id/guid-filename. The GUID prevents two uploads of
        // "contract.pdf" from overwriting each other, and keeps the original name readable.
        string safeFileName = SanitizeFileName(request.FileName);
        string blobPath = $"{request.OwnerEntityType}/{request.OwnerEntityId}/{Guid.NewGuid():N}-{safeFileName}";

        BlobClient blob = container.GetBlobClient(blobPath);

        await blob.UploadAsync(
            request.Content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = request.ContentType },
            },
            cancellationToken).ConfigureAwait(false);

        return blobPath;
    }

    public async Task<Uri> GetDownloadUriAsync(
        string blobPath, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        BlobClient blob = blobServiceClient
            .GetBlobContainerClient(_options.ContainerName)
            .GetBlobClient(blobPath);

        DateTimeOffset expiresOn = DateTimeOffset.UtcNow.Add(lifetime);

        // Start the window slightly in the past to absorb clock skew between the app tier
        // and storage. Without it, a link occasionally arrives "not yet valid".
        DateTimeOffset startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);

        Azure.Storage.Blobs.Models.UserDelegationKey delegationKey =
            await blobServiceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken)
                .ConfigureAwait(false);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = blobPath,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            Protocol = SasProtocol.Https,
        };

        builder.SetPermissions(BlobSasPermissions.Read);

        var uri = new UriBuilder(blob.Uri)
        {
            Query = builder.ToSasQueryParameters(delegationKey, blobServiceClient.AccountName).ToString(),
        };

        return uri.Uri;
    }

    public async Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        await blobServiceClient
            .GetBlobContainerClient(_options.ContainerName)
            .GetBlobClient(blobPath)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Strips path separators and control characters from an uploaded file name.
    /// </summary>
    /// <remarks>
    /// A file named <c>../../secrets.pdf</c> must not be able to write outside its prefix.
    /// Blob storage is flat and would treat the slashes as virtual directories rather than
    /// traversal, but relying on that is relying on a detail of one storage backend.
    /// </remarks>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "file";
        }

        string name = Path.GetFileName(fileName);
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new([.. name.Where(character => !invalid.Contains(character) && !char.IsControl(character))]);

        cleaned = cleaned.Replace("/", string.Empty, StringComparison.Ordinal)
                         .Replace("\\", string.Empty, StringComparison.Ordinal)
                         .Trim();

        if (cleaned.Length == 0)
        {
            return "file";
        }

        return cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }
}
