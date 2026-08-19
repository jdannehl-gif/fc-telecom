@page "/vendors"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.VendorsRead)]
@inject VendorQueries Vendors

<PageTitle>Vendors — FC Telecom Manager</PageTitle>

<div class="page-head">
    <h1>Vendors</h1>
</div>

<div class="filters">
    <input type="search" placeholder="Search vendor name" aria-label="Search vendors"
           @bind="_search" @bind:event="oninput" @bind:after="ReloadAsync" />
</div>

@if (_page is null)
{
    <p class="loading">Loading vendors…</p>
}
else if (_page.Items.Count == 0)
{
    <EmptyState Title="No vendors match" Explanation="Clear the search to see everything." />
}
else
{
    <table class="data-table">
        <thead>
            <tr>
                <th>Vendor</th><th>Role</th><th>Support</th>
                <th class="numeric">Services</th><th class="numeric">Accounts</th><th class="numeric">Monthly</th>
            </tr>
        </thead>
        <tbody>
            @foreach (VendorListItemDto vendor in _page.Items)
            {
                <tr>
                    <th scope="row">
                        <a href="/vendors/@vendor.Id">@vendor.DisplayName</a>
                        <span class="muted">@vendor.LegalName</span>
                    </th>
                    <td>@FormatKind(vendor.Kind)</td>
                    <td>@(vendor.MainSupportPhone ?? "—")</td>
                    <td class="numeric">@vendor.ServiceCount</td>
                    <td class="numeric">@vendor.AccountCount</td>
                    <td class="numeric">@DisplayFormat.Money(vendor.MonthlySpend)</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private PagedResult<VendorListItemDto>? _page;
    private string _search = string.Empty;

    protected override Task OnInitializedAsync() => ReloadAsync();

    private async Task ReloadAsync() =>
        _page = await Vendors.ListAsync(string.IsNullOrWhiteSpace(_search) ? null : _search);

    // VendorKind is a flags enum because one company genuinely plays several roles —
    // Lumen is the carrier on one circuit and the last-mile provider under a competitor's
    // circuit at the same address.
    private static string FormatKind(VendorKind kind) =>
        kind == VendorKind.None
            ? "—"
            : string.Join(", ", Enum.GetValues<VendorKind>()
                .Where(value => value != VendorKind.None && kind.HasFlag(value))
                .Select(value => value.ToString()));
}
