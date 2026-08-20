# Reference — Entra ID setup for development

> **This document has no execution order of its own.** `azure-validation.md` is authoritative
> for sequencing; it invokes the sections here at steps 1, 4 and 9. The sections are lettered
> rather than numbered to make that obvious.
>
> | Section | Called from | Why there |
> |---|---|---|
> | **§A** Groups and app registration | validation step **1** | Group object IDs go into `main.dev.bicepparam`, so they must exist before infrastructure deploys |
> | **§B** URLs and credential | validation step **4** | Needs `webAppHostName`, which only exists after deployment |
> | **§C** Mappings and test accounts | validation step **9** | Needs a signed-in administrator, which needs the bootstrap row from validation step 8 |
> | **§D** Removal | validation step **16** | — |
>
> The bootstrap `EntraGroupRoleMaps` insert is **not here**. It requires the database schema, a
> deployed application, and one successful start so `SeedReferenceDataAsync` creates the `Roles`
> rows. It lives in `azure-validation.md` step 8.

Budget **45–60 minutes** across §A and §B combined.

---

## Prerequisites

| Requirement | Why |
|---|---|
| **Microsoft Entra ID P1 or P2** | Group-based assignment to an enterprise application requires it. Without it the groups cannot be assigned, no `groups` claim is emitted, and every user resolves to zero permissions. |
| Application Developer | Create the app registration |
| Groups Administrator | Create the groups |
| Global Administrator **or** Privileged Role Administrator | Grant admin consent |

Confirm the licence first. If you do not hold these roles, find who does before starting —
discovering it halfway through is worse.

> Use a development tenant, or at minimum a development app registration in a shared tenant.

## Where each part of this runs

This document mixes two kinds of work, and on the supported setup they happen on two different
machines. Saying so once here avoids the "why won't this open" moment three sections in.

| | Runs where | Note |
|---|---|---|
| **Admin centre steps** (creating groups, app registration, consent, assignment) | A browser on your **workstation** | The validation host is a headless Ubuntu Server 26.04 box with no desktop and no browser. There is nothing to fix about that — it is a server. |
| **`az` and `pwsh` blocks** | The **Ubuntu 26.04 validation host** | Provisioned by `scripts/bootstrap/ubuntu-26.04.sh`; see `azure-validation.md` step 0. They run identically on Ubuntu 24.04 or a Windows workstation. |

Every `powershell`-tagged block below is PowerShell 7, cross-platform, and runs unchanged in
`pwsh` on Ubuntu. Where a block reads `$outputs`, it comes from
`artifacts/validation/outputs-dev.json`, written on the validation host by
`02-DeployInfra.ps1` — so run those blocks there, not on the workstation.

Sign in on the validation host with the device-code flow, because a headless server cannot
open a browser for the interactive one:

```bash
az login --use-device-code
az account set --subscription "<your dev subscription>"
```

The code it prints is completed in the browser you already have open for the admin centre.

---

# §A — Groups and app registration

*Called from validation step 1, before any infrastructure exists.*

## A1. Create the five role groups

The application has exactly five roles.

```powershell
$roles = 'AppAdministrator', 'NetworkEngineer', 'Procurement', 'HelpDesk', 'ReadOnly'

foreach ($role in $roles) {
    $group = az ad group create `
        --display-name "FCTelecom-$role" `
        --mail-nickname "FCTelecom$role" `
        --description "FC Telecom Manager - $role role" | ConvertFrom-Json

    Write-Host ('{0,-40} {1}' -f $group.displayName, $group.id)
}
```

**Record every object ID.** They go into `infra/main.dev.bicepparam` (locally, never committed)
and later into `EntraGroupRoleMaps`. Object IDs are the matching key — display names are cached
for readability only, because a group rename must not silently move who can read the static IP
inventory.

| Group | Sees | Must not see |
|---|---|---|
| `FCTelecom-AppAdministrator` | Everything, including audit and integrations | — |
| `FCTelecom-NetworkEngineer` | Static IP data (reveal writes a `SecurityEvent`) | — |
| `FCTelecom-Procurement` | Costs, contracts | Static IP data |
| `FCTelecom-HelpDesk` | Escalation detail, incidents | Financial data |
| `FCTelecom-ReadOnly` | Read-only across the estate | Static IP data |

`Admin.Manage`, `Audit.Read` and `Integrations.Manage` are held **only** by AppAdministrator —
an architecture test fails the build if that stops being true.

## A2. Create the app registration

**App registrations → New registration**

| Field | Value |
|---|---|
| Name | `FC Telecom Manager (dev)` |
| Supported account types | **Single tenant** |
| Redirect URI | leave blank — added in §B |

Single tenant is deliberate. This application holds a map of one organisation's circuits, costs
and public IP ranges; no guest tenant should authenticate to it.

Record: **Application (client) ID**, **Directory (tenant) ID**, and the tenant's **primary
domain**.

## A3. Group claims — assigned groups only

**Token configuration → Add groups claim → Groups assigned to the application**

Not "Security groups". Not "All groups". Expand **ID** and tick **Group ID**; leave
sAMAccountName and NetBIOS unchecked.

Then assign the groups: **Enterprise applications → `FC Telecom Manager (dev)` → Users and
groups → Add user/group**, and add all five `FCTelecom-*` groups.

### Why this specific setting matters

Entra caps groups in a token: **200 for JWT**, 150 for SAML, 5 for implicit. Over the limit it
**omits the `groups` claim entirely** — it does not truncate — and substitutes a pointer to
Microsoft Graph.

`PermissionClaimsEnricher` reads `principal.FindAll("groups")`. For an overage user that returns
nothing, so no mapping matches, so the user gets **zero permissions** and an empty application —
while the log reads *"Resolved 0 permission(s) … from 0 group(s)"*, which looks like a mapping
problem rather than a token problem.

Not hypothetical: long-tenured staff and IT administrators accumulate hundreds of memberships,
and they are exactly who will use this.

Assigned-groups-only emits **five groups at most**, so overage cannot occur, no Graph call is
needed, and the token stays small. It is Microsoft's own first recommendation for avoiding
overage.

### Direct membership only — no nesting

**Group-based assignment does not cascade to nested groups.** Microsoft's documentation is
explicit: *"The assignment doesn't cascade to nested groups"* and *"Nested group memberships
aren't currently supported."*

A user who is a member of `IT-Procurement-All`, which is itself a member of
`FCTelecom-Procurement`, **will not receive the claim** and will resolve to zero permissions.

This is an Entra limitation the application cannot work around, and it has a practical
consequence for rollout: you cannot map an existing departmental group hierarchy into these
roles by nesting. **Every user must be a direct member of the `FCTelecom-*` group.** If your
directory is organised around nested groups, plan for a flattening step — a scheduled sync or an
access package — before rollout, and treat that as a real work item rather than a footnote.

## A4. Update the parameter file

Put the group object IDs into `infra/main.dev.bicepparam`. **Do not commit them.**
`00-Preflight.ps1` verifies each resolves to a real group.

---

# §B — URLs and credential

*Called from validation step 4, after infrastructure exists.*

```powershell
$outputs = Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json
$outputs.webAppHostName
```

## B1. Three URLs, two different fields

The application uses the Microsoft.Identity.Web defaults from `appsettings.json`:

```json
"CallbackPath": "/signin-oidc",
"SignedOutCallbackPath": "/signout-callback-oidc"
```

Getting these wrong produces `AADSTS50011`, which names the redirect URI but not which field it
was missing from.

**Authentication → Add a platform → Web → Redirect URIs:**

```
https://<webAppHostName>/signin-oidc
https://<webAppHostName>/signout-callback-oidc
```

`signin-oidc` receives the authorization code. `signout-callback-oidc` is where Entra returns
the browser **after** sign-out — a redirect URI, not a logout URL, and the one most often
omitted.

**Front-channel logout URL** (its own separate field):

```
https://<webAppHostName>/signout-oidc
```

This is `RemoteSignOutPath`, called when the user signs out of a *different* application in the
same session. Without it, single sign-out silently does not work.

**Local development** — add as additional redirect URIs; `dotnet run` prints the actual port:

```
https://localhost:<port>/signin-oidc
https://localhost:<port>/signout-callback-oidc
```

**Implicit grant:** leave both "Access tokens" and "ID tokens" **unchecked**. This is a
confidential client using authorization code with PKCE; those checkboxes are for SPAs and
enabling them weakens the app for no benefit.

## B2. Credential

Production and staging: certificate or workload identity federation. Not a secret.

Development: a client secret, **90 days**, stored in Key Vault and never pasted into a file.

```powershell
az keyvault secret set --vault-name $outputs.keyVaultName `
    --name 'AzureAd--ClientSecret' --value '<the secret value>' -o none
```

The value is shown exactly once. Lose it and you delete and recreate.

## B3. API permissions

**API permissions → Microsoft Graph → Delegated:**

| Permission | Needed for | Add now? |
|---|---|---|
| `User.Read` | Sign-in and basic profile | **Yes** |
| `GroupMember.Read.All` | Group-overage Graph fallback | **No** |

Then **Grant admin consent** and confirm every row shows green.

`GroupMember.Read.All` is deliberately absent. **The application cannot call Graph today** —
there is no Graph client, and the `Microsoft.Graph` package was removed from
`Directory.Packages.props` during CI stabilisation because nothing referenced it. Adding a
tenant-wide read of every group membership to an application that will not use it is the wrong
trade. Add it with the code, if that code is ever written.

## B4. App Service configuration

```powershell
az webapp config appsettings set --name $outputs.webAppName --resource-group <rg> `
    --settings `
        "AzureAd__TenantId=<tenant-id>" `
        "AzureAd__ClientId=<client-id>" `
        "AzureAd__Domain=<primary-domain>" `
        "AzureAd__ClientSecret=@Microsoft.KeyVault(VaultName=$($outputs.keyVaultName);SecretName=AzureAd--ClientSecret)" `
    -o none
```

Do not override `CallbackPath` or `SignedOutCallbackPath` — they are already correct, and if you
change them here you must change the app registration to match.

---

# §C — Mappings and test accounts

*Called from validation step 9, after the bootstrap administrator can sign in.*

## C1. The remaining four mappings

Signed in as a member of `FCTelecom-AppAdministrator`, create the mappings for
NetworkEngineer, Procurement, HelpDesk and ReadOnly **through the application's admin UI**. That
exercises the UI, which is the point of doing it there rather than in SQL.

> Group membership changes are not instant. Sign out fully, or wait a few minutes, before
> concluding a mapping does not work.

## C2. Test accounts

Step 12 needs one account per role. It is the step most often skipped because creating the
accounts is tedious — create them once and keep them.

### First: a separate vault for their passwords

**Do not put test-account credentials in the application's Key Vault.** The App Service managed
identity holds a secrets-read role there, so storing them means the application can read the
passwords of the accounts used to test it. Even in dev that is a bad shape, and it is exactly
the kind of thing that gets copied to production.

Use a separate resource group so there is no shared access path:

```powershell
az group create --name rg-fctelecom-dev-testing --location eastus2 `
    --tags "application=fc-telecom" "environment=dev-testing" -o none

az keyvault create --name fctel-dev-testkv-<suffix> `
    --resource-group rg-fctelecom-dev-testing --location eastus2 `
    --enable-rbac-authorization true -o none
```

Grant **only yourself** Key Vault Secrets Officer on it. Do not grant the App Service identity
anything. An external credential manager (1Password, Bitwarden, the team's existing vault) is
equally fine and arguably better — the requirement is separation, not a specific product.

### Then the accounts

```powershell
$testVault = 'fctel-dev-testkv-<suffix>'   # NOT $outputs.keyVaultName
$domain = (az ad signed-in-user show --query userPrincipalName -o tsv).Split('@')[1]

foreach ($role in 'AppAdministrator','NetworkEngineer','Procurement','HelpDesk','ReadOnly') {
    $upn = "fctest-$($role.ToLower())@$domain"

    # A distinct random password per account. Do not reuse one: the entire point of these
    # accounts is that they are not interchangeable.
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $password = [Convert]::ToBase64String($bytes) + '!aA1'

    $user = az ad user create --display-name "FC Test $role" `
        --user-principal-name $upn --password $password `
        --force-change-password-next-sign-in false | ConvertFrom-Json

    # DIRECT membership — nested does not work (§A3)
    az ad group member add --group "FCTelecom-$role" --member-id $user.id

    az keyvault secret set --vault-name $testVault `
        --name "testaccount-$($role.ToLower())" --value $password -o none

    Write-Host "$upn  ->  FCTelecom-$role"
}
```

Rules, worth stating because they get broken:

- **Each account is a direct member of exactly one group.** An account in two roles proves
  nothing about either, and a nested membership grants nothing at all.
- **Never give them administrative roles in Azure or Entra.** They exist to test what the
  application shows; an account that is a Global Administrator passes every check for the wrong
  reason.
- **Delete them when the dev environment is torn down.** A dormant account with a stored
  password is a standing risk, and these have weak MFA posture by construction.

## C3. Optional — group overage test

**Optional, and it creates 201 objects in your directory.** Skip it on a first pass.

Assigned-groups-only (§A3) makes overage impossible through this application, and confirming
that setting is sufficient for validation sign-off. This test exists to prove the assumption
holds, and to catch a future administrator changing the setting — it is a periodic assurance
check, not a gate.

> **Directory-mutating.** Creates 201 real Entra groups. Do not run it in a tenant where group
> inventory is audited or where naming is governed, without asking first. Clean up afterwards.

```powershell
# Create 201 groups and add a test user to all of them
1..201 | ForEach-Object {
    $g = az ad group create --display-name "zz-overage-probe-$_" --mail-nickname "zzoverageprobe$_" | ConvertFrom-Json
    az ad group member add --group $g.id --member-id <test-user-object-id>
}
```

Sign in as that user and inspect the ID token.

- **Expected:** `groups` is present and contains only the `FCTelecom-*` groups. Sign-in works
  normally, because only assigned groups are emitted.
- **If instead** `groups` is absent and `_claim_names` is present, the app is configured for
  "All groups" — go back to §A3. Do not work around it in code.

```powershell
az ad group list --display-name-starts-with "zz-overage-probe" --query "[].id" -o tsv |
    ForEach-Object { az ad group delete --group $_ }
```

### If "All groups" ever becomes necessary

The Graph fallback becomes a real change, not a setting:

- Restore `Microsoft.Graph` and `Microsoft.Identity.Web.MicrosoftGraph` to
  `Directory.Packages.props` (both are in the removed-packages comment block).
- Add `GroupMember.Read.All` and grant admin consent.
- In `PermissionClaimsEnricher`, detect absent `groups` plus present `_claim_names` and call
  `POST /users/{id}/getMemberObjects` for transitive group IDs.
- Cache per session — this runs on every sign-in and the call is not free.
- **Fail closed.** If Graph fails, grant nothing and log an error. A user silently receiving
  fewer permissions is confusing; a user silently receiving more is an incident.

Tracked as a backlog item, deliberately not implemented.

---

# §D — Removal

*Called from validation step 16.*

`99-Cleanup.ps1` leaves Entra objects alone — they cost nothing and are the slowest part to
recreate. To remove them:

```powershell
foreach ($role in 'AppAdministrator','NetworkEngineer','Procurement','HelpDesk','ReadOnly') {
    az ad user delete  --id "fctest-$($role.ToLower())@<domain>"
    az ad group delete --group "FCTelecom-$role"
}
az ad group delete --group "FCTelecom-SQL-Migrators"
az ad app delete --id <client-id>

# The separate testing vault and its resource group
az group delete --name rg-fctelecom-dev-testing --yes
```

Deleted app registrations and users are recoverable for 30 days.

---

## Verification checklist

Run at validation step 12, not here:

- [ ] Sign-in completes and returns to the application
- [ ] `groups` claim present, containing only `FCTelecom-*` groups
- [ ] Log line: *"Resolved N permission(s) for … from M group(s)"* with N > 0
- [ ] A **directly** assigned user gets their role; a **nested** member does not (§A3)
- [ ] Sign-out returns to the application and the session is genuinely gone
- [ ] An interactive page responds to a click (Blazor circuit through the corrected CSP)
