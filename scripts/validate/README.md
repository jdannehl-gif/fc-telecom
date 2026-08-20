# scripts/validate

Executable parts of the Azure validation pass. Run them in order; the runbook is
`docs/runbooks/azure-validation.md`.

## Why PowerShell only

There is no bash equivalent, deliberately. Two implementations of the same checks drift, and
the one you are not running is the one that is wrong. PowerShell is also required rather than
merely convenient:

- `Invoke-Sqlcmd -AccessToken` is the only convenient SQL client that accepts a raw bearer
  token, which is how `07-TestAppIdentity.ps1` connects **as the App Service managed identity**
  rather than as you.
- `System.Security.Cryptography.AesGcm` in `09-VerifyEncryption.ps1` reproduces the application's
  exact ciphertext format. **PowerShell 7+ required** — Windows PowerShell 5.1 does not have it.

PowerShell 7 on Linux is a first-class target here, not a compatibility shim. The supported
validation host is a **standalone Ubuntu Server 26.04 LTS box** — not WSL — and every script
runs there unchanged: no COM, no registry, no Windows-only .NET APIs, forward slashes
throughout. That is checked mechanically rather than asserted:

```bash
pwsh ./scripts/validate/Test-LinuxCompatibility.ps1 -IncludeAdvisory
```

Ten rules for the ways a PowerShell script quietly stops working off Windows — drive letters,
Windows environment variables, registry, COM, Windows-only modules and cmdlets, desktop
assemblies, interactive browser login, backslash paths, and file references whose case does not
match what is on disk. Exit code is the blocking-finding count.

## Prerequisites

### Ubuntu Server 26.04 LTS — the supported host

```bash
sudo ./scripts/bootstrap/ubuntu-26.04.sh --azure-cli=container
pwsh ./scripts/validate/00-Preflight.ps1 -SkipAzureSignIn
```

`scripts/bootstrap/README.md` explains the one decision you have to make (the Azure CLI), what
each tool is installed from and why, and — explicitly — what still cannot be supported on
26.04. `.github/workflows/ubuntu-2604-compat.yml` runs the same bootstrap and the same
`-SkipAzureSignIn` preflight against a clean `ubuntu:26.04` image on every change and weekly.

### Ubuntu Server 24.04 LTS, or Windows

Both work. Instructions are in `docs/runbooks/azure-validation.md` step 0. Do not run the
26.04 bootstrap script on 24.04 — it refuses on purpose.

### Then, on any host

```bash
az login --use-device-code
az account set --subscription "<dev subscription>"
```

Run from the repository root.

## Order

| Script | Mutates | What it does |
|---|---|---|
| `00-Preflight.ps1` | no | Host and tooling versions, SQL client capability, subscription, providers, parameter file, Bicep. `-SkipAzureSignIn` runs only the checks needing no credentials |
| `01-InfraWhatIf.ps1` | no | what-if with `FullResourcePayloads`; flags destructive property changes |
| `02-DeployInfra.ps1` | **yes** | Resource group, budget **alert**, deployment; writes `outputs-<env>.json` |
| `03-SetEncryptionKeys.ps1` | **yes** | Field-encryption keys — **required before the app can start at all** |
| `04-ReviewMigration.ps1` | no | Generates migration SQL, checks four known failure modes |
| `05-GrantDatabasePrincipals.sql` | **yes** | Migration identity vs runtime identity; audit DENY |
| `06-DeployApp.ps1` | **yes** | Publishes and deploys application code, verifies the running build |
| `07-TestAppIdentity.ps1` | no | SQL permissions **as the App Service identity**; rolled-back probes |
| `08-Smoke.ps1` | no | Health, security headers, CSP, anonymous boundaries |
| `09-VerifyEncryption.ps1` | optional | AES-GCM + HMAC end to end; optional negative key test |
| `99-Cleanup.ps1` | **yes** | Tears down a dev environment; refuses prod and untagged groups |

**`docs/runbooks/azure-validation.md` is authoritative for order**, and it interleaves these
with Entra work and manual steps. Do not infer the sequence from the file numbers alone —
numbers 1, 4 and 9 of the validation sequence have no script.

`FcValidation.psm1` holds the shared context banner, the confirmation gate, and deployment-output
loading. Everything imports it.

`Test-LinuxCompatibility.ps1` is not part of the sequence — it audits the other files and is run
by CI. `05-GrantDatabasePrincipals.sql` is SQL, executed from step 6 of the runbook.

## Conventions these scripts follow

**Every script prints the context first** — subscription name and ID, tenant, signed-in user,
resource group, and where relevant the SQL server, database and web URL — before doing anything.
A stale `az account set` is the most common cause of operating on the wrong environment.

**Mutating scripts require the resource group name to be typed.** Not a y/N prompt: a y/N prompt
gets answered reflexively, and typing the group name requires reading the banner above it.

**Nothing hardcodes a resource name.** `infra/main.bicep` appends a `uniqueString()` suffix, so
names cannot be derived. Every script reads `artifacts/validation/outputs-<env>.json`, written by
`02-DeployInfra.ps1` from the real deployment outputs.

**Prohibited-operation tests run for real, inside a transaction that always rolls back.** A
permission error is the pass. Asking `HAS_PERMS_BY_NAME` would be cheaper and would not prove
the DENY is actually in force.

## Output

`artifacts/validation/` collects the what-if JSON, the generated migration, and the deployment
outputs. Keep it with the completed results table in the runbook — it is the evidence the pass
was run.
