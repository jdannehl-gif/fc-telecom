# 00 — Assumptions and Approved Decisions

**Product:** FC Telecom Manager — ISP & telecom service management for a multi-location organization
**Status:** Phase 1 baseline — approved 2026-08-19, subject to compilation, testing, and Azure validation
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

## 3. Approved answers to the five questions

Answered and approved 2026-08-19. These are now baseline, not proposals.

### Q1 — Probe-agent placement · **Two self-hosted agents plus the Azure perspective**

- **Primary agent:** a dedicated small VM in the **Dorchester datacenter**.
- **Secondary agent:** a geographically separate major location with independent power and
  internet, **not** sharing a virtualization cluster or primary network failure domain with
  the Dorchester agent.
- **Never on a domain controller.**
- **Outbound-only** agent-to-cloud communication, without exception.
- The agent is a **cross-platform .NET Worker**. **Windows Service** is the initially
  documented and supported deployment; systemd and container hosting stay available and are
  recorded on `Probe.HostKind`.
- Quorum and confidence rules are seeded for two agents plus Azure.
- **The inventory application remains fully usable with one agent or none.** Monitoring is
  decoupled by design and every availability figure is nullable.

*Encoded as:* `Probe.FailureDomain` (free text, e.g. `Dorchester DC / cluster-A / feed-1`)
and `Probe.HostKind`. Two probes are only two perspectives if they can fail independently —
same cluster or same UPS makes them one perspective wearing two hats, and the quorum rule
would count it twice and report a confident outage that is really a power event. Recorded
rather than enforced, because sometimes one perspective is all there is.

### Q2 — Monitoring targets · **Both public circuit targets and internal location targets**

- From Azure: the public IP or other suitable external endpoint for **every individual
  circuit**, monitored separately per circuit at multi-WAN sites.
- From the agents: **one internal always-on target per location** wherever routing permits.
- Preferred internal target is the **branch firewall's LAN/management address** or the
  **management VLAN gateway**. Explicitly **not** a workstation or printer.
- The internal target indicates overall location and VPN reachability.
- Circuit reachability, internal location reachability, monitoring-agent failure, and
  unknown status are **four distinct states**, never collapsed.
- Locations with no internal target appear as **monitoring coverage gaps**.

*Encoded as:* `MonitorTargetKind` (`PublicCircuitEndpoint` / `InternalLocationTarget`),
`InternalTargetKind` (with an explicit `NotSuitable` value so an existing bad choice is
recorded and reported rather than silently trusted), and
`CoverageGapReason.NoInternalTarget`. Agent failure is already distinguished by
`OutageClassification.MonitoringFailure`, which records a coverage gap instead of opening
an outage.

### Q3 — Authoritative location source · **Controlled CSV/Excel import only, in Phase 1**

- A permanent enterprise **`LocationCode`** is the required natural key.
- **Location name is never an integration key.**
- Agris may supply the initial business-location list, but **not every monitored facility
  exists as a conventional Agris location**.
- An **optional external-system identifier** is carried separately; the permanent
  `LocationCode` is never synonymous with an Agris value.
- This application is the system of record for telecom-specific location detail. A
  scheduled read-only integration with Agris or another facility master can be evaluated later.

*Encoded as:* a `LocationExternalIdentifier` child table keyed on `(SystemKey, Value)`
rather than an `AgrisLocationCode` column. A tower site, a leased closet, or a warehouse
annexe can be a real telecom location with no counterpart in the facility master — and a
nullable column named after one system invites exactly the conflation this avoids. It also
means a second external system costs a row, not a migration.

### Q4 — Ambiguous contract deadlines · **Proposed-and-confirmed workflow**

The system calculates a proposed `NoticeDeadlineDate` and it **remains explicitly
unconfirmed until a person reviews it**. Unconfirmed deadlines **continue producing alerts**
and appear in a distinct **Needs Review** state.

Recorded for every deadline:

| Field | Purpose |
|---|---|
| `ProposedNoticeDeadlineDate` | What the system calculated |
| `NoticeDeadlineDate` | The confirmed or overridden date — the one alerts use |
| `NoticeDeadlineConfirmed` | Whether a person has reviewed it |
| `NoticeDeadlineConfirmedByUserId` / `…ConfirmedBy` | Who confirmed it |
| `NoticeDeadlineConfirmedUtc` | When |
| `NoticeDeadlineInterpretationNotes` | How they read the contract language |
| `NoticeDeadlineSourceDocumentId` | The agreement or amendment it was read from |
| `NoticeDeadlineWasOverridden` | Derived: confirmed date differs from the proposal |

**A calculated deadline is never silently treated as authoritative.**

The interpretation note is what makes a confirmed deadline defensible a year later, when
the person who confirmed it has moved on and someone is arguing with a carrier about
whether notice was timely. `NoticeDeadlineWasOverridden` is derived rather than stored, and
is surfaced because a systematically wrong proposal is worth noticing: if reviewers keep
overriding the calculation the same way, the calculation is wrong.

### Q5 — Notifications · **Created, and left disabled until import review and testing are complete**

**Renewal and notice deadlines**

- Email the **contract owner** and a configurable **shared telecom/procurement mailbox**.
- Post to a configurable **Teams channel**.
- Fire at **180, 120, 90, 60, 30** days.
- **Escalate at 60 days** if the deadline remains unconfirmed **or** no action has been recorded.
- **Escalate at 30 days** to the contract owner, procurement, and a configurable **IT
  leadership** recipient.

**Confirmed outages**

- Post immediately to a configurable **IT Operations/Help Desk Teams channel**.
- Email a configurable **Help Desk distribution list**.
- **Never sent from a single advisory Dude syslog event** — confirmation requires the
  monitoring quorum rules. An ingested probe raises `Suspect`, never `Down`.

**Integration and probe failures**

- Notify application administrators.
- Probe failures are reported as **monitoring coverage loss / Unknown status**, never as
  confirmed location outages.

**All** recipients, schedules, escalation rules, and channels are editable in the
application. A **test-notification** function and a **preview** showing exactly who would
receive an alert are required before a rule can be enabled.

*Encoded as:* `NotificationChannel` becomes a flags enum (one event legitimately goes to
email and Teams at once), and escalation becomes a `NotificationEscalationStep` child
collection rather than a pair of fields — because "chase at 60 if unconfirmed" and "tell
leadership at 30 regardless" are two different audiences under two different conditions,
and flattening them loses one. The preview is
`NotificationAudienceResolver`, a pure function with its own test suite: the most common
notification failure is not a delivery bug but a rule that reaches nobody, or four hundred
people, and nobody finding out until it fired.

---

## 4. Things we are explicitly *not* doing in Phase 1

Stating these prevents scope creep from being mistaken for progress.

- No embedded Power BI. We ship SQL views and Excel exports shaped for Power BI consumption; embedding is deferred until someone asks for a specific report that exports cannot serve.
- No bidirectional IT Glue sync. Outbound only until ownership and conflict rules are written down.
- No automated invoice OCR/extraction. Phase 5.
- No map as a required navigation path. Latitude/longitude are captured; a map view is additive.
- No mobile app. The web UI is responsive and the outage view is explicitly designed for a phone.
- No ticketing integration. The schema carries `InternalTicketNumber` as free text so the data is not lost in the meantime.
