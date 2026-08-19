using FcTelecom.Domain.Common;

namespace FcTelecom.Domain.Platform;

/// <summary>
/// A person who can sign in. There is no password column and never will be — Entra ID
/// is the only authentication path, so there is no local credential store to attack.
/// </summary>
public class AppUser : BaseEntity
{
    /// <summary>
    /// The Entra object ID. <b>This is the identity key.</b> Not the UPN, which changes
    /// when someone marries, and not the email, which gets reassigned.
    /// </summary>
    public required string EntraObjectId { get; set; }

    public required string UserPrincipalName { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<RoleAssignment> RoleAssignments { get; set; } = [];
    public ICollection<UserPermissionGrant> DirectPermissions { get; set; } = [];
}

public class AppRole
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>System roles cannot be deleted, only have their permissions adjusted.</summary>
    public bool IsSystemRole { get; set; }

    public ICollection<RolePermissionGrant> Permissions { get; set; } = [];
    public ICollection<EntraGroupRoleMap> GroupMappings { get; set; } = [];
}

/// <summary>
/// A permission granted to a role — the counterpart of <see cref="UserPermissionGrant"/>,
/// which grants one to a single person.
/// </summary>
/// <remarks>
/// Named <c>RolePermissionGrant</c> rather than <c>RolePermission</c> for two reasons. It
/// pairs with <see cref="UserPermissionGrant"/>, so the two halves of the permission model
/// read the same way. And CA1711 reserves the <c>Permission</c> suffix for types deriving
/// from the old Code Access Security hierarchy, which this is not.
/// <para>
/// The table is still <c>RolePermissions</c> and the <c>DbSet</c> is still
/// <c>RolePermissions</c> — this is a type name, not a schema change.
/// </para>
/// </remarks>
public class RolePermissionGrant
{
    public int RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    /// <summary>A value from <c>Permissions</c> in the Application layer, e.g. <c>Services.Write</c>.</summary>
    public required string Permission { get; set; }
}

public class RoleAssignment
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public int RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    public DateTime AssignedUtc { get; set; }
    public Guid? AssignedByUserId { get; set; }
}

/// <summary>
/// A permission granted to one person independently of their role.
/// </summary>
/// <remarks>
/// This is the concrete form of the requirement that Procurement can be given access to
/// sensitive network detail "if separately authorized", without inventing a sixth role
/// every time such a request arrives. Granting one writes a <c>SecurityEvent</c>; so does
/// using it.
/// </remarks>
public class UserPermissionGrant
{
    public Guid Id { get; set; } = SequentialGuid.Create();

    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public required string Permission { get; set; }

    /// <summary>Why this exception exists. Required — an unexplained standing grant is a finding.</summary>
    public required string Justification { get; set; }

    public DateTime GrantedUtc { get; set; }
    public Guid? GrantedByUserId { get; set; }

    /// <summary>Null means indefinite, which access reviews should be suspicious of.</summary>
    public DateTime? ExpiresUtc { get; set; }

    public bool IsActive(DateTime utcNow) => ExpiresUtc is null || ExpiresUtc > utcNow;
}

/// <summary>
/// Maps an Entra security group to an application role.
/// </summary>
/// <remarks>
/// Keyed on the group's <b>object ID</b>. Display names are cached for the admin UI only
/// and are never used for matching — a group rename would otherwise silently revoke
/// everyone's access, or worse, silently grant it to a different group that got the name.
/// </remarks>
public class EntraGroupRoleMap : AuditableEntity
{
    public required string EntraGroupObjectId { get; set; }

    /// <summary>Cached for readability in the admin UI. Never a matching key.</summary>
    public string? EntraGroupDisplayName { get; set; }

    public int RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    public bool Enabled { get; set; } = true;
}

public enum AuditAction { Create = 1, Update = 2, Archive = 3, Restore = 4, Import = 5, Export = 6, Purge = 7 }

/// <summary>
/// One material change to business data. <b>Append-only, enforced at the database.</b>
/// </summary>
/// <remarks>
/// The application's SQL principal is granted INSERT and SELECT on this table and is
/// deliberately not granted UPDATE or DELETE. Application-level immutability can be
/// defeated by a bug; a missing grant cannot.
/// </remarks>
public class AuditEntry : IImmutableRecord
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }

    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// Denormalised on purpose. When someone leaves and their user row is deactivated,
    /// the audit trail must still say who did it in a form a human reads. Referential
    /// purity loses to forensic usefulness here.
    /// </summary>
    public string? ActorUpn { get; set; }

    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public AuditAction Action { get; set; }

    /// <summary>
    /// Per-property old and new values. Sensitive properties record
    /// <c>"[redacted]"</c> plus a changed flag rather than the values themselves — the
    /// audit log must show that the static IP block changed without becoming a second,
    /// unencrypted copy of it.
    /// </summary>
    public string? ChangesJson { get; set; }

    public Guid? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
}

public enum SecurityEventType
{
    SignIn = 1,
    SignInFailed = 2,
    AuthorizationDenied = 3,

    /// <summary>Someone revealed a masked sensitive field.</summary>
    SensitiveFieldRevealed = 4,

    ExportGenerated = 5,
    DocumentDownloaded = 6,
    PermissionGranted = 7,
    PermissionRevoked = 8,
    SecretRotated = 9,
    AgentAuthFailed = 10,
    AgentReplayRejected = 11,
    IntegrationSensitiveFieldEnabled = 12,
}

/// <summary>
/// A security-relevant event. Separate from <see cref="AuditEntry"/> because the audiences
/// and retention differ: audit answers "what changed", this answers "who looked".
/// </summary>
public class SecurityEvent : IImmutableRecord
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    public SecurityEventType EventType { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorUpn { get; set; }
    public string? Detail { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
}
