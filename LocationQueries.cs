@page "/vendors/{Id:guid}"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.VendorsRead)]
@inject VendorQueries Vendors

<PageTitle>@(_vendor?.DisplayName ?? "Vendor") — FC Telecom Manager</PageTitle>

@if (_notFound)
{
    <div class="page-message">
        <h1>Vendor not found</h1>
        <a class="button" href="/vendors">Back to vendors</a>
    </div>
}
else if (_vendor is null)
{
    <p class="loading">Loading vendor…</p>
}
else
{
    <nav class="breadcrumb"><a href="/vendors">← Vendors</a></nav>

    <header class="record-head">
        <h1>@_vendor.DisplayName</h1>
        <p class="record-subtitle">@_vendor.LegalName</p>
    </header>

    <div class="panel-row">
        <section class="panel">
            <h2>Support</h2>
            <dl class="kv">
                <dt>Phone</dt><dd>@(_vendor.MainSupportPhone ?? "—")</dd>
                <dt>Hours</dt><dd>@(_vendor.SupportHours ?? "—")</dd>
                <dt>Portal</dt>
                <dd>
                    @if (_vendor.PortalUrl is null)
                    {
                        <text>—</text>
                    }
                    else
                    {
                        <a href="@_vendor.PortalUrl" target="_blank" rel="noopener noreferrer">@_vendor.PortalUrl ↗</a>
                    }
                </dd>

                @*  A pointer to where the credentials live, never a credential. This
                    database is backed up, replicated, read by a reporting principal, and
                    exported to Excel; a portal password stored here would be in all of
                    those places.  *@
                <dt>Credentials</dt>
                <dd>
                    @if (_vendor.CredentialReference is null)
                    {
                        <span class="muted">Not recorded</span>
                    }
                    else
                    {
                        <text>@_vendor.CredentialReference</text>
                        <span class="muted"> (reference only — no credential is stored here)</span>
                    }
                </dd>
            </dl>
        </section>

        <section class="panel">
            <h2>Footprint</h2>
            <dl class="kv">
                <dt>Active services as carrier</dt><dd>@_vendor.ServiceCount</dd>
                @if (_vendor.MonthlySpend is not null)
                {
                    <dt>Monthly spend</dt>
                    <dd>@DisplayFormat.Money(_vendor.MonthlySpend)</dd>
                }
            </dl>

            @if (_vendor.RoleUsage.Count > 1)
            {
                @*  A vendor appearing as last-mile provider under several different
                    carriers is a concentration risk that is invisible on any individual
                    circuit record — nothing there looks unusual.  *@
                <h3>Roles across the estate</h3>
                <ul class="role-usage">
                    @foreach (VendorRoleUsageDto usage in _vendor.RoleUsage)
                    {
                        <li><strong>@usage.ServiceCount</strong> as @usage.Role.ToLowerInvariant()</li>
                    }
                </ul>
            }
        </section>
    </div>

    @if (_vendor.Accounts.Count > 0)
    {
        <section class="panel">
            <h2>Accounts</h2>
            <table class="data-table">
                <thead><tr><th>Account</th><th>Billing account</th><th>Description</th><th>Billing contact</th><th class="numeric">Services</th></tr></thead>
                <tbody>
                    @foreach (VendorAccountDto account in _vendor.Accounts)
                    {
                        <tr>
                            <th scope="row"><code>@account.AccountNumber</code></th>
                            <td>@(account.BillingAccountNumber is null ? "—" : account.BillingAccountNumber)</td>
                            <td>@account.Description</td>
                            <td>@(account.BillingContactName ?? "—")</td>
                            <td class="numeric">@account.ServiceCount</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    }

    @if (_vendor.Contacts.Count > 0)
    {
        <section class="panel">
            <h2>Contacts</h2>
            <table class="data-table">
                <thead><tr><th>Name</th><th>Role</th><th>Type</th><th>Phone</th><th>Email</th><th>Escalation</th></tr></thead>
                <tbody>
                    @foreach (VendorContactDto contact in _vendor.Contacts)
                    {
                        <tr>
                            <th scope="row">@contact.FullName</th>
                            <td>@(contact.JobTitle ?? "—")</td>
                            <td>@contact.Kind</td>
                            <td>@(contact.PhoneNumber ?? contact.MobileNumber ?? "—")</td>
                            <td>@(contact.Email ?? "—")</td>
                            <td>@(contact.EscalationLevel?.ToString() ?? "—")</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    }

    @if (_vendor.TicketProcedures.Count > 0)
    {
        <section class="panel">
            <h2>How to open a ticket</h2>
            @*  This exists because every carrier's process differs and the knowledge
                otherwise lives in one engineer's head. Written down, whoever is on call
                opens the ticket correctly the first time.  *@
            @foreach (TicketProcedureDto procedure in _vendor.TicketProcedures)
            {
                <article class="procedure">
                    <h3>@procedure.ScenarioName</h3>
                    <p>
                        @if (procedure.PhoneNumber is not null)
                        {
                            <text>📞 @procedure.PhoneNumber</text>
                        }
                        @if (procedure.HoursOfOperation is not null)
                        {
                            <text> · @procedure.HoursOfOperation</text>
                        }
                    </p>
                    @if (procedure.RequiredInformation is not null)
                    {
                        <p><strong>They will ask for:</strong> @procedure.RequiredInformation</p>
                    }
                    @if (procedure.Procedure is not null)
                    {
                        <pre class="procedure__steps">@procedure.Procedure</pre>
                    }
                    @if (procedure.ExpectedResponseTime is not null)
                    {
                        <p class="muted">Expected response: @procedure.ExpectedResponseTime</p>
                    }
                </article>
            }
        </section>
    }
}

@code {
    [Parameter] public Guid Id { get; set; }

    private VendorDetailDto? _vendor;
    private bool _notFound;

    protected override async Task OnParametersSetAsync()
    {
        _notFound = false;
        _vendor = null;

        try
        {
            _vendor = await Vendors.GetDetailAsync(Id);
        }
        catch (RecordNotFoundException)
        {
            _notFound = true;
        }
        catch (PermissionDeniedException)
        {
            _notFound = true;
        }
    }
}
