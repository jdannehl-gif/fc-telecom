using System.Security.Claims;
using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;

namespace FcTelecom.Web.Authorization;

/// <summary>
/// <see cref="ICurrentUser"/> backed by the ambient HTTP context.
/// </summary>
/// <remarks>
/// Permissions are read from claims materialised at sign-in, not queried per request.
/// A permission change therefore takes effect at the user's next sign-in rather than
/// instantly — an acceptable trade for not issuing several database round trips on every
/// page render, and documented here so nobody is surprised when a freshly-granted
/// permission does not appear until re-authentication.
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private IReadOnlySet<string>? _permissions;
    private Guid? _correlationId;

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out Guid id) ? id : null;

    public string? UserPrincipalName =>
        Principal?.FindFirst("preferred_username")?.Value
        ?? Principal?.FindFirst(ClaimTypes.Upn)?.Value
        ?? Principal?.FindFirst(ClaimTypes.Email)?.Value;

    public string? DisplayName =>
        Principal?.FindFirst("name")?.Value ?? UserPrincipalName;

    public IReadOnlySet<string> Permissions =>
        _permissions ??= Principal is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                Principal.FindAll(FcClaimTypes.Permission).Select(claim => claim.Value),
                StringComparer.Ordinal);

    /// <summary>
    /// Ties everything done in this request together — the audit entries, the security
    /// events, the log lines, and any outbox message the request produced, which is later
    /// processed in a different process entirely.
    /// </summary>
    public Guid CorrelationId =>
        _correlationId ??= accessor.HttpContext?.TraceIdentifier is { } trace &&
                           Guid.TryParse(trace, out Guid parsed)
            ? parsed
            : Guid.NewGuid();

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// A fixed identity for background work, where there is no signed-in user.
/// </summary>
/// <remarks>
/// Holds every permission, because a timer-triggered job legitimately needs to read costs,
/// contracts, and monitoring data. It is a distinct implementation rather than a nullable
/// <see cref="ICurrentUser"/> so that audit entries written by background work are
/// attributed to "System" instead of to nobody — an unattributed change in the audit trail
/// is the kind of thing that costs an hour during an investigation.
/// </remarks>
public sealed class SystemCurrentUser : ICurrentUser
{
    public static readonly Guid SystemUserId = new("00000000-0000-0000-0000-000000000001");

    public Guid? UserId => SystemUserId;

    public string? UserPrincipalName => "system@fctelecom";

    public string? DisplayName => "System";

    public bool IsAuthenticated => true;

    // Fully qualified: the property name shadows the imported Permissions class.
    public IReadOnlySet<string> Permissions { get; } =
        new HashSet<string>(FcTelecom.Application.Authorization.Permissions.All, StringComparer.Ordinal);

    public Guid CorrelationId { get; } = Guid.NewGuid();

    public string? IpAddress => null;
}
