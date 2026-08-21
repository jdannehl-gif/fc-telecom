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

    SECOND CORRECTION — SUBSCRIPTION SCOPE.

    The first real validation run against Ubuntu 26.04 failed here with:

      ResourceGroupNotFound: Resource group 'rg-fctelecom-dev' could not be found

    This script previewed a RESOURCE-GROUP-scoped deployment into a group that the next step,
    02-DeployInfra.ps1, was going to create. On a first deployment the group does not exist, so
    the gate could not pass — on precisely the run where a preview matters most, because
    nothing has ever been reviewed before.

    (The `The content for this response was already consumed` traceback that followed is Azure
    CLI 2.89.1 mishandling its own error response. It is noise on top of the real failure, not
    a second problem.)

    The fix is not to create the group first so the preview has something to point at. That
    would move a mutation ahead of the gate that exists to approve mutations. It is to preview
    at SUBSCRIPTION scope, against infra/subscription.bicep, so that creating the resource
    group is one of the changes shown.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$Location = 'eastus2',

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

# ── First run or repeat run ────────────────────────────────────────────────────────────
#
# Stated up front, because it changes what the output below should look like and therefore
# what "clean" means. A first run is all Create and no Modify; anything else is worth a
# second look before it is approved.
Write-FcHeading 'Target state'

$groupExists = [bool](az group exists --name $ResourceGroup 2>$null | ConvertFrom-Json)
if ($groupExists) {
    Write-FcPass "$ResourceGroup exists — this is a REPEAT deployment"
    Write-FcNote 'Expect Ignore/NoChange for most resources, and read every Modify.'
} else {
    Write-FcPass "$ResourceGroup does not exist — this is a FIRST deployment"
    Write-FcNote 'The preview below INCLUDES creating the resource group. Expect all Create,'
    Write-FcNote 'no Modify and no Delete.'
}

# ── Compile ────────────────────────────────────────────────────────────────────────────
#
# On this host, to JSON. `az` is then handed a plain ARM template and never needs a Bicep
# binary of its own — which under the containerised CLI it does not reliably have. See
# Build-FcTemplate in FcValidation.psm1.
Write-FcHeading 'Template'

$template   = Build-FcTemplate -BicepFile 'infra/subscription.bicep' `
                              -OutFile (Join-Path $results "subscription-$Environment.json")

# The operator's own infra/main.<env>.bicepparam is the source of the tenant-specific values
# and is READ, never rewritten. See Read-FcBicepParam.
$paramFile  = "infra/main.$Environment.bicepparam"
$fromBicep  = Read-FcBicepParam -Path $paramFile
Write-FcPass "read $paramFile ($($fromBicep.Count) parameter(s))"

$budgetName   = "budget-$ResourceGroup"
$budgetWindow = Get-FcBudgetWindow -ResourceGroup $ResourceGroup -BudgetName $budgetName

# Precomputed rather than inlined: an `if` is a statement, and a statement inside a hashtable
# literal does not parse.
$effectiveLocation = $Location
if ($fromBicep.Contains('location') -and $fromBicep['location']) { $effectiveLocation = $fromBicep['location'] }

$values = @{
    environmentName            = $fromBicep['environmentName']
    location                   = $effectiveLocation
    resourceGroupName          = $ResourceGroup
    keyVaultAdminGroupObjectId = $fromBicep['keyVaultAdminGroupObjectId']
    sqlAdminGroupObjectId      = $fromBicep['sqlAdminGroupObjectId']
    sqlAdminGroupName          = $fromBicep['sqlAdminGroupName']
    budgetStartDate            = $budgetWindow.StartDate
    budgetEndDate              = $budgetWindow.EndDate
    budgetAlertEmails          = @()
}

$parameters = New-FcParameterFile -Values $values -OutFile (Join-Path $results "parameters-$Environment.json")
Write-FcPass "wrote $parameters"
Write-FcNote 'budgetAlertEmails is empty here: a preview does not need recipients, and'
Write-FcNote '02-DeployInfra.ps1 supplies them from -BudgetAlertEmail.'

# ── What-if ────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Running what-if'
Write-FcNote 'Scope = subscription. ResultFormat = FullResourcePayloads (property-level detail).'

$raw = az deployment sub what-if `
    --location $Location `
    --template-file $template `
    --parameters "@$parameters" `
    --result-format FullResourcePayloads `
    --no-pretty-print 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-FcFail 'what-if itself failed:'
    $raw | Write-Host
    if (($raw -join ' ') -match 'ResourceGroupNotFound') {
        Write-FcNote ''
        Write-FcNote 'ResourceGroupNotFound at SUBSCRIPTION scope should not happen — creating the'
        Write-FcNote 'group is part of this template. If you see it, something is still invoking a'
        Write-FcNote 'resource-group-scoped what-if. Check that $template above is subscription.bicep.'
    }
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
#
# At subscription scope the template contains two Microsoft.Resources/deployments resources —
# the nested modules for the infrastructure and the budget. what-if reports those as `Deploy`
# because a nested deployment has no properties of its own to diff, not because anything about
# them is indeterminate. Flagging the module wrappers would fail every single run and train
# people to pass -AcknowledgeDestructiveModifications by habit, which is how a real finding
# gets waved through. The resources INSIDE them are expanded and reported separately, and
# those are what the checks below read.
$deploys = @($changes |
    Where-Object changeType -eq 'Deploy' |
    Where-Object { $_.resourceId -notmatch '/providers/Microsoft\.Resources/deployments/' })

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

        # Every field read through PSObject.Properties, not directly.
        #
        # Under `Set-StrictMode -Version Latest`, reading a property that is absent THROWS. ARM
        # omits `before`/`after` on any delta node that has `children` — which is every nested
        # property change, the common case — so `$entry.before` terminated the script partway
        # through the destructive-change review. A safety gate that crashes is worse than one
        # that reports nothing, because the crash arrives after "Property modifications" has
        # already printed and reads like the section finished.
        $names  = $entry.PSObject.Properties.Name
        $path   = if ($Prefix) { "$Prefix.$($entry.path)" } else { $entry.path }
        $kind   = if ($names -contains 'propertyChangeType') { $entry.propertyChangeType } else { 'Unknown' }
        $before = if ($names -contains 'before') { $entry.before } else { $null }
        $after  = if ($names -contains 'after')  { $entry.after }  else { $null }

        [pscustomobject]@{
            Path   = $path
            Kind   = $kind
            Before = $before
            After  = $after
        }
        if ($names -contains 'children' -and $entry.children) {
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
    if (-not $groupExists) {
        Write-Host "The preview above includes CREATING $ResourceGroup in $Location." -ForegroundColor Cyan
    }
    Write-Host "Next: ./scripts/validate/02-DeployInfra.ps1 -Environment $Environment -ResourceGroup $ResourceGroup"
    exit 0
}

Write-Host "$failures category/categories need attention before deploying." -ForegroundColor Red
exit 1
