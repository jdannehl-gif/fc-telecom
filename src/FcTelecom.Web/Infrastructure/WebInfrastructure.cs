using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using FcTelecom.Application.Abstractions;
using FcTelecom.Domain.Monitoring;
using FcTelecom.Domain.Notifications;
using FcTelecom.Domain.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog.Core;
using Serilog.Events;

namespace FcTelecom.Web.Infrastructure;

/// <summary>
/// Response security headers.
/// </summary>
/// <remarks>
/// The CSP is the fiddly one. Blazor Server needs <c>wasm-unsafe-eval</c> for its
/// interop and a small inline bootstrap script, but it does not need blanket
/// <c>unsafe-inline</c> — which is what most Blazor CSP examples reach for and which
/// gives away most of the protection.
/// </remarks>
public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            headers["Content-Security-Policy"] = string.Join("; ",
                "default-src 'self'",
                "script-src 'self' 'wasm-unsafe-eval'",
                "style-src 'self'",
                "img-src 'self' data:",
                "font-src 'self'",
                // Blazor Server's circuit is a WebSocket back to this origin, and nothing else.
                "connect-src 'self' wss: https://login.microsoftonline.com",
                "frame-ancestors 'none'",
                "form-action 'self' https://login.microsoftonline.com",
                "base-uri 'self'",
                "object-src 'none'");

            // Server banners tell an attacker which exploits to skip. Not a control on its
            // own; free to remove.
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            await next().ConfigureAwait(false);
        });
    }
}

public static class RateLimitingRegistration
{
    /// <summary>
    /// Rate limits, partitioned by identity where there is one and by IP where there is not.
    /// </summary>
    /// <remarks>
    /// Three separate policies because the surfaces have genuinely different shapes: a
    /// person clicking around, an agent posting batches every minute, and an export or
    /// import that is expensive per call. One global limit tuned for any of those is wrong
    /// for the other two.
    /// </remarks>
    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.User.Identity?.Name
                                  ?? context.Connection.RemoteIpAddress?.ToString()
                                  ?? "anonymous",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimits:PerUserPerMinute", 600),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.AddPolicy("agent", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.User.FindFirst("appid")?.Value
                                  ?? context.Connection.RemoteIpAddress?.ToString()
                                  ?? "unknown-agent",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimits:AgentPerMinute", 120),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // Exports and imports are deliberately stingy. Each one can produce megabytes
            // and scan a lot of rows, and nobody legitimately needs ten a minute.
            options.AddPolicy("expensive", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.User.Identity?.Name ?? "anonymous",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimits:ExpensivePerMinute", 6),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }
}

/// <summary>
/// Strips sensitive values from log events before they leave the process.
/// </summary>
/// <remarks>
/// Logging is the most common accidental disclosure channel in an application like this:
/// somebody adds a <c>logger.LogDebug("Saving {@Assignment}", assignment)</c> while
/// debugging, it survives review, and the static IP inventory ends up in Application
/// Insights where a much wider group can read it. This policy makes that specific mistake
/// harmless.
/// </remarks>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CidrEncrypted", "GatewayEncrypted", "UsableFirstEncrypted", "UsableLastEncrypted",
        "DnsPrimaryEncrypted", "DnsSecondaryEncrypted", "CidrSearchHash",
        "Cidr", "Gateway", "UsableFirst", "UsableLast",
        "ApiKey", "ApiKeySecretName", "Token", "AccessToken", "Password", "Secret",
        "HmacKeyVaultSecretName", "CredentialReference", "Authorization",
        "EncryptionKeyBase64", "SearchHashKeyBase64", "PayloadJson", "ConnectionString",
    };

    // [NotNullWhen(true)] is not decoration — Serilog's IDestructuringPolicy declares it on
    // this parameter, and an implementation that omits it is making a weaker promise than the
    // interface (CS8767). It is also true: every `return true` below assigns result first.
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory factory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(factory);

        if (value is not (ServiceIpAssignment or NotificationOutboxMessage))
        {
            result = null;
            return false;
        }

        var properties = value.GetType()
            .GetProperties()
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => new LogEventProperty(
                property.Name,
                SensitivePropertyNames.Contains(property.Name)
                    ? new ScalarValue("[redacted]")
                    : factory.CreatePropertyValue(SafeRead(property, value), destructureObjects: false)))
            .ToList();

        result = new StructureValue(properties, value.GetType().Name);
        return true;
    }

    private static object? SafeRead(System.Reflection.PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (Exception exception) when (exception is TargetInvocationException or NotSupportedException)
        {
            // A lazy navigation property that throws must not take down the logging call
            // that was trying to record something else.
            return "[unavailable]";
        }
    }
}

/// <summary>
/// Ready if the outbox is draining. A backed-up outbox means alerts are silently not
/// arriving, which is worse than an error page because nobody notices.
/// </summary>
public sealed class OutboxDepthHealthCheck(IApplicationDbContext db, IClock clock) : IHealthCheck
{
    private const int UnhealthyDepth = 100;
    private const int UnhealthyAgeMinutes = 15;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        int pending = await db.NotificationOutbox
            .CountAsync(message => message.Status == OutboxStatus.Pending, cancellationToken)
            .ConfigureAwait(false);

        DateTime? oldest = await db.NotificationOutbox
            .Where(message => message.Status == OutboxStatus.Pending)
            .MinAsync(message => (DateTime?)message.ScheduledUtc, cancellationToken)
            .ConfigureAwait(false);

        double ageMinutes = oldest is { } value ? (clock.UtcNow - value).TotalMinutes : 0;

        var data = new Dictionary<string, object>
        {
            ["pending"] = pending,
            ["oldestPendingMinutes"] = Math.Round(ageMinutes, 1),
        };

        if (pending > UnhealthyDepth || ageMinutes > UnhealthyAgeMinutes)
        {
            return HealthCheckResult.Degraded(
                $"Outbox is backing up: {pending} pending, oldest {ageMinutes:F0} minutes. " +
                "Alerts are not being delivered.", data: data);
        }

        return HealthCheckResult.Healthy($"{pending} pending.", data);
    }
}

/// <summary>
/// Ready if the probes are reporting. A silent probe does not produce false outages — it
/// produces coverage gaps — but it does mean availability data is quietly degrading, and
/// that is worth an alert rather than a discovery three weeks later.
/// </summary>
public sealed class ProbeHeartbeatHealthCheck(IApplicationDbContext db, IClock clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = clock.UtcNow.AddMinutes(-5);

        var probes = await db.Probes
            .Where(probe => probe.Status != ProbeStatus.Disabled && probe.Kind != ProbeKind.Simulated)
            .Select(probe => new { probe.Name, probe.LastHeartbeatUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (probes.Count == 0)
        {
            // Not unhealthy. Monitoring is decoupled from inventory on purpose, and an
            // inventory-only deployment is a supported configuration.
            return HealthCheckResult.Healthy("No probes configured.");
        }

        var stale = probes
            .Where(probe => probe.LastHeartbeatUtc is null || probe.LastHeartbeatUtc < cutoff)
            .Select(probe => probe.Name)
            .ToList();

        return stale.Count == 0
            ? HealthCheckResult.Healthy($"{probes.Count} probe(s) reporting.")
            : HealthCheckResult.Degraded(
                $"{stale.Count} of {probes.Count} probe(s) have not reported in 5 minutes: " +
                string.Join(", ", stale) + ". Availability coverage is degrading.");
    }
}

/// <summary>Formatting helpers shared by the Razor components.</summary>
public static class DisplayFormat
{
    public static string Bandwidth(int? kbps) =>
        kbps is null or 0 ? "—" : FcTelecom.Domain.Common.Bandwidth.FromKbps(kbps.Value).ToString();

    public static string Money(decimal? amount, string? currency = "USD") =>
        amount is null
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{amount.Value:N2} {currency}");

    public static string Percent(decimal? value, int decimals = 2) =>
        value is null ? "—" : value.Value.ToString($"F{decimals}", CultureInfo.InvariantCulture) + "%";

    /// <summary>Renders a UTC instant in a location's own time zone, with the zone named.</summary>
    /// <remarks>
    /// Falls back to UTC rather than throwing if the IANA ID is unknown — a bad time zone
    /// on one location must not break the outage page for that location, which is exactly
    /// when someone would notice.
    /// </remarks>
    public static string LocalTime(DateTime? utc, string? ianaTimeZoneId)
    {
        if (utc is not { } value)
        {
            return "—";
        }

        DateTime utcValue = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId ?? "UTC");
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utcValue, zone);
            string abbreviation = zone.IsDaylightSavingTime(utcValue) ? zone.DaylightName : zone.StandardName;
            return $"{local:yyyy-MM-dd HH:mm} {abbreviation}";
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return $"{utcValue:yyyy-MM-dd HH:mm} UTC";
        }
    }

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } value)
        {
            return "—";
        }

        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : $"{(int)value.TotalMinutes}m";
    }
}
