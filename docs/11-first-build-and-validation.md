# 11 — First Build and Validation Plan

The baseline is approved **subject to successful compilation, testing, and Azure
validation**. This document is the path to clearing that gate.

Read it before the first `dotnet build`. It will save you an afternoon.

---

## 1. What has and has not been verified

This code was written in an environment with no .NET SDK and no access to NuGet, so it has
**never been compiled**. Rather than hand it over untested, it was put through a static
analysis pass that approximates the checks a compiler would make.

### Verified mechanically

| Check | Result |
|---|---|
| Cross-file type resolution — every reference to a solution type reachable via a `using` | Clean (21 flagged, all confirmed false positives: enum members, property names, HTML text, string literals) |
| `CS1587` — XML doc comments in illegal positions | Clean |
| `CS0535` — declared interface members implemented | Clean |
| `CS0117` — enum member references valid | Clean |
| `CS9035` — `required` properties set in every object initialiser | Clean |
| Delimiter balance across all 60 C# files | Clean |
| `IApplicationDbContext` ↔ `ApplicationDbContext` DbSet parity | 54 / 54 |
| Every DbSet entity has an `IEntityTypeConfiguration` | 54 / 54 |
| Every Razor member reference resolves against its DTO | Clean |
| Every injected service call resolves to a real method | Clean |
| Every page route carries an explicit authorization policy | 9 / 9 |
| Computed properties EF would silently map as columns | 1 found, ignored explicitly |

### Not verified, and cannot be here

- Compilation. Overload resolution, generic inference, nullability warnings-as-errors.
- Package versions in `Directory.Packages.props`. These are pinned to what was current at
  the time of writing and are the **most likely single source of first-build failures**.
- EF Core model validation — the model is only validated when a context is first built.
- Any runtime behaviour.

---

## 2. Defects found and fixed during the validation pass

Recorded because the pattern is more useful than the individual fixes.

| # | Defect | Class |
|---|---|---|
| 1 | `AvailabilityRollup` and `ContractAlert` were marked `IImmutableRecord` but are legitimately updated — rollups are recomputed, alerts are stamped when sent. The append-only interceptor would have thrown on both. | Design/implementation mismatch |
| 2 | `@rendermode InteractiveServer` used without `@using static Microsoft.AspNetCore.Components.Web.RenderMode` — would have failed every interactive page. | Missing using |
| 3 | `LocationQueries` read `service.IpAssignments` without including it, so "has static IPs" was always false. | Silent wrong answer |
| 4 | `DashboardQueries` joined `Guid?` to `Guid` — type inference failure. | Compile error |
| 5 | `BlobDocumentStore` assigned `Response<UserDelegationKey>` to `UserDelegationKey` without `.Value`. | Compile error |
| 6 | `LayeringTests` fields named `Domain` and `Infrastructure` shadowed the namespaces used in their own initialisers. | Compile error |
| 7 | `OnTokenValidated` was **assigned over** Microsoft.Identity.Web's own handler, silently breaking downstream token acquisition. Now chained. | Correctness, would fail later and elsewhere |
| 8 | `AddDbContextFactory` alongside `AddDbContext` registers `DbContextOptions<T>` twice with different lifetimes. Replaced with a DI scope. | Runtime, order-dependent |
| 9 | A permission flag inside an EF projection (`canSeeCosts ? <aggregate> : null`) — translates on some provider versions, throws on others. Now computed then stripped. | Runtime, version-dependent |
| 10 | Five entities inherited `RowVersion` but their configurations never marked it a concurrency token — the column exists, nobody checks it, and concurrent edits silently overwrite. Now applied by convention. | Silent data loss |
| 11 | `ServiceMonitor.IsInternalTarget` became a computed get/set property, which EF maps by convention and duplicates state. Explicitly ignored. | Silent schema duplication |

Numbers 7, 8, 9 and 10 are the ones worth noting: all four compile, all four pass a smoke
test, and all four fail later in ways that are hard to attribute.

---

## 3. First-build checklist, in order

### Step 1 — restore

```bash
dotnet restore
```

**Expect this to be where the first failures are.** Package versions are pinned to what was
current at authoring time. If a version does not exist on your feed:

```bash
dotnet list package --outdated
```

Then correct `Directory.Packages.props`. Versions worth checking first, because they move
fastest and are most likely wrong:

| Package | Pinned | Note |
|---|---|---|
| `Microsoft.EntityFrameworkCore*` | 10.0.0 | Must match the .NET 10 band |
| `Microsoft.Identity.Web*` | 3.7.1 | Check for a .NET 10–targeted release |
| `Microsoft.Azure.Functions.Worker*` | 2.0.0 | The isolated-worker packages version independently |
| `ClosedXML` | 0.104.2 | API changed at 0.100; do not downgrade below it |
| `Shouldly` | 4.2.1 | |
| `NetArchTest.Rules` | 1.3.2 | Low release cadence; may need a fork or replacement on .NET 10 |

### Step 2 — build

```bash
dotnet build
```

`TreatWarningsAsErrors` is on. Expect nullability warnings to surface as errors on the
first build — that is intentional, but if it is blocking progress, turn it off *temporarily*
in `Directory.Build.props`, get to green, then turn it back on and work through the list:

```xml
<TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

Do not leave it off. The value is entirely in it being on by default.

### Step 3 — model validation

This is the first point at which the EF model is actually checked.

```bash
docker compose up -d
dotnet ef migrations add InitialCreate \
    --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
```

Likely findings, in order of probability:

1. **Multiple cascade paths.** SQL Server rejects more than one cascade path into a table.
   `ServiceDependency`, `MaintenanceWindow`, and `Contract`'s two `Document` references are
   already set to `NoAction` for this reason; if a new one appears, set the *less
   important* side to `NoAction`, never both to `Cascade`.
2. **Filtered index syntax.** Several indexes use `HasFilter("[IsArchived] = 0")`. If a
   column was renamed the filter silently references a non-existent column and SQL Server
   rejects the index at migration time.
3. **Owned-type nullability.** `Location.MailingAddress` is an optional owned type whose CLR
   properties are non-nullable. EF makes the columns nullable and uses a required property
   for existence detection — verify the generated migration does exactly that.
4. **`rowversion` column type.** The convention sets `SetColumnType("rowversion")`; confirm
   the migration emits `rowversion` and not `varbinary(max)`.

**Review the generated migration before applying it.** Every deploy, but especially this one.

### Step 4 — apply and seed

```bash
dotnet ef database update \
    --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
dotnet run --project src/FcTelecom.Web
```

The Development configuration seeds the demo estate. If seeding throws, the most likely
cause is a check constraint doing its job — `CK_ServiceCosts_EffectiveRange`,
`CK_ServiceDependencies_NotSelf`, or the filtered unique index allowing one open cost row
per service. That is the schema working, not failing.

### Step 5 — tests

```bash
dotnet test tests/FcTelecom.Domain.UnitTests      # no dependencies, sub-second
dotnet test tests/FcTelecom.Architecture.Tests    # layering + authorization model
dotnet test                                        # everything
```

Run the domain tests first. They cover the availability maths, notice deadlines, spend,
diversity, outage correlation, and the notification audience resolver — and they need
nothing but the assembly.

---

## 4. Azure validation plan

Ordered so that each step's failure is unambiguous. Do not skip ahead: a failure at step 5
is very hard to diagnose if step 2 was never confirmed.

### 4.1 Infrastructure

```bash
az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
```

- [ ] `what-if` output reviewed, no unexpected `Delete` lines
- [ ] Deployment succeeds
- [ ] Entra group object IDs in the parameter file are real and correct

### 4.2 Identity

- [ ] Sign-in completes and returns to the application
- [ ] `groups` claim present in the token
- [ ] A group→role map row resolves to permission claims (check the sign-in log line:
      *"Resolved N permission(s) for … from M group(s)"*)
- [ ] Sign-in still works after the `OnTokenValidated` chaining fix — specifically, confirm
      a downstream Graph call succeeds, since that is what a broken chain would break

### 4.3 Data plane

- [ ] Managed identity connects to SQL with no credential in configuration
- [ ] `DENY UPDATE, DELETE ON dbo.AuditEntries` applied and verified by attempting an update
- [ ] Reporting principal can read `rpt.*` and **cannot** read `dbo.*`
- [ ] Document upload succeeds; the download URL expires within the configured window
- [ ] Field-encryption keys resolve from Key Vault; a seeded IP assignment decrypts

### 4.4 Authorization

Do this with real accounts, one per role. It is the single most valuable hour in the plan.

- [ ] Network Engineer sees static IP data; the reveal writes a `SecurityEvent`
- [ ] Procurement sees costs and contracts, and **no** static IP data
- [ ] Help Desk sees escalation detail and **no** financial data
- [ ] Executive sees spend and **no** static IP data
- [ ] A direct URL to a page the role lacks returns the "not available" page, not the data
- [ ] An export writes a `SecurityEvent`

### 4.5 Observability

- [ ] `/health/live` and `/health/ready` return 200
- [ ] Application Insights receives traces with a correlation ID
- [ ] A log event containing a sensitive property is emitted **redacted** — test this
      explicitly rather than assuming it
- [ ] Daily cap and budget alerts configured

### 4.6 Notifications — before enabling anything

Every rule ships disabled. The order here matters.

- [ ] Import reviewed and accepted
- [ ] For each rule, open the **preview** and confirm the resolved recipient list
- [ ] Send a **test notification** and confirm it arrives
- [ ] Only then enable the rule
- [ ] Confirm the 60-day escalation does *not* fire for a contract with a confirmed
      deadline and a recorded action, and *does* for one without

### 4.7 Monitoring — Phase 3, recorded here for completeness

- [ ] Two agents deployed in genuinely different failure domains (record them in
      `Probe.FailureDomain`; the admin UI warns when a monitor's probes all share one)
- [ ] Neither agent on a domain controller
- [ ] Agent-to-cloud outbound only — verified by confirming no inbound rule exists
- [ ] Stopping one agent produces `Unknown` and a coverage gap, **not** an outage
- [ ] A location with no internal target appears as a coverage gap

---

## 5. Known gaps at this baseline

Stated so they are decisions rather than surprises.

| Gap | Consequence | Planned |
|---|---|---|
| No contract or cost editing UI | Contracts must be imported or seeded | Next slice |
| No import UI | CSV/Excel import is designed, not built | Next slice |
| Outbox is written but not drained | Notifications are recorded, not sent | Phase 2 |
| Probe agent not implemented | Monitoring schema and correlation engine exist; nothing collects | Phase 3 |
| IT Glue client not implemented | Mapping and validation done | Phase 4 |
| Seeded audit entries have a null actor | Startup seeding runs outside a request, so there is no signed-in user to attribute to | Cosmetic; a `SystemCurrentUser` swap during seeding would fix it |
| No regional/row-level data segregation | Every authorised user sees the whole portfolio | Deliberate — see the threat model |
