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
        [string]$ResourceGroup,
        [string]$DeploymentName,

        # Read a SUBSCRIPTION-scope deployment instead of a resource-group one. Used by
        # 02-DeployInfra.ps1, which now deploys infra/subscription.bicep so that creating the
        # resource group is part of the previewed change rather than a precondition.
        #
        # The other scripts keep the resource-group form and keep working: the subscription
        # deployment expands into a nested deployment INSIDE the group, and that nested
        # deployment carries main.bicep's outputs. Both paths therefore see the same values.
        [switch]$SubscriptionScope
    )

    if ($SubscriptionScope) {
        if (-not $DeploymentName) {
            $DeploymentName = az deployment sub list `
                --query "sort_by([?properties.provisioningState=='Succeeded'], &properties.timestamp)[-1].name" `
                -o tsv 2>$null
        }
        if (-not $DeploymentName) {
            throw "No successful subscription deployment found. Run 02-DeployInfra.ps1 first."
        }

        $raw = az deployment sub show --name $DeploymentName --query properties.outputs -o json 2>$null
        if (-not $raw) { throw "Could not read outputs from subscription deployment '$DeploymentName'." }
    }
    else {
        if (-not $ResourceGroup) { throw 'Get-FcDeploymentOutputs needs -ResourceGroup or -SubscriptionScope.' }

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

function Build-FcTemplate {
    <#
    .SYNOPSIS
        Compile a .bicep file to ARM JSON ON THIS HOST, and return the path to the JSON.

    .DESCRIPTION
        Every deployment command in this directory passes a compiled .json template, never a
        .bicep file. That is not a style preference — it removes a whole class of failure.

        Under the containerised Azure CLI strategy on Ubuntu 26.04, `az` runs inside
        mcr.microsoft.com/azure-cli. Handing it a .bicep file makes the CLI want a Bicep
        binary INSIDE that container, where three things are true at once: the host's
        /usr/local/bin/bicep is not visible, `az config` written during bootstrap by root
        landed in /root/.azure rather than the operator's home, and anything `az bicep
        install` downloads goes into a layer that is discarded when the container exits. The
        symptom was a warning nobody could act on:

            WARNING: The configuration value of bicep.use_binary_from_path has been set to 'false'.

        Compiling here means `az` receives JSON and never needs Bicep at all. It also makes
        the artefact reviewable: the exact template that was deployed is written to
        artifacts/validation/ next to the what-if output.

        The standalone binary is preferred and takes its path POSITIONALLY. `--file` is Azure
        CLI syntax; passing it to the standalone binary produces "Unrecognized parameter".
    #>
    param(
        [Parameter(Mandatory)][string]$BicepFile,
        [Parameter(Mandatory)][string]$OutFile
    )

    if (-not (Test-Path $BicepFile)) { throw "Template not found: $BicepFile" }

    $parent = Split-Path $OutFile -Parent
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    if (Get-Command bicep -ErrorAction SilentlyContinue) {
        $log  = & bicep build $BicepFile --outfile $OutFile 2>&1
        $code = $LASTEXITCODE
        $via  = 'bicep (standalone)'
    } else {
        $log  = & az bicep build --file $BicepFile --outfile $OutFile 2>&1
        $code = $LASTEXITCODE
        $via  = 'az bicep'
    }

    if ($code -ne 0 -or -not (Test-Path $OutFile)) {
        Write-FcFail "could not compile $BicepFile (via $via)"
        foreach ($line in @(@($log) | Select-Object -First 20)) { Write-FcNote ($line -replace '\s+$', '') }
        throw "Bicep compilation failed for $BicepFile"
    }

    Write-FcPass "compiled $BicepFile -> $OutFile (via $via)"
    foreach ($line in @(@($log) | Where-Object { $_ -match 'Warning' } | Select-Object -First 10)) {
        Write-FcNote ($line -replace '\s+$', '')
    }

    return (Resolve-Path $OutFile).Path
}

function Read-FcBicepParam {
    <#
    .SYNOPSIS
        Read infra/main.<env>.bicepparam into a hashtable.

    .DESCRIPTION
        THE POINT OF THIS FUNCTION IS THAT THE OPERATOR'S PARAMETER FILE IS NEVER REWRITTEN.

        infra/main.dev.bicepparam holds tenant-specific values — the Entra group object IDs
        someone filled in by hand on the validation host. It ships with placeholder all-zero
        GUIDs and is edited in place, so a local copy carries real identifiers that are not in
        source control and must not be clobbered by an update to this repository.

        So the subscription-scope entry point does NOT get a second parameter file duplicating
        those values. It is fed from this one, read here and passed through as a generated
        parameters JSON. main.<env>.bicepparam stays the single file anyone edits, and stays
        usable on its own for a resource-group-scoped deployment.

        Only `param name = <literal>` lines are read. Anything else — expressions, references,
        the `using` line — is ignored deliberately: this is a reader for a file of constants,
        not a Bicep interpreter, and it should fail to understand rather than guess.
    #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path)) { throw "Parameter file not found: $Path" }

    $values = [ordered]@{}
    foreach ($line in Get-Content $Path) {
        if ($line -match "^\s*param\s+(\w+)\s*=\s*'([^']*)'\s*$")   { $values[$Matches[1]] = $Matches[2]; continue }
        if ($line -match '^\s*param\s+(\w+)\s*=\s*(-?\d+)\s*$')     { $values[$Matches[1]] = [int]$Matches[2]; continue }
        if ($line -match '^\s*param\s+(\w+)\s*=\s*(true|false)\s*$'){ $values[$Matches[1]] = [bool]::Parse($Matches[2]); continue }
    }

    if ($values.Count -eq 0) { throw "No literal parameters found in $Path." }
    return $values
}

function New-FcParameterFile {
    <#
    .SYNOPSIS
        Write an ARM parameters JSON file and return its path.

    .DESCRIPTION
        A file rather than a string of `--parameters key=value` pairs, for two reasons. Arrays
        (budgetAlertEmails) have no reliable key=value spelling across shells, and the Azure
        CLI refuses more than one --parameters argument when a .bicepparam file is involved,
        which rules out overlaying overrides on one. A generated file has neither problem and
        is the exact input the deployment received, saved next to the what-if output.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Values,
        [Parameter(Mandatory)][string]$OutFile
    )

    $parameters = [ordered]@{}
    foreach ($key in $Values.Keys) { $parameters[$key] = @{ value = $Values[$key] } }

    $document = [ordered]@{
        '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters     = $parameters
    }

    $parent = Split-Path $OutFile -Parent
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    $document | ConvertTo-Json -Depth 6 | Out-File -FilePath $OutFile -Encoding utf8
    return (Resolve-Path $OutFile).Path
}

function Get-FcBudgetWindow {
    <#
    .SYNOPSIS
        Decide the budget's start and end dates, reading back an existing budget's start.

    .DESCRIPTION
        Azure REJECTS a change to an existing budget's startDate. Recomputing "first of the
        current month" every run therefore works in the month the budget was created and fails
        in every month after it — a defect that hides for up to thirty days and then makes
        every deployment fail for a reason unrelated to what changed.

        So: if the budget already exists, reuse its startDate verbatim. If it does not, use the
        first of the current month. This is one of the two places where a first run and a
        repeat run genuinely differ, and it is covered by Test-DeploymentSequencing.ps1.
    #>
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$BudgetName,
        [datetime]$Now = (Get-Date)
    )

    $start = $null

    $existing = az consumption budget show --budget-name $BudgetName --resource-group $ResourceGroup -o json 2>$null
    if ($existing) {
        try {
            $parsed = $existing | ConvertFrom-Json
            if ($parsed.timePeriod -and $parsed.timePeriod.startDate) {
                $start = ([datetime]$parsed.timePeriod.startDate).ToString('yyyy-MM-01')
            }
        } catch { $start = $null }
    }

    $reused = [bool]$start
    if (-not $start) { $start = $Now.ToString('yyyy-MM-01') }

    return [pscustomobject]@{
        StartDate = $start
        EndDate   = ([datetime]$start).AddYears(2).ToString('yyyy-MM-01')
        Reused    = $reused
    }
}

function New-FcResultsDirectory {
    param([string]$Path = 'artifacts/validation')
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    return (Resolve-Path $Path).Path
}

Export-ModuleMember -Function `
    Write-FcHeading, Write-FcPass, Write-FcFail, Write-FcWarn, Write-FcNote, `
    Get-FcAzContext, Show-FcContext, Confirm-FcMutation, Get-FcDeploymentOutputs, `
    New-FcResultsDirectory, `
    Build-FcTemplate, Read-FcBicepParam, New-FcParameterFile, Get-FcBudgetWindow
