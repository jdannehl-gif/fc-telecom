using FcTelecom.Application.Dashboard;
using FcTelecom.Application.Organization;
using FcTelecom.Application.Platform;
using FcTelecom.Application.Services;
using FcTelecom.Application.Vendors;
using Microsoft.Extensions.DependencyInjection;

namespace FcTelecom.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the application's query and command services.
    /// </summary>
    /// <remarks>
    /// Scoped, because each one holds a reference to the request-scoped
    /// <c>IApplicationDbContext</c> and <c>ICurrentUser</c>. Registering any of these as
    /// singletons would capture the first request's identity and serve it to everyone —
    /// a class of bug that is subtle to spot and catastrophic in an application with
    /// permission-scoped queries.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<LocationQueries>();
        services.AddScoped<VendorQueries>();
        services.AddScoped<TelecomServiceQueries>();
        services.AddScoped<DashboardQueries>();
        services.AddScoped<GlobalSearchService>();

        return services;
    }
}
