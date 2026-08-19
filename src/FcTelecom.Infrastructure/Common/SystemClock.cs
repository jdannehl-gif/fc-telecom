using FcTelecom.Application.Abstractions;
using FcTelecom.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace FcTelecom.Infrastructure.Common;

/// <summary>The real clock. The only place in the solution that reads the wall time.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Writes security events straight through, outside any ambient transaction.
/// </summary>
/// <remarks>
/// This uses its own <c>DbContext</c> scope deliberately. A security event must be
/// recorded even when the operation that triggered it fails and rolls back — an
/// authorization denial, a rejected agent signature, and a failed export attempt are all
/// exactly the cases where the surrounding transaction does not commit, and all three are
/// things an investigator needs to see.
/// </remarks>
public sealed class SecurityEventLogger(
    IDbContextFactory<Persistence.ApplicationDbContext> contextFactory,
    ICurrentUser currentUser,
    IClock clock) : ISecurityEventLogger
{
    public async Task LogAsync(
        SecurityEventType eventType, string? detail, CancellationToken cancellationToken = default)
    {
        await using Persistence.ApplicationDbContext context =
            await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.SecurityEvents.Add(new SecurityEvent
        {
            OccurredUtc = clock.UtcNow,
            EventType = eventType,
            ActorUserId = currentUser.UserId,
            ActorUpn = currentUser.UserPrincipalName,
            Detail = Truncate(detail, 2000),
            CorrelationId = currentUser.CorrelationId,
            IpAddress = currentUser.IpAddress,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
