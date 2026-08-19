using System.Security.Claims;
using FcTelecom.Application;
using FcTelecom.Application.Abstractions;
using FcTelecom.Application.Authorization;
using FcTelecom.Infrastructure;
using FcTelecom.Infrastructure.Persistence;
using FcTelecom.Infrastructure.Persistence.Seed;
using FcTelecom.Web.Authorization;
using FcTelecom.Web.Components;
using FcTelecom.Web.Endpoints;
using FcTelecom.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Serilog;
using Serilog.Events;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Logging ─────────────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Destructure.With(new SensitiveDataDestructuringPolicy())
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .WriteTo.Console());

builder.Services.AddApplicationInsightsTelemetry();

// ── Authentication ──────────────────────────────────────────────────────────────────
//
// Entra ID is the only authentication path. There is no local credential store, so there
// is no password database to leak and no reset flow to abuse.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options =>
    {
        builder.Configuration.GetSection("AzureAd").Bind(options);

        // Permissions are resolved once at sign-in and carried as claims for the session.
        options.Events.OnTokenValidated = async context =>
        {
            if (context.Principal is null)
            {
                return;
            }

            var enricher = context.HttpContext.RequestServices
                .GetRequiredService<PermissionClaimsEnricher>();

            IReadOnlyList<Claim> claims = await enricher.BuildClaimsAsync(context.Principal);

            if (context.Principal.Identity is ClaimsIdentity identity)
            {
                identity.AddClaims(claims);
            }
        };
    });

builder.Services.AddPermissionAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<PermissionClaimsEnricher>();

builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

// ── Application and infrastructure ──────────────────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Resilience and limits ───────────────────────────────────────────────────────────
builder.Services.AddRateLimiting(builder.Configuration);
builder.Services.AddResponseCompression();

builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("Default")!,
        name: "sql",
        tags: ["ready"])
    .AddCheck<OutboxDepthHealthCheck>("outbox", tags: ["ready"])
    .AddCheck<ProbeHeartbeatHealthCheck>("probes", tags: ["ready"]);

// App Service terminates TLS at the front end and forwards the original scheme. Without
// this, every generated URL is http:// and the OIDC redirect loop is a fun afternoon.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

WebApplication app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);

    // Two years, with subdomains. Shorten only if a subdomain genuinely cannot serve HTTPS.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSecurityHeaders();
app.UseResponseCompression();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseSerilogRequestLogging();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();
app.MapApiEndpoints();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

// ── Startup data ────────────────────────────────────────────────────────────────────
//
// Reference data (role permissions, notification rules) is seeded on every start and is
// idempotent. Schema migrations are NOT applied here — they run as an explicit pipeline
// step from a reviewed idempotent script. Calling Database.Migrate() at startup is
// convenient and is how two instances race each other into a half-applied schema during a
// slot swap.
await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
    await seeder.SeedReferenceDataAsync();

    if (app.Configuration.GetValue<bool>("SeedDemoData"))
    {
        await seeder.SeedDemoDataAsync();
    }
}

await app.RunAsync();

/// <summary>Exposed so the integration test host can reference the entry-point assembly.</summary>
public partial class Program;
