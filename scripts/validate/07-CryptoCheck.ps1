<#
.SYNOPSIS
    Create development field-encryption keys and prove the encryption path end to end.

.EXAMPLE
    # Generate keys and place them in Key Vault (dev only)
    ./scripts/validate/07-CryptoCheck.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -CreateKeys

    # Verify an already-seeded environment
    ./scripts/validate/07-CryptoCheck.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev -Verify

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

    KEYS. -CreateKeys generates two distinct 256-bit keys and stores them in Key Vault. They
    are DEVELOPMENT keys. Anything encrypted under them is disposable, and they must never be
    reused for production. The script refuses to run -CreateKeys against prod.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName,

    [switch]$CreateKeys,
    [switch]$Verify,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

if (-not $CreateKeys -and -not $Verify) {
    throw "Specify -CreateKeys, -Verify, or both."
}
if ($CreateKeys -and $Environment -eq 'prod') {
    throw "Refusing to generate keys for prod. Production keys are created and rotated through the process in docs/runbooks/rotate-secrets.md, not by a validation script."
}

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName

Show-FcContext -Operation "Field encryption: $(if ($CreateKeys) { 'create keys' }) $(if ($Verify) { 'verify' })" `
               -Environment $Environment -ResourceGroup $ResourceGroup `
               -SqlServer $outputs.sqlServerFqdn -SqlDatabase $outputs.sqlDatabaseName `
               -WebUrl $outputs.webUrl -Mutating:$CreateKeys | Out-Null

$failures = 0
function Fail { param([string]$m) Write-FcFail $m; $script:failures++ }

$vaultName    = $outputs.keyVaultName
$encSecret    = 'Security--FieldEncryption--EncryptionKeyBase64'
$hashSecret   = 'Security--FieldEncryption--SearchHashKeyBase64'

# ── Create keys ────────────────────────────────────────────────────────────────────────
if ($CreateKeys) {
    Confirm-FcMutation -ResourceGroup $ResourceGroup `
        -Summary "generate two DEVELOPMENT 256-bit keys and store them in Key Vault '$vaultName'" `
        -Force:$Force

    Write-FcHeading 'Generating keys'

    function New-Key256 {
        $bytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        return [Convert]::ToBase64String($bytes)
    }

    $encryptionKey = New-Key256
    $searchHashKey = New-Key256

    # The application refuses to start if these are equal, and it is right to. Reusing one key
    # for encryption and for a deterministic hash weakens both.
    if ($encryptionKey -eq $searchHashKey) { throw "Generated identical keys — retry." }
    Write-FcPass 'two distinct 256-bit keys generated'

    az keyvault secret set --vault-name $vaultName --name $encSecret  --value $encryptionKey -o none
    az keyvault secret set --vault-name $vaultName --name $hashSecret --value $searchHashKey -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to write secrets to Key Vault '$vaultName'." }

    Write-FcPass "stored in Key Vault '$vaultName'"
    Write-FcNote "  $encSecret"
    Write-FcNote "  $hashSecret"
    Write-FcNote ''
    Write-FcNote 'Reference them from App Service configuration as:'
    Write-FcNote "  @Microsoft.KeyVault(VaultName=$vaultName;SecretName=$encSecret)"
    Write-FcNote 'so the value is never an App Service setting in clear text.'

    Write-Host ''
    Write-FcWarn 'NEGATIVE TEST worth doing once: set both settings to the SAME key and restart.'
    Write-FcNote 'The app must refuse to start. If it starts, the guard in FieldEncryptor is not working.'
}

# ── Verify ─────────────────────────────────────────────────────────────────────────────
if (-not $Verify) { exit $failures }

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer module is required. Install-Module SqlServer -Scope CurrentUser"
}
Import-Module SqlServer -ErrorAction Stop

Write-FcHeading 'Reading keys back from Key Vault'

$encryptionKey = az keyvault secret show --vault-name $vaultName --name $encSecret  --query value -o tsv 2>$null
$searchHashKey = az keyvault secret show --vault-name $vaultName --name $hashSecret --query value -o tsv 2>$null

if (-not $encryptionKey -or -not $searchHashKey) {
    Fail "Could not read both secrets from '$vaultName'. Run with -CreateKeys first."
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
# operator is already a SQL admin. The permission model itself is tested by 05-TestAppIdentity.
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
