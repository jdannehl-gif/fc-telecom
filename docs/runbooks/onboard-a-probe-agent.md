# Runbook — Onboard a probe agent

The probe agent runs on hardware you already own and gives the monitoring module the
vantage point Azure cannot provide: ICMP, and visibility of internal targets.

## Before you start

**Outbound HTTPS only.** The agent long-polls the application for work and posts results
back. There is no inbound connection, ever, and no firewall rule to open at any site. If
someone tells you the agent needs an inbound port, they are describing a different product.

**Placement is decided.** Two agents plus the Azure perspective:

| | Host | Failure domain |
|---|---|---|
| Primary | A dedicated small VM in the **Dorchester datacenter** | e.g. `Dorchester DC / cluster-A / feed-1` |
| Secondary | A geographically separate major location with **independent power and internet** | Must not share a virtualization cluster or primary network failure domain with the primary |

**Never install an agent on a domain controller.**

Record the failure domain on the probe record. Two agents are only two perspectives if they
can fail independently — same cluster, same UPS, or same upstream circuit makes them one
perspective wearing two hats, and the quorum rule will count it twice and declare a
confident outage that is really a power event.

Requirements:

- A dedicated small VM (~100 MB RAM). The agent is a cross-platform .NET Worker;
  **Windows Service is the supported deployment**, with systemd and container available.
- Outbound HTTPS to the application and to `login.microsoftonline.com`
- Network paths to whatever it will check, including one internal target per location

## 1. Register the agent

**Administration → Probes → Add probe.**

- Name: something that identifies the site — `Agent — Dorchester DC`
- Kind: `SelfHostedAgent`
- Location: the site it sits at
- Host kind: `WindowsService`
- **Failure domain**: datacenter / cluster / power feed, e.g. `Dorchester DC / cluster-A / feed-1`

Recording the location matters: it is how the correlation engine knows that a probe going
quiet is one vantage point lost rather than a site going dark.

## 2. Create its identity

Each agent authenticates as its own application, with one app role.

```bash
az ad app create --display-name "FC Telecom Probe — Dorchester DC"
az ad sp create --id <app-id>

az keyvault secret set --vault-name <kv> \
    --name probe-hmac-dorchester-dc \
    --value "$(openssl rand -base64 32)"
```

Grant the `Probe.Submit` app role and nothing else. That role satisfies only the agent
endpoints; a user policy will reject it, and a user will never satisfy it.

## 3. Install

### Windows Service — the supported method

```powershell
# Extract to a fixed path, e.g. C:\Program Files\FcTelecom\Agent
Expand-Archive .\fctelecom-probeagent-win-x64.zip -DestinationPath 'C:\Program Files\FcTelecom\Agent'

# Configuration lives in the machine environment, not in a file next to the binary.
[Environment]::SetEnvironmentVariable('FcTelecom__ApiBaseUrl','https://fctel-prod-web.azurewebsites.net','Machine')
[Environment]::SetEnvironmentVariable('FcTelecom__TenantId','<tenant-guid>','Machine')
[Environment]::SetEnvironmentVariable('FcTelecom__ClientId','<agent-app-id>','Machine')
[Environment]::SetEnvironmentVariable('FcTelecom__ProbeId','<probe guid from step 1>','Machine')

# The client secret and HMAC key are set separately and are not echoed into a transcript.
New-Service -Name FcTelecomProbeAgent `
            -BinaryPathName '"C:\Program Files\FcTelecom\Agent\FcTelecom.ProbeAgent.exe"' `
            -DisplayName 'FC Telecom Probe Agent' `
            -StartupType Automatic
Start-Service FcTelecomProbeAgent
Get-EventLog -LogName Application -Source FcTelecomProbeAgent -Newest 20
```

Run the service as a **dedicated low-privilege account**, not LocalSystem. ICMP from a
Windows service does not require elevation, so there is nothing to gain from it.

### Linux — systemd

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
