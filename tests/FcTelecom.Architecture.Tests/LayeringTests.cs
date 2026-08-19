using System.Reflection;
using FcTelecom.Application.Authorization;
using NetArchTest.Rules;
using Shouldly;

namespace FcTelecom.Architecture.Tests;

/// <summary>
/// The layering rules, enforced by the build rather than by code review.
/// </summary>
/// <remarks>
/// A layering violation caught in review depends on the reviewer noticing a using
/// statement in a 400-line diff at 5pm on a Friday. A layering violation caught here is a
/// red build. Over the years this application will be maintained, that difference is the
/// entire reason the modular monolith stays modular.
/// </remarks>
public sealed class LayeringTests
{
    // Fully qualified deliberately: a field named `Domain` shadows the `FcTelecom.Domain`
    // namespace inside this class, so `typeof(Domain.Common.BaseEntity)` would not compile.
    private static readonly Assembly Domain = typeof(FcTelecom.Domain.Common.BaseEntity).Assembly;
    private static readonly Assembly Application = typeof(Permissions).Assembly;
    private static readonly Assembly Infrastructure =
        typeof(FcTelecom.Infrastructure.Persistence.ApplicationDbContext).Assembly;

    [Fact]
    public void Domain_DependsOnNothingInThisSolution()
    {
        TestResult result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("FcTelecom.Application", "FcTelecom.Infrastructure", "FcTelecom.Web")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <summary>
    /// The Domain project must not reference EF Core, ASP.NET, or any Azure SDK.
    /// </summary>
    /// <remarks>
    /// This is what keeps the calculations — availability, spend, notice deadlines,
    /// diversity — pure functions that can be tested against a leap-second-free set of
    /// genuinely awkward cases rather than only against whatever the database happened to
    /// contain that day.
    /// </remarks>
    [Fact]
    public void Domain_HasNoInfrastructureConcerns()
    {
        TestResult result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Azure",
                "Microsoft.Extensions.DependencyInjection",
                "Serilog")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrWeb()
    {
        TestResult result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("FcTelecom.Infrastructure", "FcTelecom.Web")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_DoesNotDependOnAspNetCore()
    {
        // Query handlers must not reach for HttpContext. Everything they need about the
        // caller arrives through ICurrentUser, which is what makes them testable and what
        // lets the Functions worker reuse them unchanged.
        TestResult result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnWeb()
    {
        TestResult result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn("FcTelecom.Web")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <summary>
    /// Every entity configuration lives in its own class, discovered by convention.
    /// </summary>
    /// <remarks>
    /// The alternative is a 900-line <c>OnModelCreating</c>, which is where schema
    /// decisions go to die: nobody can find the index they are looking for and everybody
    /// adds a duplicate.
    /// </remarks>
    [Fact]
    public void EntityConfigurations_LiveInTheConfigurationsNamespace()
    {
        TestResult result = Types.InAssembly(Infrastructure)
            .That()
            .ImplementInterface(typeof(Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<>))
            .Should()
            .ResideInNamespace("FcTelecom.Infrastructure.Persistence.Configurations")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null || result.FailingTypeNames.Count == 0
            ? "No failing types reported."
            : "Offending types: " + string.Join(", ", result.FailingTypeNames);
}

/// <summary>
/// Guards on the authorization model itself.
/// </summary>
public sealed class AuthorizationModelTests
{
    /// <summary>
    /// Every permission in the catalogue is granted to at least one role.
    /// </summary>
    /// <remarks>
    /// A permission nobody holds is a feature nobody can use, and it usually means someone
    /// added the constant and forgot the role map. The failure is silent and it presents
    /// as "the button does nothing".
    /// </remarks>
    [Fact]
    public void EveryPermission_IsGrantedToAtLeastOneRole()
    {
        var granted = Roles.DefaultPermissions.Values
            .SelectMany(permissions => permissions)
            .ToHashSet(StringComparer.Ordinal);

        var ungranted = Permissions.All.Where(permission => !granted.Contains(permission)).ToList();

        ungranted.ShouldBeEmpty(
            $"These permissions exist but no role grants them: {string.Join(", ", ungranted)}");
    }

    [Fact]
    public void EveryRolePermission_ExistsInTheCatalogue()
    {
        var known = Permissions.All.ToHashSet(StringComparer.Ordinal);

        var unknown = Roles.DefaultPermissions
            .SelectMany(entry => entry.Value.Select(permission => (entry.Key, permission)))
            .Where(pair => !known.Contains(pair.permission))
            .ToList();

        unknown.ShouldBeEmpty(
            "A role grants a permission that is not in the catalogue — almost certainly a typo, " +
            "and a typo here silently grants nothing: " +
            string.Join(", ", unknown.Select(pair => $"{pair.Key}:{pair.permission}")));
    }

    /// <summary>
    /// Static IP data is not implied by any read role.
    /// </summary>
    /// <remarks>
    /// This is the assertion that stops a well-meaning change from quietly widening access
    /// to the organisation's public address map. Procurement, Help Desk, and Executive all
    /// have legitimate reasons to read circuit records; none of them has a reason to read
    /// the IP inventory.
    /// </remarks>
    [Fact]
    public void ServiceIpData_IsNotGrantedToProcurementHelpDeskOrReadOnly()
    {
        foreach (string role in new[] { Roles.Procurement, Roles.HelpDesk, Roles.ReadOnly })
        {
            Roles.DefaultPermissions[role]
                .ShouldNotContain(Permissions.ServiceIpDataRead,
                    $"{role} must not hold ServiceIpData.Read by default. It is grantable per user, " +
                    "with a recorded justification, and that is the only intended path.");
        }
    }

    [Fact]
    public void OnlyAdministrators_HoldAdministrativePermissions()
    {
        foreach (string permission in new[]
        {
            Permissions.AdminManage, Permissions.AuditRead, Permissions.IntegrationsManage,
        })
        {
            var holders = Roles.DefaultPermissions
                .Where(entry => entry.Value.Contains(permission))
                .Select(entry => entry.Key)
                .ToList();

            holders.ShouldBe(new[] { Roles.AppAdministrator },
                $"{permission} should be held only by AppAdministrator.");
        }
    }

    [Fact]
    public void ReadOnlyRole_HoldsNoWritePermissions()
    {
        Roles.DefaultPermissions[Roles.ReadOnly]
            .Where(permission => permission.EndsWith(".Write", StringComparison.Ordinal) ||
                                 permission.EndsWith(".Manage", StringComparison.Ordinal))
            .ShouldBeEmpty("The read-only role must not be able to change anything.");
    }

    [Fact]
    public void ProbeSubmit_IsNotGrantedToAnyHumanRole()
    {
        // The agent authenticates as an application with a single app role. A person must
        // never be able to satisfy it, and an agent token must never satisfy a user policy.
        foreach ((string role, IReadOnlyList<string> permissions) in Roles.DefaultPermissions)
        {
            permissions.ShouldNotContain(Permissions.ProbeSubmit,
                $"{role} must not hold the probe submission permission.");
        }
    }
}
