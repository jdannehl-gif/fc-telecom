# 08 — Deployment Design and Cost Drivers

## 1. Environments

| Environment | Purpose | Sizing posture |
|---|---|---|
| **Local** | Development. SQL Server 2022 + Azurite in Docker; auth bypass with a role switcher; simulated monitoring provider. | Free |
| **Dev** | Shared integration environment in Azure. Real Entra ID, real Key Vault, simulated monitoring. | Smallest viable; auto-pause the database |
| **Prod** | — | Right-sized, zone-redundant where it is cheap to be |

Two Azure environments, not three. A separate staging environment is replaced by **deployment slots on the production App Service** — the slot runs the same configuration against the production database's schema after migrations are applied, which catches more real problems than a staging environment with synthetic data ever does.

---

## 2. Azure resources

| Resource | Dev | Prod | Notes |
|---|---|---|---|
| App Service Plan (Linux) | B1 | P1v3 | P1v3 is the entry point for VNet integration, deployment slots, and zone redundancy |
| App Service (Web) | 1 | 1 + `staging` slot | |
| Azure SQL Database | GP Serverless, 0.5–2 vCore, auto-pause 60 min | GP Provisioned, 2 vCore, zone-redundant | Serverless in dev costs almost nothing when idle |
| Storage Account | Standard LRS | Standard GRS | Documents + queues + Functions backing store |
| Function App | Flex Consumption | Flex Consumption | Scales to zero between timer fires |
| Key Vault | Standard | Standard | Purge protection **on** in both |
| Application Insights + Log Analytics | 30-day retention, sampling on | 90-day retention, sampling on | The largest controllable cost lever after SQL |
| Managed identities | System-assigned on Web and Functions | Same | |
| Private endpoints | No | SQL, Key Vault, Storage | Adds a modest per-endpoint hourly cost; worth it for the data tier |
| Azure Front Door + WAF | No | Optional, Phase 2 | Defer until there is a reason |

The probe agent runs on hardware you already own. It has no Azure cost.

---

## 3. What actually drives the bill

Ranked. No prices — those change and you should check current rates for your region and agreement.

1. **Application Insights ingestion.** In a system with per-minute timer functions and per-interval checks, telemetry volume can quietly exceed compute cost. Mitigations built in: adaptive sampling on, `Information` level for application events and `Warning` for framework noise, check results logged as *metrics* rather than as trace events, and a daily cap configured with an alert before it is hit.

2. **Azure SQL compute tier.** The dominant fixed cost. The data volume here is small — the tier is chosen for availability and consistent latency, not capacity. Serverless with auto-pause makes dev nearly free; production is provisioned because auto-pause resume latency is unacceptable for an outage-response tool.

3. **Raw `CheckResult` storage and IO.** This is the one thing that scales with the monitoring footprint rather than with the business. 300 monitors at a 60-second interval from 2 probes generates roughly 26 million rows per month. The 45-day retention default and the rollup strategy exist specifically to bound this. **If cost ever becomes a concern, lengthen the check interval before shortening retention** — a 5-minute interval cuts the row count by 80% and barely affects outage detection when debounce thresholds are tuned alongside it.

4. **App Service Plan.** Fixed and predictable. P1v3 in production, one instance, scale rules on CPU with a low maximum.

5. **Function executions.** Flex Consumption, scaling to zero. At this workload — a handful of timer triggers and bounded queue fan-out — this is a rounding error unless the check executor is misconfigured to run per-monitor rather than per-batch.

6. **Storage.** Documents are small in aggregate (contracts and invoices are PDFs). A lifecycle policy moving blobs to Cool at 90 days and Archive at 1 year makes this negligible.

7. **Bandwidth egress.** Minimal. Blazor Server sends diffs, not payloads. Excel exports are the largest single transfer and they are infrequent.

8. **Private endpoints.** Per-endpoint hourly charge plus data processing. Three of them in production. Small but not zero, and worth naming so it is not a surprise line item.

### Cost controls to configure on day one

- Budget alert on the resource group at 50%, 80%, and 100% of the expected monthly figure
- Daily cap on the Application Insights component, with an alert at 80% of the cap
- Auto-pause on the dev database
- Autoscale maximum instance count on the App Service Plan set deliberately low — the failure mode to avoid is a runaway scale-out from a retry loop
- Blob lifecycle management policy applied from Bicep, not by hand

---

## 4. CI/CD

Both pipelines are provided. GitHub Actions is the primary; the Azure DevOps YAML is a maintained equivalent so the choice is reversible.

### Pipeline stages

```
┌─────────┐   ┌──────────┐   ┌───────────┐   ┌──────────┐   ┌─────────┐   ┌──────┐
│ Restore │──▶│  Build   │──▶│   Test    │──▶│ Analyze  │──▶│ Publish │──▶│Deploy│
│         │   │ warnings │   │ unit +    │   │ CodeQL   │   │ web +   │   │ slot │
│         │   │ as errors│   │ arch +    │   │ deps     │   │ funcs + │   │  →   │
│         │   │          │   │ authz +   │   │ vuln scan│   │ bicep   │   │ swap │
│         │   │          │   │ integ +E2E│   │          │   │         │   │      │
└─────────┘   └──────────┘   └───────────┘   └──────────┘   └─────────┘   └──────┘
```

- **Auth to Azure uses OIDC federated credentials**, not a stored service principal secret. No long-lived credential in GitHub or Azure DevOps.
- **Integration tests run against SQL Server in a service container** (Testcontainers), applying the real migrations. Tests that run against an in-memory provider prove nothing about a SQL Server deployment.
- **Migrations are applied as an explicit pipeline step**, using a generated idempotent SQL script reviewed in the pull request — not by `Database.Migrate()` at application startup. Startup migration is convenient and it is how two instances race each other into a corrupted schema.
- **Deployment is slot-based**: deploy to `staging`, warm up, run smoke tests against the slot, then swap. Rollback is a swap back.
- **Production requires a manual approval gate.** Dev deploys automatically on merge to `main`.

### Deployment order, which matters

1. Bicep `what-if` → review → apply infrastructure
2. Apply database migrations (idempotent script)
3. Deploy Functions
4. Deploy Web to `staging` slot
5. Smoke test the slot
6. Swap

Migrations before code, and additive-only. A migration that drops a column must be split across two releases (stop writing it, then drop it) so a slot swap-back is always safe. This is written down because it is the rule everyone forgets under deadline pressure.

---

## 5. Configuration

Nothing sensitive in `appsettings.json`. Layering:

| Source | Used for | Environment |
|---|---|---|
| `appsettings.json` | Non-secret defaults | All |
| `appsettings.{Environment}.json` | Environment-specific non-secrets | All |
| .NET user secrets | Local developer secrets | Local only |
| App Service configuration | Environment names, feature flags, Key Vault URI | Azure |
| **Key Vault** | IT Glue token, agent HMAC keys, data-protection key, Graph client secret if not using managed identity | Azure |

Key Vault references are resolved at startup through the managed identity. The connection string in configuration contains **no credential** — it is `Server=...;Authentication=Active Directory Default;`.

### Key settings

| Setting | Default | Effect |
|---|---|---|
| `Monitoring:Provider` | `Simulated` (dev), `Composite` (prod) | Which check providers are active |
| `Monitoring:RawRetentionDays` | `45` | Raw `CheckResult` retention |
| `Monitoring:MinimumCoverageForConfidence` | `0.90` | Below this, rollups are flagged `LowConfidence` |
| `Contracts:AlertThresholdDays` | `[180,120,90,60,30]` | Renewal alert schedule |
| `Notifications:Enabled` | `false` | Master switch — **ships off** so a demo import cannot send hundreds of emails |
| `Documents:SasLifetimeMinutes` | `5` | Download URL TTL |
| `Integrations:ItGlue:MaxRequestsPer5Min` | `2400` | Set below the published 3000 ceiling |
| `Security:EnableDevAuthBypass` | `false` | Only honoured in `DEBUG` builds |

---

## 6. Operations

**Health checks.** `/health/live` (process is up) and `/health/ready` (SQL reachable, Blob reachable, Key Vault reachable, outbox depth below threshold, at least one probe agent heartbeat within tolerance). App Service health check probes `/health/live`; alerting watches `/health/ready`.

**Alerts to configure.**

| Condition | Severity |
|---|---|
| `/health/ready` failing for 5 minutes | Critical |
| Outbox depth > 100 or oldest pending > 15 minutes | High |
| Any probe agent heartbeat older than 3× its interval | High |
| IT Glue sync `ConsecutiveFailures` ≥ 3 | Medium |
| HTTP 5xx rate above baseline | High |
| SQL DTU/CPU sustained > 80% | Medium |
| App Insights daily cap at 80% | Low |
| Azure budget at 80% | Low |

**Runbooks** in `docs/runbooks/`: local setup, deploy, restore and DR, secret rotation, onboarding a probe agent.

**Scaling path, in order of what to try first:**

1. Scale up the SQL tier (almost always the first real bottleneck)
2. Scale out App Service instances — requires ARR affinity for Blazor Server, already enabled
3. Add a Redis backplane for the SignalR circuit if affinity becomes limiting
4. Move `CheckResult` to a partitioned table or a separate database if monitoring volume grows past comfort
5. Only then consider extracting the monitoring module — the seam is already there, but this should be the last resort, not the first instinct
