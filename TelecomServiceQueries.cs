@page "/locations/{Id:guid}"
@rendermode InteractiveServer
@attribute [Authorize(Policy = Permissions.LocationsRead)]
@inject LocationQueries Locations

@*  This page is the acceptance test for the whole inventory slice. It has to answer
    seven questions without a scroll hunt:

      1. What connectivity and telecom services are active here?
      2. Which circuit is primary and which is backup?
      3. Who is the carrier and what are the escalation details?
      4. What are the circuit IDs and technical handoff details?
      5. What are we paying?
      6. When do contracts renew, and when must notice be sent?
      7. Is the service available, and how has it performed?

    The diversity banner sits above the service list on purpose. It converts a fact
    buried in a dependency table into something a person acts on, and it is the single
    highest-value pixel on the page.  *@

<PageTitle>@(_location is null ? "Location" : _location.LocationCode + " " + _location.Name) — FC Telecom Manager</PageTitle>

@if (_notFound)
{
    <div class="page-message">
        <h1>Location not found</h1>
        <p>That location does not exist, or you do not have access to it.</p>
        <a class="button" href="/locations">Back to locations</a>
    </div>
}
else if (_location is null)
{
    <p class="loading">Loading location…</p>
}
else
{
    <nav class="breadcrumb"><a href="/locations">← Locations</a></nav>

    <header class="record-head">
        <h1>@_location.LocationCode · @_location.Name</h1>
        <p class="record-subtitle">
            @_location.AddressSingleLine · @_location.TimeZoneId
            @if (_location.RegionName is not null)
            {
                <text> · Region: @_location.RegionName</text>
            }
        </p>
        <p class="record-meta">
            <span class="badge badge--@_location.Criticality.ToString().ToLowerInvariant()">
                ◆ @_location.Criticality
            </span>
            <span>Acceptable outage: @_location.AcceptableOutageMinutes min</span>
            @if (_location.CostCenterCode is not null)
            {
                <span>Cost centre: @_location.CostCenterCode</span>
            }
            @if (_location.ItOwnerName is not null)
            {
                <span>IT owner: @_location.ItOwnerName @_location.ItOwnerPhone</span>
            }
        </p>
    </header>

    @if (_location.Diversity.Verdict is DiversityVerdict.SharedRisk or DiversityVerdict.NoBackup)
    {
        <section class="banner banner--warn" role="alert">
            <h2>⚡ Diversity risk</h2>
            <p>@_location.Diversity.Summary</p>
            @if (_location.Diversity.Risks.Count > 0)
            {
                <ul>
                    @foreach (DiversityRisk risk in _location.Diversity.Risks)
                    {
                        <li>
                            <strong>@risk.Description</strong>
                            @if (!string.IsNullOrWhiteSpace(risk.Evidence))
                            {
                                @* Naming the evidence matters. An unsourced warning gets
                                   dismissed the second time somebody sees it. *@
                                <span class="muted"> Evidence: @risk.Evidence</span>
                            }
                        </li>
                    }
                </ul>
            }
        </section>
    }
    else if (_location.Diversity.Verdict == DiversityVerdict.Unassessed)
    {
        <section class="banner banner--info">
            <p>
                ❓ @_location.Diversity.Summary
            </p>
        </section>
    }

    <section class="panel">
        <h2>Services at this location</h2>

        @if (_location.Services.Count == 0)
        {
            <EmptyState Title="No services recorded here"
                        Explanation="Add a service, or import your circuit inventory from CSV."
                        ActionUrl="/imports" ActionLabel="Import services" />
        }
        else
        {
            <ul class="service-cards">
                @foreach (LocationServiceSummaryDto service in _location.Services)
                {
                    <li class="service-card">
                        <div class="service-card__head">
                            <StatusChip State="service.MonitorState" />
                            <span class="service-card__role">@RoleLabel(service.ServiceRole)</span>
                            <span class="service-card__type">@Humanize(service.ServiceType)</span>
                            <a class="service-card__link" href="/services/@service.ServiceId">Details →</a>
                        </div>

                        <div class="service-card__body">
                            <p class="service-card__carrier">
                                <strong>@service.CarrierName</strong>
                                @if (service.CircuitId is not null)
                                {
                                    <text> · Circuit ID </text><code>@service.CircuitId</code>
                                }
                                @if (service.AccountNumber is not null)
                                {
                                    <text> · Acct </text><code>@service.AccountNumber</code>
                                }
                            </p>

                            <p class="service-card__tech">
                                @if (service.DownloadKbps is > 0)
                                {
                                    <text>@DisplayFormat.Bandwidth(service.DownloadKbps) ↓ / @DisplayFormat.Bandwidth(service.UploadKbps) ↑</text>
                                    @if (service.CommittedKbps is > 0)
                                    {
                                        <text> · CIR @DisplayFormat.Bandwidth(service.CommittedKbps)</text>
                                    }
                                    else
                                    {
                                        <text> · <span class="muted">no CIR (best effort)</span></text>
                                    }
                                }
                                @if (service.HandoffSummary is not null)
                                {
                                    <text> · @service.HandoffSummary</text>
                                }
                            </p>

                            <p class="service-card__support">
                                Support @(service.CarrierSupportPhone ?? "—") · @service.SupportPriority
                                @if (service.HasIpData)
                                {
                                    <text> · <span class="muted">static IPs recorded</span></text>
                                }
                            </p>

                            <p class="service-card__commercial">
                                @if (service.MonthlyCost is not null)
                                {
                                    <text>@DisplayFormat.Money(service.MonthlyCost)/mo</text>
                                }
                                @if (service.ContractNumber is not null)
                                {
                                    <text> · Contract @service.ContractNumber</text>
                                }
                                @if (service.NoticeDeadline is { } deadline)
                                {
                                    <text> · Notice by @deadline.ToString("yyyy-MM-dd")</text>
                                    <span class="badge badge--@DeadlineClass(service.DaysUntilNotice)">
                                        @DeadlineIcon(service.DaysUntilNotice) @service.DaysUntilNotice days
                                    </span>
                                    @if (!service.NoticeDeadlineConfirmed)
                                    {
                                        <span class="badge badge--warn">needs review</span>
                                    }
                                }
                            </p>

                            <p class="service-card__availability">
                                @if (service.Availability30Day is { } availability)
                                {
                                    <text>
                                        30-day availability @DisplayFormat.Percent(availability)
                                        <span class="muted">(coverage @DisplayFormat.Percent(service.Coverage30Day, 0))</span>
                                    </text>
                                }
                                else
                                {
                                    <span class="muted">No availability data.</span>
                                }
                            </p>

                            @foreach (string warning in service.Warnings)
                            {
                                <p class="service-card__warning">⚠ @warning</p>
                            }
                        </div>
                    </li>
                }
            </ul>
        }
    </section>

    <div class="panel-row">
        <section class="panel">
            <h2>At a glance</h2>
            <dl class="kv">
                @if (_location.TotalMonthlyCost is not null)
                {
                    <dt>Total monthly</dt>
                    <dd>@DisplayFormat.Money(_location.TotalMonthlyCost, _location.CurrencyCode)</dd>
                    <dt>Annualized</dt>
                    <dd>@DisplayFormat.Money(_location.TotalAnnualizedCost, _location.CurrencyCode)</dd>
                    <dt>Cost per Mbps (WAN)</dt>
                    <dd>@DisplayFormat.Money(_location.CostPerMbps, _location.CurrencyCode)</dd>
                }
                <dt>Services</dt>
                <dd>@_location.Services.Count · monitored @_location.MonitoredServiceCount</dd>
            </dl>
        </section>

        <section class="panel">
            <h2>Next deadlines</h2>
            @if (_location.UpcomingDeadlines.Count == 0)
            {
                <p class="muted">No contract deadlines recorded for services at this location.</p>
            }
            else
            {
                <ul class="deadline-list">
                    @foreach (UpcomingDeadlineDto deadline in _location.UpcomingDeadlines)
                    {
                        <li>
                            <time datetime="@deadline.Date.ToString("yyyy-MM-dd")">@deadline.Date.ToString("yyyy-MM-dd")</time>
                            <span>@deadline.Description</span>
                            <span class="badge badge--@DeadlineClass(deadline.DaysAway)">
                                @DeadlineIcon(deadline.DaysAway) @deadline.DaysAway days
                            </span>
                            @if (!deadline.Confirmed)
                            {
                                <span class="badge badge--warn">unconfirmed</span>
                            }
                        </li>
                    }
                </ul>
            }
        </section>
    </div>

    @if (_location.Contacts.Count > 0)
    {
        <section class="panel">
            <h2>Site contacts</h2>
            <table class="data-table">
                <thead><tr><th>Name</th><th>Role at site</th><th>Phone</th><th>Email</th></tr></thead>
                <tbody>
                    @foreach (LocationContactDto contact in _location.Contacts)
                    {
                        <tr>
                            <th scope="row">@contact.FullName @(contact.IsPrimary ? "★" : "")</th>
                            <td>@contact.RoleAtLocation</td>
                            <td>@(contact.PhoneNumber ?? "—")</td>
                            <td>@(contact.Email ?? "—")</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    }
}

@code {
    [Parameter] public Guid Id { get; set; }

    private LocationDetailDto? _location;
    private bool _notFound;

    protected override async Task OnParametersSetAsync()
    {
        _notFound = false;
        _location = null;

        try
        {
            _location = await Locations.GetDetailAsync(Id);
        }
        catch (RecordNotFoundException)
        {
            // Not found and forbidden produce the same page deliberately. Distinguishing
            // them tells someone that a record with this ID exists.
            _notFound = true;
        }
        catch (PermissionDeniedException)
        {
            _notFound = true;
        }
    }

    private static string RoleLabel(ServiceRole role) => role switch
    {
        ServiceRole.Primary => "PRIMARY",
        ServiceRole.Secondary => "BACKUP",
        ServiceRole.Tertiary => "TERTIARY",
        _ => "STANDALONE",
    };

    private static string Humanize(ServiceType type) => type switch
    {
        ServiceType.MplsVpn => "MPLS / private WAN",
        ServiceType.PointToPoint => "Point-to-point",
        ServiceType.SdWanUnderlay => "SD-WAN underlay",
        ServiceType.CellularBackup => "Cellular backup",
        ServiceType.FixedWireless => "Fixed wireless",
        ServiceType.SipTrunk => "SIP trunk",
        ServiceType.HostedVoice => "Hosted voice",
        ServiceType.AlarmLine => "Alarm line",
        ServiceType.ElevatorLine => "Elevator line",
        ServiceType.EmergencyLine => "Emergency line",
        ServiceType.FaxLine => "Fax line",
        _ => type.ToString(),
    };

    private static string DeadlineClass(int? days) => days switch
    {
        null => "neutral",
        < 0 => "urgent",
        <= 30 => "urgent",
        <= 90 => "warn",
        _ => "ok",
    };

    private static string DeadlineIcon(int? days) => days switch
    {
        null => "○",
        < 0 => "⛔",
        <= 30 => "⚠",
        <= 90 => "⏰",
        _ => "○",
    };
}
