# scripts/validate

Executable parts of the Azure validation pass. Run them in order; the runbook is
`docs/runbooks/azure-validation.md`.

## Why PowerShell only

There is no bash equivalent, deliberately. Two implementations of the same checks drift, and
the one you are not running is the one that is wrong. PowerShell is also required rather than
merely convenient:

- `Invoke-Sqlcmd -AccessToken` is the only convenient SQL client that accepts a raw bearer
  token, which is how `05-TestAppIdentity.ps1` connects **as the App Service managed identity**
  rather than as you.
- `System.Security.Cryptography.AesGcm` in `07-CryptoCheck.ps1` reproduces the application's
  exact ciphertext format. **PowerShell 7+ required** — Windows PowerShell 5.1 does not have it.

Working in Linux? Install PowerShell in WSL2 (`sudo apt install powershell`) and run the same
files.

## Prerequisites

```powershell
winget install Microsoft.PowerShell        # 7+
winget install Microsoft.AzureCLI
winget install Microsoft.DotNet.SDK.10
az bicep install
dotnet tool install --global dotnet-ef
Install-Module SqlServer -Scope CurrentUser

az login
az account set --subscription "<dev subscription>"
```

Run from the repository root.

## Order

| Script | Mutates | What it does |
|---|---|---|
| `00-Preflight.ps1` | no | Tooling, subscription, providers, parameter file, Bicep compile |
| `01-InfraWhatIf.ps1` | no | what-if with `FullResourcePayloads`; flags destructive property changes |
| `02-DeployInfra.ps1` | **yes** | Resource group, budget, deployment; writes `outputs-<env>.json` |
| — | — | `docs/runbooks/entra-setup-dev.md` — app registration, groups, role mappings |
| `03-ReviewMigration.ps1` | no | Generates migration SQL, checks four known failure modes |
| `04-GrantDatabasePrincipals.sql` | **yes** | Migration identity vs runtime identity; audit DENY |
| `05-TestAppIdentity.ps1` | no | SQL permissions **as the App Service identity**; rolled-back probes |
| `06-Smoke.ps1` | no | Health, security headers, CSP, anonymous boundaries |
| `07-CryptoCheck.ps1` | keys only | AES-GCM + HMAC end to end against synthetic IP data |
| `99-Cleanup.ps1` | **yes** | Tears down a dev environment; refuses prod and untagged groups |

`FcValidation.psm1` holds the shared context banner, the confirmation gate, and deployment-output
loading. Everything imports it.

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
