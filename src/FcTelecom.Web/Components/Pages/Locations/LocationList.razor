@page "/locations"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.LocationsRead)]
@inject LocationQueries Locations

<PageTitle>Locations — FC Telecom Manager</PageTitle>

<div class="page-head">
    <h1>Locations</h1>
    <div class="page-actions">
        <a class="button button--secondary" href="/exports/locations">Export</a>
    </div>
</div>

<div class="filters">
    <input type="search" placeholder="Search name, code, or city" aria-label="Search locations"
           @bind="_search" @bind:event="oninput" @bind:after="ReloadAsync" />

    <select aria-label="Status" @bind="_status" @bind:after="ReloadAsync">
        <option value="">Any status</option>
        @foreach (LocationStatus status in Enum.GetValues<LocationStatus>())
        {
            <option value="@status">@status</option>
        }
    </select>

    <select aria-label="Criticality" @bind="_criticality" @bind:after="ReloadAsync">
        <option value="">Any criticality</option>
        @foreach (Criticality criticality in Enum.GetValues<Criticality>())
        {
            <option value="@criticality">@criticality</option>
        }
    </select>

    <button type="button" class="button button--tertiary" @onclick="ClearAsync">Clear</button>
</div>

@if (_page is null)
{
    <p class="loading">Loading locations…</p>
}
else if (_page.Items.Count == 0)
{
    <EmptyState Title="No locations match these filters"
                Explanation="Clear the filters to see everything, or import your location list to get started."
                ActionUrl="/imports" ActionLabel="Import locations" />
}
else
{
    <table class="data-table">
        <thead>
            <tr>
                <th>Code</th><th>Name</th><th>City</th><th>Region</th>
                <th>Criticality</th><th class="numeric">Services</th>
                <th class="numeric">Monthly</th><th>Status</th>
            </tr>
        </thead>
        <tbody>
            @foreach (LocationListItemDto location in _page.Items)
            {
                <tr class="@(location.HasOpenOutage ? "row--alert" : null)">
                    <th scope="row"><a href="/locations/@location.Id">@location.LocationCode</a></th>
                    <td>
                        @location.Name
                        @if (location.HasOpenOutage)
                        {
                            <span class="badge badge--urgent">🔴 outage</span>
                        }
                    </td>
                    <td>@location.City@(location.StateOrProvince is null ? "" : ", " + location.StateOrProvince)</td>
                    <td>@(location.RegionName ?? "—")</td>
                    <td>
                        <span class="badge badge--@location.Criticality.ToString().ToLowerInvariant()">
                            @CriticalityIcon(location.Criticality) @location.Criticality
                        </span>
                    </td>
                    <td class="numeric">@location.ServiceCount</td>
                    <td class="numeric">@DisplayFormat.Money(location.MonthlyCost, location.CurrencyCode)</td>
                    <td><span class="badge">@location.Status</span></td>
                </tr>
            }
        </tbody>
    </table>

    <nav class="pager" aria-label="Pagination">
        <button type="button" disabled="@(!_page.HasPrevious)" @onclick="PreviousAsync">Previous</button>
        <span>Page @_page.Page of @Math.Max(1, _page.TotalPages) — @_page.TotalCount locations</span>
        <button type="button" disabled="@(!_page.HasNext)" @onclick="NextAsync">Next</button>
    </nav>
}

@code {
    private PagedResult<LocationListItemDto>? _page;
    private string _search = string.Empty;
    private string _status = string.Empty;
    private string _criticality = string.Empty;
    private int _pageNumber = 1;

    protected override Task OnInitializedAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        _page = await Locations.ListAsync(new LocationFilter
        {
            SearchText = string.IsNullOrWhiteSpace(_search) ? null : _search,
            Status = Enum.TryParse(_status, out LocationStatus status) ? status : null,
            Criticality = Enum.TryParse(_criticality, out Criticality criticality) ? criticality : null,
            Page = _pageNumber,
        });
    }

    private async Task ClearAsync()
    {
        _search = _status = _criticality = string.Empty;
        _pageNumber = 1;
        await ReloadAsync();
    }

    private async Task NextAsync()
    {
        _pageNumber++;
        await ReloadAsync();
    }

    private async Task PreviousAsync()
    {
        _pageNumber = Math.Max(1, _pageNumber - 1);
        await ReloadAsync();
    }

    // Icon plus word plus colour, never colour alone.
    private static string CriticalityIcon(Criticality criticality) => criticality switch
    {
        Criticality.Critical => "◆",
        Criticality.High => "▲",
        Criticality.Standard => "■",
        _ => "▪",
    };
}
