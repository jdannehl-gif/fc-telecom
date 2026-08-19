using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Application.Common;
using FcTelecom.Application.Organization;
using FcTelecom.Application.Platform;
using FcTelecom.Application.Services;
using FcTelecom.Application.Vendors;
using FcTelecom.Domain.Platform;
using Microsoft.AspNetCore.Mvc;

namespace FcTelecom.Web.Endpoints;

public static class ApiEndpoints
{
    /// <summary>
    /// Registers the JSON API and the export endpoints.
    /// </summary>
    /// <remarks>
    /// Every group carries an explicit <c>RequireAuthorization</c> with a named permission.
    /// The fallback policy would catch an omission, but only by requiring authentication —
    /// it would not enforce the right permission, so being explicit here is not redundant.
    /// </remarks>
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder api = app.MapGroup("/api").WithTags("FcTelecom");

        api.MapGet("/locations", async (
                [AsParameters] LocationQueryParameters parameters,
                LocationQueries queries,
                CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListAsync(parameters.ToFilter(), cancellationToken)))
            .RequireAuthorization(Permissions.LocationsRead)
            .WithName("ListLocations");

        api.MapGet("/locations/{id:guid}", async (
                Guid id, LocationQueries queries, CancellationToken cancellationToken) =>
            Results.Ok(await queries.GetDetailAsync(id, cancellationToken)))
            .RequireAuthorization(Permissions.LocationsRead)
            .WithName("GetLocation");

        api.MapGet("/services", async (
                [AsParameters] ServiceQueryParameters parameters,
                TelecomServiceQueries queries,
                CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListAsync(parameters.ToFilter(), cancellationToken)))
            .RequireAuthorization(Permissions.ServicesRead)
            .WithName("ListServices");

        api.MapGet("/services/{id:guid}", async (
                Guid id, TelecomServiceQueries queries, CancellationToken cancellationToken) =>
            Results.Ok(await queries.GetDetailAsync(id, cancellationToken)))
            .RequireAuthorization(Permissions.ServicesRead)
            .WithName("GetService");

        api.MapGet("/vendors", async (
                string? search, VendorQueries queries, CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListAsync(search, cancellationToken: cancellationToken)))
            .RequireAuthorization(Permissions.VendorsRead)
            .WithName("ListVendors");

        api.MapGet("/search", async (
                string q, GlobalSearchService search, CancellationToken cancellationToken) =>
            Results.Ok(await search.SearchAsync(q, cancellationToken)))
            .RequireAuthorization()
            .WithName("GlobalSearch");

        MapExportEndpoints(app);

        return app;
    }

    private static void MapExportEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder exports = app.MapGroup("/exports")
            .RequireAuthorization(Permissions.ExportRun)
            // Exports scan a lot of rows and produce megabytes. Nobody legitimately needs
            // ten a minute, and an unthrottled export endpoint is a denial of service with
            // a friendly URL.
            .RequireRateLimiting("expensive");

        exports.MapGet("/services", async (
            TelecomServiceQueries queries,
            IExcelExporter excel,
            ISecurityEventLogger securityLog,
            CancellationToken cancellationToken) =>
        {
            PagedResult<ServiceListItemDto> page = await queries.ListAsync(
                new ServiceListFilter { Page = 1, PageSize = QueryableExtensions.MaxPageSize },
                cancellationToken);

            // Every export is logged. This is one of only two reads in the system that are
            // — the other is revealing a sensitive field — because an export is the moment
            // data leaves the application's access controls entirely.
            await securityLog.LogAsync(
                SecurityEventType.ExportGenerated,
                $"Exported {page.Items.Count} service row(s).",
                cancellationToken);

            byte[] workbook = excel.Build(
                "Services",
                ["Location code", "Location", "Type", "Role", "Carrier", "Circuit ID",
                 "Download", "Upload", "Monthly cost", "Currency", "Monitor state", "Has contract"],
                page.Items.Select(item => new object?[]
                {
                    item.LocationCode, item.LocationName, item.ServiceType, item.ServiceRole,
                    item.CarrierName, item.CircuitId, item.DownloadKbps, item.UploadKbps,
                    item.MonthlyCost, item.CurrencyCode, item.MonitorState, item.HasContract,
                }));

            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"services-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
        }).WithName("ExportServices");

        exports.MapGet("/locations", async (
            LocationQueries queries,
            IExcelExporter excel,
            ISecurityEventLogger securityLog,
            CancellationToken cancellationToken) =>
        {
            PagedResult<LocationListItemDto> page = await queries.ListAsync(
                new LocationFilter { Page = 1, PageSize = QueryableExtensions.MaxPageSize },
                cancellationToken);

            await securityLog.LogAsync(
                SecurityEventType.ExportGenerated,
                $"Exported {page.Items.Count} location row(s).",
                cancellationToken);

            byte[] workbook = excel.Build(
                "Locations",
                ["Code", "Name", "Status", "Type", "City", "State", "Region",
                 "Criticality", "Services", "Monthly cost", "Currency"],
                page.Items.Select(item => new object?[]
                {
                    item.LocationCode, item.Name, item.Status, item.LocationType,
                    item.City, item.StateOrProvince, item.RegionName, item.Criticality,
                    item.ServiceCount, item.MonthlyCost, item.CurrencyCode,
                }));

            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"locations-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
        }).WithName("ExportLocations");
    }
}

public sealed record LocationQueryParameters(
    int? RegionId, string? Status, string? Search, int? Page, int? PageSize)
{
    public LocationFilter ToFilter() => new()
    {
        RegionId = RegionId,
        Status = Enum.TryParse(Status, ignoreCase: true, out Domain.Organization.LocationStatus status)
            ? status
            : null,
        SearchText = Search,
        Page = Page ?? 1,
        PageSize = PageSize ?? QueryableExtensions.DefaultPageSize,
    };
}

public sealed record ServiceQueryParameters(
    Guid? LocationId, Guid? CarrierVendorId, string? ServiceType, string? Status,
    string? Search, bool? MissingCircuitId, int? Page, int? PageSize)
{
    public ServiceListFilter ToFilter() => new()
    {
        LocationId = LocationId,
        CarrierVendorId = CarrierVendorId,
        ServiceType = Enum.TryParse(ServiceType, ignoreCase: true, out Domain.Services.ServiceType type)
            ? type
            : null,
        Status = Enum.TryParse(Status, ignoreCase: true, out Domain.Services.ServiceStatus status)
            ? status
            : null,
        SearchText = Search,
        MissingCircuitId = MissingCircuitId,
        Page = Page ?? 1,
        PageSize = PageSize ?? QueryableExtensions.DefaultPageSize,
    };
}
