using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Services;

public enum AddressFamily { IPv4 = 4, IPv6 = 6 }

/// <summary>
/// Static addressing for a service. <b>The most sensitive table in the schema.</b>
/// </summary>
/// <remarks>
/// Taken together with <c>Location</c>, this table is a map of the organisation's public
/// attack surface cross-referenced to physical addresses and criticality ratings. It is
/// a reconnaissance document, and it is treated as one:
/// <list type="bullet">
/// <item>Values are encrypted at the application layer with a Key Vault–backed key, so a
/// database copy, a read-replica, or a reporting connection yields ciphertext.</item>
/// <item>Reading requires the <c>ServiceIpData.Read</c> permission, which no role implies
/// by default and which is separately grantable per user.</item>
/// <item>Query handlers project these fields out entirely when the caller lacks the
/// permission — the value never enters a DTO, so it never reaches the render tree.
/// Masking in the UI is a decoration, not a control.</item>
/// <item>Revealing a value writes a <c>SecurityEvent</c> attributing who, what, and when.</item>
/// </list>
/// <para>
/// The encrypted properties are named <c>*Encrypted</c> so that a developer writing a new
/// query cannot casually select one without noticing what it is.
/// </para>
/// </remarks>
public class ServiceIpAssignment : AuditableEntity
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public AddressFamily AddressFamily { get; set; } = AddressFamily.IPv4;

    /// <summary>Ciphertext of the CIDR block, e.g. <c>203.0.113.8/29</c>.</summary>
    public required string CidrEncrypted { get; set; }

    public string? GatewayEncrypted { get; set; }
    public string? UsableFirstEncrypted { get; set; }
    public string? UsableLastEncrypted { get; set; }
    public string? DnsPrimaryEncrypted { get; set; }
    public string? DnsSecondaryEncrypted { get; set; }

    /// <summary>
    /// True for a block routed to you behind the WAN interface, false for the WAN
    /// interface subnet itself. The distinction matters when reconstructing a config.
    /// </summary>
    public bool IsRoutedBlock { get; set; }

    /// <summary>
    /// Deterministic HMAC of the normalised CIDR string, used to support exact-match
    /// search without decrypting every row.
    /// </summary>
    /// <remarks>
    /// This is a deliberate, bounded compromise. A deterministic hash leaks equality —
    /// an attacker with database access can tell that two services share a block, and can
    /// confirm a guessed block by computing its hash, though only if they also hold the
    /// HMAC key. Range and CIDR-contains queries are therefore resolved in memory over
    /// the rows the caller is already authorised to see. Exact match is what an engineer
    /// actually needs at 2am, and it is the only thing this index supports.
    /// </remarks>
    public byte[]? CidrSearchHash { get; set; }

    public string? AssignmentNotes { get; set; }
}

/// <summary>
/// A known shared dependency between two services — the reason a "backup" may not be one.
/// </summary>
/// <remarks>
/// Two circuits from two different carriers at the same address routinely share the
/// last-mile provider, the conduit into the building, the cell tower, or an upstream
/// transit provider. A fibre cut, a flooded vault, or a tower outage then takes both,
/// and the organisation discovers its redundancy was notional at the worst possible moment.
/// </remarks>
public class ServiceDependency : AuditableEntity
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public Guid DependsOnServiceId { get; set; }
    public TelecomService DependsOnService { get; set; } = null!;

    public DependencyType DependencyType { get; set; }

    /// <summary>
    /// How sure you are. Reporting treats anything that is not <see cref="DependencyConfidence.RuledOut"/>
    /// as a diversity risk, because the safe default when you do not know is to assume
    /// the backup is not diverse.
    /// </summary>
    public DependencyConfidence Confidence { get; set; } = DependencyConfidence.Suspected;

    /// <summary>
    /// How you know. "LOA dated 2026-03-11 shows both on Everstream fibre" is actionable;
    /// an unsourced warning gets dismissed the second time somebody sees it.
    /// </summary>
    public string? Evidence { get; set; }

    public DateOnly? AssessedOn { get; set; }
    public string? Notes { get; set; }
}

public enum DependencyType
{
    SharedLastMile = 1,
    SharedConduit = 2,
    SharedBuildingEntrance = 3,
    SharedTower = 4,
    SharedUpstreamTransit = 5,
    SharedCpe = 6,
    SharedPowerCircuit = 7,
    Other = 99,
}

public enum DependencyConfidence
{
    /// <summary>Believed shared, not verified. Treated as a risk.</summary>
    Suspected = 1,

    /// <summary>Verified shared. Definitely a risk.</summary>
    Confirmed = 2,

    /// <summary>Investigated and found genuinely independent. The only state that clears the flag.</summary>
    RuledOut = 3,
}
