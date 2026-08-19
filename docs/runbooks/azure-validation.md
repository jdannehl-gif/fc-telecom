# Runbook — Azure validation pass

The baseline was approved **subject to successful compilation, testing, and Azure validation**.
Compilation and testing now pass in CI. This runbook is the third condition.

`docs/11-first-build-and-validation.md` §4 is the checklist. This document is how to execute it,
and `scripts/validate/` automates the parts a machine can check.

**Budget about half a day**, most of it in step 5, which cannot be scripted and is the step
most worth doing properly.

---

## Before you start

Two things are true of this application that change how you should read a failure:

1. **Nothing has ever touched a database.** No migration has been applied, no query has run
   against SQL Server, and the EF model has never been validated at runtime. Step 3 is the
   first time any of that happens.
2. **Nothing has ever authenticated.** Entra ID, the claims enricher and the authorization
   policies have never run against a real tenant.

So a failure in steps 3–5 is expected in a way that a failure in CI was not. Treat the first
run as a discovery exercise, not a verification.

Record results in the table at the bottom as you go. A half-finished validation pass nobody
wrote down is worth roughly nothing a week later.

---

## Step 1 — Preflight

```bash
./scripts/validate/00-preflight.sh dev
```

Checks tooling, the signed-in subscription and tenant, resource-provider registration, and the
parameter file.

**The parameter file check is the one that matters.** `infra/main.dev.bicepparam` ships with
placeholder all-zero object IDs, because real tenant identifiers do not belong in source
control. Deploy with them in place and the Key Vault and SQL admin role assignments are made
against a principal that does not exist — the deployment *succeeds*, and then nobody can read
the vault and nobody can administer the database. The script fails on the placeholder, on
anything that is not a GUID, and on a well-formed GUID that does not resolve to a real group in
your tenant.

Fill in the real object IDs locally. Do not commit them.

> Object IDs, never display names. A group rename must not silently move who can read the
> vault.

---

## Step 2 — Infrastructure

```bash
./scripts/validate/01-infra-whatif.sh dev rg-fctelecom-dev
```

Runs `az deployment group what-if` and **fails on any `Delete` or `Deploy` operation**. §4.1
says "no unexpected Delete lines"; a checklist item that depends on someone reading several
hundred lines of diff carefully every time is one that will eventually be ticked without being
done.

`Deploy` on an existing resource means *replace*, not update — an immutable property changed.
On SQL or Storage that is data loss.

On a first deployment everything should be `Create`. Then:

```bash
az deployment group create -g rg-fctelecom-dev \
    -f infra/main.bicep -p infra/main.dev.bicepparam
```

- [ ] `what-if` clean
- [ ] Deployment succeeds
- [ ] Outputs recorded (web app name, SQL FQDN, Key Vault URI)

---

## Step 3 — Migration

**Review before applying. Every deploy, but especially this one.**

```bash
./scripts/validate/02-review-migration.sh
```

Generates the idempotent script and checks the four failure modes §3 lists as most likely:

| Check | What it catches |
|---|---|
| Cascade paths | Two cascading FKs into one table — SQL Server error 1785 at apply time |
| Filtered index predicates | An index filtering on a column its table does not have |
| `RowVersion` column type | `varbinary` instead of `rowversion` — the lost-update defect, restored |
| Owned-type nullability | `NOT NULL` on `MailingAddress_*`, making an optional address mandatory |

These are heuristics over generated SQL, not a substitute for reading it. Also scan for
unexpected table drops, `NVARCHAR(MAX)` where a length was intended, and missing check
constraints.

**If the script generation itself fails, that is the finding** — it is the first time the EF
model is genuinely validated, and model errors surface here rather than at runtime.

Then apply, and run the data-plane checks:

```bash
dotnet ef database update --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
sqlcmd -S <server>.database.windows.net -d fctelecom -G -i scripts/validate/04-dataplane-checks.sql
```

Run that as the **application's** identity, not as an admin. Several of those checks confirm the
application *cannot* do something; as an administrator it can, and the script will say so.

- [ ] Migration script reviewed, no findings
- [ ] Migration applied
- [ ] Data-plane checks pass (identity, audit immutability, rowversion, constraints)
- [ ] Reporting principal reads `rpt.*` and **cannot** read `dbo.*`

---

## Step 4 — Smoke test

```bash
./scripts/validate/03-smoke.sh https://fctelecom-dev-web.azurewebsites.net
```

Health endpoints, security headers, HTTPS redirect, and anonymous access boundaries.

`/health/ready` is the first real test of the managed-identity SQL connection. A failure there
is usually the app's identity missing a database user rather than anything wrong with the app —
the connection string carries no credential by design.

The CSP check is worth reading rather than glancing at. Blazor Server needs `wasm-unsafe-eval`;
it does **not** need blanket `unsafe-inline`, which most Blazor CSP examples reach for and which
gives away most of the protection.

- [ ] `/health/live` and `/health/ready` return 200
- [ ] Security headers present, no `unsafe-inline`, no server banners
- [ ] Anonymous requests redirect to sign-in rather than returning content

---

## Step 5 — Identity and authorization

**Not scriptable. The most valuable hour in the plan.**

The failure you are looking for is a role seeing something it should not, which needs a person
who knows what that role is *for*. Use one real account per role.

### Identity (§4.2)

- [ ] Sign-in completes and returns to the application
- [ ] `groups` claim present in the token
- [ ] Sign-in log line appears: *"Resolved N permission(s) for … from M group(s)"*
- [ ] A downstream Graph call succeeds — this specifically verifies the `OnTokenValidated`
      chaining fix. Microsoft.Identity.Web installs its own handler to populate the token
      cache; the original code assigned over it, which compiles, passes a smoke test, and
      breaks token acquisition later and somewhere else.

### Authorization (§4.4)

| Role | Must see | Must **not** see |
|---|---|---|
| Network Engineer | Static IP data (reveal writes a `SecurityEvent`) | — |
| Procurement | Costs, contracts | Static IP data |
| Help Desk | Escalation detail | Financial data |
| Executive | Spend | Static IP data |

- [ ] Each row above confirmed with a real account
- [ ] A direct URL to a page the role lacks returns the "not available" page, **not** the data
- [ ] An export writes a `SecurityEvent`

---

## Step 6 — Observability

- [ ] Application Insights receives traces with a correlation ID
- [ ] **A log event containing a sensitive property arrives redacted.** Test this explicitly
      rather than assuming it. The destructuring policy has never run — trigger a log line
      containing a `ServiceIpAssignment` and read what actually arrived.
- [ ] Daily cap and budget alerts configured

---

## Step 7 — Notifications

Every rule ships disabled, and the order matters.

- [ ] Import reviewed and accepted
- [ ] For each rule: open the **preview** and confirm the resolved recipient list
- [ ] Send a **test notification** and confirm it arrives
- [ ] Only then enable the rule
- [ ] Confirm the 60-day escalation does **not** fire for a contract with a confirmed deadline
      and a recorded action, and **does** for one without

Nothing should be enabled before its preview has been read by someone who knows who those
people are.

---

## Results

| Step | Date | Who | Result | Notes |
|---|---|---|---|---|
| 1 Preflight | | | | |
| 2 Infrastructure | | | | |
| 3 Migration | | | | |
| 3 Data plane | | | | |
| 4 Smoke test | | | | |
| 5 Identity | | | | |
| 5 Authorization | | | | |
| 6 Observability | | | | |
| 7 Notifications | | | | |

**Sign-off:** the baseline condition is met when steps 1–6 pass. Step 7 gates enabling
notifications, not the baseline.

---

## Deliberately out of scope

**Monitoring agents (§4.7)** are Phase 3. When they land, the checks are: two agents in
genuinely different failure domains, recorded in `Probe.FailureDomain`; neither on a domain
controller; outbound-only, verified by confirming no inbound rule exists; stopping one agent
produces `Unknown` and a coverage gap rather than an outage; and a location with no internal
target appears as a coverage gap.
