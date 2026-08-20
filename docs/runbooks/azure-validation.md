# Runbook — Azure validation pass

**This document is authoritative for execution order.** `entra-setup-dev.md` is a reference
invoked from steps 1, 4 and 9 below; it deliberately contains no ordering of its own, because
two documents each claiming an order is how they end up disagreeing.

The baseline was approved **subject to successful compilation, testing, and Azure validation**.
Compilation and testing pass in CI. This is the third condition.

**Budget one working day** for a first run. Steps 3, 9 and 12 take the longest.

---

## The sequence

| # | Step | Script / reference | Mutates |
|---|---|---|---|
| 0 | Prerequisites, validation host, licensing | `scripts/bootstrap/ubuntu-26.04.sh` | the host only |
| 1 | Entra Part A — role groups and app registration | `entra-setup-dev.md` §A | Entra |
| 2 | Preflight | `00-Preflight.ps1` | no |
| 3 | Infrastructure what-if and deploy | `01`, `02` | **Azure** |
| 4 | Entra Part B — URLs and client secret | `entra-setup-dev.md` §B | Entra, App Service |
| 5 | **Field-encryption keys** | `03-SetEncryptionKeys.ps1` | Key Vault, App Service |
| 6 | Database — review, apply, grant principals | `04`, `05` | **SQL** |
| 7 | **Deploy the application** | `06-DeployApp.ps1` | App Service |
| 8 | Bootstrap administrator | SQL in this document | SQL |
| 9 | Role mappings and test accounts | `entra-setup-dev.md` §C | Entra, app data |
| 10 | Runtime identity is constrained | `07-TestAppIdentity.ps1` | no |
| 11 | HTTP smoke test | `08-Smoke.ps1` | no |
| 12 | Authorization, one account per role | manual | no |
| 13 | Field encryption end to end | `09-VerifyEncryption.ps1` | optional |
| 14 | Observability | manual | no |
| 15 | Notifications | — | **BLOCKED** |
| 16 | Cleanup | `99-Cleanup.ps1` | **deletes** |

### Why this order, where it is not obvious

**Groups before infrastructure (1 before 3).** `infra/main.dev.bicepparam` takes Entra group
object IDs. Preflight refuses to continue if they do not resolve, so the groups must exist
first.

**Keys before the application starts (5 before 7).** Not a preference — a hard dependency, and
the reason this runbook was reordered. `Program.cs` resolves `DemoDataSeeder` at startup to run
`SeedReferenceDataAsync`, unconditionally. `DemoDataSeeder` takes `IFieldEncryptor`, registered
`AddSingleton`. `FieldEncryptor`'s constructor throws if either key is missing, malformed, or if
the two are identical. **A missing key means the application never starts at all** — and on
Linux App Service that presents as a container exiting during startup, which looks like a
platform or port problem long before it looks like a configuration value.

**Migration before the application deploys (6 before 7).** `SeedReferenceDataAsync` writes to
`Roles` and `RolePermissions`. Without the schema, the first start fails on a missing table.

**Bootstrap after the first successful start (8 after 7).** The bootstrap row references
`dbo.Roles.Id` for `AppAdministrator`. That row does not exist until the application has
started once and seeded reference data. This is why the bootstrap SQL lives here rather than in
the Entra runbook — it is not an Entra step, and it cannot run during Entra setup.

---

## Step 0 — Prerequisites

### Licensing and directory roles

| Requirement | Why |
|---|---|
| **Microsoft Entra ID P1 or P2** | Group-based assignment to an enterprise application. Without it, groups cannot be assigned, no `groups` claim is emitted, and every user resolves to zero permissions — which reads as a mapping bug for hours before anyone checks the licence. |
| Application Developer | Create the app registration |
| Groups Administrator | Create the `FCTelecom-*` groups |
| Global Administrator **or** Privileged Role Administrator | Grant admin consent |
| Owner or Contributor on the subscription | Deploy infrastructure |

Confirm the licence **before** step 1. Discovering it at step 12 wastes the day.

### The validation host

**Ubuntu Server 26.04 LTS is the supported validation host for this pass.** A standalone
server, not WSL. Ubuntu 24.04 LTS and Windows both work and are documented below, but 26.04 is
what the bootstrap script targets and what CI exercises on every change.

Every script runs unchanged under PowerShell 7 on Linux. There is no bash version of them, and
nothing in them is Windows-specific — no COM, no registry, no `winget`, no Windows-only .NET
APIs, forward slashes throughout. That is checked mechanically rather than asserted:

```bash
pwsh ./scripts/validate/Test-LinuxCompatibility.ps1 -IncludeAdvisory
```

`Invoke-Sqlcmd -AccessToken` (step 10) and `System.Security.Cryptography.AesGcm` (step 13) both
work on Linux — the SqlServer module has been cross-platform since v22, and `AesGcm` is a .NET
Core API. **Windows PowerShell 5.1 will not work** on either count; `00-Preflight.ps1` checks the
version and fails early.

### Tooling — Ubuntu Server 26.04 LTS (supported host)

One command:

```bash
git clone <this repository> && cd fc-telecom
sudo ./scripts/bootstrap/ubuntu-26.04.sh
```

It installs the .NET 10 SDK, PowerShell 7.6, Bicep, `dotnet-ef` and the SqlServer module, puts
`~/.dotnet/tools` on `PATH`, and prints a version summary. Re-running it is safe.

Read `scripts/bootstrap/README.md` before the first run — **there is one decision you have to
make, about the Azure CLI.** In short: Microsoft's apt packages for the Azure CLI are tested on
Ubuntu 22.04 and 24.04 only and there is no 26.04 (`resolute`) suite in the repository at the
time of writing, so `--azure-cli=auto` stops and asks rather than quietly pointing your 26.04
host at the 24.04 repository. The two workable answers are:

```bash
sudo ./scripts/bootstrap/ubuntu-26.04.sh --azure-cli=container   # official Microsoft image, needs Docker
sudo ./scripts/bootstrap/ubuntu-26.04.sh --azure-cli=apt         # once Microsoft publishes 26.04
```

Verify the host before going further:

```bash
pwsh ./scripts/validate/00-Preflight.ps1 -SkipAzureSignIn
```

That runs the host, tooling, SQL-client and Bicep checks with no Azure credentials. It is the
same command `.github/workflows/ubuntu-2604-compat.yml` runs against a clean `ubuntu:26.04`
image, so a failure here is a difference between your host and a known-good one. **Read that
workflow's most recent run before validation day** — it also probes weekly for whether Microsoft
has published the missing 26.04 packages yet.

`scripts/bootstrap/README.md` carries the explicit list of **what still cannot be supported on
26.04**, with what is done instead in each case. The short version: no `azure-cli` apt package
(use the official container image), no `powershell` apt package (the GitHub release `.deb` is
installed instead, and is a supported method), and no `msodbcsql18`/`sqlcmd` — which costs this
pass nothing, because every SQL call goes through `Invoke-Sqlcmd` and the managed driver. None
of those is worked around with an unverified substitution; in particular, a 26.04 host is never
pointed at the 24.04 repository.

Two Ubuntu behaviours the bootstrap script handles, worth knowing because they bite anyone
installing by hand:

- **`~/.dotnet/tools` is not on `PATH` by default.** `dotnet-ef` installs successfully and is
  then "not found", which reads as a failed install.
- **`az login` on a headless server cannot open a browser.** Use `az login --use-device-code`
  throughout. `Test-LinuxCompatibility.ps1` fails the audit if any script tells you otherwise.

### Tooling — Ubuntu Server 24.04 LTS (also supported)

24.04 is simpler, because Microsoft publishes everything for `noble`:

```bash
sudo apt-get update
sudo apt-get install -y wget apt-transport-https software-properties-common git unzip
source /etc/os-release
wget -q "https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb"
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y powershell dotnet-sdk-10.0
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

```bash
pwsh -Command 'Install-Module SqlServer -Scope CurrentUser -Force'
az bicep install
dotnet tool install --global dotnet-ef --version '10.*'
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc && source ~/.bashrc
```

Do **not** run `scripts/bootstrap/ubuntu-26.04.sh` on 24.04 — it refuses on purpose, because
its .NET source choice and its PowerShell install method are both 26.04-specific.

### Tooling — Windows workstation

```powershell
winget install Microsoft.PowerShell        # 7+ required
winget install Microsoft.AzureCLI
winget install Microsoft.DotNet.SDK.10
winget install Git.Git

az bicep install
dotnet tool install --global dotnet-ef
Install-Module SqlServer -Scope CurrentUser
```

### Then, on any host

```bash
az login --use-device-code       # on Windows with a browser to hand, plain `az login` is fine
az account set --subscription "<your dev subscription>"
```

Run everything from the repository root. Every script prints the subscription, tenant and target
before acting; every mutating script requires you to type the resource group name.

---

## Step 1 — Entra Part A: role groups and app registration

**→ `entra-setup-dev.md` §A**

Creates the five `FCTelecom-*` groups, the app registration shell, and configures group claims
as **assigned-groups-only**. Redirect URLs come later — they need the App Service host name.

Put the group object IDs into `infra/main.dev.bicepparam` locally. **Do not commit them.**

- [ ] Entra ID P1/P2 confirmed
- [ ] Five `FCTelecom-*` groups created, object IDs recorded
- [ ] App registration created, single-tenant
- [ ] Group claim = **Groups assigned to the application**, Group ID format
- [ ] All five groups assigned to the enterprise application
- [ ] `main.dev.bicepparam` updated locally

---

## Step 2 — Preflight

```powershell
./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Host release, tooling versions, PowerShell version, SQL client capability, subscription and
tenant, provider registration, parameter file, Bicep compile.

It reports **versions, not presence** — "az is installed" tells you nothing when the failure
three steps later is a command that needs a newer one. On Ubuntu it also names the release, and
checks that `dotnet-sdk-10.0` came from the Ubuntu archive rather than `packages.microsoft.com`,
because mixed .NET sources are the documented cause of "the runtime is there but the SDK isn't".

`-SkipAzureSignIn` runs only the checks that need no credentials — useful for verifying a
freshly bootstrapped host before you have signed in. It is **not** a substitute for this step:
it skips the subscription, the providers, the resource group, and group resolution, which are
the checks that catch deploying into the wrong place.

**The parameter file check is the one that earns its keep.** The committed file ships
placeholder all-zero object IDs. Deploy with them and the Key Vault and SQL admin role
assignments are made against a principal that does not exist — the deployment **succeeds**, and
then nobody can read the vault and nobody can administer the database.

- [ ] Preflight clean

---

## Step 3 — Infrastructure

```powershell
./scripts/validate/01-InfraWhatIf.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

./scripts/validate/02-DeployInfra.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev `
    -Location eastus2 -MonthlyBudgetUsd 150 -BudgetAlertEmail you@example.org
```

Creates the resource group (tagged so cleanup can identify it), sets a budget, deploys, and
writes `artifacts/validation/outputs-dev.json`. **Every later script reads that file** —
`infra/main.bicep` appends a `uniqueString()` suffix, so no resource name can be derived.

> **The budget is an alert, not a cap.** Reaching the amount sends email. Azure does not stop
> billing, throttle anything, or deallocate resources. Spend continues until a human acts. The
> only hard control is deleting the resource group (step 16).

- [ ] what-if reviewed, no unexplained destructive modifications
- [ ] Deployment succeeded, `outputs-dev.json` written
- [ ] Budget created, alert address monitored by someone

---

## Step 4 — Entra Part B: URLs and credential

**→ `entra-setup-dev.md` §B**

Needs `webAppHostName` from step 3. Three URLs across **two different fields**, the client
secret into Key Vault, and the `AzureAd__*` settings on App Service.

- [ ] Redirect URIs: `/signin-oidc` **and** `/signout-callback-oidc`
- [ ] Front-channel logout URL: `/signout-oidc`
- [ ] Client secret in Key Vault, referenced from App Service configuration
- [ ] `User.Read` granted with admin consent
- [ ] `AzureAd__TenantId`, `__ClientId`, `__Domain`, `__ClientSecret` set

---

## Step 5 — Field-encryption keys

**Before the application can start at all.**

```powershell
./scripts/validate/03-SetEncryptionKeys.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Generates two distinct 256-bit keys, stores them in Key Vault, writes the App Service settings
as Key Vault references, and checks the app's managed identity actually holds a secrets-read
role on the vault. That last check matters: an unresolvable reference leaves the setting as the
literal `@Microsoft.KeyVault(...)` string, which is not valid base64, so the app throws about
the *key* rather than about the missing role assignment.

Development keys. Anything encrypted under them is disposable and they must never be reused for
production; the script refuses to run against prod.

- [ ] Two distinct 256-bit keys in Key Vault
- [ ] App Service settings are Key Vault references, not values
- [ ] App identity holds **Key Vault Secrets User** on the vault

---

## Step 6 — Database

The identity that applies migrations is **not** the identity the application runs as. Collapsing
them means the web tier holds schema rights for the life of the system, so any injection flaw
reaches `DROP TABLE` rather than stopping at `SELECT`. It also hollows out the audit trail:
`dbo.AuditEntries` is `DENY UPDATE, DELETE` to the application, but a principal with `ALTER`
rights can simply remove the DENY.

| | Identity | Rights |
|---|---|---|
| **Migration** | `FCTelecom-SQL-Migrators` group (you + the CD service principal) | `db_ddladmin`, `db_datareader`, `db_datawriter` |
| **Runtime** | App Service managed identity | `db_datareader`, `db_datawriter`, `EXECUTE` — DDL explicitly denied |

### 6a. Review

```powershell
./scripts/validate/04-ReviewMigration.ps1
```

Checks four failure modes: two cascade paths into one table (error 1785), a filtered index
predicate naming a column its table lacks, `RowVersion` as `varbinary` rather than `rowversion`,
and `NOT NULL` on the optional owned `MailingAddress`. **If script generation itself fails, that
is the finding** — it is the first genuine validation of the EF model.

### 6b. Create the migration group and apply

```powershell
az ad group create --display-name "FCTelecom-SQL-Migrators" --mail-nickname "FCTelecomSQLMigrators"
az ad group member add --group "FCTelecom-SQL-Migrators" --member-id (az ad signed-in-user show --query id -o tsv)
```

Connect as the **Entra SQL admin** and run `05-GrantDatabasePrincipals.sql` with its three
placeholders replaced. Then apply as a member of the migration group:

```powershell
$outputs = Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json
$env:ConnectionStrings__Default =
    "Server=tcp:$($outputs.sqlServerFqdn),1433;Database=$($outputs.sqlDatabaseName);" +
    "Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False"

dotnet ef database update --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
Remove-Item Env:\ConnectionStrings__Default
```

`Active Directory Default` picks up your `az login`, which is what puts this under the migration
identity.

Then **re-run `05-GrantDatabasePrincipals.sql`** — the `DENY` statements on `dbo.AuditEntries`
and `dbo.SecurityEvents` are skipped on the first pass because those tables do not exist yet.

> If you deploy with `cd.yml` in step 7, note it also applies migrations using the service
> principal behind `AZURE_CREDENTIALS`. That principal must be in `FCTelecom-SQL-Migrators`.

- [ ] Migration reviewed, no findings
- [ ] `FCTelecom-SQL-Migrators` created and granted
- [ ] Migration applied **as the migration identity**
- [ ] Grant script re-run; audit DENY applied
- [ ] Final report: web app has `db_datareader` + `db_datawriter` and nothing else

---

## Step 7 — Deploy the application

**Creating App Service infrastructure does not put any application code on it.** An empty site
serves a placeholder page that will happily return 200 from `/`.

```powershell
./scripts/validate/06-DeployApp.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
# or, to use the real pipeline:
./scripts/validate/06-DeployApp.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -UseWorkflow
```

Publishes, stamps the build with the commit, deploys, waits for `/health/live`, and confirms the
running build is the one just published.

**This is the first successful application start**, and it is where steps 5 and 6 get tested for
real. If it does not come up, the script prints the three likely causes in order; read the
actual exception with `az webapp log tail` rather than guessing.

For dev, enable demo data so step 13 has encrypted rows to verify:

```powershell
az webapp config appsettings set --name $outputs.webAppName --resource-group rg-fctelecom-dev `
    --settings SeedDemoData=true -o none
az webapp restart --name $outputs.webAppName --resource-group rg-fctelecom-dev
```

Set it back to `false` once seeding has run. The seeder no-ops if locations already exist, so
leaving it on is harmless but misleading.

- [ ] Application deployed, `/health/live` returns 200
- [ ] Running build matches the commit you intended
- [ ] Demo data seeded (dev only), then `SeedDemoData=false`

---

## Step 8 — Bootstrap administrator

Group→role mappings are managed **in the application**, by someone holding `Admin.Manage`. On a
fresh database nobody holds it. Break the cycle once, with SQL, as the **Entra SQL admin**.

**This requires step 7 to have completed**, because `SeedReferenceDataAsync` creates the `Roles`
rows during the first successful start.

```sql
DECLARE @GroupObjectId nvarchar(100) = N'REPLACE-with-FCTelecom-AppAdministrator-object-id';
DECLARE @GroupName     nvarchar(200) = N'FCTelecom-AppAdministrator';

DECLARE @RoleId int = (SELECT Id FROM dbo.Roles WHERE Name = N'AppAdministrator');

IF @RoleId IS NULL
    THROW 50000, 'Roles are not seeded. The application has not completed a successful start - see step 7.', 1;

IF EXISTS (SELECT 1 FROM dbo.EntraGroupRoleMaps WHERE EntraGroupObjectId = @GroupObjectId)
    PRINT 'Mapping already exists - nothing to do.';
ELSE
BEGIN
    INSERT INTO dbo.EntraGroupRoleMaps
        (Id, EntraGroupObjectId, EntraGroupDisplayName, RoleId, Enabled, CreatedUtc, CreatedBy)
    VALUES
        (NEWID(), @GroupObjectId, @GroupName, @RoleId, 1, SYSUTCDATETIME(), N'bootstrap');
    PRINT 'Bootstrap mapping created.';
END

SELECT m.EntraGroupObjectId, m.EntraGroupDisplayName, r.Name AS [role], m.Enabled
FROM dbo.EntraGroupRoleMaps m JOIN dbo.Roles r ON r.Id = m.RoleId;
```

> Column names come from `AuditableEntity`. If the insert fails on a missing column, check
> `artifacts/validation/migration.sql` for the actual audit columns and adjust — do not remove
> the audit columns to make the insert work.

Add yourself to `FCTelecom-AppAdministrator`, then sign in.

- [ ] `dbo.Roles` contains five rows (proves step 7 seeded)
- [ ] Bootstrap mapping inserted
- [ ] You can sign in and reach an administrative page

---

## Step 9 — Role mappings and test accounts

**→ `entra-setup-dev.md` §C**

Create the other four mappings **through the application**, which exercises the admin UI, and
create one test account per role.

> **Direct membership only.** Group-based assignment to an enterprise application **does not
> cascade to nested groups**. A user who is a member of a group that is itself a member of
> `FCTelecom-Procurement` will **not** receive the claim. Every user must be a direct member of
> the assigned group. This is a documented Entra limitation, not something the application can
> work around.

- [ ] Four remaining mappings created in the UI
- [ ] Five test accounts created, each a **direct** member of exactly one group
- [ ] Test-account passwords stored **outside** the application's Key Vault (see §C)

---

## Step 10 — Runtime identity is constrained

```powershell
./scripts/validate/07-TestAppIdentity.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Obtains a database token issued to the **App Service managed identity** — by having the
container call its own IMDS endpoint — and runs every check as the application, not as you.

Prohibited operations run for real inside transactions that always roll back; a permission error
is the pass.

- [ ] Connected as the App Service identity, not you
- [ ] `db_datareader` + `db_datawriter`, no `db_owner`/`db_ddladmin`
- [ ] Permitted operations succeed; prohibited operations denied

---

## Step 11 — Smoke test

```powershell
./scripts/validate/08-Smoke.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Health, security headers, CSP, anonymous boundaries.

- [ ] `/health/live` and `/health/ready` return 200
- [ ] Headers present; no `unsafe-inline`, no `wasm-unsafe-eval`
- [ ] Anonymous requests redirect to sign-in

---

## Step 12 — Authorization

**Not scriptable, and the most valuable hour in the pass.** The failure you are hunting is a role
seeing something it should not, which needs a person who knows what that role is *for*.

| Role | Must see | Must **not** see |
|---|---|---|
| Network Engineer | Static IP data; reveal writes a `SecurityEvent` | — |
| Procurement | Costs, contracts | Static IP data |
| Help Desk | Escalation detail | Financial data |
| ReadOnly | Read-only across the estate | Static IP data |

- [ ] Each row confirmed with a real account
- [ ] A direct URL to a page the role lacks returns "not available", not the data
- [ ] An export writes a `SecurityEvent`
- [ ] An interactive page responds to a click — proves the Blazor circuit connected through the
      corrected `connect-src 'self'` CSP
- [ ] Sign-out genuinely ends the session (press Back; no cached data)
- [ ] A downstream token acquisition succeeds — verifies the `OnTokenValidated` chaining fix,
      which compiles and smoke-tests fine when broken

---

## Step 13 — Field encryption

```powershell
./scripts/validate/09-VerifyEncryption.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Reproduces the application's exact construction — `v1:` + base64(nonce‖tag‖ciphertext), and
`HMACSHA256(key, UTF8(value.Trim().ToUpperInvariant()))` — so it can decrypt what the application
wrote and recompute the search hash independently.

That last check is the valuable one. If the stored `CidrSearchHash` does not match, the write and
read paths have drifted and **exact search silently returns nothing rather than throwing**.

### Optional negative test

```powershell
./scripts/validate/09-VerifyEncryption.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -RunNegativeKeyTest
```

Sets both keys to the same value and confirms the application **refuses to start**. It backs up
all app settings first, restores in a `finally` block so recovery happens even on interruption,
and waits for `/health/ready` to confirm full recovery. If recovery fails it prints the exact
commands, including the Key Vault version history.

**Takes the application down for roughly two minutes.** Do not run it against an environment
anyone is using.

- [ ] Round trip, tamper rejection, wrong-key failure, determinism
- [ ] Stored hash matches an independently computed HMAC
- [ ] *(optional)* identical keys prevent startup, and the app recovered
- [ ] **Log redaction:** a sensitive property arrives `[redacted]` in Application Insights

---

## Step 14 — Observability

- [ ] Application Insights receives traces with a correlation ID
- [ ] Daily cap configured on the workspace
- [ ] Budget alert reaches a monitored address

---

## Step 15 — Notifications — **BLOCKED, out of scope**

Verified against the source at this commit:

| Capability | Status |
|---|---|
| `INotificationSender` | Interface only. **No implementation.** |
| Outbox drain | No processor exists |
| Guided import | No importer. `CsvHelper` is referenced but unused |
| Rules, previews, escalation | `NotificationAudienceResolver` computes who *would* be notified. Nothing sends |

The §4.6 checks — review the import, preview recipients, test-send, then enable — have nothing
behind them. Rules ship disabled and stay disabled; that is both correct and the only available
state. **Does not block baseline sign-off.**

---

## Step 16 — Cleanup

```powershell
./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -WhatIf
./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev
```

Refuses prod, refuses a group not tagged `application=fc-telecom` + `environment=dev`, requires
the name typed. Leaves the soft-deleted Key Vault and all Entra objects; `entra-setup-dev.md`
§D has Entra removal steps.

Keep `artifacts/validation/` with the table below — it is the evidence the pass was run.

---

## Results

| # | Step | Date | Who | Result | Notes |
|---|---|---|---|---|---|
| 1 | Entra Part A | | | | |
| 2 | Preflight | | | | |
| 3 | Infrastructure | | | | |
| 4 | Entra Part B | | | | |
| 5 | Encryption keys | | | | |
| 6 | Database | | | | |
| 7 | Application deployed | | | | |
| 8 | Bootstrap admin | | | | |
| 9 | Mappings and test accounts | | | | |
| 10 | Runtime identity constrained | | | | |
| 11 | Smoke | | | | |
| 12 | Authorization per role | | | | |
| 13 | Field encryption | | | | |
| 13 | Log redaction | | | | |
| 14 | Observability | | | | |
| 15 | Notifications | — | — | **BLOCKED** | Delivery and import not implemented |

**Sign-off:** the baseline condition is met when steps 1–14 pass.
