<#
.SYNOPSIS
    Read-only preflight. Run before anything is created.

.EXAMPLE
    ./scripts/validate/00-Preflight.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.EXAMPLE
    ./scripts/validate/00-Preflight.ps1 -SkipAzureSignIn

    Host and toolchain checks only. No Azure credentials needed. This is what the Ubuntu
    26.04 compatibility workflow runs.

.NOTES
    Every check here has a failure mode that is expensive or confusing to diagnose after a
    deployment and trivial to catch before one. Nothing is created or changed.

.PARAMETER SkipAzureSignIn
    Run only the checks that need no Azure credentials: host, tooling versions, SQL client
    capability, parameter-file shape, and Bicep compilation. Everything that would call an
    authenticated `az` command is skipped and reported as skipped.

    This exists for the Ubuntu 26.04 compatibility workflow, which proves the tooling installs
    and the scripts load on a clean 26.04 image. It is NOT a substitute for a real preflight —
    the checks it skips are the ones that catch a wrong subscription or an unresolvable group.
#>
[CmdletBinding(DefaultParameterSetName = 'Full')]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory, ParameterSetName = 'Full')][string]$ResourceGroup,
    [string]$Location = 'eastus2',
    [Parameter(Mandatory, ParameterSetName = 'Local')][switch]$SkipAzureSignIn
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

$script:Skipped = 0
function Skip { param([string]$m) Write-FcWarn "skipped (-SkipAzureSignIn): $m"; $script:Skipped++ }

if ($SkipAzureSignIn) {
    Write-Host ''
    Write-Host '==================================================================' -ForegroundColor Cyan
    Write-Host ' FC Telecom — Preflight, local checks only' -ForegroundColor Cyan
    Write-Host '==================================================================' -ForegroundColor Cyan
    Write-Host ' Operation      : Preflight (read-only, no Azure sign-in)'
    Write-Host (' Environment    : {0}' -f $Environment)
    Write-Host ' Subscription   : not read — no sign-in'
    Write-Host '=================================================================='
    Write-FcWarn 'This is NOT a complete preflight. Subscription, providers, resource group,'
    Write-FcNote 'group resolution and app registration are all skipped. Run without'
    Write-FcNote '-SkipAzureSignIn before deploying anything.'
} else {
    Show-FcContext -Operation 'Preflight (read-only)' -Environment $Environment -ResourceGroup $ResourceGroup | Out-Null

    Write-FcWarn 'CONFIRM the subscription and tenant above are the intended ones.'
    Write-FcNote 'A stale `az account set` is the most common cause of deploying into the wrong place.'
}

# ── Host ───────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Host'

$onLinux   = $PSVersionTable.PSVersion.Major -ge 6 -and $IsLinux
$onWindows = -not $onLinux -and $PSVersionTable.PSVersion.Major -ge 6 -and $IsWindows
if ($PSVersionTable.PSVersion.Major -lt 6) { $onWindows = $true }   # Windows PowerShell 5.1

Write-FcNote ([System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim())
Write-FcNote "architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"

$ubuntuVersion = $null
if ($onLinux -and (Test-Path '/etc/os-release')) {
    $osRelease = @{}
    foreach ($line in Get-Content '/etc/os-release') {
        if ($line -match '^([A-Z_]+)="?([^"]*)"?$') { $osRelease[$Matches[1]] = $Matches[2] }
    }

    if ($osRelease['ID'] -ne 'ubuntu') {
        Write-FcWarn "distribution is '$($osRelease['ID'])', not Ubuntu — untested for this pass"
    } else {
        $ubuntuVersion = $osRelease['VERSION_ID']
        $codename      = $osRelease['VERSION_CODENAME']
        switch ($ubuntuVersion) {
            '26.04' { Write-FcPass "Ubuntu 26.04 LTS ($codename) — the supported validation host" }
            '24.04' { Write-FcPass "Ubuntu 24.04 LTS ($codename) — also supported" }
            default {
                Write-FcWarn "Ubuntu $ubuntuVersion ($codename) — not a release this pass has been run on"
                Write-FcNote 'Supported: 26.04 LTS (primary) and 24.04 LTS. Others may work.'
            }
        }
        Write-FcNote 'Bootstrap: scripts/bootstrap/ubuntu-26.04.sh'
    }
}

# Windows PowerShell 5.1 lacks System.Security.Cryptography.AesGcm, which 09-VerifyEncryption
# needs, and -SkipHttpErrorCheck, which 08-Smoke needs. Fail early and clearly rather than
# several steps later with a missing-type or missing-parameter error.
if ($PSVersionTable.PSVersion.Major -ge 7) {
    Write-FcPass "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
    if ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 4) {
        Write-FcWarn 'PowerShell 7.0-7.3 are out of support. 7.6 LTS is the current LTS.'
    }
} else {
    Fail "PowerShell $($PSVersionTable.PSVersion) — 7.0+ required (AesGcm, -SkipHttpErrorCheck)"
}

# ── Tooling ────────────────────────────────────────────────────────────────────────────
#
# Report the VERSION of each, not just presence. "az is installed" tells you nothing when the
# failure three steps later is a command that needs a newer one.
Write-FcHeading 'Tooling'

function Install-Hint {
    param([string]$Windows, [string]$Ubuntu)
    if ($onLinux) { return $Ubuntu } else { return $Windows }
}

function Test-Tool {
    param(
        [string]$Name,
        [scriptblock]$Version,
        [string]$Install,
        [string]$Note,
        # Report absence as a warning rather than a failure. Used only for tools that no
        # remaining check depends on — under -SkipAzureSignIn, `az` is one of them.
        [switch]$Optional
    )
    $reported = $null
    try { $reported = (& $Version) } catch { $reported = $null }

    if ($reported) {
        Write-FcPass ('{0,-12} {1}' -f $Name, ($reported -join ' ').Trim())
    } elseif ($Optional) {
        Write-FcWarn "$Name not found — $Install"
        if ($Note) { Write-FcNote $Note }
    } else {
        Fail "$Name not found — $Install"
        if ($Note) { Write-FcNote $Note }
    }
    return [bool]$reported
}

Test-Tool -Name 'git' -Version { (git --version 2>$null) -replace '^git version ', '' } `
    -Install (Install-Hint 'winget install Git.Git' 'sudo apt-get install -y git') | Out-Null

# .NET 10 specifically. The application targets net10.0 and nothing here changes that; an
# older SDK cannot build it and a newer one is not required.
$dotnetOk = Test-Tool -Name 'dotnet' -Version { dotnet --version 2>$null } `
    -Install (Install-Hint 'winget install Microsoft.DotNet.SDK.10' 'sudo apt-get install -y dotnet-sdk-10.0')

if ($dotnetOk) {
    $sdks = @(dotnet --list-sdks 2>$null)
    $has10 = @($sdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
    if ($has10) {
        Write-FcPass 'a 10.x SDK is installed (required: the application targets net10.0)'
    } else {
        Fail 'no 10.x SDK found — the application targets net10.0 and will not build'
        Write-FcNote "installed: $($sdks -join '; ')"
    }

    # Mixed package sources are the documented cause of "the runtime is there but the SDK
    # isn't" on Ubuntu. Cheap to check, genuinely confusing to diagnose.
    if ($onLinux -and (Get-Command apt-cache -ErrorAction SilentlyContinue)) {
        $policy = (apt-cache policy dotnet-sdk-10.0 2>$null) -join "`n"
        if ($policy -match 'packages\.microsoft\.com') {
            Write-FcWarn '.NET appears to be sourced from packages.microsoft.com'
            Write-FcNote 'Microsoft recommends the Ubuntu archive on 22.04+. Mixing sources causes'
            Write-FcNote 'version-resolution failures. See scripts/bootstrap/ubuntu-26.04.sh.'
        }
    }
}

$azNote = ''
if ($onLinux -and $ubuntuVersion -eq '26.04') {
    $azNote = "Microsoft's apt packages are tested on Ubuntu 22.04/24.04 only. " +
              "scripts/bootstrap/ubuntu-26.04.sh explains the supported 26.04 options."
}

# Under -SkipAzureSignIn nothing left in this script calls `az`: Bicep compiles through the
# standalone binary and every authenticated section is skipped. Absence is then worth saying
# out loud but is not a failure of what this mode claims to prove.
$azOk = Test-Tool -Name 'az' -Version { az version --query '"azure-cli"' -o tsv 2>$null } `
    -Install (Install-Hint 'winget install Microsoft.AzureCLI' 'scripts/bootstrap/ubuntu-26.04.sh --azure-cli=container') `
    -Note $azNote -Optional:$SkipAzureSignIn

Test-Tool -Name 'bicep' -Version {
    if (Get-Command bicep -ErrorAction SilentlyContinue) { (bicep --version 2>$null | Select-Object -First 1) }
    elseif ($azOk) { (az bicep version 2>$null | Select-Object -First 1) }
} -Install 'az bicep install, or install the standalone binary (see the bootstrap script)' | Out-Null

$efNote = ''
if ($onLinux) { $efNote = '~/.dotnet/tools is not on PATH by default on Ubuntu — that is usually the problem.' }

Test-Tool -Name 'dotnet-ef' -Version { (dotnet-ef --version 2>$null | Select-Object -Last 1) } `
    -Install 'dotnet tool install --global dotnet-ef --version 10.*' `
    -Note $efNote | Out-Null

# ── SQL client capability ──────────────────────────────────────────────────────────────
#
# A capability probe, not a version check. 07-TestAppIdentity.ps1 depends on
# Invoke-Sqlcmd -AccessToken, which older SqlServer modules do not have — and the failure
# ("a parameter cannot be found that matches parameter name 'AccessToken'") arrives three
# steps into the pass, after a deployment.
Write-FcHeading 'SQL client'

$sqlModule = Get-Module -ListAvailable -Name SqlServer | Sort-Object Version -Descending | Select-Object -First 1

if (-not $sqlModule) {
    Fail 'SqlServer module not installed — Install-Module SqlServer -Scope CurrentUser'
} else {
    Write-FcPass "SqlServer module $($sqlModule.Version)"

    try {
        Import-Module SqlServer -ErrorAction Stop
        Write-FcPass "imports under PowerShell $($PSVersionTable.PSVersion) on $(if ($onLinux) { 'Linux' } else { 'Windows' })"

        $invoke = Get-Command Invoke-Sqlcmd -ErrorAction Stop
        if ($invoke.Parameters.ContainsKey('AccessToken')) {
            Write-FcPass 'Invoke-Sqlcmd supports -AccessToken'
            Write-FcNote 'This is how 07-TestAppIdentity.ps1 connects as the App Service identity.'
        } else {
            Fail 'Invoke-Sqlcmd has no -AccessToken parameter — 07-TestAppIdentity.ps1 cannot run'
            Write-FcNote 'Update: Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber'
        }

        foreach ($cmdlet in 'Invoke-Sqlcmd') {
            if (-not (Get-Command $cmdlet -ErrorAction SilentlyContinue)) {
                Fail "$cmdlet not available after importing SqlServer"
            }
        }
    } catch {
        Fail "SqlServer module will not import: $($_.Exception.Message)"
        Write-FcNote 'The module is cross-platform, but a partial install can leave it unloadable.'
        Write-FcNote 'Try: Uninstall-Module SqlServer; Install-Module SqlServer -Scope CurrentUser -Force'
    }
}

# unixODBC / msodbcsql are deliberately NOT required. Invoke-Sqlcmd uses the managed
# Microsoft.Data.SqlClient driver. Saying so here stops someone installing an ODBC stack that
# has no Ubuntu 26.04 package, to satisfy a dependency nothing in this pass has.
Write-FcNote 'unixODBC/msodbcsql are not required — Invoke-Sqlcmd uses the managed driver.'

# ── Subscription and providers ─────────────────────────────────────────────────────────
Write-FcHeading 'Resource providers'

if ($SkipAzureSignIn) {
    Skip 'provider registration states'
} else {
    foreach ($provider in 'Microsoft.Sql', 'Microsoft.Web', 'Microsoft.KeyVault', 'Microsoft.Storage',
                          'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.Consumption') {
        $state = az provider show -n $provider --query registrationState -o tsv 2>$null
        if ($state -eq 'Registered') { Write-FcPass "$provider registered" }
        else { Fail "$provider is '$state' — az provider register -n $provider" }
    }
}

# ── Resource group ─────────────────────────────────────────────────────────────────────
Write-FcHeading 'Resource group'

if ($SkipAzureSignIn) {
    Skip 'resource group existence and contents'
} elseif (az group exists --name $ResourceGroup 2>$null | ConvertFrom-Json) {
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
            # A placeholder is a hard failure for an operator about to deploy, and the
            # NORMAL state of a fresh clone — the committed file ships placeholders on
            # purpose, because real tenant object IDs do not belong in source control.
            # -SkipAzureSignIn is the fresh-clone case (it is what CI runs), so it says so
            # rather than failing, exactly as the app-registration check below already does.
            # The full preflight still fails, and CI asserts this line is still emitted so
            # the demotion cannot quietly become a deletion.
            if ($SkipAzureSignIn) {
                Write-FcWarn "$name is still the placeholder all-zero GUID"
                Write-FcNote 'Expected in a fresh clone. The full preflight FAILS on this.'
            } else {
                Fail "$name is still the placeholder all-zero GUID"
                Write-FcNote 'Deployment would succeed and grant vault/SQL admin to nothing.'
            }
        }
        elseif ($value -notmatch $guidPattern) {
            Fail "$name is not a GUID ('$value') — object IDs only, never display names"
            Write-FcNote 'A group rename must not silently move who can read the vault.'
        }
        elseif ($SkipAzureSignIn) {
            Write-FcPass "$name is a well-formed GUID"
            Skip "resolving $name to an Entra group"
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
        if ($SkipAzureSignIn) {
            Skip 'resolving AzureAd:ClientId to an app registration'
        } else {
            $appName = az ad app show --id $clientId --query displayName -o tsv 2>$null
            if ($appName) { Write-FcPass "app registration '$appName' resolves" }
            else { Fail "AzureAd:ClientId '$clientId' resolves to no application in this tenant" }
        }
    }
}

# ── Licensing ──────────────────────────────────────────────────────────────────────────
#
# Group-based assignment to an enterprise application requires Microsoft Entra ID P1 or P2.
# Without it you can create the FCTelecom-* groups and map them in the database, and the
# application will still grant nobody anything — because the groups never reach the token.
# The CLI cannot read the tenant's licence SKU reliably, so this is a prompt, not a check.
Write-FcHeading 'Licensing'

Write-FcWarn 'Group-based assignment to an enterprise application requires Entra ID P1 or P2.'
Write-FcNote 'Confirm in the admin centre: Overview > Licenses, or Billing > Licenses.'
Write-FcNote 'Without P1/P2 you cannot assign groups to the app, so no groups claim is emitted'
Write-FcNote 'and every user resolves to zero permissions — which looks like a mapping bug.'

# ── Bicep ──────────────────────────────────────────────────────────────────────────────
#
# Compilation is offline — `az bicep build` needs the Bicep binary, not a subscription — so
# this runs even under -SkipAzureSignIn. It is the check that proves the toolchain on this
# host can actually process the templates.
Write-FcHeading 'Bicep'

# Prefer the standalone binary over `az bicep`. On the Ubuntu 26.04 host the bootstrap script
# installs standalone Bicep precisely because the Azure CLI may be running out of a container,
# where `az bicep install` writes into a layer that disappears. Falling back to `az bicep`
# keeps this working on a Windows workstation, where `az bicep install` is the normal route.
# Compile to a temp file rather than --stdout, so the only thing captured is diagnostics.
# And the diagnostics ARE captured: the previous version swallowed stderr with 2>$null and
# reported a bare "does not compile", which is the least useful possible way to say it.
$bicepOut = Join-Path ([System.IO.Path]::GetTempPath()) 'fc-main.json'

if (Get-Command bicep -ErrorAction SilentlyContinue) {
    # The standalone CLI takes the path POSITIONALLY. `--file` is Azure CLI syntax
    # (`az bicep build --file X`); the standalone binary rejects it. Getting this wrong is
    # how this check first reported "does not compile" for a template that compiles fine in
    # `validate-infrastructure` — a false alarm on the tool, blamed on the template.
    $bicepLog  = & bicep build infra/main.bicep --outfile $bicepOut 2>&1
    $bicepExit = $LASTEXITCODE
    $bicepVia  = 'bicep'
} elseif ($azOk) {
    $bicepLog  = & az bicep build --file infra/main.bicep --outfile $bicepOut 2>&1
    $bicepExit = $LASTEXITCODE
    $bicepVia  = 'az bicep'
} else {
    $bicepLog  = @()
    $bicepExit = $null
    $bicepVia  = $null
}

if ($null -eq $bicepExit) {
    Fail 'no Bicep available — install the standalone binary or run az bicep install'
} elseif ($bicepExit -eq 0) {
    Write-FcPass "infra/main.bicep compiles (via $bicepVia)"
    foreach ($line in @(@($bicepLog) | Where-Object { $_ -match 'Warning' } | Select-Object -First 10)) {
        Write-FcNote ($line -replace '\s+$', '')
    }
} else {
    Fail "infra/main.bicep does not compile (via $bicepVia, exit $bicepExit)"
    foreach ($line in @(@($bicepLog) | Select-Object -First 20)) {
        Write-FcNote ($line -replace '\s+$', '')
    }
}

Remove-Item $bicepOut -ErrorAction SilentlyContinue

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($script:Failures -eq 0) {
    if ($SkipAzureSignIn) {
        Write-Host "Local checks clean ($($script:Skipped) Azure check(s) skipped)." -ForegroundColor Green
        Write-Host 'This proves the host and toolchain, not the subscription. Run the full'
        Write-Host 'preflight — without -SkipAzureSignIn — before deploying anything.'
        exit 0
    }
    Write-Host 'Preflight clean.' -ForegroundColor Green
    Write-Host "Next: ./scripts/validate/01-InfraWhatIf.ps1 -Environment $Environment -ResourceGroup $ResourceGroup"
    exit 0
}

Write-Host "$($script:Failures) preflight failure(s)." -ForegroundColor Red
Write-Host 'Every one of these is cheaper to fix now than to diagnose from a half-deployed environment.'
exit 1
