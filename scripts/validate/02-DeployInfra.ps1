<#
.SYNOPSIS
    Create the resource group and budget if needed, deploy the infrastructure, capture outputs.

.EXAMPLE
    ./scripts/validate/02-DeployInfra.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev `
        -Location eastus2 -MonthlyBudgetUsd 150 -BudgetAlertEmail you@example.org

.NOTES
    MUTATING. Requires confirmation. Every resource name used downstream comes from this
    deployment's outputs — nothing is assumed or reconstructed from a naming convention,
    because infra/main.bicep uses uniqueString() suffixes that cannot be derived.
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

Show-FcContext -Operation 'Deploy infrastructure' -Environment $Environment `
               -ResourceGroup $ResourceGroup -Mutating | Out-Null

$exists = az group exists --name $ResourceGroup | ConvertFrom-Json

$summary = if ($exists) {
    "deploy infra/main.bicep into the EXISTING resource group '$ResourceGroup'"
} else {
    "CREATE resource group '$ResourceGroup' in $Location, then deploy infra/main.bicep into it"
}

Confirm-FcMutation -ResourceGroup $ResourceGroup -Summary $summary -Force:$Force

# ── Resource group ─────────────────────────────────────────────────────────────────────
Write-FcHeading 'Resource group'

if ($exists) {
    Write-FcPass "$ResourceGroup already exists"
} else {
    az group create --name $ResourceGroup --location $Location `
        --tags "application=fc-telecom" "environment=$Environment" "managed-by=validation-runbook" `
        -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create resource group." }
    Write-FcPass "created $ResourceGroup in $Location"
    Write-FcNote 'Tagged so 99-Cleanup.ps1 can identify what it is safe to remove.'
}

# ── Budget ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Cost alerting'

Write-FcWarn 'An Azure budget is an ALERT, not a cap.'
Write-FcNote 'Reaching the amount sends email. It does not stop billing, throttle anything,'
Write-FcNote 'or deallocate resources. Spend continues until someone acts on the alert.'
Write-FcNote 'The only hard control is deleting the resource group — 99-Cleanup.ps1.'

if (-not $BudgetAlertEmail) {
    Write-FcWarn 'No -BudgetAlertEmail supplied; skipping budget creation.'
    Write-FcNote 'Set one anyway. A dev App Service plan, SQL database and Log Analytics'
    Write-FcNote 'workspace left running costs real money quietly, and an alert nobody'
    Write-FcNote 'configured is the reason people discover that at month end.'
} else {
    $budgetName = "budget-$ResourceGroup"
    $startDate  = (Get-Date -Day 1).ToString('yyyy-MM-01')
    $endDate    = (Get-Date -Day 1).AddYears(2).ToString('yyyy-MM-01')

    $existingBudget = az consumption budget list --query "[?name=='$budgetName'] | length(@)" -o tsv 2>$null

    if ($existingBudget -and [int]$existingBudget -gt 0) {
        Write-FcPass "budget '$budgetName' already exists"
    } else {
        # az consumption budget create-with-rg is preview and its surface has moved around;
        # if it fails, say so plainly rather than pretending a cost guard exists.
        az consumption budget create-with-rg `
            --budget-name $budgetName `
            --resource-group $ResourceGroup `
            --amount $MonthlyBudgetUsd `
            --category Cost `
            --time-grain Monthly `
            --start-date $startDate `
            --end-date $endDate `
            --contact-emails $BudgetAlertEmail `
            -o none 2>$null

        if ($LASTEXITCODE -eq 0) {
            Write-FcPass "budget '$budgetName' created: `$$MonthlyBudgetUsd/month, alerts to $BudgetAlertEmail"
        } else {
            Write-FcWarn 'Budget creation failed (the consumption CLI extension is preview).'
            Write-FcNote 'Create it in the portal: Cost Management > Budgets > Add. Do not skip this.'
        }
    }
}

# ── Deploy ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Deployment'

$deploymentName = "fctelecom-$Environment-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-FcNote "name: $deploymentName"

az deployment group create `
    --resource-group $ResourceGroup `
    --name $deploymentName `
    --template-file infra/main.bicep `
    --parameters "infra/main.$Environment.bicepparam" `
    -o none

if ($LASTEXITCODE -ne 0) { throw "Deployment failed. Check: az deployment group show -g $ResourceGroup -n $deploymentName" }
Write-FcPass 'deployment succeeded'

# ── Outputs ────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Deployment outputs'

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $deploymentName

foreach ($property in $outputs.PSObject.Properties) {
    Write-Host ('  {0,-22} {1}' -f $property.Name, $property.Value)
}

$results = New-FcResultsDirectory
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
