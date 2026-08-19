# Runbook — Restore and disaster recovery

| Target | Value |
|---|---|
| RPO | ≤ 5 minutes (Azure SQL transaction log backups) |
| RTO | ≤ 4 hours (redeploy from Bicep + restore database) |

The application tier is stateless and rebuildable from the repository. The only thing that
cannot be reconstructed is the database, the documents in Blob Storage, and the Key Vault
keys — so those are what this runbook is about.

## Restore a database to a point in time

```bash
RG=rg-fctelecom-prod
SERVER=<server-name>

az sql db restore \
    --resource-group $RG --server $SERVER \
    --name fctelecom --dest-name fctelecom-restored \
    --time "2026-08-19T14:30:00Z"
```

Restore to a **new** database, always. Never restore over the live one — if the restore
point turns out to be wrong, you have destroyed the only remaining copy of the current
state to find that out.

Then:

1. Point a scratch App Service instance at `fctelecom-restored` and verify the data.
2. If it is correct, rename: live → `fctelecom-old`, restored → `fctelecom`.
3. Restart the web and function apps.
4. Keep `fctelecom-old` for at least 7 days.

## Recover a deleted document

Blob versioning and soft delete (30 days) are enabled:

```bash
az storage blob undelete --account-name <storage> --container-name documents \
    --name "<blob-path>" --auth-mode login
```

## Recover a Key Vault secret

Soft delete (90 days) and purge protection are on:

```bash
az keyvault secret recover --vault-name <kv> --name field-encryption-key
```

**If the field-encryption key is genuinely lost**, every `ServiceIpAssignment` row is
permanently unreadable. Nothing else in the database is affected — the encryption is
scoped to one table on purpose — but the static addressing has to be re-entered from
carrier records. This is the single most important secret to have a tested backup of.

## Full environment loss

1. `az group create` in the target region.
2. `az deployment group create` with the production parameter file.
3. Restore the database from geo-redundant backup.
4. Restore Key Vault secrets from your escrow copy.
5. Redeploy the application from the pipeline (or the last artefact).
6. Re-register the probe agents — see [onboard-a-probe-agent.md](onboard-a-probe-agent.md).
7. Verify with the post-deploy checklist in [deploy.md](deploy.md).

Everything in step 2 comes from Bicep. Nothing was configured by hand in the portal, which
is what makes this a procedure rather than an archaeology exercise.

## Rehearsal

**Quarterly**, restore production to a scratch database and confirm:

- The restore completes inside the RTO.
- Row counts on `Locations`, `Services`, `ServiceCosts`, and `AuditEntries` are plausible.
- The application starts against it and a location detail page renders.
- `ServiceIpAssignment` values decrypt with the current key.

Record the date and the elapsed time. A DR plan that has never been executed is a document,
not a capability.

## Getting your data out

Any user with `Export.Run` can export the full portfolio to Excel, on demand, without
involving anyone. This is the exit strategy, and it is stated deliberately: a system you
cannot get your data out of is a trap, regardless of how good it is while you are using it.

For a complete extract including history:

```sql
SELECT * FROM rpt.ServiceSpendMonthly;
SELECT * FROM rpt.ContractRenewalPipeline;
SELECT * FROM rpt.AvailabilityByServiceMonth;
-- ServiceIpAssignments requires application-level decryption; export it
-- through the application, not through SQL.
```
