using System.Security.Cryptography;
using System.Text;
using FcTelecom.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FcTelecom.Infrastructure.Security;

public sealed class FieldEncryptionOptions
{
    public const string SectionName = "Security:FieldEncryption";

    /// <summary>Base64 256-bit AES key. In Azure this is a Key Vault reference; locally, user secrets.</summary>
    public string? EncryptionKeyBase64 { get; set; }

    /// <summary>Base64 256-bit HMAC key for the deterministic search hash. Distinct from the AES key.</summary>
    public string? SearchHashKeyBase64 { get; set; }
}

/// <summary>
/// AES-GCM encryption for the static IP fields, plus the deterministic HMAC used for
/// exact-match search.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why only this table.</b> Encrypting everything would break indexing, searching, and
/// reporting for no threat-model benefit. Encrypting the static IP inventory addresses one
/// specific risk: a database backup, a read-replica, or a reporting connection leaking a
/// map of the organisation's public attack surface, cross-referenced to physical addresses
/// and criticality ratings. That is a reconnaissance document, and it is worth the
/// inconvenience.
/// </para>
/// <para>
/// <b>Why AES-GCM.</b> Authenticated encryption. A tampered ciphertext fails to decrypt
/// rather than producing plausible garbage that gets read out to a carrier as a gateway address.
/// </para>
/// <para>
/// <b>Why a separate HMAC key.</b> Using the encryption key for the search hash would let
/// anyone who can compute a search hash learn something about the encryption key's use.
/// Two keys, two purposes, rotated independently.
/// </para>
/// <para>
/// <b>Ciphertext format.</b> <c>v1:</c> + base64(nonce ‖ tag ‖ ciphertext). The version
/// prefix exists so a future key rotation or algorithm change can decrypt old rows while
/// writing new ones — without it, rotation means a downtime window and a bulk rewrite.
/// </para>
/// </remarks>
public sealed class FieldEncryptor : IFieldEncryptor
{
    private const string Version = "v1";
    private const int NonceSize = 12;   // AES-GCM standard
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _searchHashKey;

    public FieldEncryptor(IOptions<FieldEncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FieldEncryptionOptions value = options.Value;

        _encryptionKey = DecodeKey(value.EncryptionKeyBase64, nameof(value.EncryptionKeyBase64));
        _searchHashKey = DecodeKey(value.SearchHashKeyBase64, nameof(value.SearchHashKeyBase64));

        if (_encryptionKey.SequenceEqual(_searchHashKey))
        {
            throw new InvalidOperationException(
                "The field-encryption key and the search-hash key must be different. Reusing " +
                "one key for both purposes weakens both.");
        }
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(_encryptionKey, TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        byte[] envelope = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, NonceSize);
        ciphertext.CopyTo(envelope, NonceSize + TagSize);

        return $"{Version}:{Convert.ToBase64String(envelope)}";
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);

        int separator = ciphertext.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new FormatException("Encrypted value is missing its version prefix.");
        }

        string version = ciphertext[..separator];
        if (version != Version)
        {
            throw new NotSupportedException(
                $"Encrypted value uses format '{version}', which this build cannot read. " +
                "A key rotation or algorithm change needs a decryptor for the old format.");
        }

        byte[] envelope = Convert.FromBase64String(ciphertext[(separator + 1)..]);

        if (envelope.Length < NonceSize + TagSize)
        {
            throw new FormatException("Encrypted value is truncated.");
        }

        ReadOnlySpan<byte> span = envelope;
        ReadOnlySpan<byte> nonce = span[..NonceSize];
        ReadOnlySpan<byte> tag = span.Slice(NonceSize, TagSize);
        ReadOnlySpan<byte> payload = span[(NonceSize + TagSize)..];

        byte[] plaintext = new byte[payload.Length];

        using (var aes = new AesGcm(_encryptionKey, TagSize))
        {
            // Throws CryptographicException if the tag does not verify — which is the
            // behaviour we want. A silently-wrong gateway address is worse than an error.
            aes.Decrypt(nonce, payload, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    public byte[] ComputeSearchHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Trim and upper-case only. No further normalisation here — CIDR normalisation
        // happens in GlobalSearchService before this is called, and doing it in two places
        // is how the write path and the read path drift apart.
        byte[] input = Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant());
        return HMACSHA256.HashData(_searchHashKey, input);
    }

    private static byte[] DecodeKey(string? base64, string settingName)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException(
                $"{FieldEncryptionOptions.SectionName}:{settingName} is not configured. " +
                "Generate one with: openssl rand -base64 32");
        }

        byte[] key = Convert.FromBase64String(base64);

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{FieldEncryptionOptions.SectionName}:{settingName} must be a 256-bit " +
                $"(32-byte) key; got {key.Length} bytes.");
        }

        return key;
    }
}
