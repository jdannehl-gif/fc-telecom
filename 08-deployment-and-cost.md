# 00 — Assumptions, Decisions, and Open Questions

**Product:** FC Telecom Manager — ISP & telecom service management for a multi-location organization
**Status:** Design baseline for Phase 1
**Last updated:** 2026-08-19

---

## 1. Decisions already confirmed

These four were confirmed at kickoff and are treated as settled. They are the ones that would have been expensive to reverse later.

| # | Decision | Choice | Why it sticks |
|---|---|---|---|
| D1 | UI framework | **Blazor Web App, `InteractiveServer` render mode** | One C# codebase, one build, one auth pipeline. Sensitive fields (static IP blocks, contract terms) are rendered server-side and never serialized to a browser the way a REST+SPA split forces. A small internal IT team can maintain it without a second toolchain. |
| D2 | Hosting | **Azure App Service (Linux)**, container-ready | Managed TLS, deployment slots, native Entra ID (Easy Auth optional but we use the app's own OIDC pipeline), lowest ops burden. A `Dockerfile` and clean container boundaries keep a later move to Container Apps a config change. |
| D3 | Session scope | Full design package → scaffold → vertical slice | — |
| D4 | Monitoring direction | **Self-hosted probe agent + Azure-native checks** | Two independent vantage points, which the availability math requires. App Service and Functions cannot emit ICMP; a self-hosted agent can, and it is the only way to see internal targets. |

---

## 2. Assumptions

Each is stated so you can veto it cheaply. None are load-bearing enough that reversing one in the next few weeks would be painful.

### Platform and runtime

- **A1 — .NET 10 (LTS).** Released November 2025, supported to November 2028. This is the current LTS and the correct target for a system that will be in service for years.
- **A2 — Azure SQL Database, single database, General Purpose serverless tier for non-production and Provisioned for production.** Data volume here is small (thousands of services, millions of raw check results at most); the cost driver is monitoring retention, not inventory.
- **A3 — SQL Server is the only supported database provider.** Local development uses SQL Server 2022 in Docker via `docker-compose`; integration tests use the same image. We deliberately do **not** support SQLite as a "quick start" path — EF Core provider drift between SQLite and SQL Server silently changes behaviour around `decimal` precision, computed columns, temporal tables, and collation, and that divergence eventually costs more than the Docker dependency.
- **A4 — Background work runs in Azure Functions (.NET isolated worker), Flex Consumption plan.** Timer triggers for scheduled sync/alert evaluation, queue triggers for fan-out. This keeps long-running work off the web tier so App Service can scale on request load alone.
- **A5 — Single tenant, single organization, single currency default (USD) with a currency column carried on every money row** so a future acquisition in another country does not require a schema migration.

### Identity and access

- **A6 — Entra ID with group-to-role mapping**, configured as data (an `EntraGroupRoleMap` table) rather than hardcoded app roles. Group object IDs are the mapping key, never group display names — display names are renameable and are not stable identifiers.
- **A7 — Roles are coarse; permissions are fine.** Five roles ship (Application Administrator, Network/Telecom Engineer, Procurement/Finance, Help Desk/Operations, Executive/Read Only), but authorization is enforced against ~20 named permissions. This is what makes "Procurement can see costs but not static IP blocks" expressible without inventing a sixth role every time a request arrives.
- **A8 — Static IP data is a separately grantable permission (`ServiceIpData.Read`), not implied by any role.** It can be attached to a Network Engineer by default and to a specific Procurement user by exception.

### Data and integrations

- **A9 — This application is the system of record for structured ISP/telecom data.** IT Glue receives a projection of it. Sync starts one-way, outbound only.
- **A10 — No credentials of any kind are stored in the application database.** Vendor portal access is recorded as a *reference* (a free-text pointer such as "1Password vault: Carriers → Lumen portal") plus, optionally, an IT Glue password record ID. Integration tokens live in Key Vault; the database stores only the secret's *name*.
- **A11 — Document storage is Azure Blob Storage** with server-side encryption, private containers, and time-limited user-delegation SAS URLs issued per download request. No public container, no permanent URLs.
- **A12 — Money is `decimal(19,4)`; bandwidth is stored as integer kilobits per second; durations are stored as integer seconds.** No floats anywhere near money or SLA math. Display units are a presentation concern.
- **A13 — All timestamps are stored UTC as `datetime2(3)`.** Locations carry an IANA time zone ID (`America/Chicago`), not a Windows time zone name, so the same value works on Linux App Service and in any future non-.NET consumer.

### Monitoring

- **A14 — Monitoring is decoupled from inventory.** The inventory MVP ships and is useful with zero monitors configured. Every availability figure is nullable and every dashboard tile degrades to "not monitored" rather than to "0%".
- **A15 — A circuit with no monitoring coverage is `Unknown`, never `Up`.** Unknown time is excluded from the eligible-time denominator rather than counted as available. This is the single most common way uptime reporting lies, and we design it out at the schema level.
- **A16 — Raw check results are retained 45 days by default (configurable); hourly rollups 13 months; daily rollups 7 years.** Availability reporting reads rollups, never raw.

### Financial

- **A17 — Cost history is effective-dated and append-only.** A price change closes the current `ServiceCost` row (`EffectiveTo`) and inserts a new one. Nothing is ever updated in place. Every historical report is therefore reproducible.
- **A18 — Invoice reconciliation compares an imported invoice line to the *expected* cost derived from the effective-dated cost record for that billing period**, and raises a variance rather than silently overwriting the expected cost.

---

## 3. Open questions

Five questions, each with a recommended default. If you say nothing, we build the default.

### Q1 — Where should the self-hosted probe agent run, and how many?

The availability design needs at least two independent vantage points. Azure Functions give one (public internet, no ICMP). The agent gives the other.

**Recommended default:** one agent at your primary datacenter/HQ and a second at a geographically separate large site, both on hardware you already have (a small VM or a container on an existing host). Two agents plus the Azure perspective means a single agent going offline degrades confidence rather than fabricating an outage.

**Why it matters now:** it determines whether the agent is a Windows Service, a systemd unit, or a container, and whether agent-to-cloud is outbound-only. *Our design is outbound-only long-polling in all three cases*, so this is cheap to defer — but the count affects the quorum rules we seed.

---

### Q2 — Do you want per-location internal targets monitored, or public circuit IPs only?

Monitoring only a circuit's public IP has a well-known blind spot: the carrier's CPE or your firewall answers while the transport behind it is degraded or the LAN behind it is down. Monitoring an internal target (a switch, an AP controller, a printer VLAN gateway) catches that, but requires the probe agent to have a path to the site.

**Recommended default:** monitor the public IP of every circuit from Azure *and* one internal target per location from the agent, where a path exists. Record locations without an internal target as a **coverage gap** on the dashboard rather than pretending coverage is complete.

---

### Q3 — What is the authoritative source for the location list today?

Most organizations already have one — Active Directory sites, an ERP location table, a store-master spreadsheet, or IT Glue itself. Whichever it is becomes the import template's shape and, later, the candidate for a scheduled inbound sync.

**Recommended default:** treat the CSV/Excel import as the only inbound path for Phase 1 and make `LocationCode` the natural key that must match your existing system. No integration keys on names.

---

### Q4 — How should contract *notice deadlines* be handled when the contract paperwork is ambiguous?

Real telecom contracts are frequently vague: "90 days prior to the end of the then-current term," where "then-current term" is itself disputed after an auto-renewal.

**Recommended default:** store `NoticeDeadlineDate` as an explicit, human-confirmable date field that the system *proposes* (from `EndDate − NoticePeriodDays`) but a person must **confirm**. Unconfirmed deadlines appear on the dashboard in a distinct "needs review" state. The alternative — computing it silently — produces a number nobody trusts, which defeats the purpose of the tool.

---

### Q5 — Who receives renewal and outage alerts, and through which channel first?

**Recommended default:** two notification rules ship seeded and disabled:
- *Renewal notice deadline* → email to the contract owner + a Teams channel via Microsoft Graph, at 180/120/90/60/30 days.
- *Outage confirmed* → Teams channel immediately, email to the Help Desk distribution list.

Both are editable in the UI. They ship **disabled** so a demo import cannot send 400 emails on day one.

---

## 4. Things we are explicitly *not* doing in Phase 1

Stating these prevents scope creep from being mistaken for progress.

- No embedded Power BI. We ship SQL views and Excel exports shaped for Power BI consumption; embedding is deferred until someone asks for a specific report that exports cannot serve.
- No bidirectional IT Glue sync. Outbound only until ownership and conflict rules are written down.
- No automated invoice OCR/extraction. Phase 5.
- No map as a required navigation path. Latitude/longitude are captured; a map view is additive.
- No mobile app. The web UI is responsive and the outage view is explicitly designed for a phone.
- No ticketing integration. The schema carries `InternalTicketNumber` as free text so the data is not lost in the meantime.
