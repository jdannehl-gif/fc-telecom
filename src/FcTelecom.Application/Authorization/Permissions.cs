namespace FcTelecom.Application.Authorization;

/// <summary>
/// Every permission the application recognises.
/// </summary>
/// <remarks>
/// Roles are coarse and permissions are fine. Five roles ship, but authorization is
/// enforced against these named permissions — which is what makes "Procurement can see
/// costs but not static IP blocks" expressible without inventing a sixth role every time
/// a request arrives.
/// <para>
/// <see cref="All"/> is used by a completeness test that fails if an endpoint appears
/// without a corresponding entry in the authorization matrix. Adding a permission here
/// and forgetting to test it is therefore a red build, not a code review someone might miss.
/// </para>
/// </remarks>
public static class Permissions
{
    public const string LocationsRead = "Locations.Read";
    public const string LocationsWrite = "Locations.Write";

    public const string VendorsRead = "Vendors.Read";
    public const string VendorsWrite = "Vendors.Write";

    public const string ServicesRead = "Services.Read";
    public const string ServicesWrite = "Services.Write";

    /// <summary>
    /// Static IP and CIDR data. <b>No role implies this by default</b> — it is attached to
    /// Network Engineer explicitly and can be granted to an individual by exception.
    /// Taken together with the location list, this data is a map of the organisation's
    /// public attack surface, so it is gated separately from everything else about a circuit.
    /// </summary>
    public const string ServiceIpDataRead = "ServiceIpData.Read";
    public const string ServiceIpDataWrite = "ServiceIpData.Write";

    public const string CostsRead = "Costs.Read";
    public const string CostsWrite = "Costs.Write";

    public const string ContractsRead = "Contracts.Read";
    public const string ContractsWrite = "Contracts.Write";

    public const string IncidentsRead = "Incidents.Read";
    public const string IncidentsWrite = "Incidents.Write";

    public const string MonitoringManage = "Monitoring.Manage";

    public const string DocumentsRead = "Documents.Read";
    public const string DocumentsWrite = "Documents.Write";

    public const string ImportRun = "Import.Run";
    public const string ExportRun = "Export.Run";

    public const string IntegrationsManage = "Integrations.Manage";
    public const string AuditRead = "Audit.Read";
    public const string AdminManage = "Admin.Manage";

    /// <summary>Reserved for the probe agent's app registration. Never granted to a person.</summary>
    public const string ProbeSubmit = "Probe.Submit";

    public static readonly IReadOnlyList<string> All =
    [
        LocationsRead, LocationsWrite,
        VendorsRead, VendorsWrite,
        ServicesRead, ServicesWrite,
        ServiceIpDataRead, ServiceIpDataWrite,
        CostsRead, CostsWrite,
        ContractsRead, ContractsWrite,
        IncidentsRead, IncidentsWrite,
        MonitoringManage,
        DocumentsRead, DocumentsWrite,
        ImportRun, ExportRun,
        IntegrationsManage, AuditRead, AdminManage,
    ];

    /// <summary>
    /// Permissions that grant access to restricted data and therefore warrant a
    /// <c>SecurityEvent</c> when granted, revoked, or exercised.
    /// </summary>
    public static readonly IReadOnlySet<string> Sensitive = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceIpDataRead, ServiceIpDataWrite, AuditRead, AdminManage, IntegrationsManage,
    };
}

/// <summary>The five shipped roles and what each one can do.</summary>
public static class Roles
{
    public const string AppAdministrator = "AppAdministrator";
    public const string NetworkEngineer = "NetworkEngineer";
    public const string Procurement = "Procurement";
    public const string HelpDesk = "HelpDesk";
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlyList<string> All =
        [AppAdministrator, NetworkEngineer, Procurement, HelpDesk, ReadOnly];

    /// <summary>
    /// The shipped role-to-permission map. Seeded into the database, where it becomes
    /// editable — this constant is the starting point, not the enforcement mechanism.
    /// </summary>
    /// <remarks>
    /// Two allocations worth noticing, because they look like mistakes and are not:
    /// <list type="bullet">
    /// <item>Executive/Read Only holds <c>Costs.Read</c> but not <c>ServiceIpData.Read</c>.
    /// An executive needs the spend figure; they have no use for the public IP inventory.</item>
    /// <item>Help Desk is close to the inverse of Procurement: incidents and carrier
    /// escalation detail, no financial data at all.</item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultPermissions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [AppAdministrator] = Permissions.All,

            [NetworkEngineer] =
            [
                Permissions.LocationsRead, Permissions.LocationsWrite,
                Permissions.VendorsRead,
                Permissions.ServicesRead, Permissions.ServicesWrite,
                Permissions.ServiceIpDataRead, Permissions.ServiceIpDataWrite,
                Permissions.ContractsRead,
                Permissions.IncidentsRead, Permissions.IncidentsWrite,
                Permissions.MonitoringManage,
                Permissions.DocumentsRead, Permissions.DocumentsWrite,
                Permissions.ImportRun, Permissions.ExportRun,
            ],

            [Procurement] =
            [
                Permissions.LocationsRead,
                Permissions.VendorsRead, Permissions.VendorsWrite,
                Permissions.ServicesRead,
                Permissions.CostsRead, Permissions.CostsWrite,
                Permissions.ContractsRead, Permissions.ContractsWrite,
                Permissions.DocumentsRead, Permissions.DocumentsWrite,
                Permissions.ImportRun, Permissions.ExportRun,
            ],

            [HelpDesk] =
            [
                Permissions.LocationsRead,
                Permissions.VendorsRead,
                Permissions.ServicesRead,
                Permissions.IncidentsRead, Permissions.IncidentsWrite,
                Permissions.DocumentsRead,
                Permissions.ExportRun,
            ],

            [ReadOnly] =
            [
                Permissions.LocationsRead,
                Permissions.VendorsRead,
                Permissions.ServicesRead,
                Permissions.CostsRead,
                Permissions.ContractsRead,
                Permissions.IncidentsRead,
                Permissions.ExportRun,
            ],
        };
}
