# 04 — Prioritized Product Backlog

Three buckets: **MVP** (Phase 1, the first release), **Next** (Phases 2–3), **Later** (Phases 4–5).

Sizing is relative (S / M / L / XL), not hours. Anything XL is a signal it should be split before it is started.

---

## MVP — Phase 1: Foundation and inventory

*Definition of success: an engineer can find any circuit in under 15 seconds during an outage, and Finance can answer "what do we spend at this location" without a spreadsheet.*

### Foundation

| # | Story | Size | Notes |
|---|---|---|---|
| F-01 | Solution scaffold with Domain/Application/Infrastructure/Web boundaries and an architecture test that fails the build on a forbidden reference | M | The test is the point; without it the layering rots in a month |
| F-02 | EF Core model, `DbContext`, initial migration, SQL Server via docker-compose | L | |
| F-03 | Audit interceptor writing `AuditEntry` in the same transaction as the change | M | Append-only enforced by SQL grants, not convention |
| F-04 | Soft-delete (`ISoftDeletable`) with global query filter and `IncludeArchived()` escape hatch | S | |
| F-05 | Seed and demo data: 12 locations, 9 vendors, ~40 services across 8 service types, cost history, 6 contracts, 3 outages | M | Realistic enough to demo; deliberately includes two "fake diversity" cases and three incomplete records |
| F-06 | Health checks (`/health/live`, `/health/ready`) covering DB, Blob, Key Vault, outbox depth, agent heartbeat | S | |
| F-07 | Serilog → App Insights with correlation ID and a redaction processor | M | Redaction is not optional — see threat model |
| F-08 | OpenAPI document generation for `/api/*` | S | |

### Identity and authorization

| # | Story | Size | Notes |
|---|---|---|---|
| A-01 | Entra ID OIDC sign-in via `Microsoft.Identity.Web` | M | |
| A-02 | `EntraGroupRoleMap` table + admin UI to map group object IDs to roles | M | Object IDs only. Display names are cached for readability and never used as keys |
| A-03 | Permission claims materialized at sign-in; ~23 named permissions; policy registration | M | |
| A-04 | Fallback authorization policy (authenticated by default) so a missing attribute fails closed | S | |
| A-05 | Field-level projection: sensitive properties dropped in the query handler, not masked in the UI | M | The value must never enter the render tree |
| A-06 | `ServiceIpData.Read` as a per-user grantable permission independent of role | S | |
| A-07 | Automated authorization tests: every endpoint × every role, asserting allow/deny | L | This is the test suite that matters most. It is a fixture-driven matrix, not 400 hand-written tests |
| A-08 | Local development auth bypass with a role switcher, **compiled out of Release builds** | S | `#if DEBUG` plus a config gate. Two locks, because this one is dangerous |

### Inventory (the vertical slice)

| # | Story | Size | Notes |
|---|---|---|---|
| I-01 | Locations: list with filter/sort/column selection, detail, create, edit, archive | L | |
| I-02 | Vendors: list, detail, create, edit, archive; contacts and escalation procedures | L | |
| I-03 | Services/circuits: list, detail, create, edit, archive | XL | *Split:* base service → type-specific detail panels → identifiers → IP assignments → dependencies |
| I-04 | Contacts: shared entity usable from location and vendor context | M | |
| I-05 | Service identifier aliases (carrier-specific ID types) | S | |
| I-06 | Static IP assignments with app-level encryption and permission-gated reveal | L | Reveal writes a `SecurityEvent` |
| I-07 | Service dependency graph with `Confidence` and `Evidence` | M | |
| I-08 | **Location detail page** answering the seven required questions on one screen | L | This is the acceptance test for the whole slice |
| I-09 | Document upload/download to Blob with per-request user-delegation SAS | M | |
| I-10 | Per-record audit history panel | M | |

### Costs and contracts

| # | Story | Size | Notes |
|---|---|---|---|
| C-01 | Effective-dated `ServiceCost` with non-overlap constraint and "change price" workflow | L | Editing a cost creates a new row; the UI must make this obvious |
| C-02 | One-time charges | S | |
| C-03 | Contract CRUD with the three distinct dates | L | |
| C-04 | Notice deadline proposal + human confirmation workflow | M | |
| C-05 | Contract ⟷ service many-to-many with per-service end dates | M | |
| C-06 | Contract amendments with document attachment | S | |
| C-07 | Cost allocation across cost centers by percent | M | |

### Search, export, dashboard

| # | Story | Size | Notes |
|---|---|---|---|
| S-01 | Global search: location, circuit ID, account number, carrier, phone number, contract number, static IP — permission-scoped | L | IP search uses the deterministic hash; results a user may not see are excluded, not shown-and-blocked |
| S-02 | Saved views (filters + columns) per user, optionally shared | M | |
| S-03 | Excel export from every list view, matching `rpt.*` semantics | M | Writes a `SecurityEvent` |
| S-04 | Portfolio dashboard: active locations, active services, monthly + annualized spend, expiring contracts, missing documentation, current outages, availability, locations without true diversity | L | Every tile links to a filtered list — a number nobody can drill into is decoration |
| S-05 | `rpt.*` SQL views + read-only reporting SQL principal | M | |

### Import

| # | Story | Size | Notes |
|---|---|---|---|
| M-01 | CSV/XLSX import templates for locations, vendors, services, costs, contracts | M | |
| M-02 | Guided import: upload → parse → validate → dry-run preview → commit | XL | *Split:* parse+validate → preview UI → duplicate detection → transactional commit |
| M-03 | Duplicate detection on circuit ID, vendor+account, location code | M | |
| M-04 | Per-row error reporting with downloadable error file | M | |

### Outage response

| # | Story | Size | Notes |
|---|---|---|---|
| O-01 | **Fast outage view**: carrier support number, circuit ID, account number, demarc/CPE, last known state, related incidents | L | Phone-first layout. This page must render correctly on a 375px viewport and survive a page reload |
| O-02 | "Copy support summary" button producing a paste-ready block for a carrier ticket | S | Small story, disproportionate daily value |
| O-03 | Manual incident recording (no monitoring required) | M | Incidents must work before monitoring exists |

### Non-functional

| # | Story | Size | Notes |
|---|---|---|---|
| N-01 | Bicep for App Service, SQL, Storage, Key Vault, App Insights, Function App, managed identities, RBAC | L | |
| N-02 | GitHub Actions: build → test → migrate → deploy to slot → swap. Azure DevOps YAML equivalent documented | L | |
| N-03 | Dockerfile + docker-compose for local dev | M | |
| N-04 | Unit tests for all domain calculations | M | |
| N-05 | Integration tests against SQL Server via Testcontainers | L | |
| N-06 | Playwright E2E: sign in → find circuit → view cost → export | M | |
| N-07 | Accessibility pass: keyboard navigation, focus management, contrast, status conveyed by icon+text not colour alone | M | |
| N-08 | Documentation: local setup, config reference, deploy, restore, integration setup | M | |

---

## NEXT — Phases 2 and 3

### Phase 2: Renewals, alerts, finance controls

| # | Story | Size |
|---|---|---|
| P2-01 | Transactional outbox + Functions drain with retry, backoff, dedupe | L |
| P2-02 | Microsoft Graph email sender | M |
| P2-03 | Microsoft Teams channel messages via Graph; Power Automate webhook alternative | M |
| P2-04 | Notification rule engine with UI (event type → channel → recipients → thresholds) | L |
| P2-05 | Renewal alerts at 180/120/90/60/30 days, per-threshold dedupe | M |
| P2-06 | Renewal pipeline view with owner assignment and decision capture (renew / renegotiate / cancel) | L |
| P2-07 | Invoice import with vendor-specific column profiles | L |
| P2-08 | Invoice reconciliation: expected vs actual, variance thresholds, dispute workflow | XL |
| P2-09 | Unexpected-invoice-change alerts | M |
| P2-10 | Cost-per-Mbps and outlier detection report | M |
| P2-11 | Spend reports by carrier, region, location, service type, cost center, business unit | L |
| P2-12 | Disconnected-but-still-billed report | M |
| P2-13 | Data completeness report naming exactly which records are missing which fields | M |
| P2-14 | Diversity risk report: no backup / same last-mile / same conduit / same upstream | L |

### Phase 3: Monitoring

| # | Story | Size |
|---|---|---|
| P3-01 | Monitoring schema, `IMonitoringProvider` abstraction, simulated provider | L |
| P3-02 | Azure Functions check executor (HTTP/HTTPS/TCP/DNS) | L |
| P3-03 | Probe agent: worker service, outbound long-poll work pull, ICMP/TCP/HTTP/DNS execution | XL |
| P3-04 | Agent authentication (Entra client credentials) + HMAC-signed result payloads + replay protection | L |
| P3-05 | Agent offline tolerance: local result buffering, batch upload on reconnect, clock-skew handling | L |
| P3-06 | Outage correlation state machine with debounce and probe quorum | XL |
| P3-07 | Failure classification: carrier vs site vs CPE vs monitoring-system | L |
| P3-08 | Maintenance windows incl. recurring, with exclusion that preserves the underlying event | M |
| P3-09 | Coverage gap tracking and `Unknown` state | M |
| P3-10 | Hourly/daily/monthly rollups with `LowConfidence` flagging | L |
| P3-11 | Availability reporting by location, circuit, carrier, month, rolling 30/90/365 | L |
| P3-12 | SLA comparison and service-credit candidate identification | M |
| P3-13 | Latency and packet-loss trend charts | M |
| P3-14 | Outage → incident workflow with carrier ticket tracking and MTTR | L |
| P3-15 | Raw-result retention job | S |

### Phase 4: IT Glue and other integrations

| # | Story | Size |
|---|---|---|
| P4-01 | IT Glue client: JSON:API, `x-api-key`, rate limiter below 3000/5min, resilience pipeline | L |
| P4-02 | Flexible asset type provisioning/detection for "ISP & Telecom Circuit" | M |
| P4-03 | Field mapping configuration page with sensitive-field blocking | L |
| P4-04 | Sync preview / dry run showing exactly what would be created and updated | L |
| P4-05 | One-way outbound sync (manual + scheduled) with `ExternalRecordLink` idempotency | XL |
| P4-06 | Per-record sync error log, retry, and orphan detection | M |
| P4-07 | Deep-link back from IT Glue to the circuit record here | S |
| P4-08 | The Dude syslog ingestion adapter (see `09-integration-validation.md`) | L |
| P4-09 | Generic webhook ingest for other monitoring platforms | M |

---

## LATER — Phase 5: Optimization

| # | Story | Size |
|---|---|---|
| P5-01 | Carrier scorecards: availability, MTTR, ticket responsiveness, billing accuracy | L |
| P5-02 | Spend optimization: duplicate services, oversized circuits, unused capacity | XL |
| P5-03 | Contract benchmarking against internal price history per service type and speed tier | L |
| P5-04 | Automated invoice/document extraction (Azure AI Document Intelligence) with mandatory human review | XL |
| P5-05 | Capacity trend analysis and "nearing committed rate" alerts | L |
| P5-06 | Redundancy risk scoring across the whole portfolio | L |
| P5-07 | Location map view with outage overlay | M |
| P5-08 | Embedded Power BI, if exports prove insufficient | L |
| P5-09 | Ticketing integration (ServiceNow / Jira / Freshservice) | L |
| P5-10 | ERP/AP integration for invoice approval hand-off | XL |
| P5-11 | Bidirectional IT Glue sync — **only** after ownership and conflict rules are written and approved | XL |

---

## Deliberately out of scope

| Item | Why |
|---|---|
| Multi-tenancy | Single organization. Adding tenancy later is a schema change, but adding it now taxes every query for no current benefit |
| Native mobile app | The responsive web UI, particularly the outage view, covers the actual phone use case |
| Carrier portal scraping/automation | Fragile, frequently against terms of service, and a support burden that never ends |
| Storing vendor portal passwords | Explicit guardrail. A reference to your credential manager only |
| Real-time NetFlow / SNMP polling of circuit utilization | A different product. If utilization data is needed, ingest summaries from the platform that already collects it |
