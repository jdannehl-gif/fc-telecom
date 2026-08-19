# FC Telecom Manager

A single source of truth for ISP and telecom services across a multi-location
organization: circuits, carriers, escalation paths, static addressing, cost history,
contracts, renewal deadlines, and service availability.

**Status:** Phase 1 vertical slice. Design package complete; inventory slice implemented.

---

## ⚠ Read this before you build

**This code has not been compiled.** It was written in an environment with no .NET SDK
and no access to NuGet, so `dotnet build` has never run against it. The design, the
schema, the calculations, and the tests are all written carefully and cross-checked by
hand — but expect to fix a handful of compile errors on the first build. Package versions
in `Directory.Packages.props` in particular are pinned to what was current at the time of
writing and may need adjusting to what actually exists on your feed.

Everything below assumes you are running the first build yourself.

---

## Quick start

```bash
# 1. Dependencies (SQL Server 2022 + Azurite)
docker compose up -d

# 2. Restore and build
dotnet restore
dotnet build

# 3. Create the database
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate \
    --project src/FcTelecom.Infrastructure \
    --startup-project src/FcTelecom.Web
dotnet ef database update \
    --project src/FcTelecom.Infrastructure \
    --startup-project src/FcTelecom.Web

# 4. Run (seeds demo data automatically in Development)
dotnet run --project src/FcTelecom.Web

# 5. Tests
dotnet test
```

The Development configuration seeds a demo estate: 12 locations, 9 vendors, ~40 services
across 8 service types, effective-dated cost history, 3 contracts, and monitoring data.

**The demo data is deliberately imperfect.** It includes two locations whose "diverse"
backup shares a last-mile provider, several services with no circuit ID, a legacy POTS
group with no contract terms on file, an unmonitored SIP trunk, a circuit disconnected six
months ago that is still being billed, and a legacy T1 at an absurd cost per Mbps. Seed
data where everything is complete and healthy demonstrates nothing — every report that
matters renders as an empty state and nobody finds out whether it works.

### Signing in locally

Entra ID is the only authentication path in a deployed environment. For local development,
set `Security:EnableDevAuthBypass` in `appsettings.Development.json` and use the role
switcher. That bypass is inside `#if DEBUG`, gated on configuration, **and** asserted
unreachable in a Release build by a test — three locks, because a development auth bypass
reaching production is the single most common way a carefully-designed authorization model
ends up with no authorization at all.

To use real Entra ID locally, fill in the `AzureAd` section and register
`https://localhost:7139/signin-oidc` as a redirect URI.

---

## What is here

| | |
|---|---|
| **Design package** | [`docs/`](docs/) — 11 documents covering architecture, domain model, backlog, screens, threat model, deployment, monitoring design, and the integration validation findings. |
| **Domain** | ~60 entities across 8 business modules, plus pure calculation classes for availability, spend, notice deadlines, diversity, and outage correlation. |
| **Application** | Query and command services with permission-scoped projections. |
| **Infrastructure** | EF Core with an audit interceptor, soft delete, field-level encryption, Blob document storage, Excel export, and the demo seeder. |
| **Web** | Blazor Web App (InteractiveServer) — dashboard, locations, services, vendors, global search — plus a JSON API with OpenAPI. |
| **Tests** | Domain unit tests for every calculation, and architecture tests that fail the build on a layering or authorization-model violation. |
| **Infra** | Bicep for the whole environment; GitHub Actions and an Azure DevOps equivalent. |

Start with [`docs/00-assumptions-and-questions.md`](docs/00-assumptions-and-questions.md) —
it lists the five open questions, each with a recommended default.

---

## Naming: code vs. documents

Three entities are named differently in code than in the design documents, each for a
concrete reason:

| Document | Code | Why |
|---|---|---|
| `Directory` module | `Organization` | A namespace named `Directory` shadows `System.IO.Directory` and turns every file operation in the assembly into a puzzle. |
| `Service` | `TelecomService` | A type named `Service` in a codebase full of `IServiceProvider`, `ServiceCollection`, and application services reads ambiguously in every file that touches it. |
| `Monitor` | `ServiceMonitor` | `System.Threading.Monitor` is in scope everywhere, and a domain type of the same name makes `lock`-adjacent code genuinely confusing. |

Everything else matches.

---

## Design decisions worth knowing before you change anything

**Cost history is append-only and effective-dated.** A price change closes the current
`ServiceCost` row and inserts a new one. Two database constraints enforce it — a check
constraint on the date range and a filtered unique index allowing one open row per service
— so a bug that tries to leave two current prices on one circuit fails loudly instead of
silently doubling that circuit's contribution to every spend report. The UI action is
labelled "Record a price change", not "Edit cost", for the same reason.

**Unknown is not Up.** A circuit with no monitoring coverage is `Unknown`, and unknown time
is removed from the availability denominator rather than counted as available. Every
availability figure is displayed with its coverage percentage next to it. 99.94% over 96%
coverage and 99.94% over 40% coverage are completely different statements, and presenting
them identically is a lie of omission.

**A service has four vendor roles, not one.** Carrier, reseller, last-mile provider, and
underlying network owner are separate foreign keys. You buy from a reseller, who resells a
carrier, who leases fibre from the incumbent, whose backbone belongs to someone else.
Collapse these and "is our backup real?" becomes unanswerable — which is one of the seven
questions the location page exists to answer.

**Static IP data is encrypted at the application layer and gated on its own permission.**
No role implies `ServiceIpData.Read`; it is attached to Network Engineer explicitly and
grantable per user with a recorded justification. Query handlers project the fields away
entirely when the caller lacks it, so the value never enters the render tree. Masking in
the UI is a decoration, not a control.

**Three contract dates, deliberately kept apart.** Contract end date, per-service end date,
and cancellation-notice deadline. Conflating any two is the most expensive modelling error
in this domain. The system *proposes* a notice deadline; a person *confirms* it — and an
unconfirmed deadline still raises alerts, labelled as unconfirmed, because suppressing an
alert on a technicality is worse than sending an uncertain one.

**Notifications ship disabled.** Every seeded rule starts switched off. A demo import that
fires four hundred emails on day one is how a rollout becomes an incident.

**Migrations run in the pipeline, not at startup.** `Database.Migrate()` on boot is
convenient and it is how two instances race each other into a half-applied schema during a
slot swap. Migrations are applied as a reviewed idempotent script, additive only; dropping
a column is split across two releases so a swap-back is always safe.

---

## Two validated findings

**IT Glue integration is viable.** JSON:API format, `x-api-key` header, 3000 requests per
rolling 5-minute window, 1000 records per page maximum, regional base URLs. The recommended
approach is a **hybrid**: sync the fields a technician needs at 2am without leaving IT Glue
(circuit ID, carrier, account, support phone, demarc, CPE), deep-link back for everything
else, and never sync static IP data. Detail in
[`docs/09-integration-validation.md`](docs/09-integration-validation.md).

**MikroTik The Dude has no supported programmatic interface.** No REST API, no documented
export, no outbound SNMP traps, no webhooks. Its only machine-readable output is **syslog**.
So we build a syslog ingest adapter that is explicitly advisory — it can raise suspicion but
cannot alone confirm an outage — and a documented transition path to the probe agent. Nothing
in this application depends on The Dude, and nothing will.

---

## What is not built yet

Phase 1 delivers the inventory slice. Still to come, in order:

1. Cost and contract editing UI (the schema and calculations are done)
2. CSV/Excel import with dry-run preview
3. Transactional outbox drain, Graph email and Teams alerts
4. Invoice reconciliation
5. Monitoring: probe agent, correlation engine, availability rollups
6. IT Glue one-way sync

The full ordered backlog is in [`docs/04-backlog.md`](docs/04-backlog.md).

---

## Runbooks

- [Local setup](docs/runbooks/local-setup.md)
- [Deploy](docs/runbooks/deploy.md)
- [Restore and disaster recovery](docs/runbooks/restore-and-dr.md)
- [Rotate secrets](docs/runbooks/rotate-secrets.md)
- [Onboard a probe agent](docs/runbooks/onboard-a-probe-agent.md)
