# Runbook — Onboard a probe agent

The probe agent runs on hardware you already own and gives the monitoring module the
vantage point Azure cannot provide: ICMP, and visibility of internal targets.

## Before you start

**Outbound HTTPS only.** The agent long-polls the application for work and posts results
back. There is no inbound connection, ever, and no firewall rule to open at any site. If
someone tells you the agent needs an inbound port, they are describing a different product.

Requirements:

- A small Linux or Windows host at the site (a VM or a container is fine; ~100 MB RAM)
- Outbound HTTPS to the application and to `login.microsoftonline.com`
- Network paths to whatever it will check

## 1. Register the agent

**Administration → Probes → Add probe.**

- Name: something that identifies the site — `Agent — Chicago DC`
- Kind: `SelfHostedAgent`
- Location: the site it sits at

Recording the location matters: it is how the correlation engine knows that a probe going
quiet is one vantage point lost rather than a site going dark.

## 2. Create its identity

Each agent authenticates as its own application, with one app role.

```bash
az ad app create --display-name "FC Telecom Probe — Chicago DC"
az ad sp create --id <app-id>

az keyvault secret set --vault-name <kv> \
    --name probe-hmac-chicago-dc \
    --value "$(openssl rand -base64 32)"
```

Grant the `Probe.Submit` app role and nothing else. That role satisfies only the agent
endpoints; a user policy will reject it, and a user will never satisfy it.

## 3. Install

```bash
# Linux
sudo mkdir -p /opt/fctelecom-agent
sudo tar xzf fctelecom-probeagent.tar.gz -C /opt/fctelecom-agent

sudo tee /etc/fctelecom-agent.env >/dev/null <<'EOF'
FcTelecom__ApiBaseUrl=https://fctel-prod-web.azurewebsites.net
FcTelecom__TenantId=<tenant-guid>
FcTelecom__ClientId=<agent-app-id>
FcTelecom__ClientSecret=<from your secret store>
FcTelecom__ProbeId=<probe guid from step 1>
FcTelecom__HmacKey=<the key from step 2>
EOF
sudo chmod 600 /etc/fctelecom-agent.env

sudo systemctl enable --now fctelecom-agent
sudo journalctl -u fctelecom-agent -f
```

ICMP from a non-root process needs a capability rather than running the agent as root:

```bash
sudo setcap cap_net_raw+ep /opt/fctelecom-agent/FcTelecom.ProbeAgent
```

## 4. Verify

Within a minute or two:

- **Administration → Probes** shows the agent `Healthy` with a current heartbeat.
- `/health/ready` reports the probe as reporting.
- Assigned monitors start producing results.

## 5. Assign monitors

**Monitoring → Monitors**, then assign this agent alongside the Azure probe.

Two independent vantage points is the default for a reason: a single perspective cannot
distinguish "the circuit is down" from "the path to the observer is down" from "the
observer is down". A monitor with only one probe still opens outages, but the UI flags it
as reduced confidence and the rollups are marked accordingly.

## Monitoring a NAT'd or dynamic circuit

Cellular and consumer-grade backup circuits often have no stable public address, so they
cannot be checked from outside at all. The only reliable method is to check **outbound
through that circuit** from the agent, which needs source routing on the agent host:

```bash
# Linux: policy routing so traffic from a chosen source address uses the backup circuit.
ip route add default via <backup-gateway> dev <backup-iface> table 100
ip rule add from <agent-ip-on-backup-subnet> table 100
```

Then configure the monitor with the agent as its only probe and a well-known external
target. Record it as reduced confidence; that is accurate, not a limitation to hide.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Heartbeat never arrives | Outbound HTTPS blocked | Check egress rules to the app and to `login.microsoftonline.com` |
| `401` in the agent log | App role not granted, or admin consent missing | Verify `Probe.Submit` and consent |
| `403` with a signature error | HMAC key mismatch | Re-set the Key Vault secret and the agent's copy together |
| Results rejected as replays | Host clock is off | Fix NTP. Batches beyond a 5-minute window are rejected by design |
| ICMP checks all fail, TCP fine | Missing `cap_net_raw` | Apply the `setcap` line above |
| Monitors go `Unknown` after an agent restart | Expected | The gap is recorded as a coverage gap, not an outage. Nothing to fix |
