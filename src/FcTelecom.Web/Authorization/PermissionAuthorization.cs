using System.Security.Claims;
using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Domain.Platform;
using FcTelecom.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Web.Authorization;

/// <summary>The claim type carrying a granted permission.</summary>
public static class FcClaimTypes
{
    public const string Permission = "fc:permission";
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        bool granted = context.User.Claims.Any(claim =>
            claim.Type == FcClaimTypes.Permission &&
            string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal));

        if (granted)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationRegistration
{
    /// <summary>
    /// Registers one policy per permission, plus a fallback that requires authentication.
    /// </summary>
    /// <remarks>
    /// Policies are generated from <see cref="Permissions.All"/> rather than written by
    /// hand, so adding a permission cannot leave a policy missing. The fallback policy is
    /// the important half: a page or endpoint with no <c>[Authorize]</c> attribute fails
    /// closed instead of being silently anonymous, which is the single most common way a
    /// carefully-designed authorization model gets a hole in it.
    /// </remarks>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // Fail closed. An endpoint or page that nobody remembered to decorate requires
            // authentication rather than being silently anonymous.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            foreach (string permission in Permissions.All)
            {
                options.AddPolicy(permission, policy =>
                    policy.Requirements.Add(new PermissionRequirement(permission)));
            }

            // The probe agent authenticates as an application, not a person, and holds one
            // app role. Kept as a separate policy so a person can never satisfy it and an
            // agent token can never satisfy a user policy.
            options.AddPolicy(Permissions.ProbeSubmit, policy =>
                policy.RequireClaim("roles", "Probe.Submit"));
        });

        return services;
    }
}

/// <summary>
/// Turns Entra group membership into permission claims at sign-in.
/// </summary>
/// <remarks>
/// <para>
/// Mapping is data, not configuration: <c>EntraGroupRoleMaps</c> holds group object ID →
/// role, and <c>RolePermissions</c> holds role → permission. An administrator changes
/// access in the UI, not in an app manifest followed by a redeploy.
/// </para>
/// <para>
/// Group <b>object IDs</b> are the matching key. Display names are cached for readability
/// only — matching on a name means a group rename silently revokes everyone's access, or
/// silently grants it to whichever different group inherits the name.
/// </para>
/// </remarks>
public sealed class PermissionClaimsEnricher(
    ApplicationDbContext db,
    IClock clock,
    ILogger<PermissionClaimsEnricher> logger)
{
    public async Task<IReadOnlyList<Claim>> BuildClaimsAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? objectId = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        string? upn = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;

        if (string.IsNullOrWhiteSpace(objectId))
        {
            logger.LogWarning("Signed-in principal has no object ID claim; no permissions granted.");
            return [];
        }

        AppUser user = await UpsertUserAsync(objectId, upn, principal, cancellationToken)
            .ConfigureAwait(false);

        if (!user.IsActive)
        {
            logger.LogWarning("User {Upn} is deactivated; no permissions granted.", upn);
            return [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())];
        }

        // Group object IDs from the token. Entra emits these as "groups" claims when the
        // app registration requests them; large group memberships fall back to a Graph
        // call, which is why AAD's "groups overage" claim is checked too.
        var groupIds = principal.FindAll("groups").Select(claim => claim.Value).ToList();

        if (principal.HasClaim(claim => claim.Type == "_claim_names"))
        {
            logger.LogWarning(
                "Group claims overflowed the token for {Upn}. Permissions from group membership " +
                "may be incomplete until Graph-based group resolution is enabled.", upn);
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);

        if (groupIds.Count > 0)
        {
            List<string> fromGroups = await db.EntraGroupRoleMaps
                .Where(map => map.Enabled && groupIds.Contains(map.EntraGroupObjectId))
                .SelectMany(map => map.Role.Permissions.Select(permission => permission.Permission))
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            permissions.UnionWith(fromGroups);
        }

        // Direct role assignments, for users managed in-app rather than by group.
        List<string> fromRoles = await db.RoleAssignments
            .Where(assignment => assignment.UserId == user.Id)
            .SelectMany(assignment => assignment.Role.Permissions.Select(permission => permission.Permission))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        permissions.UnionWith(fromRoles);

        // Individual grants — the "unless separately authorized" path. Expired grants are
        // filtered here rather than cleaned up by a job, so an expiry takes effect at the
        // next sign-in with no moving parts.
        DateTime now = clock.UtcNow;

        List<string> direct = await db.UserPermissionGrants
            .Where(grant => grant.UserId == user.Id &&
                            (grant.ExpiresUtc == null || grant.ExpiresUtc > now))
            .Select(grant => grant.Permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        permissions.UnionWith(direct);

        var claims = new List<Claim>(permissions.Count + 1)
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };

        claims.AddRange(permissions.Select(permission => new Claim(FcClaimTypes.Permission, permission)));

        // Guarded rather than called unconditionally. CA1873 treats property reads in a
        // logging call as potentially expensive — it cannot know that Count on a HashSet and
        // a List are O(1). Here they are, so the guard buys nothing at runtime, but leaving
        // the rule enabled means it still fires the day someone logs the result of an actual
        // query. The two lines are worth keeping that.
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Resolved {Count} permission(s) for {Upn} from {GroupCount} group(s).",
                permissions.Count, upn, groupIds.Count);
        }

        return claims;
    }

    private async Task<AppUser> UpsertUserAsync(
        string objectId, string? upn, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        AppUser? user = await db.Users
            .FirstOrDefaultAsync(item => item.EntraObjectId == objectId, cancellationToken)
            .ConfigureAwait(false);

        string displayName = principal.FindFirst("name")?.Value ?? upn ?? objectId;

        if (user is null)
        {
            user = new AppUser
            {
                EntraObjectId = objectId,
                UserPrincipalName = upn ?? objectId,
                DisplayName = displayName,
                Email = principal.FindFirst(ClaimTypes.Email)?.Value ?? upn,
                IsActive = true,
            };

            db.Users.Add(user);
        }
        else
        {
            // Keep the cached copy current. The object ID never changes; everything else can.
            user.UserPrincipalName = upn ?? user.UserPrincipalName;
            user.DisplayName = displayName;
        }

        user.LastLoginUtc = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }
}
