# 01 — Architecture

## 1. Shape of the system

A **modular monolith** on ASP.NET Core 10, deployed as one web application plus one Azure Functions app, with an optional self-hosted probe agent outside Azure.

```
FcTelecom.Web (Blazor Web App, InteractiveServer)  ──┐
                                                     ├── FcTelecom.Application ── FcTelecom.Domain
FcTelecom.Worker (Azure Functions, isolated)      ──┘              │
                                                                   │
                                                  FcTelecom.Infrastructure
                                                   (EF Core, Blob, Graph,
                                                    IT Glue, Key Vault)
FcTelecom.ProbeAgent (self-hosted, outbound-only) ── FcTelecom.Contracts
```

Four layers, enforced by project references that only point inward:

| Project | Contains | References |
|---|---|---|
| `FcTelecom.Domain` | Entities, value objects, enums, domain rules, calculation logic (availability, annualized spend, cost-per-Mbps, notice deadlines). **No EF Core, no Azure SDK, no ASP.NET.** | *(nothing)* |
| `FcTelecom.Application` | Use cases (command/query handlers), DTOs, validation, authorization policy definitions, provider *interfaces* (`IMonitoringProvider`, `INotificationSender`, `IDocumentStore`, `IItGlueClient`, `IInvoiceImporter`). | Domain |
| `FcTelecom.Infrastructure` | EF Core `DbContext`, configurations, migrations, repositories, Blob/Graph/IT Glue/Key Vault implementations, outbox dispatcher. | Application, Domain |
| `FcTelecom.Web` | Blazor components, minimal API endpoints, auth wiring, DI composition root. | Application, Infrastructure, Domain |
| `FcTelecom.Worker` | Functions: timer-triggered alert evaluation, sync, rollup; queue-triggered fan-out. Shares the same Application/Infrastructure. | Application, Infrastructure |
| `FcTelecom.ProbeAgent` | Self-hosted worker. Pulls check assignments, executes ICMP/TCP/HTTP/DNS, posts signed results. | Contracts only |
| `FcTelecom.Contracts` | Wire DTOs shared between the agent and the API. Deliberately tiny and versioned. | *(nothing)* |

### Business modules

Inside `Domain` and `Application`, code is organized by **business module**, not by technical layer-within-layer. This is what makes the monolith survivable:

```
Application/
  Directory/      Locations, regions, cost centers, business units, tags
  Vendors/        Vendors, accounts, contacts, escalation procedures
  Services/       Services, circuits, identifiers, IP assignments, dependencies
  Financials/     Cost history, invoices, reconciliation, imports, allocation
  Contracts/      Contracts, amendments, renewal pipeline, notice deadlines
  Monitoring/     Monitors, probes, check results, outages, availability
  Integrations/   IT Glue, The Dude ingest, sync state, field mapping
  Notifications/  Rules, outbox, Graph email + Teams
  Platform/       Audit, security events, documents, saved views, search
```

Modules talk to each other through the Application layer's public interfaces and through domain events dispatched via the outbox — never by reaching into another module's repositories. If a module ever needs to be extracted into its own service, the seam is already there. We are not going to extract one, and that is fine.

---

## 2. Why this fits a Microsoft-centered IT team

The honest argument, not the brochure one.

**One language, one debugger.** Blazor Server means a validation rule written once in `Domain` runs identically in the UI and in the API. In a React split, that rule is written in C#, re-written in TypeScript, and drifts within six months. For a team whose job is running a network, not running a frontend build, that duplication is the actual cost.

**Entra ID is not a bolt-on.** `Microsoft.Identity.Web` handles OIDC, token caching, group claims, and downstream Graph calls in one configuration block. Group-to-role mapping becomes a database table you edit in the UI, not a manifest you edit in the portal and redeploy.

**Azure SQL is a boring, correct choice here.** The workload is relational (a circuit belongs to a location, is billed on an account, is covered by a contract, is watched by monitors). Point-in-time restore, automatic tuning, and Transparent Data Encryption arrive for free. Temporal tables give us change history at the storage layer as a backstop under the application audit log.

**The parts that must not be in the web tier, aren't.** Scheduled sync, alert evaluation, and availability rollups run in Functions. A slow IT Glue sync cannot make a location page hang, and a request spike cannot delay a renewal alert.

**The one place we deviate from Microsoft-native:** ICMP. Neither App Service nor Functions can send it. Anyone who tells you a cloud-only design can ping your circuits is wrong. The self-hosted agent is a small .NET worker you run on hardware you already own — still C#, still your stack, just not in Azure.

### What we considered and rejected

| Option | Why not |
|---|---|
| Microservices | Five to nine services for a system a two-person team operates. The coordination cost exceeds any scaling benefit at this size. |
| Dataverse / Power Apps | Fast to a first screen, then a wall: effective-dated cost history, availability math, and a self-hosted agent protocol are all fighting the platform. |
| Azure Container Apps for the web tier | More concepts (environments, revisions, ingress, Dapr) for a workload App Service handles. We keep the Dockerfile so this stays a one-day migration if scaling ever demands it. |
| SPA + REST | Second toolchain, second auth flow, duplicated validation, and sensitive field values traveling to a browser that a user can inspect. |
| Azure Monitor / Application Insights availability tests as the monitoring engine | Fine for HTTP from Azure; no ICMP, no internal targets, no per-circuit SLA math, and outage correlation would still be ours to build. We use App Insights for *application* telemetry, which is what it is good at. |

---

## 3. Cross-cutting mechanisms

### Authorization

Two-level enforcement, both mandatory.

1. **Page / endpoint level** — every Blazor page carries `[Authorize(Policy = ...)]`; every minimal API endpoint carries `.RequireAuthorization(policy)`. A default `FallbackPolicy` requiring an authenticated user means a forgotten attribute fails closed.
2. **Field level** — sensitive properties are projected out of DTOs by the query handler when the caller lacks the permission. The value never reaches the render tree. Masking in the UI alone is not a control; it is a decoration.

Permissions are claims materialized at sign-in from `EntraGroupRoleMap` → roles → permissions, cached for the session with a short TTL.

### Audit

Every material change writes an `AuditEntry` in the **same transaction** as the change, via an EF Core `SaveChangesInterceptor`. The audit table has no `UPDATE` or `DELETE` grant for the application's SQL principal — append-only is enforced at the database, not by convention. Sensitive field values are recorded as `"[redacted]"` with a change-detected flag rather than as before/after values.

Reads are not audited by default, with two exceptions that are: **revealing a sensitive field** and **generating an export**. Both write a `SecurityEvent`.

### Soft delete

`ISoftDeletable` entities carry `IsArchived`, `ArchivedUtc`, `ArchivedByUserId`. A global query filter excludes archived rows; an explicit `.IncludeArchived()` opts back in. Nothing is hard-deleted through the application. Historical cost, contract, monitoring, and audit rows are never archived at all — they are immutable by design.

### Outbox

Domain events (contract approaching notice deadline, outage confirmed, invoice variance detected, sync failed) are written to a `NotificationOutbox` table in the same transaction as the state change. A Functions timer trigger drains it with retry, exponential backoff, and a dedupe key so a redeploy mid-drain cannot double-send. This is why an alert is never lost and never duplicated.

### External API resilience

Every outbound integration goes through a typed `HttpClient` with a standard resilience pipeline (`Microsoft.Extensions.Http.Resilience`): timeout → retry with jittered backoff on 429/5xx → circuit breaker. IT Glue's published limit is 3000 requests per 5-minute window, so its client additionally carries a token-bucket rate limiter set below that ceiling.

### Observability

Structured logging via Serilog to Application Insights, with a `CorrelationId` flowing from the HTTP request through the outbox into the Functions worker. Health checks at `/health/live` and `/health/ready` (database, Blob, Key Vault, outbox depth, agent heartbeat age). A redaction processor strips tokens, full CIDR blocks, and document contents from log properties before they leave the process.

---

## 4. Data protection specifics

| Data | At rest | In transit | Application-level |
|---|---|---|---|
| Everything in Azure SQL | TDE (service-managed key) | TLS 1.2+, `Encrypt=True` enforced | — |
| Static IP assignments (`ServiceIpAssignment`) | TDE | TLS | **Yes** — encrypted with a Key Vault–backed data protection key. Decryption happens only in a query handler that has already verified `ServiceIpData.Read`. This means a database export or a read-only reporting connection cannot leak the IP inventory. |
| Documents (contracts, invoices, diagrams) | Blob SSE, private container | TLS | Access only via short-lived user-delegation SAS, issued per request, logged as a `SecurityEvent`. |
| Integration tokens | Key Vault | TLS | Never in the database. The database stores the secret *name*. Redacted from all logs. |
| Vendor portal credentials | **Not stored** | — | A reference string and/or an IT Glue password record ID only. |

Application-level encryption is applied to exactly one table on purpose. Encrypting everything would break indexing, searching, and reporting for no threat-model benefit; encrypting the static IP inventory addresses the specific risk that a database copy leaks a map of the organization's public attack surface.

---

## 5. Deployment topology

```
Internet
   │
   ├── Azure Front Door (optional, Phase 2) ── WAF
   │
   ├── App Service (Linux, P1v3) ── FcTelecom.Web ── VNet integration
   │        │                                            │
   │        ├── Managed Identity ──────────────────┬─────┤
   │                                               │     │
   ├── Function App (Flex Consumption) ── Worker ──┤     │
   │                                               │     │
   └── (outbound-only, from your network)          │     │
        ProbeAgent ── HTTPS ── /api/agent ─────────┘     │
                                                          │
                              ┌───────────────────────────┴──────────────┐
                              │  Private endpoints where practical:      │
                              │  Azure SQL │ Key Vault │ Blob │ App Ins  │
                              └──────────────────────────────────────────┘
```

- App Service and the Function App authenticate to SQL, Key Vault, and Blob using **managed identity**. No connection-string secrets in configuration.
- The probe agent connects **outbound only** over HTTPS. No inbound firewall rule at your sites, ever. It authenticates with a client-credentials token from a dedicated Entra app registration and signs each result payload with a per-agent HMAC key.
- Deployment slots on App Service: `staging` → warm up → swap.

---

## 6. Reporting posture

Power BI–friendly, embedding deferred (per the brief).

- A set of `rpt.*` SQL views forms a stable reporting contract: `rpt.ServiceSpendMonthly`, `rpt.ContractRenewalPipeline`, `rpt.AvailabilityByServiceMonth`, `rpt.InvoiceVariance`, `rpt.DiversityRisk`, `rpt.DataCompleteness`.
- A dedicated read-only SQL user is granted `SELECT` on `rpt.*` **only** — not on base tables. This is how Power BI connects, and it is why the static IP table being application-encrypted matters: the reporting principal cannot read it even if pointed at it.
- Every list view in the UI exports to `.xlsx` with the same column semantics as the corresponding view, so a manual export and a Power BI refresh never disagree.

---

## 7. Implementation order

The sequence is chosen so that each step produces something usable and nothing built early has to be torn out.

1. **Foundation** — solution, layering, EF Core model, migrations, audit interceptor, soft delete, seed data, health checks.
2. **Identity** — Entra OIDC, group→role map, permission claims, policy definitions, authorization tests.
3. **Inventory vertical slice** — Locations, Vendors, Services: list, detail, create, edit, archive. This is the slice that proves the layering.
4. **Costs and contracts** — effective-dated cost history, contract records, notice-deadline computation and confirmation.
5. **Search and export** — global search across the permitted surface, Excel export, saved views.
6. **Dashboard** — portfolio tiles backed by the `rpt.*` views.
7. **Import** — CSV/Excel with dry-run preview, validation, duplicate detection.
8. **Alerts** — outbox, Graph email, Teams, renewal rules.
9. **Monitoring** — schema, providers, simulated provider, agent protocol, correlation, rollups.
10. **IT Glue** — field mapping, dry run, one-way sync.

Steps 1–3 are what "first vertical slice" means. Steps 4–8 complete Phase 1 and 2.

---

## 8. Risks and tradeoffs worth stating plainly

| Risk | Impact | Mitigation |
|---|---|---|
| **Blazor Server holds a SignalR circuit per user.** A network blip drops the circuit; the user sees a reconnect banner. | Mild annoyance on flaky connections — including, ironically, during the outages this tool exists to manage. | Long-form edits autosave to draft state; the outage view is built to be **read-mostly and reload-safe**, so it works even if the circuit is re-established. Server-side circuit state is kept small. If this ever proves painful, `InteractiveAuto` is a per-page opt-in, not a rewrite. |
| **Monitoring credibility.** One false outage alert and people stop reading the alerts. | The monitoring module becomes worthless. | Multi-probe quorum, debounce thresholds, explicit `Unknown` state, and monitoring-system-failure classification. We would rather report "we don't know" than report a wrong "down". |
| **Data quality on import.** Garbage circuit IDs make the whole tool untrustworthy. | Adoption failure. | Mandatory dry-run preview, duplicate detection on circuit ID + account, a "data completeness" dashboard that names exactly which records are missing which fields. |
| **The Dude has no supported API.** (Validated — see `09-integration-validation.md`.) | A promised integration cannot be built as imagined. | Syslog ingestion adapter only, clearly labelled as low-fidelity, with the self-hosted agent as the migration path. We do not claim more than the platform supports. |
| **App-level encryption on IP data breaks search on that field.** | "Find circuit by static IP" cannot use a plain SQL `LIKE`. | A deterministic HMAC index column (`IpSearchHash`) supports exact-match lookup; range/CIDR-contains queries are resolved in memory over the authorized subset. Exact-match is the actual outage-time need. |
| **Blazor Server scaling is stateful.** Sticky sessions or a backplane are required beyond one instance. | Scale-out complexity. | ARR affinity on App Service for Phase 1 (adequate to hundreds of concurrent users at this org size); documented Redis backplane path if needed. |
