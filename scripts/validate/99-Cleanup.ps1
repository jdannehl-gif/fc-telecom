<#
.SYNOPSIS
    Tear down a development validation environment.

.EXAMPLE
    ./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -WhatIf
    ./scripts/validate/99-Cleanup.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.NOTES
    MUTATING and irreversible. Refuses to run against prod, refuses to run against a resource
    group it did not create (checked by tag), and requires the resource group name to be typed.

    Run -WhatIf first. Always.

    What this does NOT delete, deliberately:
      - The Entra app registration and groups. Those are tenant objects, they cost nothing, and
        recreating them is the slowest part of the setup. docs/runbooks/entra-setup-dev.md
        covers removing them if you genuinely want to.
      - Soft-deleted Key Vaults. Purge protection is a feature; see the note at the end.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('dev')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

Show-FcContext -Operation 'DELETE the validation environment' `
               -Environment $Environment -ResourceGroup $ResourceGroup -Mutating | Out-Null

if (-not (az group exists --name $ResourceGroup | ConvertFrom-Json)) {
    Write-FcPass "$ResourceGroup does not exist — nothing to do"
    exit 0
}

# ── Guard: is this ours, and is it dev? ────────────────────────────────────────────────
Write-FcHeading 'Safety checks'

$tags = az group show --name $ResourceGroup --query tags -o json | ConvertFrom-Json

$envTag = if ($tags -and $tags.PSObject.Properties.Name -contains 'environment') { $tags.environment } else { $null }
$appTag = if ($tags -and $tags.PSObject.Properties.Name -contains 'application') { $tags.application } else { $null }

if ($appTag -ne 'fc-telecom') {
    throw "Refusing to delete: '$ResourceGroup' is not tagged application=fc-telecom (found '$appTag'). If this group was created by hand, delete it by hand."
}
Write-FcPass "tagged application=fc-telecom"

if ($envTag -ne 'dev') {
    throw "Refusing to delete: '$ResourceGroup' is tagged environment='$envTag', not 'dev'."
}
Write-FcPass "tagged environment=dev"

# ── Inventory ──────────────────────────────────────────────────────────────────────────
Write-FcHeading 'What will be deleted'

$resources = az resource list -g $ResourceGroup --query "[].{name:name, type:type}" -o json | ConvertFrom-Json
if (-not $resources) {
    Write-FcNote 'the group is empty'
} else {
    foreach ($resource in $resources | Sort-Object type) {
        Write-Host ('  {0,-52} {1}' -f $resource.type, $resource.name)
    }
    Write-Host ''
    Write-FcWarn "$(@($resources).Count) resource(s), including the SQL database and all its data."
}

if ($WhatIfPreference) {
    Write-Host ''
    Write-Host '-WhatIf: nothing was deleted.' -ForegroundColor Cyan
    exit 0
}

Confirm-FcMutation -ResourceGroup $ResourceGroup `
    -Summary "PERMANENTLY DELETE resource group '$ResourceGroup' and everything in it" `
    -Force:$Force

# ── Delete ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Deleting'

if ($PSCmdlet.ShouldProcess($ResourceGroup, 'Delete resource group')) {
    az group delete --name $ResourceGroup --yes --no-wait
    if ($LASTEXITCODE -ne 0) { throw "Delete failed." }
    Write-FcPass "deletion started (running in the background)"
    Write-FcNote "watch it: az group show -n $ResourceGroup --query properties.provisioningState -o tsv"
}

Write-Host @"

Left behind on purpose:

  Key Vault      Deleted vaults are retained under soft-delete for the configured period, and
                 the name stays reserved. That is a feature — it is what stops an accidental
                 delete from destroying every secret. To reuse the name immediately:
                   az keyvault purge --name <vault> --location <region>
                 Think before purging. Purge is the irreversible one.

  Entra objects  The app registration, the FCTelecom-* groups and the test accounts are tenant
                 objects. They cost nothing and recreating them is the slowest part of setup,
                 so cleanup leaves them. docs/runbooks/entra-setup-dev.md has removal steps if
                 you want them gone.

  Budget         Deleted with the resource group.

  Local files    artifacts/validation/ still holds the what-if output, the generated migration
                 and the deployment outputs. Keep them with the completed results table — they
                 are the evidence that the pass was actually run.
"@
