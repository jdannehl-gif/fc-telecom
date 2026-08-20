<#
.SYNOPSIS
    Read-only preflight. Run before anything is created.

.EXAMPLE
    ./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.NOTES
    Every check here has a failure mode that is expensive or confusing to diagnose after a
    deployment and trivial to catch before one. Nothing is created or changed.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$Location = 'eastus2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

$script:Failures = 0
function Fail { param([string]$m) Write-FcFail $m; $script:Failures++ }

# Hoisted: used by both the parameter-file and app-registration sections. PowerShell has no
# block scope, but under StrictMode an unassigned variable throws, and the parameter-file
# section is skippable.
$guidPattern = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'

Show-FcContext -Operation 'Preflight (read-only)' -Environment $Environment -ResourceGroup $ResourceGroup | Out-Null

Write-FcWarn 'CONFIRM the subscription and tenant above are the intended ones.'
Write-FcNote 'A stale `az account set` is the most common cause of deploying into the wrong place.'

# ── Tooling ────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Tooling'

$tools = @(
    @{ Name = 'az';     Test = { az version -o json 2>$null };        Install = 'winget install Microsoft.AzureCLI' }
    @{ Name = 'dotnet'; Test = { dotnet --version 2>$null };          Install = 'winget install Microsoft.DotNet.SDK.10' }
)

foreach ($tool in $tools) {
    if (& $tool.Test) { Write-FcPass "$($tool.Name) present" }
    else { Fail "$($tool.Name) not found — $($tool.Install)" }
}

if (az bicep version 2>$null) { Write-FcPass 'bicep present' }
else { Fail 'bicep not installed — az bicep install' }

if (Get-Command dotnet-ef -ErrorAction SilentlyContinue) { Write-FcPass 'dotnet-ef present' }
else { Fail 'dotnet-ef not installed — dotnet tool install --global dotnet-ef' }

# SqlServer module is what lets us test SQL under the App Service identity later, because it
# is the only convenient client that accepts a raw access token.
if (Get-Module -ListAvailable -Name SqlServer) { Write-FcPass 'SqlServer PowerShell module present' }
else { Fail 'SqlServer module missing — Install-Module SqlServer -Scope CurrentUser' }

# ── Subscription and providers ─────────────────────────────────────────────────────────
Write-FcHeading 'Resource providers'

foreach ($provider in 'Microsoft.Sql', 'Microsoft.Web', 'Microsoft.KeyVault', 'Microsoft.Storage',
                      'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.Consumption') {
    $state = az provider show -n $provider --query registrationState -o tsv 2>$null
    if ($state -eq 'Registered') { Write-FcPass "$provider registered" }
    else { Fail "$provider is '$state' — az provider register -n $provider" }
}

# ── Resource group ─────────────────────────────────────────────────────────────────────
Write-FcHeading 'Resource group'

if (az group exists --name $ResourceGroup 2>$null | ConvertFrom-Json) {
    Write-FcPass "$ResourceGroup exists"
    $existing = az resource list -g $ResourceGroup --query "length(@)" -o tsv 2>$null
    if ([int]$existing -gt 0) {
        Write-FcWarn "$ResourceGroup already contains $existing resource(s)."
        Write-FcNote 'This is not a clean first deployment. Read the what-if output carefully.'
    }
} else {
    Write-FcNote "$ResourceGroup does not exist yet — 02-DeployInfra.ps1 will offer to create it in $Location."
}

# ── Parameter file ─────────────────────────────────────────────────────────────────────
#
# The check that earns its keep. infra/main.dev.bicepparam ships placeholder all-zero object
# IDs because real tenant identifiers do not belong in source control. Deploy with them in
# place and the Key Vault and SQL admin role assignments are made against a principal that
# does not exist: the deployment SUCCEEDS, and then nobody can read the vault and nobody can
# administer the database.
Write-FcHeading "Parameter file: infra/main.$Environment.bicepparam"

$paramFile = "infra/main.$Environment.bicepparam"
if (-not (Test-Path $paramFile)) {
    Fail "$paramFile not found"
} else {
    Write-FcPass 'exists'

    $placeholder = '00000000-0000-0000-0000-000000000000'

    foreach ($line in Get-Content $paramFile) {
        if ($line -notmatch "^param\s+(\w*ObjectId)\s*=\s*'([^']*)'") { continue }
        $name = $Matches[1]; $value = $Matches[2]

        if ($value -eq $placeholder) {
            Fail "$name is still the placeholder all-zero GUID"
            Write-FcNote 'Deployment would succeed and grant vault/SQL admin to nothing.'
        }
        elseif ($value -notmatch $guidPattern) {
            Fail "$name is not a GUID ('$value') — object IDs only, never display names"
            Write-FcNote 'A group rename must not silently move who can read the vault.'
        }
        else {
            $display = az ad group show --group $value --query displayName -o tsv 2>$null
            if ($display) { Write-FcPass "$name resolves to Entra group '$display'" }
            else { Fail "$name is a valid GUID but resolves to no group in this tenant" }
        }
    }
}

# ── Entra app registration ─────────────────────────────────────────────────────────────
#
# The app registration is created by hand (see docs/runbooks/entra-setup-dev.md). Check it
# exists before deploying infrastructure that expects it.
Write-FcHeading 'Entra app registration'

$appSettings = 'src/FcTelecom.Web/appsettings.json'
if (Test-Path $appSettings) {
    $config = Get-Content $appSettings -Raw | ConvertFrom-Json
    $clientId = $config.AzureAd.ClientId
    if ($clientId -like 'REPLACE*') {
        Write-FcWarn "AzureAd:ClientId is still a placeholder in $appSettings"
        Write-FcNote 'Expected for a fresh clone. Set it in App Service configuration, not in this file.'
    } elseif ($clientId -match $guidPattern) {
        $appName = az ad app show --id $clientId --query displayName -o tsv 2>$null
        if ($appName) { Write-FcPass "app registration '$appName' resolves" }
        else { Fail "AzureAd:ClientId '$clientId' resolves to no application in this tenant" }
    }
}

# ── Bicep ──────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Bicep'

az bicep build --file infra/main.bicep --stdout 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Write-FcPass 'infra/main.bicep compiles' }
else { Fail 'infra/main.bicep does not compile' }

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host 'Preflight clean.' -ForegroundColor Green
    Write-Host "Next: ./scripts/validate/01-InfraWhatIf.ps1 -Environment $Environment -ResourceGroup $ResourceGroup"
    exit 0
}

Write-Host "$($script:Failures) preflight failure(s)." -ForegroundColor Red
Write-Host 'Every one of these is cheaper to fix now than to diagnose from a half-deployed environment.'
exit 1
