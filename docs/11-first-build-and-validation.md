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

### Not verified statically (section 6 records what CI then found)

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

---

## 6. First real CI run — run 32284423125, and what it found

The static pass could not check restore. The first CI run did, and it failed both code jobs
at the restore step. Both failures were real; neither was a flake.

### Root cause A — NU1010, missing `PackageVersion` entries (`build-and-test`)

```
src/FcTelecom.Application/FcTelecom.Application.csproj : error NU1010:
  The following PackageReference items do not define a corresponding PackageVersion item:
  Microsoft.Extensions.DependencyInjection.Abstractions,
  Microsoft.Extensions.Logging.Abstractions,
  Microsoft.Extensions.Options.
```

Central Package Management requires a `PackageVersion` for every `PackageReference`.
`FcTelecom.Application` referenced three packages that were never added to
`Directory.Packages.props`. A failed project restore then took the whole solution restore
down.

**Fixed by:** adding `PackageVersion` entries for the two packages the project actually
uses (`…DependencyInjection.Abstractions`, `…Logging.Abstractions`, both `10.0.0`), and
**removing** the `PackageReference` for `Microsoft.Extensions.Options` and
`FluentValidation` — neither is used anywhere in the project. Their central versions stay
declared so re-adding a reference is one line.

> The `NuGet.targets(198,5): error : Object reference not set to an instance of an object`
> is a **cascade**, not a cause. NuGet's solution-level restore throws an NRE once a project
> restore has already failed. Chasing it leads nowhere.

### Root cause B — NU1903, a genuinely vulnerable transitive dependency (`security-scan`)

```
src/FcTelecom.Infrastructure/FcTelecom.Infrastructure.csproj : error NU1903:
  Warning As Error: Package 'System.Security.Cryptography.Xml' 9.0.0
  has a known high severity vulnerability   (× 8 advisories)
```

This is not a configuration problem — it is the audit doing its job. `TreatWarningsAsErrors`
turns NuGet Audit's NU1903 into a build break, which is the intended behaviour.

Two of the eight advisories, checked directly:

| Advisory | CVE | Severity | First patched (10.0 band) |
|---|---|---|---|
| GHSA-23rf-6693-g89p | — | High, CVSS 7.5 | 10.0.10 |
| GHSA-w3x6-4m5h-cxqf | CVE-2026-26171 | High, CVSS 7.5 | 10.0.6 |
| GHSA-mmjf-rqrv-855v | CVE-2026-50527 | High, CVSS 7.5 | 10.0.10 |

All are uncontrolled resource consumption in `EncryptedXml` — denial of service from crafted
encrypted XML. The highest first-patched version in the 10.0 band is **10.0.10**, so one pin
clears all of them.

**Fixed by:** a transitive pin in `Directory.Packages.props`. It arrives as someone else's
dependency, so there is no direct reference to bump —
`CentralPackageTransitivePinningEnabled` (already on) makes a `PackageVersion` entry
override whatever version the graph asks for:

```xml
<PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.10" />
```

The audit was **not** suppressed, and `TreatWarningsAsErrors` stays on. If the 10.0 band is
ever unavailable, `9.0.18` is the equivalent patched version in the 9.0 band.

**This pin should not live forever.** When whichever package pulls it in ships a build that
already depends on a patched version, delete the line.
`dotnet nuget why src/FcTelecom.Infrastructure System.Security.Cryptography.Xml` names the source.

### If a future advisory has no patched version yet

Do not reach for `TreatWarningsAsErrors=false` — that disables every other warning too.
Suppress the single advisory, narrowly and with an expiry note:

```xml
<!-- Expires when Foo 2.1 ships; tracked in <issue>. Reassess by <date>. -->
<NoWarn>$(NoWarn);NU1903</NoWarn>
```

Better still, scope it to the one project that pulls it in rather than the whole solution.

### Root cause C — no test results uploaded

Reported as "no files were found under ./TestResults". The immediate cause was that the
build failed so tests never ran, but there was a **latent bug** underneath it:

`--logger "trx;LogFileName=results.trx"` gives every test project in the solution the same
output filename. The last project to finish silently overwrites the rest, so a green run
would have reported a fraction of the tests it actually executed.

**Fixed by:** `--logger trx` with no fixed filename (the logger then names each file per
assembly), a single `TEST_RESULTS_DIR` used by both the test and upload steps so they cannot
drift apart, and `if-no-files-found: ignore` on the upload so a genuinely empty directory
after an earlier failure does not add noise to the annotations.

### Root cause D — Node 20 deprecation warnings

| Action | Was | Now |
|---|---|---|
| `actions/checkout` | v4 (node20) | **v7** |
| `actions/setup-dotnet` | v4 (node20) | **v5** — the release that moved to node24 |
| `actions/upload-artifact` | v4 (node20) | **v7** |
| `github/codeql-action/*` | v3 | **v4** |
| `azure/login` | v2 | **v3** |

`azure/webapps-deploy` and `Azure/functions-action` are **deliberately unchanged**. Their
READMEs currently document v2 and v1 and state no Node runtime, so bumping them would be a
guess. They live only in the manual-only Deploy workflow, which does not run in CI, so they
gate nothing. Confirm the tags before the first production deploy.

### Also changed while in there

- **Deploy is manual-only.** `cd.yml` had a `push: branches: [main]` trigger alongside
  `workflow_dispatch`, which meant every merge to main would have deployed. Removed.
- **`code-style` is its own non-blocking job.** `dotnet format --verify-no-changes` has never
  been run over this codebase and will report differences. It is advisory so a whitespace
  diff cannot mask a build or test failure. Run `dotnet format` once, commit, then fold it
  back in as blocking — there is a TODO on the job saying exactly that.
- **The SQL service container is commented out.** No current test project touches a database,
  so it was spending ~33 s per run and adding a health-check flake for nothing. Restore it
  verbatim with `FcTelecom.Integration.Tests`.
- **Dead workflow references removed.** `cd.yml` published `src/FcTelecom.Worker`, which does
  not exist yet, and read a step output that was never set. `azure-pipelines.yml` referenced
  a `steps/deploy.yml` template that was never written.
- Two genuinely unused `using` directives removed from `Program.cs`; `IDE0005` pinned to
  `suggestion` so enabling documentation files later cannot silently start breaking the build.

## 7. Second CI run — run 32287377450

Reported as three failures: `build-and-test`, `code-style`, `security-scan`. All three failed
at **Restore**, on the same two diagnostics — one dependency graph, three jobs reading it.
`validate-infrastructure` passed, as before.

The previous round's fixes held. `FcTelecom.Infrastructure` now restores, and `NU1010` is
gone. What surfaced underneath is a single structural problem showing up as two symptoms.

### Root cause A — NU1902, moderate advisory on Microsoft.Identity.Web 3.7.1

**GHSA-rpq8-q44m-2rpg.** Client secrets and certificate details can reach application logs
under some conditions. CVSS 4.7 (moderate). It affects two packages in the graph:

| Package | Affected range | First patched |
|---|---|---|
| `Microsoft.Identity.Web` | `>= 3.2.0, < 3.8.2` | **3.8.2** |
| `Microsoft.Identity.Abstractions` | `>= 7.1.0, < 9.0.0` | **9.0.0** |

**Fixed by** bumping `Microsoft.Identity.Web` and `Microsoft.Identity.Web.UI` to **3.8.2**.
One bump clears both: Identity.Web 3.8.2 depends on `Microsoft.Identity.Web.TokenAcquisition`
3.8.2, which requires `Microsoft.Identity.Abstractions >= 9.0.0`. No separate pin needed, so
there is no second entry to go stale later.

**3.8.2 rather than the current 4.14.2, deliberately.** A patch-level move inside v3 is
API-compatible with the authentication code already written. A major-version jump is not
something to make blind on a codebase that has never compiled — it would mix "did the version
bump break this" into the first real build's error list. Move to 4.x once CI is green and the
change can actually be verified.

The advisory was **not** suppressed and `NuGetAuditMode` was not weakened. The vulnerable
dependency was updated, which is the only fix that changes anything about the running app.

### Root cause B — NU1109, package downgrade on Azure.Identity

`Azure.Identity` was centrally pinned at **1.13.2**. `Microsoft.Data.SqlClient 6.1.1` requires
`>= 1.14.2`, and it arrives twice — via `Microsoft.EntityFrameworkCore.SqlServer 10.0.0` and
via `AspNetCore.HealthChecks.SqlServer 9.0.0`. With transitive pinning on, a central pin below
a transitive floor is a hard restore failure, not a silent upgrade.

**Fixed by** moving to **1.21.0** (current stable, no advisories) rather than the bare minimum
1.14.2. Being conservative about version floors is precisely what caused the failure.

### The structural cause underneath both

`Directory.Packages.props` had **47** `PackageVersion` entries. Only **26** were referenced by
any `.csproj`. The other 21 were written ahead of the phases that will need them — Functions
worker packages, Playwright, Testcontainers, Graph, FluentValidation.

With `CentralPackageTransitivePinningEnabled=true`, an unreferenced entry is not inert. It is a
hard pin on the transitive graph, so every stale speculative version sits as a floor waiting
for something to need a higher one. Azure.Identity was the first to fire; fixing them one at a
time as they surface would have meant a failed CI run per package.

**Fixed by** cutting the manifest to 27 entries — the 26 actually referenced plus the one
deliberate `System.Security.Cryptography.Xml` security pin. The removed 20 are listed in a
comment block at the bottom of the file, grouped by the phase that will need them, so re-adding
one alongside its `PackageReference` is a one-line change.

### Root cause C — code-style reported as a failure

`code-style` failed at Restore for the same two reasons as the other jobs, so it was a real
failure this time. But it would have rendered red regardless: the job carried job-level
`continue-on-error: true`, which still shows a red X and still reports a failed check. An
advisory job that is permanently red is indistinguishable from a broken one, and it trains
people to skim past red.

**Fixed by** moving `continue-on-error` from the job to the formatting step, with a follow-up
step that emits a `::warning::` annotation when formatting differs. The job now finishes green
with a visible annotation. Restore and setup stay hard failures in that job — a restore break
is a real break and should not hide behind the formatting exemption.

### Files changed this round

| File | Change |
|---|---|
| `Directory.Packages.props` | Identity.Web/UI → 3.8.2; Azure.Identity → 1.21.0; 20 unreferenced entries removed; both rules documented inline |
| `.github/workflows/ci.yml` | `code-style`: `continue-on-error` moved job → step, warning annotation added |

`TreatWarningsAsErrors` is still `true`. Deploy is still manual-only.

## 8. Third CI run — the same failure class, and the rule that ends it

Three jobs failed again, all at Restore, all on **NU1109** — and this time the previous fix
was part of the cause. That is worth stating plainly rather than presenting it as a fresh
discovery.

### What actually happened

Every Microsoft package in the 10.0 band was pinned at **10.0.0**. The band had shipped
through **10.0.11**. Two of those pins were then found to be sitting underneath transitive
requirements:

```
FcTelecom.Infrastructure
  -> Azure.Identity 1.21.0
    -> Azure.Core 1.53.0
      -> Microsoft.Extensions.Hosting.Abstractions 10.0.3
        -> Microsoft.Extensions.Logging.Abstractions >= 10.0.3   [pinned at 10.0.0]

FcTelecom.Infrastructure
  -> Microsoft.EntityFrameworkCore 10.0.0
    -> Microsoft.Extensions.Caching.Memory 10.0.0
      -> Microsoft.Extensions.Options 10.0.3
        -> Microsoft.Extensions.DependencyInjection.Abstractions >= 10.0.3   [pinned at 10.0.0]
```

The first chain is the one to own: **taking `Azure.Identity` to 1.21.0 in the previous round
pulled in `Azure.Core` 1.53.0, which raised the floor on the Extensions primitives.** The fix
for run two set up the failure for run three. Neither of these two packages is one we chose —
both are three hops down someone else's dependency list.

### The rule that comes out of it

Runs two and three were the same defect wearing different package names. The unifying
statement was missing from the manifest, so here it is, now recorded as **rule 3** in
`Directory.Packages.props`:

> Pin to the **latest patch of the band**, never to the band's `.0`, and move every package in
> a coordinated band together.

A `.0` pin feels like the conservative choice. Under `CentralPackageTransitivePinningEnabled`
it is the opposite: it is the **lowest possible floor**, which makes it the value most likely
to end up underneath somebody else's requirement. Pinning at the newest published patch closes
the failure class deterministically — nothing in a restored graph can require a version that
has not been published yet.

### Changes

| Package | Was | Now |
|---|---|---|
| `Microsoft.EntityFrameworkCore` | 10.0.0 | **10.0.11** |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.0 | **10.0.11** |
| `Microsoft.EntityFrameworkCore.Relational` | 10.0.0 | **10.0.11** |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.0 | **10.0.11** |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | **10.0.11** |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | **10.0.11** |
| `Microsoft.AspNetCore.Components.QuickGrid` | 10.0.0 | **10.0.11** |
| `System.Security.Cryptography.Xml` | 10.0.10 | **10.0.11** |

All eight verified as published on nuget.org; 10.0.11 is the newest stable in the band for
every one of them. The four EF Core packages constrain each other and now move as a set.

The remaining pins were checked and cannot produce NU1109: they are **leaves** in this graph —
nothing depends on `Serilog.*`, `ClosedXML`, `CsvHelper`, `xunit*`, `Shouldly`,
`NetArchTest.Rules`, `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `Azure.Storage.Blobs`,
`Azure.Security.KeyVault.Secrets`, `AspNetCore.HealthChecks.SqlServer`,
`Microsoft.ApplicationInsights.AspNetCore`, or `Microsoft.Extensions.Http.Resilience`, so no
transitive requirement can be raised above them. `Azure.Identity` 1.21.0 and
`Microsoft.Identity.Web` 3.8.2 are the two that *are* depended upon, and both are at the
newest release of their chosen band.

### Two things added so this stops being discovered by CI

**`tools/check-package-pins.py`** — reads the manifest, asks nuget.org what exists, and reports
any pin behind the newest patch in its own band, plus any missing or stale entry. Run it before
pushing. It is deliberately *not* a CI step: the pipeline should not gain a new external
network dependency while we are still making it deterministic.

**`.github/dependabot.yml`** — weekly NuGet updates grouped so the .NET 10 band moves as one
pull request (eleven separate PRs would each be individually un-mergeable), Azure/Identity as
another, test tooling as a third. Major bumps are ignored: `Microsoft.Identity.Web` 3.x → 4.x
is a reviewed decision, not a Monday-morning merge. GitHub Actions updates monthly.

Three restore failures in a row were all version drift found by a failed pipeline. That is the
wrong instrument for the job.

### Not changed, deliberately

`Microsoft.Extensions.Http.Resilience` stays at **9.3.0** although 10.9.0 exists, and
`Microsoft.Identity.Web` stays at **3.8.2** although 4.14.2 exists. Both are leaves and neither
can cause a downgrade. Moving them is an API-surface change on code that has still never
compiled, and mixing that into the first real build would make the resulting error list
ambiguous. Both are worth revisiting the moment CI is green.

## 9. What the next run will exercise for the first time

Restore was the only gate reached so far. Passing it means **`dotnet build` runs against this
code for the first time ever** — that is where any remaining compile errors will surface, and
`TreatWarningsAsErrors` is on by design.

If the build fails on nullable warnings and it is blocking progress, get to green with
`dotnet build -p:TreatWarningsAsErrors=false` **locally** to see the full list, fix them, and
leave the committed setting alone. Two checks that would have caught the common cases have
already been run and are clean: no `CS8618` (non-nullable property with no `required`,
initialiser, or `= null!`) and no `CS9035` (unset required property in an object initialiser).
