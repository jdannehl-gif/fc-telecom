@page "/services"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.ServicesRead)]
@inject TelecomServiceQueries Services
@inject NavigationManager Navigation

<PageTitle>Services — FC Telecom Manager</PageTitle>

<div class="page-head">
    <h1>Services</h1>
    <div class="page-actions">
        <a class="button button--secondary" href="/exports/services">Export</a>
    </div>
</div>

<div class="filters">
    <input type="search" placeholder="Circuit ID, carrier, location, or any carrier alias"
           aria-label="Search services" @bind="_search" @bind:event="oninput" @bind:after="ReloadAsync" />

    <select aria-label="Service type" @bind="_type" @bind:after="ReloadAsync">
        <option value="">Any type</option>
        @foreach (ServiceType type in Enum.GetValues<ServiceType>())
        {
            <option value="@type">@type</option>
        }
    </select>

    <select aria-label="Status" @bind="_status" @bind:after="ReloadAsync">
        <option value="">Any status</option>
        @foreach (ServiceStatus status in Enum.GetValues<ServiceStatus>())
        {
            <option value="@status">@status</option>
        }
    </select>

    <label class="check">
        <input type="checkbox" @bind="_missingCircuitId" @bind:after="ReloadAsync" />
        Missing circuit ID
    </label>

    <label class="check">
        <input type="checkbox" @bind="_missingContract" @bind:after="ReloadAsync" />
        No contract
    </label>

    <button type="button" class="button button--tertiary" @onclick="ClearAsync">Clear</button>
</div>

@if (_page is null)
{
    <p class="loading">Loading services…</p>
}
else if (_page.Items.Count == 0)
{
    <EmptyState Title="No services match these filters"
                Explanation="Clear the filters to see everything, or import your circuit inventory."
                ActionUrl="/imports" ActionLabel="Import services" />
}
else
{
    <table class="data-table">
        <thead>
            <tr>
                <th>State</th><th>Location</th><th>Type</th><th>Role</th>
                <th>Carrier</th><th>Circuit ID</th><th class="numeric">Speed</th>
                <th class="numeric">Monthly</th><th>Flags</th>
            </tr>
        </thead>
        <tbody>
            @foreach (ServiceListItemDto service in _page.Items)
            {
                <tr>
                    <td><StatusChip State="service.MonitorState" /></td>
                    <th scope="row">
                        <a href="/locations/@service.LocationId">@service.LocationCode</a>
                        <span class="muted">@service.LocationName</span>
                    </th>
                    <td>@service.ServiceType</td>
                    <td>@service.ServiceRole</td>
                    <td>@service.CarrierName</td>
                    <td>
                        @if (service.CircuitId is null)
                        {
                            <span class="badge badge--warn">⚠ missing</span>
                        }
                        else
                        {
                            <a href="/services/@service.Id"><code>@service.CircuitId</code></a>
                        }
                    </td>
                    <td class="numeric">@DisplayFormat.Bandwidth(service.DownloadKbps)</td>
                    <td class="numeric">@DisplayFormat.Money(service.MonthlyCost, service.CurrencyCode)</td>
                    <td>
                        @if (!service.HasContract)
                        {
                            <span class="badge badge--warn" title="No contract linked — renewal and cancellation terms are unknown.">no contract</span>
                        }
                        @if (service.HasIpData)
                        {
                            <span class="badge badge--muted" title="Static addressing is recorded for this service.">IPs</span>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>

    <nav class="pager" aria-label="Pagination">
        <button type="button" disabled="@(!_page.HasPrevious)" @onclick="PreviousAsync">Previous</button>
        <span>Page @_page.Page of @Math.Max(1, _page.TotalPages) — @_page.TotalCount services</span>
        <button type="button" disabled="@(!_page.HasNext)" @onclick="NextAsync">Next</button>
    </nav>
}

@code {
    private PagedResult<ServiceListItemDto>? _page;
    private string _search = string.Empty;
    private string _type = string.Empty;
    private string _status = string.Empty;
    private bool _missingCircuitId;
    private bool _missingContract;
    private int _pageNumber = 1;

    protected override async Task OnInitializedAsync()
    {
        // Dashboard tiles deep-link into this page with the filter pre-applied. A number
        // you cannot drill into is a decoration, so the query string has to be honoured.
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            new Uri(Navigation.Uri).Query);

        _missingCircuitId = query.TryGetValue("missingCircuitId", out var missingId) && missingId == "true";
        _missingContract = query.TryGetValue("missingContract", out var missingContract) && missingContract == "true";

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _page = await Services.ListAsync(new ServiceListFilter
        {
            SearchText = string.IsNullOrWhiteSpace(_search) ? null : _search,
            ServiceType = Enum.TryParse(_type, out ServiceType type) ? type : null,
            Status = Enum.TryParse(_status, out ServiceStatus status) ? status : null,
            MissingCircuitId = _missingCircuitId ? true : null,
            MissingContract = _missingContract ? true : null,
            Page = _pageNumber,
        });
    }

    private async Task ClearAsync()
    {
        _search = _type = _status = string.Empty;
        _missingCircuitId = _missingContract = false;
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
}
