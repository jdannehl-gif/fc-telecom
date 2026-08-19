# Runbook — Rotate secrets

## What exists, and what does not

| Secret | Where | Rotation |
|---|---|---|
| Field-encryption key | Key Vault | Two-phase, below. **Read the whole procedure first.** |
| Field search-hash key | Key Vault | Requires rehashing every IP row |
| IT Glue API token | Key Vault | Simple — generate, update, verify |
| Probe agent HMAC keys | Key Vault, one per agent | Simple — re-register the agent |
| SQL credentials | **Do not exist** | Entra-only authentication |
| Storage account keys | **Do not exist** | `allowSharedKeyAccess` is disabled |
| Vendor portal passwords | **Do not exist** | Only a reference to your credential manager |

Three rows of that table say "do not exist". That is the point of the design: the fastest
way to make credential rotation easy is to have very few credentials.

## IT Glue API token

1. Generate a new key in IT Glue. **Leave password access disabled** — our integration
   never reads credentials from IT Glue, and a key that cannot is a key that cannot leak them.
2. `az keyvault secret set --vault-name <kv> --name itglue-api-token --value "ITG.xxx"`
3. Restart the function app (or wait for the next cold start).
4. Trigger a dry-run sync from **Administration → Integrations** and confirm it succeeds.
5. Revoke the old key in IT Glue.
6. Confirm a `SecretRotated` security event was written.

## Probe agent HMAC key

Each agent has its own key, so one compromised agent cannot forge another's results.

1. Mark the agent disabled in **Administration → Probes**. Its monitors go `Unknown`, not
   `Down` — coverage gaps accrue, no false outages are raised.
2. `az keyvault secret set --vault-name <kv> --name probe-hmac-<agent> --value "$(openssl rand -base64 32)"`
3. Re-register the agent with the new key (see
   [onboard-a-probe-agent.md](onboard-a-probe-agent.md)).
4. Re-enable it and confirm results are arriving and the heartbeat is current.

## Field-encryption key — read this in full before starting

Rotating this key is the riskiest operation in the system. Get it wrong and every static IP
record becomes unreadable.

The ciphertext format is `v1:` + base64(nonce ‖ tag ‖ ciphertext). The version prefix
exists specifically so that a rotation can decrypt old rows while writing new ones.

**Two-phase:**

**Phase 1 — dual-read.** Deploy a build that reads both `v1` and `v2` and writes `v2`. Add
the new key as `field-encryption-key-v2`; keep the old one in place. Nothing re-encrypts
yet. Verify that new writes produce `v2` and that existing `v1` rows still read correctly.

**Phase 2 — rewrite.** Run the re-encryption job (`Administration → Maintenance →
Re-encrypt sensitive fields`). It processes in batches, is resumable, and logs progress.
Verify `SELECT COUNT(*) FROM ServiceIpAssignments WHERE CidrEncrypted LIKE 'v1:%'` returns
zero, then deploy a build that no longer reads `v1` and retire the old key.

**Before Phase 1:**

- [ ] Take a database backup and note the restore point.
- [ ] Confirm the current key is in escrow and the escrow copy has been tested.
- [ ] Confirm you can decrypt a sample row with the current key.
- [ ] Schedule outside business hours.

## Search-hash key

Rotating this invalidates every `CidrSearchHash`. Exact-match IP search silently returns
nothing until the rows are rehashed — silently, which is what makes it worth calling out.

1. Rotate the field-encryption key first if both are changing (the rehash needs plaintext).
2. Run the rehash job, which decrypts each row and recomputes the hash with the new key.
3. Verify by searching for a known block.

Rotate this only if the key is believed compromised. There is no routine schedule, because
the risk of the rotation exceeds the risk it mitigates.

## Schedule

| Secret | Frequency |
|---|---|
| IT Glue token | Annually, or on staff change |
| Probe HMAC keys | Annually, or on agent host rebuild |
| Field-encryption key | Every 3 years, or on suspected compromise |
| Search-hash key | On suspected compromise only |

Every rotation writes a `SecretRotated` security event. If one is missing from the log,
the rotation did not go through the supported path — find out why.
