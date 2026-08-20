<#
.SYNOPSIS
    Create the field-encryption keys and wire them to App Service. MUST run before the
    application starts for the first time.

.EXAMPLE
    ./scripts/validate/03-SetEncryptionKeys.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.DESCRIPTION
    WHY THIS IS STEP 3 AND NOT STEP 9.

    An earlier revision of this pass created the keys after smoke and authorization testing.
    That ordering cannot work, and the failure it produces is deeply unhelpful. Tracing it
    through the code:

      Program.cs  resolves DemoDataSeeder from DI at startup, to call SeedReferenceDataAsync
                  (roles and their permissions). This is unconditional — it happens on every
                  start, with or without SeedDemoData.

      DemoDataSeeder  takes IFieldEncryptor as a constructor dependency.

      IFieldEncryptor  is registered AddSingleton<IFieldEncryptor, FieldEncryptor>().

      FieldEncryptor's constructor  calls DecodeKey on both settings and throws
                  InvalidOperationException if either is missing, is not valid base64, is not
                  256-bit, or if the two are identical.

    So resolving the seeder constructs the encryptor, and a missing key throws before the
    application ever serves a request. On Linux App Service that presents as a container that
    exits during startup — which looks like a platform problem, a port-binding problem, or a
    bad connection string long before it looks like a missing configuration value.

    Both keys must therefore exist, be valid, be distinct, and be readable by the App Service
    managed identity BEFORE the first start.

    Keys created here are DEVELOPMENT keys. Anything encrypted under them is disposable. They
    must never be reused for production, which is why this refuses to run against prod.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName,

    # Regenerate even if keys already exist. Destroys the ability to read anything already
    # encrypted, so it is off by default.
    [switch]$Rotate,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

if ($Environment -eq 'prod') {
    throw "Refusing to generate keys for prod. Production keys are created and rotated through docs/runbooks/rotate-secrets.md, not by a validation script."
}

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName

Show-FcContext -Operation 'Create field-encryption keys (required before first app start)' `
               -Environment $Environment -ResourceGroup $ResourceGroup `
               -WebUrl $outputs.webUrl -Mutating | Out-Null

$vaultName  = $outputs.keyVaultName
$webAppName = $outputs.webAppName
$encSecret  = 'Security--FieldEncryption--EncryptionKeyBase64'
$hashSecret = 'Security--FieldEncryption--SearchHashKeyBase64'

# ── Does anything already exist? ───────────────────────────────────────────────────────
Write-FcHeading 'Existing keys'

$existingEnc  = az keyvault secret show --vault-name $vaultName --name $encSecret  --query value -o tsv 2>$null
$existingHash = az keyvault secret show --vault-name $vaultName --name $hashSecret --query value -o tsv 2>$null

if ($existingEnc -and $existingHash -and -not $Rotate) {
    Write-FcPass 'both secrets already present in Key Vault'
    Write-FcNote 'Use -Rotate to replace them. That makes existing encrypted data unreadable,'
    Write-FcNote 'which on a dev environment means re-seeding.'
} else {
    if ($Rotate -and ($existingEnc -or $existingHash)) {
        Write-FcWarn 'ROTATING: anything already encrypted under the current keys becomes unreadable.'
    }

    Confirm-FcMutation -ResourceGroup $ResourceGroup `
        -Summary "generate two DEVELOPMENT 256-bit keys and store them in Key Vault '$vaultName'" `
        -Force:$Force

    Write-FcHeading 'Generating'

    function New-Key256 {
        $bytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        return [Convert]::ToBase64String($bytes)
    }

    $encryptionKey = New-Key256
    $searchHashKey = New-Key256

    # FieldEncryptor refuses to construct if these are equal, and it is right to: reusing one
    # key for encryption and for a deterministic hash weakens both.
    while ($encryptionKey -eq $searchHashKey) { $searchHashKey = New-Key256 }

    az keyvault secret set --vault-name $vaultName --name $encSecret  --value $encryptionKey  -o none
    az keyvault secret set --vault-name $vaultName --name $hashSecret --value $searchHashKey -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to write secrets to Key Vault '$vaultName'." }

    Write-FcPass 'two distinct 256-bit keys generated and stored'
    Write-FcNote "  $encSecret"
    Write-FcNote "  $hashSecret"
}

# ── Wire them to App Service ───────────────────────────────────────────────────────────
Write-FcHeading 'App Service configuration'

$encReference  = "@Microsoft.KeyVault(VaultName=$vaultName;SecretName=$encSecret)"
$hashReference = "@Microsoft.KeyVault(VaultName=$vaultName;SecretName=$hashSecret)"

az webapp config appsettings set `
    --name $webAppName --resource-group $ResourceGroup `
    --settings "Security__FieldEncryption__EncryptionKeyBase64=$encReference" `
               "Security__FieldEncryption__SearchHashKeyBase64=$hashReference" `
    -o none

if ($LASTEXITCODE -ne 0) { throw "Failed to set App Service configuration." }
Write-FcPass 'settings written as Key Vault references (no key material in App Service)'

# ── Can the app actually resolve them? ─────────────────────────────────────────────────
#
# A Key Vault reference that cannot resolve leaves the setting as the literal
# "@Microsoft.KeyVault(...)" string, which is not valid base64 — so the app throws at startup
# with a message about the key rather than about the missing role assignment. Check now.
Write-FcHeading 'Reference resolution'

Write-FcNote 'waiting for App Service to re-read configuration...'
Start-Sleep -Seconds 15

$settings = az webapp config appsettings list --name $webAppName --resource-group $ResourceGroup -o json | ConvertFrom-Json
$encSetting = $settings | Where-Object name -eq 'Security__FieldEncryption__EncryptionKeyBase64'

if (-not $encSetting) {
    Write-FcFail 'setting not present after write'
} else {
    # The portal reports resolution status; the CLI does not surface it directly, so check the
    # role assignment that makes resolution possible instead.
    $principalId = az webapp identity show --name $webAppName --resource-group $ResourceGroup --query principalId -o tsv 2>$null
    if (-not $principalId) {
        Write-FcFail 'the web app has no system-assigned managed identity'
        Write-FcNote 'Key Vault references cannot resolve without one.'
    } else {
        $vaultId = az keyvault show --name $vaultName --query id -o tsv
        $roles = az role assignment list --assignee $principalId --scope $vaultId --query "[].roleDefinitionName" -o tsv 2>$null

        if ($roles -match 'Key Vault Secrets User' -or $roles -match 'Key Vault Secrets Officer') {
            Write-FcPass "app identity holds a secrets-read role on '$vaultName'"
        } else {
            Write-FcFail "app identity has no secrets-read role on '$vaultName' (found: $roles)"
            Write-FcNote 'Grant it, or the reference resolves to a literal string and the app'
            Write-FcNote 'fails at startup complaining about the key rather than the permission:'
            Write-FcNote "  az role assignment create --assignee $principalId --role 'Key Vault Secrets User' --scope $vaultId"
        }
    }
}

Write-Host ''
Write-Host 'Keys are in place.' -ForegroundColor Green
Write-Host ''
Write-Host 'The application still cannot start successfully until the database schema exists —' -ForegroundColor Cyan
Write-Host 'SeedReferenceDataAsync runs at startup and needs the Roles table.' -ForegroundColor Cyan
Write-Host 'Next: step 4 (review migration), then step 5 (apply it), then step 6 (deploy the app).'
