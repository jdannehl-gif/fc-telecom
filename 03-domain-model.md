# 09 — Integration Validation

The brief says: *"Do not promise an integration until the current API or supported interface has been validated."* This document records what was actually checked, on 2026-08-19, and what the findings mean for the design.

---

## 1. IT Glue — validated, integration is viable

### What the API actually offers

| Property | Finding | Design consequence |
|---|---|---|
| Base URLs | `https://api.itglue.com` (US), `https://api.eu.itglue.com` (EU), `https://api.au.itglue.com` (AU) | Region is a configuration value on `IntegrationConnection.BaseUrl`, not a constant |
| Auth | `x-api-key: ITG.xxxxxxxx…` header, generated per-key in the IT Glue console | Token in Key Vault; database stores the secret *name* only |
| Format | JSON:API specification. `Content-Type: application/vnd.api+json` required | The client must emit and parse JSON:API envelopes (`data`/`attributes`/`relationships`), not plain JSON. This is a real implementation detail people get wrong on day one |
| **Rate limit** | **3000 requests per rolling 5-minute window**; `429` on overage | Token-bucket limiter configured at 2400/5min (80% of ceiling), plus retry-with-backoff on 429 |
| Pagination | Default 50 per page, **maximum 1000** | Bulk reads use `page[size]=1000`; the sync must page, not assume one response |
| Payload limit | 10 MB (Amazon API Gateway enforced) | Batch writes are chunked well below this |
| Query support | `filter`, `sort`, `include`, `page[number]`, `page[size]` | Enables incremental sync by filtering on updated-at rather than full re-reads |
| Password access | A **per-key opt-in** setting; the Passwords API returns values only if enabled on that key | **We never enable it.** Our key is created without password access, so even a leaked token cannot read credentials from IT Glue |

### Relevant endpoints

`organizations`, `locations`, `configurations`, `contacts`, `flexible_asset_types`, `flexible_assets`, `documents`, `passwords` — all supporting `GET` / `POST` / `PATCH` / `DELETE`.

### Recommended mapping

| Local entity | IT Glue target | Why |
|---|---|---|
| Organization (yours) | `organizations` — one record | Single-tenant deployment |
| `Location` | `locations` under that organization | Direct structural match |
| `Vendor` + carrier contacts | `contacts` tagged by vendor, plus a "Carrier" flexible asset for escalation procedures | IT Glue has no first-class vendor object |
| **`Service` / circuit** | **Custom flexible asset type: "ISP & Telecom Circuit"** | This is the cleanest fit and the answer to the brief's question |
| `Contract` | Flexible asset "Telecom Contract" — **summary fields only** | Full terms stay here; IT Glue gets the renewal-relevant subset |
| `ServiceIpAssignment` | **Not synced.** Blocked by default in `FieldMapping` | Explicit guardrail |
| Documents | Not synced in Phase 4 | Avoids duplicating storage and the access-control question. Revisit if asked |

### Recommendation on sync depth: **hybrid, weighted toward summary**

The brief asks whether the best experience is a full data sync, a summarized flexible asset with a deep link, or a hybrid. The answer is **hybrid**:

- Sync the fields a technician needs **at 2am inside IT Glue without switching tools**: circuit ID, carrier, account number, support phone, support priority, demarc location, handoff type, CPE make/model, service role, status.
- Do **not** sync the fields that are only meaningful here and would go stale or leak: full cost history, contract terms beyond a renewal date, static IP data, dependency graphs, availability figures.
- Include a **deep link** field on every flexible asset pointing back to the circuit record here, so the full picture is one click away.

Rationale: a full sync makes IT Glue a second, drifting system of record and doubles the surface where stale data can mislead someone. A link-only asset means the technician has to leave IT Glue during an outage, which defeats the purpose of documenting there at all. The hybrid keeps IT Glue useful standalone for the emergency case while leaving this application unambiguously authoritative.

### Sync design

- **One-way, outbound only.** `SyncDirection = OutboundOnly` is the only supported value in Phase 4. Bidirectional stays disabled until ownership and conflict rules are written and approved (backlog item P5-11).
- **Idempotent by `ExternalRecordLink`.** Unique on `(ConnectionId, LocalEntityType, LocalEntityId)` and on `(ConnectionId, ExternalType, ExternalId)`. Re-running a sync updates; it never duplicates. Names are never used as keys.
- **Dry run first.** Every sync — manual or scheduled — can run in preview mode showing exactly which records would be created and updated, with a field-level diff.
- **Change detection by hash.** `LocalVersionHash` over the mapped fields only. A change to a field that is not mapped does not trigger a write, which keeps request volume far below the rate limit.
- **Per-record error log** with retry count and an `Orphaned` state for external records whose local counterpart was archived.

---

## 2. MikroTik The Dude — validated, and the finding is negative

### What was checked

The Dude's documented capabilities were reviewed for: a REST or HTTP API, a documented database schema or export format, SNMP trap generation, webhook support, and scriptable event handlers.

### Finding

**The Dude has no supported programmatic integration interface.** What it documents is:

| Capability | Available? | Notes |
|---|---|---|
| REST / HTTP API | **No** | Not documented, not supported. The Dude client speaks a proprietary protocol to the Dude server |
| Documented database export | **No** | Data lives in a proprietary store on the RouterOS device. No supported export format |
| SNMP trap generation *(outbound)* | **No** | The Dude is an SNMP *poller*, not a trap source. This distinction is frequently confused |
| Webhooks / HTTP notifications | **No** | Not among the documented notification types |
| Scriptable event handlers | Limited | Notifications fire on triggers, but the notification types are fixed |
| **Email notification** | **Yes** | Via the RouterOS SMTP tool, with template variables (`%[DeviceName]`, `%[Status]`, `%[Address]`) |
| **Syslog notification** | **Yes** | Configurable target server, port, facility, severity. This is the only usable machine-readable outbound path |

The Dude's architecture is built for **alerting a human**, not for feeding another system.

### What this means, stated plainly

**We will not build a "The Dude integration" in the sense of pulling inventory or availability data from it.** Doing so would require either scraping a proprietary protocol or parsing an undocumented on-device store — both of which break on any RouterOS update, and neither of which we are willing to put in a production path.

### What we will build instead

A **syslog ingestion adapter**, clearly labelled as low-fidelity:

- The probe agent (or a small standalone collector) listens for syslog from The Dude.
- Messages are parsed against a configurable regex profile into a device name, address, and state transition.
- Matches are correlated to a `Service` or `Monitor` by IP address or by an explicit mapping table — **never by device name**, which is not a stable key.
- Ingested events land as `CheckResult` rows attributed to a `Probe` of kind `External`, with a **lower quorum weight** than a first-party probe.
- They can **open a `Suspect` state but cannot alone confirm an outage**. That requires a first-party probe agreeing.

That last rule is the important one. Syslog from The Dude tells you The Dude thinks something changed. It does not tell you what The Dude was measuring, from where, with what timeout, or whether The Dude itself was healthy. Treating it as authoritative would poison the availability numbers that the rest of the system works hard to keep honest.

### Recommended transition path

1. **Now** — Run The Dude as-is. Nothing changes. Optionally enable syslog ingestion for supplementary signal.
2. **Phase 3** — Deploy the probe agent alongside The Dude at the same sites. Both run in parallel for a full month.
3. **Compare** — The agent produces coverage-aware availability figures with classification and quorum. The Dude produces up/down alerts. After a month you will have a concrete basis to judge whether the agent covers what you rely on The Dude for.
4. **Decide** — Either retire The Dude for circuit monitoring (keeping it for LAN device mapping, which it is genuinely good at and which this system does not attempt), or keep both with The Dude as a supplementary signal.

There is no step where this application depends on The Dude. That is deliberate and non-negotiable per the brief.

### The alternative, if you would rather move platform

If you decide to replace The Dude outright, ingesting from a platform with a real API (Zabbix, LibreNMS, PRTG, Checkmk, Datadog) is a materially better path than syslog. The `IMonitoringProvider` abstraction and the `Probe` model already accommodate this — an ingest adapter for any of them is a Phase 4 story, not a redesign. But the inventory application must not become dependent on that platform either, which is why first-party probes remain the confirmation authority regardless of what else is ingesting.

---

## 3. Microsoft Graph — no validation concern

Email and Teams channel messages via Graph are well-documented, stable, and first-party. The only design notes worth recording:

- Sending mail as a user requires `Mail.Send` application permission plus an application access policy scoping which mailboxes the app may send as. Configure the policy — an unscoped `Mail.Send` grant lets the application send as *anyone* in the tenant, which is a significant and commonly overlooked over-grant.
- Teams channel messages require `ChannelMessage.Send`, which is subject to Microsoft's protected-API request process for some tenants. **Verify this is approved for your tenant before committing to Teams as the primary channel.** The Power Automate webhook alternative is implemented as a fallback for exactly this reason.
- Both run in the Functions worker, never in the web request path.

---

## 4. Sources

- [Getting started with the IT Glue API — IT Glue Help](https://help.itglue.kaseya.com/help/Content/1-admin/it-glue-api/getting-started-with-the-it-glue-api.html)
- [IT Glue API developer documentation](https://api.itglue.com/developer/)
- [The Dude Network Monitor — MikroTik RouterOS docs](https://mikrotikdocs.fyi/tools/the-dude/)
- [Dude Manual — MikroTik Wiki](https://wandy.nl/nms/Dude%20Manual.htm)
- [SNMP — RouterOS, MikroTik Documentation](https://help.mikrotik.com/docs/spaces/ROS/pages/8978519/SNMP)
- [.NET and .NET Core official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Announcing .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)
