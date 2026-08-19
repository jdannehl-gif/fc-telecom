using Azure.Identity;
using Azure.Storage.Blobs;
using FcTelecom.Application.Abstractions;
using FcTelecom.Infrastructure.Common;
using FcTelecom.Infrastructure.Documents;
using FcTelecom.Infrastructure.Export;
using FcTelecom.Infrastructure.Persistence;
using FcTelecom.Infrastructure.Persistence.Interceptors;
using FcTelecom.Infrastructure.Persistence.Seed;
using FcTelecom.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FcTelecom.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FieldEncryptionOptions>(
            configuration.GetSection(FieldEncryptionOptions.SectionName));
        services.Configure<DocumentStorageOptions>(
            configuration.GetSection(DocumentStorageOptions.SectionName));

        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. In Azure this is an " +
                "Entra-authenticated connection string with no credential in it: " +
                "'Server=...;Database=...;Authentication=Active Directory Default;Encrypt=True'.");

        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                // Transient-fault retries. Azure SQL will occasionally drop a connection
                // during a planned failover, and without this the user sees an error page
                // for something that would have succeeded a second later.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);

                sql.CommandTimeout(60);
            });

            options.AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>());

            // Suppressed deliberately. Several join entities (LocationContact,
            // ContractService) have a required navigation to a soft-deletable principal.
            // EF warns that the filter may produce surprising results; here it is exactly
            // what we want — archiving a location should take its contact links out of
            // every query too.
            options.ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        });

        // A factory alongside the scoped context, for the security event logger — which
        // must be able to write outside the caller's transaction (see SecurityEventLogger).
        services.AddDbContextFactory<ApplicationDbContext>((provider, options) =>
        {
            options.UseSqlServer(connectionString);
            options.ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        }, lifetime: ServiceLifetime.Singleton);

        services.AddScoped<IApplicationDbContext>(provider =>
            provider.GetRequiredService<ApplicationDbContext>());

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFieldEncryptor, FieldEncryptor>();
        services.AddSingleton<IExcelExporter, ExcelExporter>();
        services.AddScoped<ISecurityEventLogger, SecurityEventLogger>();

        string? blobUri = configuration["Documents:BlobServiceUri"];

        if (!string.IsNullOrWhiteSpace(blobUri))
        {
            services.AddSingleton(_ => new BlobServiceClient(
                new Uri(blobUri),
                // DefaultAzureCredential picks up the managed identity in Azure and the
                // developer's az-cli / VS login locally. No storage account key exists in
                // configuration anywhere, which is the point.
                new DefaultAzureCredential()));

            services.AddScoped<IDocumentStore, BlobDocumentStore>();
        }
        else if (!string.IsNullOrWhiteSpace(configuration["Documents:ConnectionString"]))
        {
            // Azurite for local development only. Guarded so a production misconfiguration
            // cannot silently fall back to a connection-string credential.
            services.AddSingleton(_ => new BlobServiceClient(configuration["Documents:ConnectionString"]));
            services.AddScoped<IDocumentStore, BlobDocumentStore>();
        }

        services.AddScoped<DemoDataSeeder>();

        return services;
    }
}
