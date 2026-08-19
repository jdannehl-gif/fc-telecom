using FcTelecom.Application.Abstractions;
using FcTelecom.Domain.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace FcTelecom.Infrastructure.Common;

/// <summary>The real clock. The only place in the solution that reads the wall time.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Writes security events on their own <c>DbContext</c>, outside any ambient transaction.
/// </summary>
/// <remarks>
/// <para>
/// A security event must be recorded even when the operation that triggered it fails and
/// rolls back. An authorization denial, a rejected agent signature, and a failed export
/// attempt are all exactly the cases where the surrounding transaction does not commit —
/// and all three are things an investigator needs to see.
/// </para>
/// <para>
/// A fresh DI scope is used rather than an <c>IDbContextFactory</c>. Registering both
/// <c>AddDbContext</c> and <c>AddDbContextFactory</c> for the same context type registers
/// <c>DbContextOptions&lt;T&gt;</c> twice with different lifetimes, and the resulting
/// resolution depends on registration order — a subtle failure that appears only under
/// scope validation. A scope costs nothing here and has one obvious meaning.
/// </para>
/// </remarks>
public sealed class SecurityEventLogger(
    IServiceScopeFactory scopeFactory,
    ICurrentUser currentUser,
    IClock clock) : ISecurityEventLogger
{
    public async Task LogAsync(
        SecurityEventType eventType, string? detail, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        Persistence.ApplicationDbContext context =
            scope.ServiceProvider.GetRequiredService<Persistence.ApplicationDbContext>();

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
