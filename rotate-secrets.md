# Runbook — Deploy

## Order, and why it matters

1. Bicep `what-if` → review → apply
2. Database migrations (idempotent script)
3. Functions
4. Web → `staging` slot
5. Smoke test the slot
6. Swap

**Migrations before code, and additive only.** A migration that drops a column must be
split across two releases — stop writing it, then drop it — so that swapping the slot back
is always safe. This is the rule everyone forgets under deadline pressure, which is why it
is the first thing in this runbook.

## First-time setup

### 1. Entra app registration

```bash
az ad app create --display-name "FC Telecom Manager" \
    --web-redirect-uris "https://<app>.azurewebsites.net/signin-oidc" \
    --enable-id-token-issuance true
```

Add the `groups` optional claim to the ID token. If your groups are large enough to
overflow the token, the application logs a warning at sign-in — plan for Graph-based group
resolution before that happens rather than after.

### 2. Deployment identity

Federated credentials, not a stored secret:

```bash
az ad app federated-credential create --id <app-id> --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<org>/<repo>:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

### 3. Groups

Create the five role groups and one SQL admin group. Record their **object IDs** —
they go into the Bicep parameter files and into `EntraGroupRoleMaps`.

## Deploying

```bash
RG=rg-fctelecom-prod

az deployment group what-if -g $RG \
    -f infra/main.bicep -p infra/main.prod.bicepparam
# read the output properly, especially any Delete lines

az deployment group create -g $RG \
    -f infra/main.bicep -p infra/main.prod.bicepparam
```

### Migrations

```bash
dotnet ef migrations script --idempotent \
    --project src/FcTelecom.Infrastructure \
    --startup-project src/FcTelecom.Web \
    --output migrate.sql

# Review migrate.sql. Every deploy. It is the one artefact that can destroy data.

TOKEN=$(az account get-access-token --resource https://database.windows.net --query accessToken -o tsv)
sqlcmd -S <server>.database.windows.net -d fctelecom -G -P "$TOKEN" -i migrate.sql
```

### Post-deploy grants

Run once per environment, and again if the application's identity changes:

```sql
-- The application's managed identity as a contained database user.
CREATE USER [fctel-prod-web] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [fctel-prod-web];
ALTER ROLE db_datawriter ADD MEMBER [fctel-prod-web];

-- Append-only enforced at the database, not by application convention.
-- Application logic can be defeated by a bug; a missing grant cannot.
DENY UPDATE, DELETE ON dbo.AuditEntries  TO [fctel-prod-web];
DENY UPDATE, DELETE ON dbo.SecurityEvents TO [fctel-prod-web];

-- Reporting principal: rpt.* views only, no base tables. This is why the
-- application-level encryption on ServiceIpAssignments matters — the reporting
-- identity cannot read it even if pointed straight at it.
CREATE USER [fctel-reporting] FROM EXTERNAL PROVIDER;
GRANT SELECT ON SCHEMA::rpt TO [fctel-reporting];
DENY SELECT ON SCHEMA::dbo TO [fctel-reporting];
```

### Secrets

```bash
KV=$(az deployment group show -g $RG -n main --query properties.outputs.keyVaultName.value -o tsv)

az keyvault secret set --vault-name $KV --name field-encryption-key \
    --value "$(openssl rand -base64 32)"
az keyvault secret set --vault-name $KV --name field-search-hash-key \
    --value "$(openssl rand -base64 32)"
```

> **Back these up before anything is encrypted with them.** Losing the field-encryption
> key makes every static IP record permanently unreadable. Key Vault purge protection is
> enabled, which prevents accidental deletion — it does not help if the key is rotated
> incorrectly.

## Rollback

- **Application:** swap the slot back. Seconds.
- **Schema:** there is no automatic rollback. Migrations are additive precisely so that a
  swap-back does not need one. If a migration must be reversed, write a forward migration
  that undoes it.
- **Data:** point-in-time restore. See [restore-and-dr.md](restore-and-dr.md).

## Verify after deploying

- [ ] `/health/ready` returns 200
- [ ] Sign-in completes and lands on the dashboard
- [ ] A user in each role sees the expected navigation and nothing more
- [ ] A location detail page renders with services, costs, and deadlines
- [ ] An export downloads and opens
- [ ] No `Error` entries in Application Insights in the first ten minutes
- [ ] Outbox depth is zero or draining
