<#
.SYNOPSIS
    Deploy the infrastructure at subscription scope — resource group, budget and all — and
    capture the outputs.

.EXAMPLE
    ./scripts/validate/02-DeployInfra.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev `
        -Location eastus2 -MonthlyBudgetUsd 150 -BudgetAlertEmail you@example.org

.NOTES
    MUTATING. Requires the resource group name to be typed.

    THE RESOURCE GROUP IS CREATED BY THE TEMPLATE, NOT BY THIS SCRIPT.

    It used to be created here with `az group create`, before the deployment. That is what
    made the what-if in step 3 impossible to run on a first deployment: the preview pointed at
    a group that did not exist yet, and Azure answered ResourceGroupNotFound. Creating the
    group earlier would have "fixed" it by performing a mutation before the gate that exists
    to approve mutations.

    Now both the preview and the deployment run against infra/subscription.bicep at
    subscription scope, so creating the group is part of the reviewed change. The two commands
    differ only in the verb.

    Every resource name used downstream comes from this deployment's outputs — nothing is
    assumed or reconstructed from a naming convention, because infra/main.bicep uses
    uniqueString() suffixes that cannot be derived.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$Location = 'eastus2',

    # An Azure budget is an ALERT THRESHOLD, not a spending cap. Azure does not stop billing
    # or deallocate anything when the amount is reached — it emails whoever is listed. Nothing
    # in this script can prevent spend; it can only make sure someone finds out.
    [int]$MonthlyBudgetUsd = 150,
    [string]$BudgetAlertEmail,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

Show-FcContext -Operation 'Deploy infrastructure (subscription scope)' -Environment $Environment `
               -ResourceGroup $ResourceGroup -Mutating | Out-Null

$results = New-FcResultsDirectory

# ── First run or repeat run ────────────────────────────────────────────────────────────
Write-FcHeading 'Target state'

$groupExists = [bool](az group exists --name $ResourceGroup 2>$null | ConvertFrom-Json)

if ($groupExists) {
    Write-FcPass "$ResourceGroup exists — REPEAT deployment"
    $summary = "deploy infra/subscription.bicep into the EXISTING resource group '$ResourceGroup'"
} else {
    Write-FcPass "$ResourceGroup does not exist — FIRST deployment"
    Write-FcNote "The deployment will create it in $Location, tagged application=fc-telecom and"
    Write-FcNote "environment=$Environment."
    $summary = "CREATE resource group '$ResourceGroup' in $Location and deploy infra/subscription.bicep into it"
}

Confirm-FcMutation -ResourceGroup $ResourceGroup -Summary $summary -Force:$Force

# ── Template and parameters ────────────────────────────────────────────────────────────
#
# Compiled on this host. `az` receives ARM JSON and never needs a Bicep binary of its own,
# which under the containerised Azure CLI on Ubuntu 26.04 it does not reliably have.
Write-FcHeading 'Template'

$template  = Build-FcTemplate -BicepFile 'infra/subscription.bicep' `
                             -OutFile (Join-Path $results "subscription-$Environment.json")

# infra/main.<env>.bicepparam is READ, never rewritten. It holds the Entra group object IDs
# someone filled in on this host, and it stays the only file anyone edits.
$paramFile = "infra/main.$Environment.bicepparam"
$fromBicep = Read-FcBicepParam -Path $paramFile
Write-FcPass "read $paramFile ($($fromBicep.Count) parameter(s))"

$effectiveLocation = $Location
if ($fromBicep.Contains('location') -and $fromBicep['location']) { $effectiveLocation = $fromBicep['location'] }

# ── Budget ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Cost alerting'

Write-FcWarn 'An Azure budget is an ALERT, not a cap.'
Write-FcNote 'Reaching the amount sends email. It does not stop billing, throttle anything,'
Write-FcNote 'or deallocate resources. Spend continues until someone acts on the alert.'
Write-FcNote 'The only hard control is deleting the resource group — 99-Cleanup.ps1.'

$budgetName = "budget-$ResourceGroup"
$emails     = @()

if ($BudgetAlertEmail) {
    $emails = @($BudgetAlertEmail)
    Write-FcPass "budget '$budgetName': `$$MonthlyBudgetUsd/month, alerts to $BudgetAlertEmail"
    Write-FcNote 'Scoped to this resource group only — the subscription contains unrelated'
    Write-FcNote 'resources, and an alert that fires on someone else spend gets ignored.'
} else {
    Write-FcWarn 'No -BudgetAlertEmail supplied; the budget resource is skipped.'
    Write-FcNote 'Set one anyway. A dev App Service plan, SQL database and Log Analytics'
    Write-FcNote 'workspace left running costs real money quietly, and an alert nobody'
    Write-FcNote 'configured is the reason people discover that at month end.'
}

# Azure rejects a change to an existing budget's startDate, so an existing one is read back
# rather than recomputed. Without this a deployment succeeds in the month the budget was
# created and fails in every month after it.
$budgetWindow = Get-FcBudgetWindow -ResourceGroup $ResourceGroup -BudgetName $budgetName
if ($budgetWindow.Reused) {
    Write-FcPass "reusing the existing budget start date $($budgetWindow.StartDate)"
    Write-FcNote 'Azure rejects a change to startDate on an existing budget.'
} else {
    Write-FcNote "budget window $($budgetWindow.StartDate) to $($budgetWindow.EndDate)"
}

$values = @{
    environmentName            = $fromBicep['environmentName']
    location                   = $effectiveLocation
    resourceGroupName          = $ResourceGroup
    keyVaultAdminGroupObjectId = $fromBicep['keyVaultAdminGroupObjectId']
    sqlAdminGroupObjectId      = $fromBicep['sqlAdminGroupObjectId']
    sqlAdminGroupName          = $fromBicep['sqlAdminGroupName']
    monthlyBudgetUsd           = $MonthlyBudgetUsd
    budgetAlertEmails          = $emails
    budgetStartDate            = $budgetWindow.StartDate
    budgetEndDate              = $budgetWindow.EndDate
}

$parameters = New-FcParameterFile -Values $values -OutFile (Join-Path $results "parameters-$Environment.json")

# ── Deploy ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Deployment'

$deploymentName = "fctelecom-$Environment-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-FcNote "name: $deploymentName (subscription scope, location $Location)"

az deployment sub create `
    --location $Location `
    --name $deploymentName `
    --template-file $template `
    --parameters "@$parameters" `
    -o none

if ($LASTEXITCODE -ne 0) { throw "Deployment failed. Check: az deployment sub show -n $deploymentName" }
Write-FcPass 'deployment succeeded'

if (-not $groupExists) {
    Write-FcPass "$ResourceGroup created by the deployment"
}

# ── Outputs ────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Deployment outputs'

$outputs = Get-FcDeploymentOutputs -SubscriptionScope -DeploymentName $deploymentName

foreach ($property in $outputs.PSObject.Properties) {
    Write-Host ('  {0,-22} {1}' -f $property.Name, $property.Value)
}

$outputFile = Join-Path $results "outputs-$Environment.json"
$outputs | ConvertTo-Json -Depth 5 | Out-File -FilePath $outputFile -Encoding utf8

Write-Host ''
Write-FcPass "outputs saved to $outputFile"
Write-FcNote 'Later scripts read these rather than assuming resource names.'

Write-Host ''
Write-Host 'Next steps, in order:' -ForegroundColor Cyan
Write-Host '  Validation step 4  docs/runbooks/entra-setup-dev.md B - redirect URLs, client secret'
Write-Host '  Validation step 5  ./scripts/validate/03-SetEncryptionKeys.ps1'
Write-Host '                     REQUIRED before the application can start at all'
Write-Host '  Validation step 6  ./scripts/validate/04-ReviewMigration.ps1, then apply the'
Write-Host '                     migration as the migration identity, then'
Write-Host '                     05-GrantDatabasePrincipals.sql'
Write-Host '  Validation step 7  ./scripts/validate/06-DeployApp.ps1'
Write-Host ''
Write-Host 'docs/runbooks/azure-validation.md is authoritative for order.' -ForegroundColor Cyan
