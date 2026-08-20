<#
.SYNOPSIS
    Prove the field-encryption path end to end against keys already in use.

.EXAMPLE
    ./scripts/validate/09-VerifyEncryption.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

    # Optional, and it deliberately takes the app down for a minute:
    ./scripts/validate/09-VerifyEncryption.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -RunNegativeKeyTest

.NOTES
    Keys are created in step 3 (03-SetEncryptionKeys.ps1), not here. They must exist before the
    application starts at all — FieldEncryptor is a singleton constructed while resolving
    DemoDataSeeder during startup, and it throws if either key is missing, malformed, or if the
    two are identical. This script verifies keys that are already in place and already in use.

.DESCRIPTION
    The AES-GCM path and the HMAC search index have never had a key, so this is the first time
    either runs. Rather than asserting they work, this reproduces the application's exact
    construction in PowerShell and checks the two agree.

      Ciphertext   "v1:" + base64( nonce[12] || tag[16] || ciphertext )   AES-256-GCM
      Search hash  HMACSHA256( searchHashKey, UTF8( value.Trim().ToUpperInvariant() ) )

    Because this script holds the same keys the application holds, it can decrypt what the
    application wrote and recompute the search hash independently. If both match, the write
    path and the read path genuinely agree — which is the thing that silently breaks, since a
    mismatch makes exact search return nothing rather than throwing.

    KEYS. Created in step 3. This script reads them back and proves the application's
    encryption and search-hash paths agree with an independent implementation.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName,

    # Optional. Takes the application down deliberately, then restores it.
    [switch]$RunNegativeKeyTest,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName

Show-FcContext -Operation 'Verify field encryption end to end' `
               -Environment $Environment -ResourceGroup $ResourceGroup `
               -SqlServer $outputs.sqlServerFqdn -SqlDatabase $outputs.sqlDatabaseName `
               -WebUrl $outputs.webUrl | Out-Null

$failures = 0
function Fail { param([string]$m) Write-FcFail $m; $script:failures++ }

$vaultName    = $outputs.keyVaultName
$encSecret    = 'Security--FieldEncryption--EncryptionKeyBase64'
$hashSecret   = 'Security--FieldEncryption--SearchHashKeyBase64'

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer module is required. Install-Module SqlServer -Scope CurrentUser"
}
Import-Module SqlServer -ErrorAction Stop

Write-FcHeading 'Reading keys back from Key Vault'

$encryptionKey = az keyvault secret show --vault-name $vaultName --name $encSecret  --query value -o tsv 2>$null
$searchHashKey = az keyvault secret show --vault-name $vaultName --name $hashSecret --query value -o tsv 2>$null

if (-not $encryptionKey -or -not $searchHashKey) {
    Fail "Could not read both secrets from '$vaultName'. Run 03-SetEncryptionKeys.ps1 first."
    exit 1
}

$encKeyBytes  = [Convert]::FromBase64String($encryptionKey)
$hashKeyBytes = [Convert]::FromBase64String($searchHashKey)

if ($encKeyBytes.Length -ne 32) { Fail "encryption key is $($encKeyBytes.Length) bytes, expected 32" }
else { Write-FcPass 'encryption key is 256-bit' }

if ($hashKeyBytes.Length -ne 32) { Fail "search hash key is $($hashKeyBytes.Length) bytes, expected 32" }
else { Write-FcPass 'search hash key is 256-bit' }

if ([Convert]::ToBase64String($encKeyBytes) -eq [Convert]::ToBase64String($hashKeyBytes)) {
    Fail 'the two keys are IDENTICAL — the application should refuse to start'
} else { Write-FcPass 'the two keys differ' }

# ── The application's exact construction, reimplemented ────────────────────────────────
function ConvertFrom-FcCiphertext {
    param([Parameter(Mandatory)][string]$Envelope, [Parameter(Mandatory)][byte[]]$Key)

    $separator = $Envelope.IndexOf(':')
    if ($separator -lt 0) { throw "missing version prefix" }
    $version = $Envelope.Substring(0, $separator)
    if ($version -ne 'v1') { throw "unknown format '$version'" }

    $bytes = [Convert]::FromBase64String($Envelope.Substring($separator + 1))
    if ($bytes.Length -lt 28) { throw "truncated" }

    $nonce      = $bytes[0..11]
    $tag        = $bytes[12..27]
    $ciphertext = $bytes[28..($bytes.Length - 1)]
    $plaintext  = [byte[]]::new($ciphertext.Length)

    $aes = [System.Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Decrypt($nonce, $ciphertext, $tag, $plaintext) }
    finally { $aes.Dispose() }

    return [System.Text.Encoding]::UTF8.GetString($plaintext)
}

function Get-FcSearchHash {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][byte[]]$Key)
    # Trim + upper-case only, exactly as FieldEncryptor.ComputeSearchHash does.
    $input = [System.Text.Encoding]::UTF8.GetBytes($Value.Trim().ToUpperInvariant())
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($Key)
    try { return $hmac.ComputeHash($input) } finally { $hmac.Dispose() }
}

# ── Round trip against our own synthetic data ──────────────────────────────────────────
Write-FcHeading 'Synthetic round trip (no database involved)'

# TEST-NET-3 (203.0.113.0/24, RFC 5737). Reserved for documentation, routable nowhere, so it
# cannot be confused with a real circuit if it escapes into a log.
$synthetic = '203.0.113.8/29'

$plaintextBytes = [System.Text.Encoding]::UTF8.GetBytes($synthetic)
$nonce = [byte[]]::new(12); [System.Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
$cipherBytes = [byte[]]::new($plaintextBytes.Length)
$tagBytes = [byte[]]::new(16)

$aes = [System.Security.Cryptography.AesGcm]::new($encKeyBytes, 16)
try { $aes.Encrypt($nonce, $plaintextBytes, $cipherBytes, $tagBytes) } finally { $aes.Dispose() }

$envelope = 'v1:' + [Convert]::ToBase64String($nonce + $tagBytes + $cipherBytes)

try {
    $decrypted = ConvertFrom-FcCiphertext -Envelope $envelope -Key $encKeyBytes
    if ($decrypted -eq $synthetic) { Write-FcPass "encrypt/decrypt round trip: '$synthetic'" }
    else { Fail "round trip returned '$decrypted'" }
} catch { Fail "round trip threw: $($_.Exception.Message)" }

if ($envelope -notmatch [regex]::Escape($synthetic)) { Write-FcPass 'ciphertext does not contain the plaintext' }
else { Fail 'the plaintext is visible in the ciphertext' }

# Tamper: AES-GCM must reject, not return plausible garbage. A silently-wrong gateway address
# read out to a carrier is worse than an error.
$tampered = [Convert]::FromBase64String($envelope.Substring(3))
$tampered[$tampered.Length - 1] = $tampered[$tampered.Length - 1] -bxor 0x01
$tamperedEnvelope = 'v1:' + [Convert]::ToBase64String($tampered)

try {
    ConvertFrom-FcCiphertext -Envelope $tamperedEnvelope -Key $encKeyBytes | Out-Null
    Fail 'a tampered ciphertext DECRYPTED — authentication is not being verified'
} catch {
    Write-FcPass 'tampered ciphertext rejected (AES-GCM tag verified)'
}

# Wrong key must fail closed too.
$wrongKey = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($wrongKey)
try {
    ConvertFrom-FcCiphertext -Envelope $envelope -Key $wrongKey | Out-Null
    Fail 'decryption with the WRONG key succeeded'
} catch {
    Write-FcPass 'wrong key fails closed'
}

# Determinism: the same input must always hash the same, or exact search silently finds nothing.
$hashA = Get-FcSearchHash -Value $synthetic -Key $hashKeyBytes
$hashB = Get-FcSearchHash -Value $synthetic -Key $hashKeyBytes
if ([Convert]::ToBase64String($hashA) -eq [Convert]::ToBase64String($hashB)) {
    Write-FcPass 'search hash is deterministic'
} else { Fail 'search hash is NOT deterministic' }

$hashDifferent = Get-FcSearchHash -Value '203.0.113.16/29' -Key $hashKeyBytes
if ([Convert]::ToBase64String($hashA) -ne [Convert]::ToBase64String($hashDifferent)) {
    Write-FcPass 'different inputs hash differently'
} else { Fail 'two different CIDRs produced the same hash' }

# ── Against what the application actually wrote ────────────────────────────────────────
Write-FcHeading 'Verifying rows the application encrypted'
Write-FcNote 'Requires SeedDemoData=true on a dev environment, or manually entered IP data.'

$sqlArgs = @{
    ServerInstance = $outputs.sqlServerFqdn
    Database       = $outputs.sqlDatabaseName
    ErrorAction    = 'Stop'
}

# Connect as the operator here — this is a read of ciphertext for verification, and the
# operator is already a SQL admin. The permission model itself is tested by 07-TestAppIdentity.
try {
    $rows = Invoke-Sqlcmd @sqlArgs -AccessToken (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv) -Query @'
SELECT TOP (5) Id, CidrEncrypted, CidrSearchHash
FROM dbo.ServiceIpAssignments
WHERE CidrEncrypted IS NOT NULL;
'@
} catch {
    Fail "Could not read dbo.ServiceIpAssignments: $($_.Exception.Message)"
    $rows = @()
}

if (-not $rows -or @($rows).Count -eq 0) {
    Write-FcWarn 'No encrypted IP assignments found.'
    Write-FcNote 'Set SeedDemoData=true in App Service configuration, restart, then re-run.'
    Write-FcNote 'Remember to set it back to false afterwards.'
} else {
    foreach ($row in $rows) {
        $id = $row.Id
        try {
            $cidr = ConvertFrom-FcCiphertext -Envelope $row.CidrEncrypted -Key $encKeyBytes
            Write-FcPass "row $id decrypts to '$cidr'"

            # The real prize: does the stored search hash match one we compute independently?
            # A mismatch means the write path and the read path disagree, and exact search
            # returns nothing rather than throwing — which is the failure nobody notices.
            $expected = Get-FcSearchHash -Value $cidr -Key $hashKeyBytes
            $stored   = [byte[]]$row.CidrSearchHash

            if ([Convert]::ToBase64String($expected) -eq [Convert]::ToBase64String($stored)) {
                Write-FcPass "row $id search hash matches an independently computed HMAC"
            } else {
                Fail "row $id search hash does NOT match"
                Write-FcNote 'The write path normalises differently from ComputeSearchHash.'
                Write-FcNote 'Exact search for this value will silently return nothing.'
                Write-FcNote 'Check GlobalSearchService.NormalizeCidr against what was stored.'
            }
        } catch {
            Fail "row $id failed to decrypt: $($_.Exception.Message)"
        }
    }
}

# ── Negative test: identical keys must stop the application ────────────────────────────
#
# FieldEncryptor refuses to construct when both keys are the same. That guard is worth proving
# rather than trusting, because it is the kind of check that gets removed during a refactor by
# someone who cannot see why it is there.
#
# This DELIBERATELY BREAKS THE RUNNING APPLICATION and then restores it. Everything needed to
# get back is captured first, and the restore runs in a finally block so it happens even if the
# test itself throws or you interrupt it.
if ($RunNegativeKeyTest) {
    Write-FcHeading 'Negative test: identical keys (application will go down)'

    Write-FcWarn 'This takes the application offline for roughly two minutes.'
    Write-FcNote 'Do not run it against an environment anyone is currently using.'

    Confirm-FcMutation -ResourceGroup $ResourceGroup `
        -Summary 'temporarily set both encryption keys to the same value, confirm the app refuses to start, then restore' `
        -Force:$Force

    $webAppName = $outputs.webAppName

    # ── Backup ─────────────────────────────────────────────────────────────────────────
    Write-FcHeading 'Backup before breaking anything'

    $allSettings = az webapp config appsettings list `
        --name $webAppName --resource-group $ResourceGroup -o json | ConvertFrom-Json

    $backupPath = Join-Path (New-FcResultsDirectory) "appsettings-backup-$webAppName.json"
    $allSettings | ConvertTo-Json -Depth 5 | Out-File $backupPath -Encoding utf8
    Write-FcPass "all app settings saved to $backupPath"

    $originalEncSetting = ($allSettings | Where-Object name -eq 'Security__FieldEncryption__EncryptionKeyBase64').value
    if (-not $originalEncSetting) { throw "Encryption key setting not found — nothing to restore afterwards. Aborting." }
    Write-FcPass 'original encryption key setting captured'
    Write-FcNote 'It is a Key Vault reference, so no key material is in the backup file.'

    # Key Vault keeps every version, so the secret itself is recoverable independently:
    $encVersions = az keyvault secret list-versions --vault-name $vaultName --name $encSecret --query "length(@)" -o tsv 2>$null
    Write-FcNote "Key Vault holds $encVersions version(s) of $encSecret as a second line of defence."

    $restored = $false
    try {
        # ── Break it ───────────────────────────────────────────────────────────────────
        Write-FcHeading 'Setting both keys to the same value'

        $hashReference = "@Microsoft.KeyVault(VaultName=$vaultName;SecretName=$hashSecret)"
        az webapp config appsettings set --name $webAppName --resource-group $ResourceGroup `
            --settings "Security__FieldEncryption__EncryptionKeyBase64=$hashReference" -o none | Out-Null

        az webapp restart --name $webAppName --resource-group $ResourceGroup -o none
        Write-FcNote 'restarted; waiting to see whether it comes up...'

        $cameUp = $false
        foreach ($attempt in 1..8) {
            Start-Sleep -Seconds 15
            try {
                $probe = Invoke-WebRequest -Uri "$($outputs.webUrl.TrimEnd('/'))/health/live" `
                    -TimeoutSec 20 -SkipHttpErrorCheck -ErrorAction Stop
                if ($probe.StatusCode -eq 200) { $cameUp = $true; break }
            } catch { }
        }

        if ($cameUp) {
            Fail 'the application STARTED with two identical keys — the guard in FieldEncryptor is not working'
            Write-FcNote 'Check the constructor: it should throw when the keys are SequenceEqual.'
        } else {
            Write-FcPass 'the application refused to start, as designed'
            Write-FcNote 'Confirm the reason rather than assuming it — any startup failure looks'
            Write-FcNote 'the same from outside:'
            Write-FcNote "  az webapp log tail --name $webAppName --resource-group $ResourceGroup"
            Write-FcNote 'Expect: "The field-encryption key and the search-hash key must be different."'
        }
    }
    finally {
        # ── Restore ────────────────────────────────────────────────────────────────────
        Write-FcHeading 'Restoring'

        az webapp config appsettings set --name $webAppName --resource-group $ResourceGroup `
            --settings "Security__FieldEncryption__EncryptionKeyBase64=$originalEncSetting" -o none | Out-Null

        az webapp restart --name $webAppName --resource-group $ResourceGroup -o none
        Write-FcNote 'original setting restored; waiting for a healthy start...'

        foreach ($attempt in 1..12) {
            Start-Sleep -Seconds 15
            try {
                $probe = Invoke-WebRequest -Uri "$($outputs.webUrl.TrimEnd('/'))/health/ready" `
                    -TimeoutSec 20 -SkipHttpErrorCheck -ErrorAction Stop
                if ($probe.StatusCode -eq 200) { $restored = $true; break }
            } catch { }
        }

        if ($restored) {
            Write-FcPass '/health/ready returns 200 — the application recovered fully'
        } else {
            Write-FcFail 'THE APPLICATION DID NOT RECOVER. Restore it by hand before doing anything else.'
            Write-FcNote ''
            Write-FcNote "  1. Compare current settings against the backup:"
            Write-FcNote "     $backupPath"
            Write-FcNote "  2. Re-apply the encryption key reference:"
            Write-FcNote "     az webapp config appsettings set --name $webAppName ``"
            Write-FcNote "         --resource-group $ResourceGroup ``"
            Write-FcNote "         --settings 'Security__FieldEncryption__EncryptionKeyBase64=$originalEncSetting'"
            Write-FcNote "  3. az webapp restart --name $webAppName --resource-group $ResourceGroup"
            Write-FcNote "  4. If the secret itself is damaged, Key Vault keeps every version:"
            Write-FcNote "     az keyvault secret list-versions --vault-name $vaultName --name $encSecret"
            Write-FcNote "  5. Read the actual exception:"
            Write-FcNote "     az webapp log tail --name $webAppName --resource-group $ResourceGroup"
            $script:failures++
        }
    }
} else {
    Write-FcHeading 'Negative test: identical keys — SKIPPED'
    Write-FcNote 'Re-run with -RunNegativeKeyTest to prove the guard works. It takes the'
    Write-FcNote 'application down for about two minutes and restores it automatically.'
}

# ── Log redaction ──────────────────────────────────────────────────────────────────────
Write-FcHeading 'Log redaction — manual step'

Write-Host @"
  The destructuring policy has never run. Test it explicitly rather than assuming it.

  1. Sign in as a Network Engineer and reveal a static IP on a service detail page.
     That writes a SecurityEvent and exercises the logging path around IP data.

  2. Run this in Application Insights (Logs) for '$($outputs.webAppName)':

       union traces, requests, exceptions
       | where timestamp > ago(30m)
       | where * contains "203.0.113" or * contains "CidrEncrypted" or * contains "Gateway"
       | project timestamp, message, customDimensions

  3. EXPECTED: any ServiceIpAssignment property appears as "[redacted]".
     FAILING:   an actual CIDR or gateway address appears anywhere in the output.

  A hit here is a real disclosure, not a formatting problem — Application Insights is
  readable by a much wider group than the ServiceIpData.Read permission.
"@

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($failures -eq 0) {
    Write-Host 'Field encryption verified end to end.' -ForegroundColor Green
    exit 0
}
Write-Host "$failures failure(s)." -ForegroundColor Red
exit 1
