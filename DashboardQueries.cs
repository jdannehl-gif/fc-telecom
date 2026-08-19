@page "/services/{Id:guid}"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.ServicesRead)]
@inject TelecomServiceQueries Services

<PageTitle>@(_service?.CircuitId ?? "Service") — FC Telecom Manager</PageTitle>

@if (_notFound)
{
    <div class="page-message">
        <h1>Service not found</h1>
        <p>That service does not exist, or you do not have access to it.</p>
        <a class="button" href="/services">Back to services</a>
    </div>
}
else if (_service is null)
{
    <p class="loading">Loading service…</p>
}
else
{
    <nav class="breadcrumb">
        <a href="/locations/@_service.LocationId">← @_service.LocationCode @_service.LocationName</a>
    </nav>

    <header class="record-head">
        <h1>
            <StatusChip State="_service.MonitorState" />
            @_service.CarrierName @_service.ServiceType
        </h1>
        <p class="record-subtitle">
            Circuit ID <code>@(_service.CircuitId ?? "not recorded")</code>
            <span class="badge">@_service.ServiceRole</span>
            <span class="badge">@_service.Status</span>
        </p>
        <div class="page-actions">
            <a class="button" href="/outages/service/@_service.Id">Outage view</a>
        </div>
    </header>

    <div class="panel-row">
        <section class="panel">
            <h2>Commercial</h2>
            <dl class="kv">
                <dt>Carrier</dt>
                <dd>@_service.CarrierName</dd>

                <dt>Reseller</dt>
                <dd>@(_service.ResellerName ?? "—")</dd>

                @*  Last-mile and backbone owner are separate fields, and this is why: two
                    circuits from two different carriers can share the same physical path.
                    If these were collapsed into one vendor, "is our backup real?" would be
                    unanswerable.  *@
                <dt>Last-mile provider</dt>
                <dd>
                    @(_service.LastMileVendorName ?? "—")
                    @if (_service.LastMileVendorName is not null &&
                         _service.LastMileVendorName != _service.CarrierName)
                    {
                        <span class="muted"> (differs from the carrier — check diversity)</span>
                    }
                </dd>

                <dt>Underlying network</dt>
                <dd>@(_service.UnderlyingNetworkOwnerName ?? "—")</dd>

                <dt>Account</dt>
                <dd><code>@(_service.AccountNumber ?? "—")</code></dd>

                @if (_service.BillingAccountNumber is not null)
                {
                    <dt>Billing account</dt>
                    <dd><code>@_service.BillingAccountNumber</code></dd>
                }

                <dt>Installed</dt>
                <dd>@(_service.InstallDate?.ToString("yyyy-MM-dd") ?? "—")</dd>

                <dt>Activated</dt>
                <dd>@(_service.ActivationDate?.ToString("yyyy-MM-dd") ?? "—")</dd>
            </dl>
        </section>

        <section class="panel">
            <h2>Technical</h2>
            <dl class="kv">
                <dt>Service type</dt><dd>@_service.ServiceType</dd>
                <dt>Media</dt><dd>@_service.Media</dd>
                <dt>Handoff</dt><dd>@_service.HandoffType</dd>
                <dt>Demarc</dt><dd>@(_service.DemarcLocation ?? "—")</dd>
                <dt>CPE</dt>
                <dd>
                    @if (_service.CpeMake is null)
                    {
                        <text>—</text>
                    }
                    else
                    {
                        <text>@_service.CpeMake @_service.CpeModel</text>
                        @if (_service.CpeSerial is not null)
                        {
                            <text> · SN @_service.CpeSerial</text>
                        }
                    }
                </dd>
                <dt>CPE managed by</dt>
                <dd>@(_service.CpeManagedByCarrier ? "Carrier" : "Us")</dd>
                <dt>WAN interface</dt><dd>@(_service.WanInterface ?? "—")</dd>
                <dt>Support priority</dt><dd>@_service.SupportPriority</dd>
                <dt>Support phone</dt><dd>@(_service.CarrierSupportPhone ?? "—")</dd>
                @if (_service.CarrierPortalUrl is not null)
                {
                    <dt>Portal</dt>
                    <dd><a href="@_service.CarrierPortalUrl" target="_blank" rel="noopener noreferrer">@_service.CarrierPortalUrl ↗</a></dd>
                }
            </dl>
        </section>
    </div>

    @if (_service.Bandwidth is { } bandwidth)
    {
        <section class="panel">
            <h2>Bandwidth &amp; SLA</h2>
            <p>
                @DisplayFormat.Bandwidth(bandwidth.DownloadKbps) down ·
                @DisplayFormat.Bandwidth(bandwidth.UploadKbps) up ·
                @if (bandwidth.CommittedInformationRateKbps > 0)
                {
                    <text>CIR @DisplayFormat.Bandwidth(bandwidth.CommittedInformationRateKbps)</text>
                }
                else
                {
                    @* Worth saying out loud. A "1 Gbps" best-effort service and a 1 Gbps CIR
                       service are different purchases, and the cost-per-Mbps report treats
                       them differently for exactly this reason. *@
                    <span class="muted">no committed rate — best effort</span>
                }
                @if (bandwidth.DataCapGb is { } cap)
                {
                    <text> · cap @cap GB</text>
                }
            </p>
            @if (bandwidth.SlaAvailabilityPercent is { } sla)
            {
                <p>
                    SLA: @DisplayFormat.Percent(sla) availability
                    @if (bandwidth.SlaLatencyMs is { } latency)
                    {
                        <text> · ≤@latency ms latency</text>
                    }
                    @if (bandwidth.SlaPacketLossPercent is { } loss)
                    {
                        <text> · ≤@DisplayFormat.Percent(loss) loss</text>
                    }
                </p>
            }
            else
            {
                <p class="muted">No SLA terms recorded. Service-credit eligibility cannot be assessed.</p>
            }
        </section>
    }

    @if (_service.Identifiers.Count > 0)
    {
        <section class="panel">
            <h2>Other identifiers</h2>
            @* Carriers do not agree on what to call anything. These aliases are all
               searchable, so an engineer can paste whichever string they were given. *@
            <table class="data-table">
                <thead><tr><th>Type</th><th>Value</th><th>Notes</th></tr></thead>
                <tbody>
                    @foreach (ServiceIdentifierDto identifier in _service.Identifiers)
                    {
                        <tr>
                            <th scope="row">@identifier.IdentifierType</th>
                            <td><code>@identifier.Value</code></td>
                            <td class="muted">@identifier.Notes</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    }

    <section class="panel">
        <h2>Addressing <span class="badge badge--restricted">🔒 restricted</span></h2>

        @if (!_service.CanViewIpData)
        {
            <p class="muted">
                @if (_service.HasHiddenIpData)
                {
                    <text>
                        Static addressing is recorded for this service but is restricted. Ask an
                        administrator for the network-data permission if you need it.
                    </text>
                }
                else
                {
                    <text>No static addressing is recorded for this service.</text>
                }
            </p>
        }
        else if (_service.IpAssignments.Count == 0)
        {
            <p class="muted">No static addressing is recorded for this service.</p>
        }
        else
        {
            <p class="muted">Revealing these values is recorded against your account.</p>
            <table class="data-table">
                <thead>
                    <tr><th>Family</th><th>Block</th><th>Gateway</th><th>Usable range</th><th>Routed</th></tr>
                </thead>
                <tbody>
                    @foreach (ServiceIpAssignmentDto assignment in _service.IpAssignments)
                    {
                        <tr>
                            <th scope="row">@assignment.AddressFamily</th>
                            <td><SensitiveField Value="@assignment.Cidr" /></td>
                            <td><SensitiveField Value="@assignment.Gateway" /></td>
                            <td><SensitiveField Value="@($"{assignment.UsableFirst} – {assignment.UsableLast}")" /></td>
                            <td>@(assignment.IsRoutedBlock ? "Yes" : "No")</td>
                        </tr>
                    }
                </tbody>
            </table>
        }
    </section>

    @if (_service.Dependencies.Count > 0)
    {
        <section class="panel">
            <h2>Dependencies</h2>
            <table class="data-table">
                <thead><tr><th>Depends on</th><th>Type</th><th>Confidence</th><th>Evidence</th><th>Assessed</th></tr></thead>
                <tbody>
                    @foreach (ServiceDependencyDto dependency in _service.Dependencies)
                    {
                        <tr>
                            <th scope="row"><a href="/services/@dependency.DependsOnServiceId">@dependency.DependsOnServiceLabel</a></th>
                            <td>@dependency.DependencyType</td>
                            <td>
                                <span class="badge badge--@(dependency.Confidence == DependencyConfidence.RuledOut ? "ok" : "warn")">
                                    @(dependency.Confidence == DependencyConfidence.RuledOut ? "✔" : "⚠") @dependency.Confidence
                                </span>
                            </td>
                            <td class="muted">@dependency.Evidence</td>
                            <td>@(dependency.AssessedOn?.ToString("yyyy-MM-dd") ?? "—")</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    }

    @if (!string.IsNullOrWhiteSpace(_service.TechnicalNotes))
    {
        <section class="panel">
            <h2>Notes</h2>
            <p>@_service.TechnicalNotes</p>
        </section>
    }
}

@code {
    [Parameter] public Guid Id { get; set; }

    private ServiceDetailDto? _service;
    private bool _notFound;

    protected override async Task OnParametersSetAsync()
    {
        _notFound = false;
        _service = null;

        try
        {
            _service = await Services.GetDetailAsync(Id);
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
