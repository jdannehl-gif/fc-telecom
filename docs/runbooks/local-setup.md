# Runbook — Local setup

## Prerequisites

- .NET 10 SDK (`global.json` pins the band; `dotnet --version` should report 10.0.1xx)
- Docker Desktop or a compatible container runtime
- `dotnet-ef` — `dotnet tool install --global dotnet-ef`

## Steps

```bash
git clone <repo> && cd fc-telecom

# SQL Server 2022 + Azurite. There is no SQLite path — see below.
docker compose up -d
docker compose ps          # wait until sql reports (healthy)

dotnet restore
dotnet build

# First run only: create the initial migration.
dotnet ef migrations add InitialCreate \
    --project src/FcTelecom.Infrastructure \
    --startup-project src/FcTelecom.Web

dotnet ef database update \
    --project src/FcTelecom.Infrastructure \
    --startup-project src/FcTelecom.Web

dotnet run --project src/FcTelecom.Web
```

Open `https://localhost:7139`. Demo data seeds automatically because
`SeedDemoData` is `true` in `appsettings.Development.json`.

## Why there is no SQLite quick-start

EF Core provider drift between SQLite and SQL Server silently changes behaviour around
`decimal` precision, filtered indexes, computed columns, and collation — and every one of
those matters in this schema. The effective-dated cost model depends on a filtered unique
index; the availability maths depends on `decimal` precision. A container is a cheaper
dependency than a class of bugs that only appears in production.

## Development authentication

`Security:EnableDevAuthBypass` is `true` in the Development configuration. It provides a
role switcher so you can see the application as each of the five roles without an Entra
tenant.

The bypass is protected three ways: it is inside `#if DEBUG`, it is gated on the
configuration flag, and a test asserts it is unreachable in a Release build. If you are
adding to it, keep all three.

To use real Entra ID locally instead:

1. Register an application in your tenant.
2. Add `https://localhost:7139/signin-oidc` as a redirect URI.
3. Enable ID tokens and the `groups` optional claim.
4. Fill in the `AzureAd` section via user secrets:

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant-guid>" --project src/FcTelecom.Web
dotnet user-secrets set "AzureAd:ClientId" "<client-guid>" --project src/FcTelecom.Web
dotnet user-secrets set "Security:EnableDevAuthBypass" "false" --project src/FcTelecom.Web
```

5. Map your Entra group object ID to a role in **Administration → Roles**, or insert a row
   into `EntraGroupRoleMaps` directly. Object IDs only — display names are never matching
   keys.

## Field-encryption keys

The Development configuration ships two committed keys. They encrypt nothing but seeded
addresses from the RFC 5737 documentation ranges. **Do not reuse them anywhere real.**

Generate real ones with:

```bash
openssl rand -base64 32   # encryption key
openssl rand -base64 32   # search-hash key (must differ from the encryption key)
```

## Resetting

```bash
docker compose down -v     # destroys the database volume
docker compose up -d
dotnet ef database update --project src/FcTelecom.Infrastructure --startup-project src/FcTelecom.Web
```

## Running tests

```bash
dotnet test                                                  # everything
dotnet test tests/FcTelecom.Domain.UnitTests                 # calculations only, no dependencies
dotnet test tests/FcTelecom.Architecture.Tests               # layering + authorization model
```

The domain unit tests have no external dependencies and run in under a second. If you are
changing a calculation, run those first.

## Common problems

| Symptom | Cause | Fix |
|---|---|---|
| `A network-related or instance-specific error` | SQL container not healthy yet | `docker compose ps`, wait for `(healthy)` |
| `Login failed for user 'sa'` | Volume from an older compose file with a different password | `docker compose down -v` and start again |
| `The field-encryption key is not configured` | Running outside Development without user secrets | Set `Security:FieldEncryption:*` |
| Blob operations fail locally | Azurite not running | `docker compose up -d azurite` |
| `dotnet ef` not found | Tool not installed | `dotnet tool install --global dotnet-ef` |
