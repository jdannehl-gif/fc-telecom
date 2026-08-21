<#
.SYNOPSIS
    Test the first-deployment and repeat-deployment paths of 01-InfraWhatIf and 02-DeployInfra.

.EXAMPLE
    pwsh ./scripts/validate/Test-DeploymentSequencing.ps1

.DESCRIPTION
    This exists because of a defect that only a first run could expose, and a first run is the
    one nobody repeats. The what-if step previewed a RESOURCE-GROUP-scoped deployment into
    rg-fctelecom-dev while the group was still going to be created by the NEXT step, so Azure
    answered:

        ResourceGroupNotFound: Resource group 'rg-fctelecom-dev' could not be found

    Once the group exists, the same code path works forever and the defect is invisible. So the
    test has to be able to assert the state where it does not exist.

    Azure is not called. `az` is replaced on PATH with a recorded test double, which lets this
    assert three things that matter and cannot be checked by reading the scripts:

      * WHICH commands were issued — specifically that the deployment is previewed and created
        at SUBSCRIPTION scope, and that nothing falls back to `az deployment group what-if`.
      * That no mutation happens before the gate. On a first run the group must be created BY
        THE DEPLOYMENT, not by an `az group create` ahead of the preview.
      * That the destructive-change gate still fails a destructive modification, and still
        requires -AcknowledgeDestructiveModifications to pass one. A sequencing fix that
        quietly softened the safety gate would otherwise look like a success.

    The Bicep is compiled for real. The template assertions below are about the actual ARM JSON
    the deployment would receive, not about the source text.

    Scenarios:
      A  first deployment   — resource group absent
      B  repeat deployment  — resource group present, benign property changes
      C  repeat deployment  — destructive modification present (negative control)
#>
[CmdletBinding()]
param(
    [switch]$KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$failures = 0
$passes   = 0

function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        Write-Host '  [ ok ] ' -ForegroundColor Green -NoNewline; Write-Host $Name
        $script:passes++
    } catch {
        Write-Host '  [FAIL] ' -ForegroundColor Red -NoNewline; Write-Host $Name
        Write-Host "         $($_.Exception.Message)" -ForegroundColor DarkGray
        $script:failures++
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

# The test double is a shell script, so this needs a POSIX shell. The validation host is
# Ubuntu 26.04 and CI runs the same image; on Windows the rest of the pass still works, this
# particular test just cannot run.
if ($IsWindows) {
    Write-Host ''
    Write-Host 'Test-DeploymentSequencing requires a POSIX shell for the az test double.' -ForegroundColor Yellow
    Write-Host 'Skipped on Windows. It runs on the Ubuntu validation host and in CI.'
    exit 0
}

Write-Host ''
Write-Host 'Deployment sequencing tests' -ForegroundColor White
Write-Host ('-' * 27) -ForegroundColor DarkGray

# ── Workspace ──────────────────────────────────────────────────────────────────────────
#
# A copy, not the repository. These scripts write compiled templates, generated parameter
# files and outputs into artifacts/validation/, and a test must not leave its fixtures sitting
# where a real run would later read them as evidence.
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) "fc-seq-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'infra')  (Join-Path $workspace 'infra')  -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $workspace 'scripts') -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'scripts/validate') (Join-Path $workspace 'scripts/validate') -Recurse -Force

$binDir = Join-Path $workspace 'fakebin'
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$logFile = Join-Path $workspace 'az-invocations.log'

# ── The az test double ─────────────────────────────────────────────────────────────────
#
# Records every invocation, then answers the handful of commands these two scripts issue.
# Anything unrecognised exits non-zero and says so, so a new call added to a script shows up
# as a test failure rather than as a silent empty result.
$fakeAz = @'
#!/usr/bin/env bash
# Test double for the Azure CLI. Written by Test-DeploymentSequencing.ps1.
printf '%s\n' "$*" >> "$FC_AZ_LOG"

case "$1 $2 $3" in
  "account show -o"|"account show --output"|"account show ")
    cat <<'JSON'
{"name":"Test Subscription","id":"11111111-1111-1111-1111-111111111111",
 "tenantId":"22222222-2222-2222-2222-222222222222","user":{"name":"tester@example.org"}}
JSON
    exit 0 ;;
esac

case "$1 $2" in
  "group exists")
    echo "$FC_AZ_RG_EXISTS"; exit 0 ;;

  "group create")
    # Deliberately fails. On a first deployment the resource group must be created BY THE
    # TEMPLATE, inside the previewed change — not by the script beforehand.
    echo "az group create must not be called; the template creates the group" >&2
    exit 64 ;;

  "consumption budget")
    if [ "$FC_AZ_BUDGET_EXISTS" = "true" ]; then
      echo '{"name":"budget-rg-fctelecom-dev","timePeriod":{"startDate":"2026-01-01T00:00:00Z","endDate":"2028-01-01T00:00:00Z"}}'
      exit 0
    fi
    exit 3 ;;

  "deployment sub")
    case "$3" in
      what-if) cat "$FC_AZ_WHATIF"; exit 0 ;;
      create)  exit 0 ;;
      list)    echo "fctelecom-dev-test"; exit 0 ;;
      show)    echo '{"webAppName":{"value":"fctel-dev-web-abc"},"webAppHostName":{"value":"fctel-dev-web-abc.azurewebsites.net"},"keyVaultName":{"value":"fctel-dev-kv-abc"}}'; exit 0 ;;
    esac
    echo "unhandled: az $*" >&2; exit 65 ;;

  "deployment group")
    # The defect this whole file exists for. A resource-group-scoped deployment command is a
    # regression, so fail loudly rather than returning something plausible.
    echo "az deployment group must not be used by 01/02; scope is subscription" >&2
    exit 66 ;;

  "bicep build")
    echo "az bicep build should not be needed; the host binary compiles" >&2
    exit 67 ;;
esac

echo "unhandled: az $*" >&2
exit 65
'@

$azPath = Join-Path $binDir 'az'
$fakeAz -replace "`r`n", "`n" | Out-File -FilePath $azPath -Encoding ascii -NoNewline
& chmod +x $azPath

# ── What-if fixtures ───────────────────────────────────────────────────────────────────
$rgId  = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-fctelecom-dev'
$depId = "$rgId/providers/Microsoft.Resources/deployments/fctelecom-dev-infra"

# First run: everything created, plus the module wrappers reported as Deploy. Those wrappers
# are the reason 01 filters Microsoft.Resources/deployments out of the Deploy check — without
# that filter this fixture alone would fail every first deployment.
$whatIfFirst = @{
    status  = 'Succeeded'
    changes = @(
        @{ changeType = 'Create'; resourceId = $rgId },
        @{ changeType = 'Deploy'; resourceId = $depId },
        @{ changeType = 'Create'; resourceId = "$rgId/providers/Microsoft.Sql/servers/fctel-dev-sql-abc" },
        @{ changeType = 'Create'; resourceId = "$rgId/providers/Microsoft.Web/sites/fctel-dev-web-abc" },
        @{ changeType = 'Create'; resourceId = "$rgId/providers/Microsoft.KeyVault/vaults/fctel-dev-kv-abc" }
    )
} | ConvertTo-Json -Depth 8

$whatIfRepeat = @{
    status  = 'Succeeded'
    changes = @(
        @{ changeType = 'Ignore';   resourceId = $rgId },
        @{ changeType = 'Deploy';   resourceId = $depId },
        @{ changeType = 'NoChange'; resourceId = "$rgId/providers/Microsoft.Sql/servers/fctel-dev-sql-abc" },
        @{ changeType = 'Modify'
           resourceId = "$rgId/providers/Microsoft.Web/sites/fctel-dev-web-abc"
           delta      = @(@{ path = 'properties.siteConfig.appSettings'; propertyChangeType = 'Modify' }) }
    )
} | ConvertTo-Json -Depth 8

$whatIfDestructive = @{
    status  = 'Succeeded'
    changes = @(
        @{ changeType = 'Ignore'; resourceId = $rgId },
        @{ changeType = 'Deploy'; resourceId = $depId },
        @{ changeType = 'Modify'
           resourceId = "$rgId/providers/Microsoft.Sql/servers/fctel-dev-sql-abc/databases/fctel-dev-db"
           delta      = @(@{ path = 'properties.collation'; propertyChangeType = 'Modify'
                             before = 'SQL_Latin1_General_CP1_CI_AS'; after = 'Latin1_General_100_CS_AS' }) }
    )
} | ConvertTo-Json -Depth 8

$fixtures = @{
    first       = Join-Path $workspace 'whatif-first.json'
    repeat      = Join-Path $workspace 'whatif-repeat.json'
    destructive = Join-Path $workspace 'whatif-destructive.json'
}
$whatIfFirst       | Out-File $fixtures.first       -Encoding utf8
$whatIfRepeat      | Out-File $fixtures.repeat      -Encoding utf8
$whatIfDestructive | Out-File $fixtures.destructive -Encoding utf8

# ── Runner ─────────────────────────────────────────────────────────────────────────────
function Invoke-Scenario {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$WhatIfFixture,
        [bool]$GroupExists,
        [bool]$BudgetExists = $false,
        [string[]]$ExtraArgs = @()
    )

    if (Test-Path $logFile) { Remove-Item $logFile -Force }

    $previousPath = $env:PATH
    $env:PATH               = "$binDir$([System.IO.Path]::PathSeparator)$previousPath"
    $env:FC_AZ_LOG          = $logFile
    $env:FC_AZ_WHATIF       = $WhatIfFixture
    $env:FC_AZ_RG_EXISTS    = $GroupExists.ToString().ToLowerInvariant()
    $env:FC_AZ_BUDGET_EXISTS= $BudgetExists.ToString().ToLowerInvariant()

    $previousLocation = Get-Location
    Set-Location $workspace
    try {
        $arguments = @('-NoProfile', '-File', "scripts/validate/$Script",
                       '-Environment', 'dev', '-ResourceGroup', 'rg-fctelecom-dev') + $ExtraArgs
        $output = & pwsh @arguments 2>&1
        $code   = $LASTEXITCODE
    } finally {
        Set-Location $previousLocation
        $env:PATH = $previousPath
    }

    $log = if (Test-Path $logFile) { Get-Content $logFile } else { @() }
    return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n"); Log = $log }
}

# ── Template assertions ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Template' -ForegroundColor White

$compiled = Join-Path $workspace 'subscription.json'
$bicepCmd = Get-Command bicep -ErrorAction SilentlyContinue

Test-Case 'infra/subscription.bicep compiles' {
    Assert-True ([bool]$bicepCmd) 'no bicep binary on PATH — run scripts/bootstrap/ubuntu-26.04.sh'
    $log = & bicep build (Join-Path $workspace 'infra/subscription.bicep') --outfile $compiled 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "bicep build failed: $($log -join '; ')"
}

$template = if (Test-Path $compiled) { Get-Content $compiled -Raw | ConvertFrom-Json } else { $null }

Test-Case 'it is a SUBSCRIPTION-scope template' {
    Assert-True ($null -ne $template) 'template did not compile'
    Assert-True ($template.'$schema' -match 'subscriptionDeploymentTemplate') `
        "schema is $($template.'$schema') — a resource-group template cannot create its own group"
}

Test-Case 'it creates the resource group' {
    $group = @($template.resources | Where-Object type -eq 'Microsoft.Resources/resourceGroups')
    Assert-True ($group.Count -eq 1) "expected one resourceGroups resource, found $($group.Count)"
}

Test-Case 'the resource group carries application=fc-telecom and environment' {
    $group = @($template.resources | Where-Object type -eq 'Microsoft.Resources/resourceGroups')[0]
    Assert-True ($null -ne $group.tags) 'resource group has no tags'
    $variableName = "$($group.tags)" -replace ".*variables\('([^']+)'\).*", '$1'
    $tags = $template.variables.$variableName
    Assert-True ($tags.application -eq 'fc-telecom') "application tag is '$($tags.application)'"
    Assert-True ("$($tags.environment)" -match 'environmentName') 'environment tag is not the environment parameter'
}

Test-Case 'main.bicep is deployed as a module scoped to that group' {
    $nested = @($template.resources | Where-Object type -eq 'Microsoft.Resources/deployments')
    Assert-True ($nested.Count -ge 2) "expected the infra and budget modules, found $($nested.Count)"
    $infra = @($nested | Where-Object { "$($_.name)" -match 'infra' })
    Assert-True ($infra.Count -eq 1) 'no infrastructure module found'
    Assert-True ("$($infra[0].resourceGroup)" -match 'resourceGroupName') 'the module is not scoped to the new group'
}

Test-Case 'the budget is filtered to this resource group only' {
    $json    = Get-Content $compiled -Raw
    Assert-True ($json -match 'Microsoft\.Consumption/budgets') 'no budget in the template'
    Assert-True ($json -match 'ResourceGroupName') `
        'the budget has no ResourceGroupName filter — it would alert on the unrelated Capture resources'
}

# ── Scenario A: first deployment ───────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Scenario A — first deployment, resource group does not exist' -ForegroundColor White

$a1 = Invoke-Scenario -Script '01-InfraWhatIf.ps1' -WhatIfFixture $fixtures.first -GroupExists $false

Test-Case 'what-if succeeds when the resource group does not exist' {
    Assert-True ($a1.ExitCode -eq 0) "exit $($a1.ExitCode):`n$($a1.Output)"
}

Test-Case 'it previews at subscription scope' {
    Assert-True ([bool](@($a1.Log) -match '^deployment sub what-if')) `
        "no subscription what-if in the invocation log:`n$($a1.Log -join "`n")"
}

Test-Case 'it never issues a resource-group-scoped what-if' {
    Assert-True (-not (@($a1.Log) -match '^deployment group')) `
        'a resource-group-scoped deployment command was issued — this is the original defect'
}

Test-Case 'it says out loud that this is a first deployment' {
    Assert-True ($a1.Output -match 'FIRST deployment') 'the operator is not told which state they are in'
}

$a2 = Invoke-Scenario -Script '02-DeployInfra.ps1' -WhatIfFixture $fixtures.first -GroupExists $false `
                      -ExtraArgs @('-Force', '-BudgetAlertEmail', 'ops@example.org')

Test-Case 'deployment succeeds when the resource group does not exist' {
    Assert-True ($a2.ExitCode -eq 0) "exit $($a2.ExitCode):`n$($a2.Output)"
}

Test-Case 'the group is created by the deployment, not by az group create' {
    Assert-True (-not (@($a2.Log) -match '^group create')) `
        'az group create was called — that mutates before the gate that approves mutations'
    Assert-True ([bool](@($a2.Log) -match '^deployment sub create')) 'no subscription deployment was created'
}

# ── Scenario B: repeat deployment ──────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Scenario B — repeat deployment, resource group already exists' -ForegroundColor White

$b1 = Invoke-Scenario -Script '01-InfraWhatIf.ps1' -WhatIfFixture $fixtures.repeat -GroupExists $true

Test-Case 'what-if succeeds on a benign repeat run' {
    Assert-True ($b1.ExitCode -eq 0) "exit $($b1.ExitCode):`n$($b1.Output)"
}

Test-Case 'the module wrappers are not reported as indeterminate' {
    Assert-True ($b1.Output -notmatch 'Indeterminate changes') `
        "Microsoft.Resources/deployments leaked into the Deploy check:`n$($b1.Output)"
}

Test-Case 'it says out loud that this is a repeat deployment' {
    Assert-True ($b1.Output -match 'REPEAT deployment') 'the operator is not told which state they are in'
}

$b2 = Invoke-Scenario -Script '02-DeployInfra.ps1' -WhatIfFixture $fixtures.repeat -GroupExists $true `
                      -BudgetExists $true -ExtraArgs @('-Force', '-BudgetAlertEmail', 'ops@example.org')

Test-Case 'repeat deployment succeeds' {
    Assert-True ($b2.ExitCode -eq 0) "exit $($b2.ExitCode):`n$($b2.Output)"
}

Test-Case 'an existing budget keeps its original start date' {
    Assert-True ($b2.Output -match 'reusing the existing budget start date 2026-01-01') `
        "Azure rejects a change to an existing budget's startDate:`n$($b2.Output)"
}

# ── Scenario C: the gate still bites ───────────────────────────────────────────────────
Write-Host ''
Write-Host '  Scenario C — destructive modification (the gate must still fail)' -ForegroundColor White

$c1 = Invoke-Scenario -Script '01-InfraWhatIf.ps1' -WhatIfFixture $fixtures.destructive -GroupExists $true

Test-Case 'a destructive modification fails the what-if gate' {
    Assert-True ($c1.ExitCode -ne 0) `
        "exit 0 on an immutable collation change — the safety gate has been weakened:`n$($c1.Output)"
    Assert-True ($c1.Output -match 'POTENTIALLY DESTRUCTIVE') 'the destructive change was not reported'
}

$c2 = Invoke-Scenario -Script '01-InfraWhatIf.ps1' -WhatIfFixture $fixtures.destructive -GroupExists $true `
                      -ExtraArgs @('-AcknowledgeDestructiveModifications')

Test-Case 'and passes only when explicitly acknowledged' {
    Assert-True ($c2.ExitCode -eq 0) "exit $($c2.ExitCode) even with acknowledgement:`n$($c2.Output)"
}

# ── Parameter-file handling ────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Operator parameter file' -ForegroundColor White

Test-Case 'infra/main.dev.bicepparam is read, never rewritten' {
    $source = Join-Path $repoRoot 'infra/main.dev.bicepparam'
    $copy   = Join-Path $workspace 'infra/main.dev.bicepparam'
    $before = (Get-FileHash $source).Hash
    $after  = (Get-FileHash $copy).Hash
    Assert-True ($before -eq $after) `
        'the scripts modified main.dev.bicepparam — an operator local tenant IDs would be lost'
}

Test-Case 'no second parameter file duplicates the tenant object IDs' {
    $duplicates = @(Get-ChildItem (Join-Path $repoRoot 'infra') -Filter '*.bicepparam' |
                    Where-Object { $_.Name -like 'subscription*' } |
                    ForEach-Object { $_.Name })
    # Built before the assertion: under StrictMode, .Name on an empty array throws, and an
    # eagerly-evaluated failure message must not be able to fail before the check does.
    $found = $duplicates -join ', '
    Assert-True ($duplicates.Count -eq 0) "found $found — object IDs must live in exactly one file"
}

# ── Result ─────────────────────────────────────────────────────────────────────────────
if ($KeepWorkspace) {
    Write-Host ''
    Write-Host "  workspace kept: $workspace" -ForegroundColor DarkGray
} else {
    Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host "$passes passed, 0 failed." -ForegroundColor Green
    exit 0
}
Write-Host "$passes passed, $failures FAILED." -ForegroundColor Red
exit $failures
