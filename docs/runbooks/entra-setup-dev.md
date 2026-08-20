# Runbook — Entra ID setup for development

Everything needed to make sign-in work against a development tenant, in the order it has to be
done. Budget **45–60 minutes** the first time.

This is a prerequisite for steps 5 and 6 of `azure-validation.md`. Do it after the
infrastructure deploys, because two of the URLs depend on the App Service host name, which
carries a `uniqueString()` suffix you cannot guess.

> **Use a development tenant, or at minimum a development app registration in a shared
> tenant.** Several steps below grant tenant-wide read permissions and require Global
> Administrator or Privileged Role Administrator to consent. If you do not hold those roles,
> stop and find who does before you start — discovering it halfway through is worse.

---

## 0. What you need first

| Thing | Where it comes from |
|---|---|
| App Service host name | `webAppHostName` in `artifacts/validation/outputs-dev.json` |
| Ability to create app registrations | Application Developer role (or higher) |
| Ability to grant admin consent | Global Administrator or Privileged Role Administrator |
| Ability to create groups | Groups Administrator (or higher) |

```powershell
# The host name every URL below is built from
(Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json).webAppHostName
```

---

## 1. Create the app registration

**Entra admin centre → App registrations → New registration**

| Field | Value |
|---|---|
| Name | `FC Telecom Manager (dev)` |
| Supported account types | **Accounts in this organizational directory only (single tenant)** |
| Redirect URI | leave blank for now — added in step 2 |

Single tenant is deliberate. This application holds a map of one organisation's circuits,
costs and public IP ranges; there is no scenario in which a guest tenant should authenticate
to it.

Record from the Overview blade:

- **Application (client) ID** → `AzureAd:ClientId`
- **Directory (tenant) ID** → `AzureAd:TenantId`
- **Primary domain** (Entra → Overview) → `AzureAd:Domain`

---

## 2. Redirect and logout URLs — exact values

**Authentication → Add a platform → Web**

The application uses the Microsoft.Identity.Web defaults, which appear in
`src/FcTelecom.Web/appsettings.json`:

```json
"CallbackPath": "/signin-oidc",
"SignedOutCallbackPath": "/signout-callback-oidc"
```

Three URLs, and they go in **two different fields**. Getting these wrong produces
`AADSTS50011`, which names the redirect URI but not which field it was missing from.

### Redirect URIs (the "Redirect URIs" list)

```
https://<webAppHostName>/signin-oidc
https://<webAppHostName>/signout-callback-oidc
```

`signin-oidc` receives the authorization code. `signout-callback-oidc` is where Entra returns
the browser **after** sign-out completes — it is a redirect URI, not a logout URL, and it is
the one most often omitted.

### Front-channel logout URL (its own field)

```
https://<webAppHostName>/signout-oidc
```

This is `RemoteSignOutPath`, which Entra calls when the user signs out of a *different*
application in the same session, so this app can drop its cookie too. Without it, single
sign-out silently does not work and the user stays signed in here after signing out elsewhere.

### Local development

Add these as **additional** redirect URIs on the same platform. `dotnet run` prints the port it
bound — use the actual one:

```
https://localhost:<port>/signin-oidc
https://localhost:<port>/signout-callback-oidc
```

### Implicit grant

Leave **both** "Access tokens" and "ID tokens" **unchecked**. This is a confidential client
using the authorization code flow with PKCE. The implicit-flow checkboxes exist for
single-page applications and enabling them here weakens the app for no benefit.

---

## 3. Credentials

### Production and staging: certificate, or workload identity federation

Do not use a client secret outside development. Secrets expire silently, get copied into
configuration files, and appear in support tickets.

### Development: client secret, in Key Vault

**Certificates & secrets → New client secret**

- Description: `dev-local`
- Expires: **90 days** — short on purpose; a dev secret that outlives the dev environment is
  a credential nobody is tracking

Then store it, never paste it into a file:

```powershell
$outputs = Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json

az keyvault secret set `
    --vault-name $outputs.keyVaultName `
    --name 'AzureAd--ClientSecret' `
    --value '<the secret value>' -o none
```

Reference it from App Service configuration so the value never exists as an App Service
setting in clear text:

```
AzureAd__ClientSecret = @Microsoft.KeyVault(VaultName=<vault>;SecretName=AzureAd--ClientSecret)
```

The App Service managed identity needs **Key Vault Secrets User** on the vault for that
reference to resolve. `infra/main.bicep` grants it; confirm with:

```powershell
az role assignment list --assignee $outputs.webAppPrincipalId `
    --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.KeyVault/vaults/$($outputs.keyVaultName)" `
    --query "[].roleDefinitionName" -o tsv
```

> **Note.** The secret value is shown exactly once. If you lose it, delete it and make a new
> one — there is no way to read it back.

---

## 4. API permissions

**API permissions → Add a permission → Microsoft Graph → Delegated permissions**

| Permission | Type | Needed for | Required now? |
|---|---|---|---|
| `User.Read` | Delegated | Sign-in and basic profile | **Yes** |
| `GroupMember.Read.All` | Delegated | Group-overage fallback (§6) | **No — see below** |

Then **Grant admin consent for \<tenant\>** and confirm every row shows a green
"Granted for \<tenant\>".

### Why `GroupMember.Read.All` is not in the list yet

**The application cannot call Microsoft Graph today.** There is no Graph client in it — the
`Microsoft.Graph` package was removed from `Directory.Packages.props` during CI stabilisation
precisely because nothing referenced it.

Adding the permission before the code that uses it grants a tenant-wide read of every group
membership to an application that will not use it. Add it at the same time as the code, not
before. §6 explains what that code would be and how to avoid needing it.

---

## 5. Group claims

**Token configuration → Add groups claim**

Select **Groups assigned to the application**. **Not** "Security groups", and **not** "All
groups".

This one choice is the most consequential in this runbook, and §6 explains why.

Then expand **ID** and set:

- ☑ **Group ID** — emit the object ID

Leave sAMAccountName and NetBIOS unchecked. The application matches on object IDs and treats
display names as cosmetic, because a group rename must not silently move who can read the
static IP inventory.

---

## 6. Group overage — read this before assuming sign-in works

Microsoft Entra caps the number of groups it will put in a token:

| Token type | Limit |
|---|---|
| JWT | **200** groups |
| SAML | 150 groups |
| Implicit flow | 5 groups |

Limits include nested groups.

**When a user exceeds the limit, Entra omits the `groups` claim entirely.** It does not
truncate. It substitutes a pointer to Microsoft Graph (surfaced as `_claim_names` and
`_claim_sources`) and expects the application to go and ask.

### What that means for this application, concretely

`PermissionClaimsEnricher` reads `principal.FindAll("groups")`. For an overage user that
returns nothing, so no group→role mapping matches, so the user is granted **zero permissions**
and sees an application with no data in it — while the sign-in log line reads
*"Resolved 0 permission(s) for … from 0 group(s)"*, which looks like a mapping problem rather
than a token problem.

This is not hypothetical in a large organisation. Long-tenured staff and IT administrators are
exactly the people who accumulate hundreds of group memberships, and they are exactly the
people who will use this application.

### The fix that costs nothing: assigned groups only

Setting the group claim to **"Groups assigned to the application"** (§5) means Entra emits only
the groups explicitly assigned to this app registration — the five `FCTelecom-*` groups below.
Five is not close to 200, so **overage cannot occur**, no Graph call is needed, and the token
stays small.

This is Microsoft's own first recommendation for avoiding overage, and it is why §5 says not to
pick "All groups".

**Assign the groups to the application:** Entra admin centre → Enterprise applications →
`FC Telecom Manager (dev)` → Users and groups → Add user/group → select each `FCTelecom-*`
group. A group that is not assigned here will not appear in the token even if the user is in
it — which is the trade, and it is a good one.

### Testing it anyway

Assigned-groups-only makes overage impossible *through this application*, but the assumption
should still be tested, because a future administrator may change the setting.

1. Create a test account, or use an existing account with many memberships.
2. Add it to **more than 200** groups. Bulk-create with:

   ```powershell
   # Deliberately verbose — you are creating 201 objects in a directory
   1..201 | ForEach-Object {
       $group = az ad group create --display-name "zz-overage-probe-$_" --mail-nickname "zzoverageprobe$_" | ConvertFrom-Json
       az ad group member add --group $group.id --member-id <test-user-object-id>
   }
   ```

3. Sign in as that user. Inspect the ID token (browser dev tools, or the App Service
   authentication logs).
4. **Expected with assigned-groups-only:** the `groups` claim is present and contains only the
   `FCTelecom-*` groups the user belongs to. Sign-in works normally.
5. **If instead** `groups` is absent and `_claim_names` is present, the app is configured for
   "All groups" — go back to §5. Do not work around it in code.
6. Clean up: `az ad group list --display-name-starts-with "zz-overage-probe" --query "[].id" -o tsv | ForEach-Object { az ad group delete --group $_ }`

### If you genuinely need "All groups" later

Then the Graph fallback becomes necessary, and it is a real change rather than a setting:

- Add `Microsoft.Graph` and `Microsoft.Identity.Web.MicrosoftGraph` back to
  `Directory.Packages.props` (both are listed in the removed-packages comment block).
- Add the `GroupMember.Read.All` delegated permission and grant admin consent.
- In `PermissionClaimsEnricher`, detect the absence of `groups` together with the presence of
  `_claim_names`, and call `POST /users/{id}/getMemberObjects` to retrieve the transitive group
  IDs.
- Cache the result for the session. This runs on every sign-in and the call is not free.
- **Fail closed.** If the Graph call fails, grant no permissions and log it as an error. A user
  silently receiving fewer permissions than they should is confusing; a user silently receiving
  more is a security incident.

This is tracked as a backlog item, not implemented. It is deliberately not in the current
build, because assigned-groups-only removes the need for it and adding a tenant-wide group read
to avoid a configuration change is the wrong trade.

---

## 7. Groups and role mappings

### Create one group per role

The application has exactly five roles. Create five groups:

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

**Record every object ID.** They go into `EntraGroupRoleMaps`, and object IDs are the matching
key — display names are cached for readability only.

Then assign all five to the enterprise application (§6), or their members' groups will not
appear in the token.

### What each role can see

| Group | Role | Sees | Must not see |
|---|---|---|---|
| `FCTelecom-AppAdministrator` | AppAdministrator | Everything, including audit and integrations | — |
| `FCTelecom-NetworkEngineer` | NetworkEngineer | Static IP data (reveal writes a `SecurityEvent`) | — |
| `FCTelecom-Procurement` | Procurement | Costs, contracts | Static IP data |
| `FCTelecom-HelpDesk` | HelpDesk | Escalation detail, incidents | Financial data |
| `FCTelecom-ReadOnly` | ReadOnly | Read-only across the estate | Static IP data, no write permissions |

`Admin.Manage`, `Audit.Read` and `Integrations.Manage` are held **only** by AppAdministrator —
there is an architecture test that fails the build if that stops being true.

---

## 8. Bootstrap administrator

A chicken-and-egg problem worth naming: the group→role mappings are managed **in the
application**, by someone holding `Admin.Manage`. On a fresh database nobody holds it, so
nobody can sign in with enough permission to create the first mapping.

Break it once, with SQL, connected as the **Entra SQL administrator** (not the app identity):

```sql
-- Bootstrap: map the AppAdministrator group to the AppAdministrator role.
-- Run ONCE on a fresh database. Everything after this is managed in the UI.

DECLARE @GroupObjectId  nvarchar(100) = N'REPLACE-with-FCTelecom-AppAdministrator-object-id';
DECLARE @GroupName      nvarchar(200) = N'FCTelecom-AppAdministrator';

DECLARE @RoleId int = (SELECT Id FROM dbo.Roles WHERE Name = N'AppAdministrator');

IF @RoleId IS NULL
    THROW 50000, 'Roles are not seeded. Start the application once so SeedReferenceDataAsync runs, then re-run this.', 1;

IF EXISTS (SELECT 1 FROM dbo.EntraGroupRoleMaps WHERE EntraGroupObjectId = @GroupObjectId)
BEGIN
    PRINT 'Mapping already exists - nothing to do.';
END
ELSE
BEGIN
    INSERT INTO dbo.EntraGroupRoleMaps
        (Id, EntraGroupObjectId, EntraGroupDisplayName, RoleId, Enabled, CreatedUtc, CreatedBy)
    VALUES
        (NEWID(), @GroupObjectId, @GroupName, @RoleId, 1, SYSUTCDATETIME(), N'bootstrap');

    PRINT 'Bootstrap mapping created.';
END

SELECT m.EntraGroupObjectId, m.EntraGroupDisplayName, r.Name AS [role], m.Enabled
FROM dbo.EntraGroupRoleMaps m
JOIN dbo.Roles r ON r.Id = m.RoleId;
```

> Reference data (roles and their permissions) is seeded on every application start and is
> idempotent, so **start the app once before running this** or `@RoleId` will be null.
>
> Column names come from `AuditableEntity`. If the insert fails on a missing column, check the
> generated migration in `artifacts/validation/migration.sql` for the actual audit columns and
> adjust — do not disable the audit columns to make the insert work.

Add yourself to `FCTelecom-AppAdministrator`, sign in, and **create the other four mappings
through the application**. That exercises the admin UI, which is the point.

> Group membership changes are not instant. Sign out fully, or wait a few minutes, before
> concluding a mapping does not work.

---

## 9. Test accounts

Step 6 of the validation runbook needs **one account per role**, and it is the step most often
skipped because creating the accounts is tedious. Create them once and keep them.

```powershell
$domain = (az ad signed-in-user show --query userPrincipalName -o tsv).Split('@')[1]
$roles  = 'AppAdministrator', 'NetworkEngineer', 'Procurement', 'HelpDesk', 'ReadOnly'

foreach ($role in $roles) {
    $upn = "fctest-$($role.ToLower())@$domain"

    # Generate a distinct random password per account. Do not reuse one across accounts:
    # the whole point of these accounts is that they are NOT interchangeable.
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $password = [Convert]::ToBase64String($bytes) + '!aA1'

    $user = az ad user create `
        --display-name "FC Test $role" `
        --user-principal-name $upn `
        --password $password `
        --force-change-password-next-sign-in false | ConvertFrom-Json

    az ad group member add --group "FCTelecom-$role" --member-id $user.id

    Write-Host "$upn  ->  FCTelecom-$role"
    # Store the password in Key Vault immediately. Do not keep it in the terminal history.
    az keyvault secret set --vault-name '<dev-vault>' `
        --name "testaccount-$($role.ToLower())" --value $password -o none
}
```

Rules for these accounts, worth stating because they get broken:

- **Each is in exactly one group.** An account in two roles proves nothing about either.
- **Never given administrative roles in Azure or Entra.** They exist to test what the
  application shows, and an account that is a Global Administrator will pass every check for
  the wrong reason.
- **Delete them when the dev environment is torn down.** A dormant account with a stored
  password is a standing risk, and these have weak MFA posture by construction.

---

## 10. Application configuration

Set on the App Service, not in `appsettings.json`:

```powershell
$outputs = Get-Content artifacts/validation/outputs-dev.json | ConvertFrom-Json

az webapp config appsettings set `
    --name $outputs.webAppName `
    --resource-group <rg> `
    --settings `
        "AzureAd__TenantId=<tenant-id>" `
        "AzureAd__ClientId=<client-id>" `
        "AzureAd__Domain=<primary-domain>" `
        "AzureAd__ClientSecret=@Microsoft.KeyVault(VaultName=$($outputs.keyVaultName);SecretName=AzureAd--ClientSecret)" `
    -o none
```

`CallbackPath` and `SignedOutCallbackPath` are already correct in `appsettings.json` and should
not be overridden — if you change them here you must change the app registration to match, and
the two drifting apart is a bad hour.

---

## 11. Verify

```powershell
./scripts/validate/06-Smoke.ps1 -Environment dev -ResourceGroup <rg>
```

Then by hand:

- [ ] Sign-in completes and returns to the application
- [ ] `groups` claim present, containing only `FCTelecom-*` groups
- [ ] Sign-in log line: *"Resolved N permission(s) for … from M group(s)"* with N > 0
- [ ] Sign-out returns to the application and the session is genuinely gone (press Back — you
      should not see cached data)
- [ ] An interactive page responds to a click (proves the Blazor circuit connected through the
      corrected CSP)
- [ ] An account with >200 group memberships still signs in with a working `groups` claim (§6)

---

## Cleanup

`99-Cleanup.ps1` deliberately leaves Entra objects alone — they cost nothing and are the
slowest part to recreate. To remove them:

```powershell
foreach ($role in 'AppAdministrator','NetworkEngineer','Procurement','HelpDesk','ReadOnly') {
    az ad user delete  --id "fctest-$($role.ToLower())@<domain>"
    az ad group delete --group "FCTelecom-$role"
}
az ad app delete --id <client-id>
```

Deleted app registrations and users are recoverable for 30 days.
