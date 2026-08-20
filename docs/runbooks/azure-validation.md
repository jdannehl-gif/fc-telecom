# Runbook — Azure validation pass

The baseline was approved **subject to successful compilation, testing, and Azure validation**.
Compilation and testing pass in CI. This is the third condition.

`docs/11-first-build-and-validation.md` §4 is the checklist; this document executes it.
`scripts/validate/` automates what a machine can check honestly, and this runbook is explicit
about what it cannot.

**Budget one working day** for a first run, most of it in steps 2 and 6.

---

## Corrections in this revision

Three things in the first version of this pass were wrong. They are called out here rather than
quietly fixed, because if you had run it you would have got a green result you could not trust.

**1. `Deploy` is not a replacement.** The what-if guard failed the run on the `Deploy` change
type, believing it meant "this resource will be replaced". It does not. Per the ARM
documentation, `Deploy` means what-if *could not determine* whether properties will change, and
it only appears when `ResultFormat` is `ResourceIdOnly`. The fix is to ask for the information
— `FullResourcePayloads` — not to fail. `Delete` at resource level only occurs in **complete
mode**, so with incremental deployments it should never appear at all. The destructive changes
that genuinely can happen here are property-level, inside a `Modify`, and those are now what the
script surfaces.

**2. `sqlcmd -G` tested the wrong identity.** The data-plane checks ran from your workstation as
*you*. Since you must be in the SQL admin group to have created the users at all, every "the
application cannot do this" check would have passed for entirely the wrong reason. That is worse
than not testing, because it produces a tick. `05-TestAppIdentity.ps1` now obtains a token
issued to the App Service managed identity and runs the checks with that.

**3. The CSP required `wasm-unsafe-eval` for no reason.** This application renders with
`InteractiveServer` and contains no WebAssembly anywhere — that directive was copied from Blazor
WebAssembly guidance, where the Mono runtime genuinely needs it. It has been removed from
`WebInfrastructure.cs`, along with a blanket `connect-src wss:` that allowed a WebSocket to any
host while the comment above it claimed the opposite. **Watch for one side effect:** if
interactive pages render but never respond, `connect-src 'self'` is blocking the Blazor circuit
— check the browser console for a CSP violation before suspecting the server.

---

## Prerequisites (Windows)

Everything is PowerShell. There is no bash version, deliberately: two implementations of the
same checks drift, and the one you are not running is the one that is wrong.

```powershell
# PowerShell 7+ — Windows PowerShell 5.1 lacks the AesGcm APIs used in step 7
winget install Microsoft.PowerShell

winget install Microsoft.AzureCLI
winget install Microsoft.DotNet.SDK.10

az bicep install
dotnet tool install --global dotnet-ef

# Required for -AccessToken support, which is how step 5 tests the app's identity
Install-Module SqlServer -Scope CurrentUser
```

Then:

```powershell
az login
az account set --subscription "<your dev subscription>"
```

Run everything from the repository root. Every script prints the subscription, tenant and
target before doing anything, and every mutating script requires you to type the resource group
name — not a y/N prompt, because a y/N prompt gets answered without reading.

> **WSL2 alternative.** If you would rather work in Linux, install PowerShell inside WSL2
> (`sudo apt install powershell`) and run the same `.ps1` files. Note that `az login` inside
> WSL2 opens a browser on the Windows side; if it hangs, use `az login --use-device-code`.

---

## Before you start

Two things are true of this application that change how to read a failure:

1. **Nothing has ever touched a database.** No migration applied, no query run, the EF model
   never validated at runtime. Step 3 is the first time any of that happens.
2. **Nothing has ever authenticated.** Entra, the claims enricher and the authorization
   policies have never run against a real tenant.

A failure in steps 3–6 is expected in a way a CI failure was not. Treat the first run as
discovery, not verification. Record results as you go — a half-finished pass nobody wrote down
is worth nothing a week later.

---

## Step 1 — Preflight (read-only)

```powershell
./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Tooling, subscription and tenant, provider registration, parameter file, Bicep compile.

**The parameter file check is the one that earns its keep.** `infra/main.dev.bicepparam` ships
placeholder all-zero object IDs, because real tenant identifiers do not belong in source
control. Deploy with them in place and the Key Vault and SQL admin role assignments are made
against a principal that does not exist — the deployment **succeeds**, and then nobody can read
the vault and nobody can administer the database.

Fill in real object IDs locally. Do not commit them. Object IDs, never display names.

---

## Step 2 — Infrastructure

```powershell
./scripts/validate/01-InfraWhatIf.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

./scripts/validate/02-DeployInfra.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev `
    -Location eastus2 -MonthlyBudgetUsd 150 -BudgetAlertEmail you@example.org
```

`02-DeployInfra.ps1` creates the resource group if needed (tagged so cleanup can identify it),
sets a monthly budget with email alerts, deploys, and **writes the outputs to
`artifacts/validation/outputs-dev.json`**. Every later script reads that file. Nothing
downstream assumes a resource name — `infra/main.bicep` appends a `uniqueString()` suffix that
cannot be derived from the environment name.

Set the budget. A dev App Service plan, SQL database and Log Analytics workspace left running
costs real money quietly, which is a slightly embarrassing way to be surprised by a telecom
cost-management application.

- [ ] what-if reviewed, no unexplained destructive modifications
- [ ] Deployment succeeded
- [ ] `outputs-dev.json` written
- [ ] Budget created and confirmed in Cost Management

---

## Step 3 — Entra ID

**→ `docs/runbooks/entra-setup-dev.md`**, all of it, before continuing.

It depends on the App Service host name from step 2, which is why it comes here rather than
first. Roughly an hour: app registration, the three URLs in two different fields, credential
into Key Vault, Graph permissions, **group claims set to assigned-groups-only** (which is what
prevents the group-overage failure), the five role groups, the bootstrap administrator, and one
test account per role.

- [ ] App registration created, all three URLs correct
- [ ] Client secret in Key Vault, referenced from App Service configuration
- [ ] Group claim = **Groups assigned to the application**
- [ ] Five `FCTelecom-*` groups created and assigned to the enterprise application
- [ ] Bootstrap `EntraGroupRoleMaps` row inserted
- [ ] Five test accounts created, one group each

---

## Step 4 — Migration, as the migration identity

**The identity that applies migrations is not the identity the application runs as.** Collapsing
them means the web tier holds schema rights for the life of the system, so any injection or
deserialisation flaw reaches `DROP TABLE` rather than stopping at `SELECT`. It also hollows out
the audit trail: `dbo.AuditEntries` is `DENY UPDATE, DELETE` to the application, but a principal
with `ALTER` rights can simply remove the DENY.

| | Identity | Rights | Used by |
|---|---|---|---|
| **Migration** | `FCTelecom-SQL-Migrators` group (you + the CD service principal) | `db_ddladmin`, `db_datareader`, `db_datawriter` | `dotnet ef database update`, the deploy pipeline |
| **Runtime** | App Service managed identity | `db_datareader`, `db_datawriter`, `EXECUTE` — DDL explicitly denied | The running application, always |

### 4a. Review before applying

```powershell
./scripts/validate/03-ReviewMigration.ps1
```

Generates the idempotent script and checks the four failure modes §3 ranks most likely: two
cascade paths into one table (error 1785), a filtered index predicate naming a column its table
lacks, `RowVersion` emitted as `varbinary` rather than `rowversion`, and `NOT NULL` on the
optional owned `MailingAddress`.

**If script generation itself fails, that is the finding** — it is the first genuine validation
of the EF model.

Heuristics over generated SQL, not a substitute for reading it. Also scan for unexpected table
drops, `NVARCHAR(MAX)` where a length was intended, and missing check constraints.

### 4b. Create the migration group, then apply

```powershell
az ad group create --display-name "FCTelecom-SQL-Migrators" --mail-nickname "FCTelecomSQLMigrators"
az ad group member add --group "FCTelecom-SQL-Migrators" --member-id (az ad signed-in-user show --query id -o tsv)
```

Connect to the database as the **Entra SQL admin** and run `04-GrantDatabasePrincipals.sql`
after replacing its three placeholders. Then apply the migration as a member of the migration
group:

```powershell
$outputs = Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json
$env:ConnectionStrings__Default =
    "Server=tcp:$($outputs.sqlServerFqdn),1433;Database=$($outputs.sqlDatabaseName);" +
    "Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False"

dotnet ef database update --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
Remove-Item Env:\ConnectionStrings__Default
```

`Active Directory Default` picks up your `az login` credentials, which is what puts this under
the migration identity rather than the app's.

Then re-run `04-GrantDatabasePrincipals.sql` once more — the `DENY` statements on
`dbo.AuditEntries` and `dbo.SecurityEvents` are skipped on the first pass, because those tables
do not exist until the migration has run.

- [ ] Migration reviewed, no findings
- [ ] `FCTelecom-SQL-Migrators` created and granted
- [ ] Migration applied **as the migration identity**
- [ ] `04-GrantDatabasePrincipals.sql` re-run; audit DENY applied
- [ ] Final report shows the web app with `db_datareader` + `db_datawriter` and nothing else

---

## Step 5 — Prove the runtime identity is constrained

```powershell
./scripts/validate/05-TestAppIdentity.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

This is the step that replaces the broken one. It asks the container to call its own IMDS
endpoint for a database token, which only code running inside the app can do, and runs every
check as the App Service managed identity.

**Permitted** — `SELECT` from the core tables, `INSERT`/`UPDATE` permission on `dbo.Locations`.
**Prohibited** — `CREATE TABLE`, `ALTER TABLE`, `DROP TABLE`, `UPDATE`/`DELETE` on
`dbo.AuditEntries` and `dbo.SecurityEvents`, `CREATE USER`. Each runs for real inside a
transaction that is always rolled back; a permission error is the pass.

It also asserts the identity holds **neither** `db_owner` nor `db_ddladmin`.

> The token is a real credential valid roughly 24 hours. It is held in memory, never written to
> disk, and cleared at the end. If the SCM endpoint is blocked by policy, the script prints the
> exact command to run in the portal's SSH console instead.

- [ ] Connected as the App Service identity (not you)
- [ ] `db_datareader` + `db_datawriter`, no `db_owner`/`db_ddladmin`
- [ ] Every permitted operation succeeded
- [ ] Every prohibited operation denied
- [ ] Reporting principal reads `rpt.*` and cannot read `dbo.*` *(manual, as that principal)*

---

## Step 6 — Application: smoke, identity, authorization

```powershell
./scripts/validate/06-Smoke.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Health endpoints, security headers, the corrected CSP expectations, anonymous access
boundaries. `/health/ready` is the first real exercise of the managed-identity SQL connection;
a failure there is usually a missing database user, not a broken app.

### Then, by hand — the most valuable hour in the pass

Not scriptable. The failure you are hunting is a role seeing something it should not, which
needs a person who knows what that role is *for*. One real account per role, one at a time.

| Role | Must see | Must **not** see |
|---|---|---|
| Network Engineer | Static IP data; reveal writes a `SecurityEvent` | — |
| Procurement | Costs, contracts | Static IP data |
| Help Desk | Escalation detail | Financial data |
| Executive / ReadOnly | Spend | Static IP data |

- [ ] Each row confirmed with a real account
- [ ] A direct URL to a page the role lacks returns "not available", **not** the data
- [ ] An export writes a `SecurityEvent`
- [ ] An interactive page responds to a click — proves the Blazor circuit connected through the
      corrected CSP
- [ ] Sign-out genuinely ends the session (press Back; no cached data)
- [ ] A downstream token acquisition succeeds — verifies the `OnTokenValidated` chaining fix,
      which compiles and smoke-tests fine when broken
- [ ] An account with >200 group memberships still signs in with a working `groups` claim
      (`entra-setup-dev.md` §6)

---

## Step 7 — Field encryption

```powershell
./scripts/validate/07-CryptoCheck.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -CreateKeys
# restart the app, seed demo data, then:
./scripts/validate/07-CryptoCheck.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -Verify
```

The AES-GCM path and the HMAC search index have never had a key. The script reproduces the
application's exact construction in PowerShell — `v1:` + base64(nonce‖tag‖ciphertext), and
`HMACSHA256(key, UTF8(value.Trim().ToUpperInvariant()))` — so it can decrypt what the
application wrote and recompute the search hash independently.

That last check is the valuable one. If the stored `CidrSearchHash` does not match an
independently computed HMAC, the write path and the read path have drifted, and **exact search
silently returns nothing rather than throwing**. It is the kind of failure that survives a
demo.

Synthetic data uses `203.0.113.0/24` (TEST-NET-3, RFC 5737) — reserved for documentation and
routable nowhere, so it cannot be mistaken for a real circuit if it escapes into a log.

- [ ] Two distinct 256-bit keys created and stored in Key Vault
- [ ] Encrypt/decrypt round trip succeeds
- [ ] Tampered ciphertext is **rejected** (AES-GCM tag verified)
- [ ] Wrong key **fails closed**
- [ ] Search hash deterministic; different inputs hash differently
- [ ] Stored hash matches an independently computed HMAC
- [ ] **Negative test:** set both keys to the same value — the app must refuse to start
- [ ] **Log redaction:** a sensitive property arrives `[redacted]` in Application Insights
      (the script prints the KQL query)

---

## Step 8 — Observability

- [ ] Application Insights receives traces with a correlation ID
- [ ] Daily cap configured on the workspace
- [ ] Budget alert fires to a monitored address (step 2)

---

## Step 9 — Notifications — **BLOCKED, out of scope for this pass**

This step cannot be executed, and saying so is more useful than leaving a checklist nobody can
tick.

Verified against the source at this commit:

| Capability | Status |
|---|---|
| `INotificationSender` | Interface only, in `Application/Abstractions/Services.cs`. **No implementation exists.** |
| Outbox drain | No processor. No class references the outbox for delivery. |
| Guided import | No importer. `CsvHelper` is referenced by the project but not used by any code. |
| Notification rules, previews, escalation logic | `NotificationAudienceResolver` is implemented and unit-tested as a **pure function** — it computes who *would* receive a notification. Nothing sends one. |

So the §4.6 checks — review the import, preview each rule's recipients, send a test
notification, then enable — have nothing behind them. There is no import to review and no
delivery to test.

**What this means practically:** notification rules ship disabled and stay disabled. That is
the correct state, and it is also the only available state. When delivery lands, run §4.6 in
its documented order — preview, test-send, *then* enable — and never enable a rule whose
resolved recipient list has not been read by someone who knows who those people are.

**This does not block baseline sign-off.** Steps 1–8 are the baseline condition. Step 9 gates
enabling notifications, which cannot happen yet regardless.

---

## Step 10 — Monitoring agents — Phase 3, not now

Recorded for completeness. When agents land: two in genuinely different failure domains
(recorded in `Probe.FailureDomain`), neither on a domain controller, outbound-only verified by
confirming no inbound rule exists, stopping one agent produces `Unknown` and a coverage gap
rather than an outage, and a location with no internal target appears as a coverage gap.

---

## Cleanup

```powershell
./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -WhatIf
./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Refuses to run against prod, refuses to touch a resource group not tagged
`application=fc-telecom` + `environment=dev`, and requires the group name typed.

Left behind on purpose: the soft-deleted Key Vault (purge protection is a feature — purge only
if you need the name back immediately), and all Entra objects, which cost nothing and are the
slowest part to recreate. `entra-setup-dev.md` has removal steps.

Keep `artifacts/validation/` with the completed table below. It is the evidence the pass was
actually run.

---

## Results

| Step | Date | Who | Result | Notes |
|---|---|---|---|---|
| 1 Preflight | | | | |
| 2 Infrastructure | | | | |
| 3 Entra setup | | | | |
| 4 Migration (migration identity) | | | | |
| 5 Runtime identity constrained | | | | |
| 6 Smoke | | | | |
| 6 Authorization (per role) | | | | |
| 7 Field encryption | | | | |
| 7 Log redaction | | | | |
| 8 Observability | | | | |
| 9 Notifications | — | — | **BLOCKED** | Delivery and import not implemented |

**Sign-off:** the baseline condition is met when steps 1–8 pass.
