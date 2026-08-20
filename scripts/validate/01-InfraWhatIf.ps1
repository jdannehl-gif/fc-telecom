<#
.SYNOPSIS
    Run what-if with full property payloads and classify the result. Read-only.

.EXAMPLE
    ./scripts/validate/01-InfraWhatIf.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.NOTES
    CORRECTION TO AN EARLIER VERSION OF THIS SCRIPT.

    An earlier revision failed the run on the `Deploy` change type, on the belief that it meant
    "resource will be replaced". That is wrong. Per the ARM what-if documentation:

      Deploy   The resource exists and is defined in the template. It will be redeployed. The
               properties MAY OR MAY NOT change. Returned only when the operation does not have
               enough information to decide — i.e. only when ResultFormat is ResourceIdOnly.

      Modify   The resource exists and is defined in the template, and properties WILL change.
               Returned when ResultFormat is FullResourcePayloads (the default).

      Delete   Applies ONLY to complete-mode deployments, where a resource exists but is absent
               from the template.

    So `Deploy` is not a danger signal, it is an absence of information — and the fix is to ask
    for the information rather than to fail. This script requests FullResourcePayloads
    explicitly and treats a `Deploy` result as a diagnostic problem to be resolved, not a
    destructive change.

    Two consequences follow. First, because these deployments are INCREMENTAL, `Delete` should
    never appear at all; if it does, something is deploying in complete mode and that is a hard
    stop. Second, the destructive changes that actually can occur here are property-level: a
    property being removed, or an immutable property changing, inside a `Modify`. Those are
    what this script surfaces for review.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,

    # Set once the destructive modifications listed by a previous run have been read and
    # accepted. Deliberately not a switch you would set by habit.
    [switch]$AcknowledgeDestructiveModifications
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

Show-FcContext -Operation 'Infrastructure what-if (read-only)' `
               -Environment $Environment -ResourceGroup $ResourceGroup | Out-Null

$results = New-FcResultsDirectory
$outFile = Join-Path $results "whatif-$Environment.json"

Write-FcHeading 'Running what-if'
Write-FcNote 'ResultFormat = FullResourcePayloads (property-level detail).'

$raw = az deployment group what-if `
    --resource-group $ResourceGroup `
    --template-file infra/main.bicep `
    --parameters "infra/main.$Environment.bicepparam" `
    --result-format FullResourcePayloads `
    --no-pretty-print 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-FcFail 'what-if itself failed:'
    $raw | Write-Host
    exit 1
}

$raw | Out-File -FilePath $outFile -Encoding utf8
Write-FcPass "saved to $outFile"

try { $payload = $raw | ConvertFrom-Json }
catch { Write-FcFail "Could not parse what-if output as JSON. Review $outFile by hand."; exit 1 }

$changes = if ($payload.PSObject.Properties.Name -contains 'changes') { $payload.changes } else { $payload }

# ── Summary ────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Change summary'

$byType = $changes | Group-Object changeType | Sort-Object Name
foreach ($group in $byType) {
    '{0,-10} {1}' -f $group.Name, $group.Count | ForEach-Object { Write-Host "  $_" }
}

$failures = 0

# ── Delete: should be impossible in incremental mode ───────────────────────────────────
$deletes = @($changes | Where-Object changeType -eq 'Delete')
Write-FcHeading 'Resource deletions'
if ($deletes.Count -eq 0) {
    Write-FcPass 'none (expected — these deployments are incremental)'
} else {
    Write-FcFail "$($deletes.Count) resource(s) would be DELETED."
    Write-FcNote 'Resource-level Delete only occurs in COMPLETE mode. If you did not ask for'
    Write-FcNote 'complete mode, something is wrong with how the deployment is being invoked.'
    $deletes | ForEach-Object { Write-Host "    $($_.resourceId)" -ForegroundColor Red }
    $failures++
}

# ── Deploy: missing information, not danger ────────────────────────────────────────────
$deploys = @($changes | Where-Object changeType -eq 'Deploy')
if ($deploys.Count -gt 0) {
    Write-FcHeading 'Indeterminate changes'
    Write-FcWarn "$($deploys.Count) resource(s) returned changeType 'Deploy'."
    Write-FcNote 'That means what-if could not determine property-level changes, which should'
    Write-FcNote 'not happen with FullResourcePayloads. It usually indicates a nested-template'
    Write-FcNote 'expansion limit or a provider that does not support what-if fully.'
    Write-FcNote 'Treat these as UNREVIEWED rather than safe — inspect them by hand.'
    $deploys | ForEach-Object { Write-Host "    $($_.resourceId)" -ForegroundColor Yellow }
    $failures++
}

# ── Modify: where real destruction hides ───────────────────────────────────────────────
#
# Properties that cannot be changed in place. Changing one forces the provider to recreate the
# resource, and on a database or storage account that is data loss. The list is deliberately
# short and specific rather than a guess at everything.
$immutableHints = @(
    'location', 'kind', 'sku.family', 'sku.tier', 'sku.name',
    'properties.administratorLogin', 'properties.collation',
    'properties.accountType', 'properties.isHnsEnabled',
    'properties.createMode', 'properties.elasticPoolId',
    'properties.serverFarmId', 'properties.reserved',
    'properties.enableSoftDelete', 'properties.enablePurgeProtection'
)

function Get-FlattenedDelta {
    param($Delta, [string]$Prefix = '')
    foreach ($entry in @($Delta)) {
        if (-not $entry) { continue }
        $path = if ($Prefix) { "$Prefix.$($entry.path)" } else { $entry.path }
        [pscustomobject]@{
            Path   = $path
            Kind   = $entry.propertyChangeType
            Before = $entry.before
            After  = $entry.after
        }
        if ($entry.PSObject.Properties.Name -contains 'children' -and $entry.children) {
            Get-FlattenedDelta -Delta $entry.children -Prefix $path
        }
    }
}

$modifies = @($changes | Where-Object changeType -eq 'Modify')
Write-FcHeading 'Property modifications'

if ($modifies.Count -eq 0) {
    Write-FcPass 'no resources will be modified'
} else {
    $destructive = @()

    foreach ($change in $modifies) {
        $deltas = @()
        if ($change.PSObject.Properties.Name -contains 'delta' -and $change.delta) {
            $deltas = @(Get-FlattenedDelta -Delta $change.delta)
        }

        # Capture the outer pipeline item before the inner Where-Object, because $_ and
        # $PSItem are the same variable and the nested pipeline rebinds it.
        $risky = $deltas | Where-Object {
            $delta = $_
            $delta.Kind -eq 'Delete' -or
            @($immutableHints | Where-Object { $delta.Path -like "*$_*" }).Count -gt 0
        }

        if ($risky) {
            $destructive += [pscustomobject]@{ ResourceId = $change.resourceId; Deltas = $risky }
        } else {
            Write-FcPass "$($change.resourceId.Split('/')[-1]) — $($deltas.Count) benign property change(s)"
        }
    }

    if ($destructive.Count -gt 0) {
        Write-Host ''
        Write-FcFail "$($destructive.Count) resource(s) have POTENTIALLY DESTRUCTIVE modifications:"
        foreach ($item in $destructive) {
            Write-Host ''
            Write-Host "    $($item.ResourceId)" -ForegroundColor Red
            foreach ($delta in $item.Deltas) {
                Write-Host ("      [{0}] {1}" -f $delta.Kind, $delta.Path) -ForegroundColor Yellow
                Write-Host ("          before: {0}" -f ($delta.Before | ConvertTo-Json -Compress -Depth 3)) -ForegroundColor DarkGray
                Write-Host ("          after : {0}" -f ($delta.After  | ConvertTo-Json -Compress -Depth 3)) -ForegroundColor DarkGray
            }
        }
        Write-Host ''
        Write-FcNote 'A property REMOVAL means the template no longer specifies something the'
        Write-FcNote 'resource currently has. An IMMUTABLE property change forces recreation,'
        Write-FcNote 'which on SQL or Storage is data loss.'
        Write-FcNote ''
        Write-FcNote 'Read each one. If they are intended, take a backup, record the decision,'
        Write-FcNote 'and re-run with -AcknowledgeDestructiveModifications.'

        if ($AcknowledgeDestructiveModifications) {
            Write-Host ''
            Write-FcWarn 'Acknowledged by the operator — not counted as a failure.'
        } else {
            $failures++
        }
    }
}

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($failures -eq 0) {
    Write-Host 'what-if reviewed: no unexplained destructive changes.' -ForegroundColor Green
    Write-Host "Next: ./scripts/validate/02-DeployInfra.ps1 -Environment $Environment -ResourceGroup $ResourceGroup"
    exit 0
}

Write-Host "$failures category/categories need attention before deploying." -ForegroundColor Red
exit 1
