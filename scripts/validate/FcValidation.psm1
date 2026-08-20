<#
.SYNOPSIS
    Shared helpers for the Azure validation scripts.

.DESCRIPTION
    Three things every script in this directory needs:

      1. A context banner. Before anything happens, print the subscription, tenant, resource
         group and — where relevant — the SQL server and database being targeted. Deploying
         dev infrastructure into a production subscription is the expensive kind of mistake,
         and it is almost always a stale `az account set` rather than a bad command.

      2. A confirmation gate on anything that mutates. Read-only scripts run without asking.
         Anything that creates, changes or deletes stops and requires the operator to type the
         resource group name.

      3. Deployment outputs, read from Azure rather than assumed. Nothing here hardcodes a web
         app URL or a SQL FQDN; names contain a uniqueString() suffix that cannot be guessed.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-FcHeading {
    param([Parameter(Mandatory)][string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor White
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkGray
}

function Write-FcPass  { param([string]$Text) Write-Host '  [ ok ] ' -ForegroundColor Green -NoNewline; Write-Host $Text }
function Write-FcFail  { param([string]$Text) Write-Host '  [FAIL] ' -ForegroundColor Red   -NoNewline; Write-Host $Text }
function Write-FcWarn  { param([string]$Text) Write-Host '  [warn] ' -ForegroundColor Yellow -NoNewline; Write-Host $Text }
function Write-FcNote  { param([string]$Text) Write-Host "         $Text" -ForegroundColor DarkGray }

function Get-FcAzContext {
    <#
        The signed-in account, as Azure sees it right now. Fails loudly rather than returning
        null, because every caller treats this as a precondition.
    #>
    $raw = az account show -o json 2>$null
    if (-not $raw) {
        # --use-device-code, not a bare `az login`. The validation host is a headless Ubuntu
        # server: a bare `az login` tries to open a browser, fails or hangs, and the operator
        # is left following an instruction that cannot work. The device-code flow prints a
        # code to complete in a browser on any other machine.
        $hint = if ($IsWindows) { 'az login' } else { 'az login --use-device-code' }
        throw "Not signed in to Azure. Run: $hint"
    }
    return $raw | ConvertFrom-Json
}

function Show-FcContext {
    <#
    .SYNOPSIS
        Print exactly what is about to be operated on. Called by every script, first thing.
    #>
    param(
        [Parameter(Mandatory)][string]$Operation,
        [string]$ResourceGroup,
        [string]$Environment,
        [string]$SqlServer,
        [string]$SqlDatabase,
        [string]$WebUrl,
        [switch]$Mutating
    )

    $ctx = Get-FcAzContext

    Write-Host ''
    Write-Host '==================================================================' -ForegroundColor Cyan
    Write-Host ' FC Telecom — Azure validation' -ForegroundColor Cyan
    Write-Host '==================================================================' -ForegroundColor Cyan
    Write-Host (' Operation      : {0}' -f $Operation)
    if ($Mutating) {
        Write-Host ' Mode           : MUTATING — this will change Azure resources' -ForegroundColor Yellow
    } else {
        Write-Host ' Mode           : read-only'
    }
    Write-Host (' Subscription   : {0}' -f $ctx.name)
    Write-Host (' Subscription ID: {0}' -f $ctx.id)
    Write-Host (' Tenant         : {0}' -f $ctx.tenantId)
    Write-Host (' Signed in as   : {0}' -f $ctx.user.name)
    if ($Environment)  { Write-Host (' Environment    : {0}' -f $Environment) }
    if ($ResourceGroup){ Write-Host (' Resource group : {0}' -f $ResourceGroup) }
    if ($SqlServer)    { Write-Host (' SQL server     : {0}' -f $SqlServer) }
    if ($SqlDatabase)  { Write-Host (' SQL database   : {0}' -f $SqlDatabase) }
    if ($WebUrl)       { Write-Host (' Web app        : {0}' -f $WebUrl) }
    Write-Host '==================================================================' -ForegroundColor Cyan

    return $ctx
}

function Confirm-FcMutation {
    <#
    .SYNOPSIS
        Require the operator to type the resource group name before a mutating operation.

    .DESCRIPTION
        Deliberately not a y/N prompt. A y/N prompt is answered reflexively; typing the
        resource group name requires reading the banner above it, which is the entire point.

        -Force skips the prompt, for use only in a pipeline where the context is already
        pinned by the workflow.
    #>
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$Summary,
        [switch]$Force
    )

    if ($Force) {
        Write-FcWarn "-Force supplied; skipping confirmation."
        return
    }

    Write-Host ''
    Write-Host 'About to: ' -NoNewline; Write-Host $Summary -ForegroundColor Yellow
    Write-Host ''
    $typed = Read-Host "Type the resource group name ($ResourceGroup) to continue, or anything else to abort"

    if ($typed -ne $ResourceGroup) {
        throw "Aborted — '$typed' does not match '$ResourceGroup'. Nothing was changed."
    }
}

function Get-FcDeploymentOutputs {
    <#
    .SYNOPSIS
        Read the real deployment outputs from Azure. Never guess a resource name.

    .DESCRIPTION
        Resource names in infra/main.bicep carry a uniqueString() suffix, so they cannot be
        derived from the environment name. Everything downstream — the web URL, the SQL FQDN,
        the Key Vault URI — comes from here.

        Falls back to the most recent successful deployment in the group when no name is
        supplied, which is what you want during an interactive validation pass.
    #>
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [string]$DeploymentName
    )

    if (-not $DeploymentName) {
        $DeploymentName = az deployment group list `
            --resource-group $ResourceGroup `
            --query "sort_by([?properties.provisioningState=='Succeeded'], &properties.timestamp)[-1].name" `
            -o tsv 2>$null
    }

    if (-not $DeploymentName) {
        throw "No successful deployment found in '$ResourceGroup'. Run 02-DeployInfra.ps1 first."
    }

    $raw = az deployment group show `
        --resource-group $ResourceGroup `
        --name $DeploymentName `
        --query properties.outputs -o json 2>$null

    if (-not $raw) {
        throw "Could not read outputs from deployment '$DeploymentName' in '$ResourceGroup'."
    }

    $outputs = $raw | ConvertFrom-Json
    $result = [ordered]@{ DeploymentName = $DeploymentName }

    foreach ($property in $outputs.PSObject.Properties) {
        $result[$property.Name] = $property.Value.value
    }

    # Derived, not assumed: the host name comes from the deployment output.
    if ($result.Contains('webAppHostName')) {
        $result['webUrl'] = "https://$($result['webAppHostName'])"
    }

    return [pscustomobject]$result
}

function New-FcResultsDirectory {
    param([string]$Path = 'artifacts/validation')
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    return (Resolve-Path $Path).Path
}

Export-ModuleMember -Function `
    Write-FcHeading, Write-FcPass, Write-FcFail, Write-FcWarn, Write-FcNote, `
    Get-FcAzContext, Show-FcContext, Confirm-FcMutation, Get-FcDeploymentOutputs, `
    New-FcResultsDirectory
