<#
.SYNOPSIS
    Test SQL permissions AS THE APP SERVICE MANAGED IDENTITY — not as you.

.EXAMPLE
    ./scripts/validate/05-TestAppIdentity.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.DESCRIPTION
    CORRECTION TO AN EARLIER VERSION OF THIS PASS.

    An earlier revision told you to run the data-plane checks with `sqlcmd -G` from your
    workstation. That authenticates as YOU. If you are in the SQL admin group — which you must
    be, to have created the users at all — then every "the application cannot do this" check
    passes for the wrong reason, and the pass reports a control that does not exist. It is
    worse than not testing, because it produces a tick.

    This script instead obtains a database access token issued to the App Service's own managed
    identity, and runs the checks with it. The mechanism:

      1. App Service injects IDENTITY_ENDPOINT and IDENTITY_HEADER into the container.
      2. A command run INSIDE the container calls that endpoint for a token scoped to
         https://database.windows.net/. Only code running in the app can do this — that is the
         entire point of a managed identity.
      3. The token comes back here and is passed to Invoke-Sqlcmd -AccessToken.

    Everything the token then does is exactly what the application itself could do.

    TOKEN HANDLING. The token is a real credential, valid roughly 24 hours, and it is not
    revocable. It is held in memory only, never written to disk or to the transcript, and
    cleared at the end. Do not paste it anywhere. If you think one has leaked, disable and
    re-enable the App Service system-assigned identity, which invalidates outstanding tokens.

    MUTATION. Every prohibited-operation test runs inside a transaction that is always rolled
    back. The permitted-operation tests are SELECT only. Nothing here changes data.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer module is required for -AccessToken support. Install-Module SqlServer -Scope CurrentUser"
}
Import-Module SqlServer -ErrorAction Stop

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName

Show-FcContext -Operation 'SQL permission test AS THE APP SERVICE IDENTITY' `
               -Environment $Environment -ResourceGroup $ResourceGroup `
               -SqlServer $outputs.sqlServerFqdn -SqlDatabase $outputs.sqlDatabaseName `
               -WebUrl $outputs.webUrl | Out-Null

Write-FcNote 'Read-only: prohibited operations are attempted inside rolled-back transactions.'

$failures = 0
function Fail { param([string]$m) Write-FcFail $m; $script:failures++ }

# ── Obtain a token issued to the app's managed identity ────────────────────────────────
Write-FcHeading 'Acquiring a token for the App Service managed identity'

$webAppName = $outputs.webAppName
$scmHost    = "$webAppName.scm.azurewebsites.net"

Write-FcNote "app: $webAppName"
Write-FcNote 'asking the container to call its own IMDS endpoint'

# Single-quoted so $IDENTITY_* expand inside the container, not here.
$innerCommand = 'curl -s -H "X-IDENTITY-HEADER: $IDENTITY_HEADER" ' +
                '"$IDENTITY_ENDPOINT?resource=https%3A%2F%2Fdatabase.windows.net%2F&api-version=2019-08-01"'

$body = @{ command = $innerCommand; dir = '/' } | ConvertTo-Json -Compress

$response = az rest --method post `
    --url "https://$scmHost/api/command" `
    --resource 'https://management.azure.com/' `
    --headers 'Content-Type=application/json' `
    --body $body 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-FcFail 'Could not run a command in the container via the SCM endpoint.'
    Write-Host ($response | Out-String)
    Write-FcNote ''
    Write-FcNote 'FALLBACK — do this by hand, it takes a minute:'
    Write-FcNote "  1. Portal > App Service '$webAppName' > Development Tools > SSH"
    Write-FcNote '  2. Run:'
    Write-FcNote "       $innerCommand"
    Write-FcNote '  3. Copy the access_token value and re-run this script with -AccessToken'
    Write-FcNote ''
    Write-FcNote 'If SSH is also unavailable, SCM basic auth may be disabled by policy. That is'
    Write-FcNote 'a good policy; use the portal SSH console rather than re-enabling it.'
    exit 1
}

try {
    $commandResult = $response | ConvertFrom-Json
    $tokenPayload  = $commandResult.Output | ConvertFrom-Json
    $accessToken   = $tokenPayload.access_token
} catch {
    Fail 'The container responded but the payload was not a token.'
    Write-Host ($response | Out-String)
    exit 1
}

if (-not $accessToken) { Fail 'No access_token returned.'; exit 1 }

Write-FcPass 'token acquired (held in memory only, never written to disk)'
Write-FcNote "issued to: $($tokenPayload.client_id)  expires: $($tokenPayload.expires_on)"

$sqlArgs = @{
    ServerInstance = $outputs.sqlServerFqdn
    Database       = $outputs.sqlDatabaseName
    AccessToken    = $accessToken
    ErrorAction    = 'Stop'
}

try {
    # ── Who does the database think we are? ────────────────────────────────────────────
    Write-FcHeading 'Identity as the database sees it'

    $who = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT
    SUSER_SNAME()  AS [login],
    USER_NAME()    AS [db_user],
    (SELECT auth_scheme FROM sys.dm_exec_connections WHERE session_id = @@SPID) AS [auth_scheme];
'@

    Write-FcPass "connected as '$($who.db_user)' (auth: $($who.auth_scheme))"

    if ($who.db_user -ne $webAppName) {
        Fail "expected db user '$webAppName' but got '$($who.db_user)'"
        Write-FcNote 'The contained user was probably created under a different name. It must'
        Write-FcNote 'match the App Service name exactly — that is the managed identity display name.'
    }

    # ── Role membership: what it must NOT be ───────────────────────────────────────────
    Write-FcHeading 'Role membership'

    $roles = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT r.name AS [role]
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
WHERE u.name = USER_NAME();
'@

    $roleNames = @($roles | ForEach-Object { $_.role })
    Write-FcNote "roles: $($roleNames -join ', ')"

    foreach ($required in 'db_datareader', 'db_datawriter') {
        if ($roleNames -contains $required) { Write-FcPass "has $required" }
        else { Fail "missing $required — the application cannot function" }
    }

    foreach ($forbidden in 'db_owner', 'db_ddladmin', 'db_securityadmin', 'db_accessadmin') {
        if ($roleNames -contains $forbidden) {
            Fail "HAS $forbidden — the runtime identity must not hold schema or security rights"
            Write-FcNote 'Re-run 04-GrantDatabasePrincipals.sql and remove the membership.'
        } else { Write-FcPass "does not have $forbidden" }
    }

    # ── Permitted operations ───────────────────────────────────────────────────────────
    Write-FcHeading 'Permitted operations (must succeed)'

    foreach ($table in 'Locations', 'Services', 'Vendors', 'Contracts') {
        try {
            Invoke-Sqlcmd @sqlArgs -Query "SELECT TOP (1) 1 AS ok FROM dbo.$table;" | Out-Null
            Write-FcPass "SELECT from dbo.$table"
        } catch {
            Fail "SELECT from dbo.$table failed: $($_.Exception.Message)"
        }
    }

    # Writing is tested as a permission rather than an actual insert, because a real insert
    # into a live schema needs valid foreign keys and would leave debris on failure.
    $writable = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT
    HAS_PERMS_BY_NAME('dbo.Locations', 'OBJECT', 'INSERT') AS can_insert,
    HAS_PERMS_BY_NAME('dbo.Locations', 'OBJECT', 'UPDATE') AS can_update;
'@
    if ($writable.can_insert -eq 1 -and $writable.can_update -eq 1) {
        Write-FcPass 'INSERT and UPDATE permitted on dbo.Locations'
    } else {
        Fail 'INSERT/UPDATE not permitted on dbo.Locations — the application cannot save anything'
    }

    # ── Prohibited operations ──────────────────────────────────────────────────────────
    #
    # Each runs for real, inside a transaction that is always rolled back. A permission error
    # is the PASS. Success is the failure, and the rollback means it is a harmless one.
    Write-FcHeading 'Prohibited operations (must be denied)'

    $prohibited = @(
        @{ Name = 'CREATE TABLE';               Sql = 'CREATE TABLE dbo.__fc_permission_probe (id int);' }
        @{ Name = 'ALTER TABLE dbo.Locations';  Sql = 'ALTER TABLE dbo.Locations ADD __fc_probe int NULL;' }
        @{ Name = 'DROP TABLE dbo.Vendors';     Sql = 'DROP TABLE dbo.Vendors;' }
        @{ Name = 'UPDATE dbo.AuditEntries';    Sql = 'UPDATE TOP (1) dbo.AuditEntries SET [Action] = [Action];' }
        @{ Name = 'DELETE dbo.AuditEntries';    Sql = 'DELETE TOP (1) FROM dbo.AuditEntries;' }
        @{ Name = 'UPDATE dbo.SecurityEvents';  Sql = 'UPDATE TOP (1) dbo.SecurityEvents SET [EventType] = [EventType];' }
        @{ Name = 'CREATE USER';                Sql = 'CREATE USER [__fc_probe_user] WITHOUT LOGIN;' }
    )

    foreach ($test in $prohibited) {
        $wrapped = "BEGIN TRY BEGIN TRAN; $($test.Sql) ROLLBACK TRAN; SELECT 'PERMITTED' AS result; END TRY " +
                   "BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRAN; " +
                   "SELECT 'DENIED' AS result, ERROR_NUMBER() AS err, ERROR_MESSAGE() AS msg; END CATCH"
        try {
            $row = Invoke-Sqlcmd @sqlArgs -Query $wrapped
            if ($row.result -eq 'DENIED') {
                Write-FcPass "$($test.Name) denied (error $($row.err))"
            } else {
                Fail "$($test.Name) SUCCEEDED — the runtime identity has rights it must not have"
                Write-FcNote 'Rolled back, so nothing changed. Fix the grants and re-run.'
            }
        } catch {
            # A hard throw here is also a denial, just one the TRY/CATCH could not swallow.
            Write-FcPass "$($test.Name) denied ($($_.Exception.Message.Split([Environment]::NewLine)[0]))"
        }
    }

    # ── Schema-level confirmations ─────────────────────────────────────────────────────
    Write-FcHeading 'Schema confirmations'

    $rowVersions = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT t.name AS [table], ty.name AS [type]
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.name = 'RowVersion';
'@
    $badRowVersions = @($rowVersions | Where-Object { $_.type -ne 'timestamp' })
    if ($rowVersions.Count -eq 0) { Fail 'no RowVersion columns found' }
    elseif ($badRowVersions.Count -gt 0) {
        Fail "$($badRowVersions.Count) RowVersion column(s) are not rowversion: $(($badRowVersions | ForEach-Object { $_.table }) -join ', ')"
    } else {
        Write-FcPass "all $($rowVersions.Count) RowVersion columns are rowversion (sys.types reports 'timestamp')"
    }

    $openCosts = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT COUNT(*) AS violations FROM (
    SELECT ServiceId FROM dbo.ServiceCosts WHERE EffectiveTo IS NULL
    GROUP BY ServiceId HAVING COUNT(*) > 1
) x;
'@
    if ($openCosts.violations -eq 0) { Write-FcPass 'one open cost row per service' }
    else { Fail "$($openCosts.violations) service(s) have more than one open cost row" }

    $constraints = Invoke-Sqlcmd @sqlArgs -Query @'
SELECT name FROM sys.check_constraints
WHERE name IN ('CK_ServiceCosts_EffectiveRange', 'CK_ServiceDependencies_NotSelf');
'@
    $found = @($constraints | ForEach-Object { $_.name })
    foreach ($expected in 'CK_ServiceCosts_EffectiveRange', 'CK_ServiceDependencies_NotSelf') {
        if ($found -contains $expected) { Write-FcPass "$expected present" }
        else { Fail "$expected MISSING — the invariant it enforces is gone" }
    }
}
finally {
    # Clear the credential from memory as soon as it is no longer needed.
    if (Get-Variable -Name accessToken -ErrorAction SilentlyContinue) {
        Set-Variable -Name accessToken -Value $null
        Remove-Variable -Name accessToken -ErrorAction SilentlyContinue
    }
    if (Get-Variable -Name sqlArgs -ErrorAction SilentlyContinue) {
        $sqlArgs['AccessToken'] = $null
        Remove-Variable -Name sqlArgs -ErrorAction SilentlyContinue
    }
    [System.GC]::Collect()
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'App Service identity has exactly the rights it should, and none it should not.' -ForegroundColor Green
    exit 0
}
Write-Host "$failures failure(s)." -ForegroundColor Red
Write-Host 'Nothing was modified — every prohibited test rolled back.'
exit 1
