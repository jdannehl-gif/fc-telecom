@page "/contracts"
@page "/contracts/{Id:guid}"
@page "/outages"
@page "/outages/service/{ServiceId:guid}"
@page "/imports"
@page "/admin"
@page "/reports/diversity"
@attribute [Authorize]

@* Dashboard tiles link to these routes because they are the designed destinations, and a
   number nobody can drill into is a decoration. Until the pages exist, landing here and
   being told which phase delivers them is more useful than a bare "page not found" that
   reads like a bug. *@

<PageTitle>Not built yet — FC Telecom Manager</PageTitle>

<div class="page-message">
    <h1>@Title</h1>
    <p>@Explanation</p>
    <p class="muted">
        The data model, calculations, and reports behind this screen are complete —
        only the user interface is outstanding. See <code>docs/04-backlog.md</code>
        for the full ordered backlog.
    </p>
    <a class="button" href="/">Back to the dashboard</a>
</div>

@code {
    [Parameter] public Guid? Id { get; set; }

    [Parameter] public Guid? ServiceId { get; set; }

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private string Title => Section switch
    {
        "contracts" => "Contracts — coming in Phase 1b",
        "outages" => "Outage view — coming in Phase 3",
        "imports" => "Guided import — coming in Phase 1b",
        "admin" => "Administration — coming in Phase 1b",
        "reports" => "Diversity report — coming in Phase 2",
        _ => "Not built yet",
    };

    private string Explanation => Section switch
    {
        "contracts" =>
            "Contract records, the three distinct dates, and the notice-deadline confirmation " +
            "workflow are modelled and tested. The editing screens are the next slice.",
        "outages" =>
            "The outage view — carrier support number, circuit ID, account, demarc, CPE, and " +
            "the copy-support-summary action — is specified in docs/05-wireframes.md and arrives " +
            "with the monitoring module.",
        "imports" =>
            "CSV and Excel import with a dry-run preview, validation, and duplicate detection " +
            "is specified but not yet implemented.",
        "admin" =>
            "Group-to-role mapping, individual permission grants, integrations, and notification " +
            "rules are all modelled in the schema and need their configuration screens.",
        "reports" =>
            "The diversity analysis runs today — it produces the banner on every location page " +
            "and the dashboard count. The portfolio-wide report view is outstanding.",
        _ => "This section is designed but not yet implemented.",
    };

    private string Section
    {
        get
        {
            string path = new Uri(Navigation.Uri).AbsolutePath.Trim('/');
            int slash = path.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? path : path[..slash];
        }
    }
}
