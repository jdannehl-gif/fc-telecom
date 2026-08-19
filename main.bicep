# 06 — Repository Structure

```
fc-telecom/
├── FcTelecom.sln
├── Directory.Build.props           # TFM, nullable, warnings-as-errors, analyzers
├── Directory.Packages.props        # Central package management — one version, everywhere
├── global.json                     # Pins the SDK band
├── .editorconfig                   # Style + analyzer severity, enforced in CI
├── docker-compose.yml              # SQL Server 2022 + Azurite for local dev
├── Dockerfile                      # Multi-stage build of FcTelecom.Web
├── README.md
├── CONTRIBUTING.md
│
├── docs/
│   ├── 00-assumptions-and-questions.md
│   ├── 01-architecture.md
│   ├── 02-diagrams.md
│   ├── 03-domain-model.md
│   ├── 04-backlog.md
│   ├── 05-wireframes.md
│   ├── 06-repository-structure.md
│   ├── 07-threat-model.md
│   ├── 08-deployment-and-cost.md
│   ├── 09-integration-validation.md
│   ├── 10-monitoring-design.md
│   └── runbooks/
│       ├── local-setup.md
│       ├── deploy.md
│       ├── restore-and-dr.md
│       ├── rotate-secrets.md
│       └── onboard-a-probe-agent.md
│
├── src/
│   ├── FcTelecom.Domain/
│   │   ├── Common/                 # BaseEntity, IAuditable, ISoftDeletable, DomainEvent
│   │   ├── Directory/              # Location, Region, CostCenter, BusinessUnit, Contact
│   │   ├── Vendors/                # Vendor, VendorAccount, VendorTicketProcedure
│   │   ├── Services/               # Service, ServiceIdentifier, ServiceBandwidth,
│   │   │                           #   ServiceIpAssignment, ServiceDependency
│   │   ├── Financials/             # ServiceCost, OneTimeCharge, Invoice, InvoiceLine
│   │   ├── Contracts/              # Contract, ContractService, ContractAmendment
│   │   ├── Monitoring/             # Monitor, Probe, CheckResult, OutageEvent,
│   │   │                           #   MaintenanceWindow, CoverageGap, AvailabilityRollup
│   │   ├── Platform/               # AppUser, AppRole, AuditEntry, SecurityEvent, Document
│   │   ├── Integrations/           # IntegrationConnection, ExternalRecordLink, FieldMapping
│   │   └── Calculations/           # ⭐ Pure functions — no I/O, exhaustively unit tested
│   │       ├── AvailabilityCalculator.cs
│   │       ├── SpendCalculator.cs
│   │       ├── NoticeDeadlineCalculator.cs
│   │       ├── CostPerMbpsCalculator.cs
│   │       └── DiversityAnalyzer.cs
│   │
│   ├── FcTelecom.Application/
│   │   ├── Abstractions/           # IApplicationDbContext, ICurrentUser, IClock,
│   │   │                           #   IDocumentStore, INotificationSender,
│   │   │                           #   IMonitoringProvider, IItGlueClient, IFieldEncryptor
│   │   ├── Authorization/          # Permissions.cs, PolicyNames.cs, PermissionRequirement
│   │   ├── Directory/              # Commands, Queries, DTOs, Validators
│   │   ├── Vendors/
│   │   ├── Services/
│   │   ├── Financials/
│   │   ├── Contracts/
│   │   ├── Monitoring/
│   │   ├── Integrations/
│   │   ├── Notifications/
│   │   ├── Platform/               # Audit, search, saved views, export
│   │   └── DependencyInjection.cs
│   │
│   ├── FcTelecom.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── ApplicationDbContext.cs
│   │   │   ├── Configurations/     # One IEntityTypeConfiguration per entity
│   │   │   ├── Interceptors/       # AuditSaveChangesInterceptor, SoftDeleteInterceptor
│   │   │   ├── Migrations/
│   │   │   ├── Views/              # rpt.* view definitions applied by migration
│   │   │   └── Seed/               # DemoDataSeeder — realistic, incl. deliberate gaps
│   │   ├── Identity/               # Entra group→role resolution, permission claims
│   │   ├── Security/               # KeyVaultFieldEncryptor, HmacSearchHasher
│   │   ├── Documents/              # BlobDocumentStore (user-delegation SAS)
│   │   ├── Notifications/          # GraphNotificationSender, TeamsSender, OutboxDispatcher
│   │   ├── Monitoring/             # SimulatedProvider, AzureCheckProvider, AgentProvider
│   │   ├── Integrations/
│   │   │   ├── ItGlue/             # JSON:API client, rate limiter, mappers
│   │   │   └── TheDude/            # Syslog ingest adapter
│   │   ├── Imports/                # CsvImporter, ExcelImporter, column profiles
│   │   ├── Export/                 # ExcelExporter
│   │   └── DependencyInjection.cs
│   │
│   ├── FcTelecom.Web/
│   │   ├── Program.cs              # Composition root
│   │   ├── Components/
│   │   │   ├── App.razor, Routes.razor, _Imports.razor
│   │   │   ├── Layout/             # MainLayout, NavMenu, TopBar, GlobalSearch
│   │   │   ├── Shared/             # StatusChip, SensitiveField, DataGrid, EmptyState,
│   │   │   │                       #   ConfirmDialog, AuditPanel, SavedViewPicker
│   │   │   └── Pages/
│   │   │       ├── Dashboard/
│   │   │       ├── Locations/
│   │   │       ├── Services/
│   │   │       ├── Vendors/
│   │   │       ├── Contracts/
│   │   │       ├── Costs/
│   │   │       ├── Outages/
│   │   │       ├── Reports/
│   │   │       ├── Imports/
│   │   │       └── Admin/
│   │   ├── Endpoints/              # Minimal APIs, grouped per module
│   │   │   ├── AgentEndpoints.cs   # /api/agent/work, /api/agent/results
│   │   │   └── ...
│   │   ├── Authorization/          # Policy registration, handlers
│   │   ├── wwwroot/
│   │   └── appsettings*.json
│   │
│   ├── FcTelecom.Worker/           # Azure Functions, .NET isolated
│   │   ├── Program.cs
│   │   └── Functions/
│   │       ├── EvaluateContractAlertsFunction.cs   # timer, nightly
│   │       ├── DrainOutboxFunction.cs              # timer, every minute
│   │       ├── CorrelateOutagesFunction.cs         # timer, every minute
│   │       ├── RollUpAvailabilityFunction.cs       # timer, hourly
│   │       ├── ExecuteAzureChecksFunction.cs       # timer, per interval bucket
│   │       ├── PurgeRawCheckResultsFunction.cs     # timer, nightly
│   │       └── SyncItGlueFunction.cs               # timer + queue
│   │
│   ├── FcTelecom.ProbeAgent/       # Self-hosted worker service
│   │   ├── Program.cs
│   │   ├── WorkPuller.cs           # Outbound long-poll
│   │   ├── Checkers/               # IcmpChecker, TcpChecker, HttpChecker, DnsChecker
│   │   ├── ResultBuffer.cs         # Survives disconnection; batches on reconnect
│   │   └── ResultSigner.cs         # HMAC over canonical payload
│   │
│   └── FcTelecom.Contracts/        # Wire DTOs shared with the agent. Versioned. Tiny.
│
├── tests/
│   ├── FcTelecom.Domain.UnitTests/         # Calculations — the highest-value tests
│   ├── FcTelecom.Application.UnitTests/    # Handlers with fakes
│   ├── FcTelecom.Architecture.Tests/       # ⭐ Layering rules; fails build on violation
│   ├── FcTelecom.Integration.Tests/        # Testcontainers SQL Server; real migrations
│   │   └── Authorization/                  # ⭐ Endpoint × role matrix
│   └── FcTelecom.E2E.Tests/                # Playwright
│
├── infra/
│   ├── main.bicep
│   ├── main.dev.bicepparam
│   ├── main.prod.bicepparam
│   └── modules/
│       ├── appservice.bicep
│       ├── sql.bicep
│       ├── storage.bicep
│       ├── keyvault.bicep
│       ├── functions.bicep
│       ├── monitoring.bicep
│       └── rbac.bicep
│
├── pipelines/
│   └── azure-pipelines.yml         # Azure DevOps equivalent of the GH Actions workflow
│
└── .github/
    └── workflows/
        ├── ci.yml                  # build → test → analyze
        ├── cd-dev.yml              # deploy to dev on merge to main
        ├── cd-prod.yml             # manual approval → slot → swap
        └── codeql.yml              # + dependency review
```

## Conventions

**Central package management.** `Directory.Packages.props` pins every package version once. Individual `.csproj` files reference packages without versions. This removes the class of bug where two projects resolve different EF Core versions and the migration behaves differently in tests than in production.

**Warnings as errors, nullable enabled, analyzers on.** Set in `Directory.Build.props`, so a new project inherits them automatically rather than being a hole in the policy.

**One `IEntityTypeConfiguration` per entity.** No configuration in `OnModelCreating` beyond `ApplyConfigurationsFromAssembly`. A 900-line `OnModelCreating` is where schema decisions go to die.

**`Calculations/` contains pure functions only.** No `DateTime.UtcNow`, no database, no injection. Time is passed in via `IClock`. This is what makes availability and spend math testable to the edge cases that matter (leap seconds are not one; month boundaries, partial coverage, and overlapping maintenance windows are).

**The architecture test is not optional.** `FcTelecom.Architecture.Tests` asserts that `Domain` references nothing, `Application` references only `Domain`, and `Infrastructure` is never referenced by `Application`. It runs in CI. A layering violation is a red build, not a code review comment somebody might miss.

**The authorization test matrix is fixture-driven.** One table of `(endpoint, permission)` pairs and one table of `(role, permissions)` generate the full cross-product of allow/deny assertions. Adding an endpoint without adding it to the table fails a completeness test. This is the only practical way to keep "roles cannot access restricted endpoints" true over years of feature work.
