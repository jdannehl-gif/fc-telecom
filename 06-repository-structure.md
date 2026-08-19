# 02 — System Diagrams

## 2.1 System context (C4 level 1)

Who and what touches FC Telecom Manager.

```mermaid
graph TB
    subgraph People["People"]
        ADMIN["Application Administrator<br/><i>Config, users, roles</i>"]
        ENG["Network / Telecom Engineer<br/><i>Circuits, IPs, outages</i>"]
        FIN["Procurement / Finance<br/><i>Vendors, spend, contracts</i>"]
        HD["Help Desk / Operations<br/><i>Outage response, incidents</i>"]
        EXEC["Executive / Read Only<br/><i>Dashboards</i>"]
    end

    APP["<b>FC Telecom Manager</b><br/>ASP.NET Core 10 · Blazor Web App<br/><br/>Single source of truth for locations,<br/>circuits, carriers, cost, contracts,<br/>and service availability"]

    subgraph Microsoft["Microsoft platform"]
        ENTRA["Microsoft Entra ID<br/><i>SSO, groups, app roles</i>"]
        GRAPH["Microsoft Graph<br/><i>Mail + Teams messages</i>"]
        PBI["Power BI<br/><i>Reads rpt.* views</i>"]
    end

    subgraph External["External systems"]
        ITG["IT Glue<br/><i>Documentation platform</i>"]
        DUDE["MikroTik The Dude<br/><i>Existing monitoring — syslog only</i>"]
        CARRIER["Carrier portals<br/><i>Reference links only, no automation</i>"]
    end

    AGENT["FC Probe Agent<br/><i>Self-hosted, on your network</i>"]
    TARGETS["Monitored targets<br/><i>Circuit public IPs,<br/>internal site devices</i>"]

    ADMIN --> APP
    ENG --> APP
    FIN --> APP
    HD --> APP
    EXEC --> APP

    APP -->|"OIDC sign-in,<br/>group claims"| ENTRA
    APP -->|"Send alert email<br/>and Teams messages"| GRAPH
    PBI -->|"Read-only SELECT<br/>on rpt.* views"| APP
    APP -->|"One-way sync:<br/>orgs, configs, contacts,<br/>flexible assets"| ITG
    DUDE -.->|"Syslog events<br/>(low fidelity)"| APP
    ENG -.->|"Opens tickets manually"| CARRIER

    AGENT -->|"Outbound HTTPS:<br/>pull work, post signed results"| APP
    AGENT -->|"ICMP · TCP · HTTP · DNS"| TARGETS

    classDef system fill:#1f4e79,stroke:#0d2b45,color:#ffffff,stroke-width:2px
    classDef person fill:#e8eef5,stroke:#5b7fa6,color:#12263a
    classDef ext fill:#f2f2f2,stroke:#999999,color:#333333
    class APP,AGENT system
    class ADMIN,ENG,FIN,HD,EXEC person
    class ENTRA,GRAPH,PBI,ITG,DUDE,CARRIER,TARGETS ext
```

---

## 2.2 Container diagram (C4 level 2)

The deployable pieces and how they communicate.

```mermaid
graph TB
    USER["Browser<br/><i>Desktop and mobile</i>"]

    subgraph Azure["Microsoft Azure"]
        subgraph AppTier["Compute"]
            WEB["<b>FcTelecom.Web</b><br/>App Service (Linux, P1v3)<br/>Blazor Web App · InteractiveServer<br/>Minimal APIs + OpenAPI<br/><i>Managed identity</i>"]
            FN["<b>FcTelecom.Worker</b><br/>Azure Functions · .NET isolated<br/>Flex Consumption<br/><i>Timer + queue triggers</i>"]
        end

        subgraph DataTier["Data"]
            SQL[("<b>Azure SQL Database</b><br/>Inventory · cost history<br/>contracts · outages<br/>audit (append-only)<br/>availability rollups")]
            BLOB[("<b>Blob Storage</b><br/>Contracts · invoices<br/>LOAs · diagrams · photos<br/><i>Private, SSE, SAS-only</i>")]
            QUEUE[("<b>Storage Queues</b><br/>Sync + check fan-out")]
        end

        subgraph SecTier["Platform services"]
            KV["<b>Key Vault</b><br/>IT Glue token · agent HMAC keys<br/>data-protection key"]
            AI["<b>Application Insights</b><br/>Traces · metrics · availability"]
        end
    end

    subgraph OnPrem["Your network"]
        AG1["<b>FcTelecom.ProbeAgent</b><br/>Primary datacenter"]
        AG2["<b>FcTelecom.ProbeAgent</b><br/>Secondary site"]
        DUDE["MikroTik The Dude"]
    end

    ENTRA["Entra ID"]
    GRAPH["Microsoft Graph"]
    ITG["IT Glue API<br/><i>api.itglue.com</i>"]
    PBI["Power BI"]

    USER -->|"HTTPS + WebSocket<br/>(SignalR circuit)"| WEB
    USER -.->|"OIDC redirect"| ENTRA
    WEB <-->|"OIDC / token cache"| ENTRA

    WEB -->|"EF Core · TLS<br/>managed identity"| SQL
    WEB -->|"User-delegation SAS"| BLOB
    WEB -->|"Secrets"| KV
    WEB -->|"Enqueue"| QUEUE
    WEB --> AI

    FN -->|"EF Core"| SQL
    FN -->|"Dequeue"| QUEUE
    FN -->|"Secrets"| KV
    FN -->|"Send mail / Teams"| GRAPH
    FN -->|"One-way sync<br/>rate-limited, 3000/5min"| ITG
    FN --> AI

    AG1 -->|"Outbound HTTPS only<br/>POST /api/agent/results<br/>GET /api/agent/work"| WEB
    AG2 -->|"Outbound HTTPS only"| WEB
    DUDE -.->|"Syslog UDP → collector<br/>(optional adapter)"| AG1

    PBI -->|"Read-only SELECT<br/>on rpt.* views"| SQL

    classDef azure fill:#1f4e79,stroke:#0d2b45,color:#ffffff,stroke-width:2px
    classDef data fill:#2d6a4f,stroke:#1b4332,color:#ffffff
    classDef onprem fill:#7b4f9d,stroke:#4a2f61,color:#ffffff
    classDef ext fill:#f2f2f2,stroke:#999999,color:#333333
    class WEB,FN azure
    class SQL,BLOB,QUEUE,KV,AI data
    class AG1,AG2,DUDE onprem
    class ENTRA,GRAPH,ITG,PBI,USER ext
```

---

## 2.3 Component view — the modular monolith

```mermaid
graph LR
    subgraph WebP["FcTelecom.Web"]
        PAGES["Blazor pages<br/>+ components"]
        API["Minimal APIs<br/>/api/* + /api/agent/*"]
    end

    subgraph AppP["FcTelecom.Application"]
        DIR["Directory"]
        VEN["Vendors"]
        SVC["Services"]
        FINM["Financials"]
        CON["Contracts"]
        MON["Monitoring"]
        INT["Integrations"]
        NOT["Notifications"]
        PLAT["Platform<br/><i>audit · docs · search</i>"]
    end

    subgraph DomP["FcTelecom.Domain"]
        ENT["Entities<br/>+ value objects"]
        CALC["Calculations<br/><i>availability · annualized spend<br/>cost/Mbps · notice deadline</i>"]
    end

    subgraph InfP["FcTelecom.Infrastructure"]
        EFC["EF Core<br/>DbContext · migrations<br/>audit interceptor"]
        PROV["Providers<br/><i>Blob · Graph · IT Glue<br/>Key Vault · importers</i>"]
    end

    PAGES --> DIR & VEN & SVC & FINM & CON & MON
    API --> SVC & MON & PLAT
    DIR & VEN & SVC & FINM & CON & MON & INT & NOT & PLAT --> ENT
    FINM & CON & MON --> CALC
    EFC -.->|"implements<br/>repository interfaces"| AppP
    PROV -.->|"implements<br/>provider interfaces"| AppP

    classDef w fill:#1f4e79,stroke:#0d2b45,color:#fff
    classDef a fill:#4a7ba7,stroke:#2c4f6b,color:#fff
    classDef d fill:#2d6a4f,stroke:#1b4332,color:#fff
    classDef i fill:#7b4f9d,stroke:#4a2f61,color:#fff
    class PAGES,API w
    class DIR,VEN,SVC,FINM,CON,MON,INT,NOT,PLAT a
    class ENT,CALC d
    class EFC,PROV i
```

**Dependency rule:** arrows point inward only. `Infrastructure` implements interfaces that `Application` declares — it is never referenced by `Application`. This is enforced by an architecture test (`ArchitectureTests.cs`) that fails the build if a forbidden reference appears.

---

## 2.4 Outage detection sequence

How a raw failed check becomes a confirmed outage and an alert — including the three ways we refuse to jump to conclusions.

```mermaid
sequenceDiagram
    autonumber
    participant AG as Probe Agent<br/>(your network)
    participant AZ as Azure check<br/>(Function)
    participant API as Web API
    participant COR as Correlation job<br/>(Functions timer)
    participant DB as Azure SQL
    participant OUT as Outbox
    participant GR as Graph

    Note over AG,AZ: Two independent vantage points, always
    AG->>API: GET /api/agent/work (long-poll, outbound only)
    API-->>AG: Assignments: ICMP 203.0.113.10, TCP 10.20.1.1:443
    AG->>AG: Execute checks
    AG->>API: POST /api/agent/results (HMAC-signed batch)
    AZ->>API: POST results (HTTP/TCP/DNS perspective)
    API->>DB: Insert CheckResult rows (raw, 45-day retention)

    COR->>DB: Read recent results per monitor
    Note over COR: Debounce — N consecutive failures<br/>required before leaving Up

    alt All monitors from one probe failing
        COR->>DB: Classify = MonitoringFailure<br/>record CoverageGap, do NOT open outage
        Note over COR: The probe is down, not the circuit
    else All services at the location failing
        COR->>DB: Open OutageEvent, Classification = SiteFailure
        Note over COR: Power or site event, not a carrier issue
    else One circuit down, sibling circuit up
        COR->>DB: Open OutageEvent, Classification = CarrierFailure
    else Only one probe reporting, no quorum
        COR->>DB: State = Suspect, no outage yet
    end

    opt Outage opened and not inside a MaintenanceWindow
        COR->>OUT: Enqueue OutageConfirmed (dedupe key = monitorId+startUtc)
        OUT->>GR: Teams message + email
        GR-->>OUT: Delivered → mark sent
    end

    Note over COR,DB: On recovery: SuccessThreshold consecutive<br/>successes close the event, then hourly rollup<br/>recomputes availability from eligible time
```

---

## 2.5 Contract renewal alert flow

```mermaid
flowchart TD
    A["Contract created or edited"] --> B{"NoticePeriodDays<br/>and EndDate present?"}
    B -->|No| C["Flag: Incomplete contract terms<br/>→ Data completeness report"]
    B -->|Yes| D["Propose NoticeDeadlineDate<br/>= EndDate − NoticePeriodDays"]
    D --> E{"Confirmed by a person?"}
    E -->|No| F["State: Needs review<br/>Shown on dashboard in amber<br/><b>Alerts still fire</b>, labelled unconfirmed"]
    E -->|Yes| G["State: Confirmed"]
    F --> H
    G --> H["Nightly timer job evaluates<br/>all active contracts"]
    H --> I{"Days until deadline<br/>∈ 180/120/90/60/30?"}
    I -->|No| J["No action"]
    I -->|Yes| K{"Alert already sent<br/>for this threshold?"}
    K -->|Yes| J
    K -->|No| L["Write ContractAlert row<br/>+ Outbox message<br/>dedupe = contractId:threshold"]
    L --> M["Outbox drain → Graph<br/>email to contract owner<br/>+ Teams channel"]
    M --> N["Mark ContractAlert.SentAt"]

    style C fill:#8a4b08,stroke:#5a3005,color:#fff
    style F fill:#8a6d08,stroke:#5a4705,color:#fff
    style N fill:#2d6a4f,stroke:#1b4332,color:#fff
```

---

## 2.6 Import dry-run flow

```mermaid
flowchart LR
    U["User uploads<br/>CSV / XLSX"] --> P["Parse into<br/>ImportRow records"]
    P --> V["Validate:<br/>required fields, types,<br/>referential lookups,<br/>enum values"]
    V --> D["Detect duplicates:<br/>CircuitId, AccountNumber+Vendor,<br/>LocationCode"]
    D --> PRE["<b>Dry-run preview</b><br/>Will create: n<br/>Will update: n<br/>Errors: n<br/>Duplicates: n"]
    PRE --> Q{"User<br/>approves?"}
    Q -->|No| X["Discard batch<br/>(ImportBatch kept for audit)"]
    Q -->|Yes| C["Commit in one transaction<br/>per batch chunk"]
    C --> A["Write AuditEntry per row<br/>+ ImportBatch summary"]

    style PRE fill:#1f4e79,stroke:#0d2b45,color:#fff
    style A fill:#2d6a4f,stroke:#1b4332,color:#fff
```
