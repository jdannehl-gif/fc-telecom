<#
.SYNOPSIS
    Unauthenticated smoke test against the deployed app. Read-only.

.EXAMPLE
    ./scripts/validate/06-Smoke.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.NOTES
    The URL comes from the deployment output, never from a naming convention — infra/main.bicep
    appends a uniqueString() suffix that cannot be derived.

    Deliberately does NOT cover authorization. That needs one real account per role, and it is
    the most valuable hour in the pass precisely because it cannot be scripted.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

$outputs = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName
$baseUrl = $outputs.webUrl.TrimEnd('/')

Show-FcContext -Operation 'HTTP smoke test (read-only)' -Environment $Environment `
               -ResourceGroup $ResourceGroup -WebUrl $baseUrl | Out-Null

$failures = 0
function Fail { param([string]$m) Write-FcFail $m; $script:failures++ }

function Invoke-Probe {
    param([string]$Url, [int]$TimeoutSec = 60)
    try {
        return Invoke-WebRequest -Uri $Url -MaximumRedirection 0 -SkipHttpErrorCheck `
                                 -TimeoutSec $TimeoutSec -ErrorAction Stop
    } catch {
        return $null
    }
}

# ── Health ─────────────────────────────────────────────────────────────────────────────
Write-FcHeading 'Health endpoints'

$live = Invoke-Probe "$baseUrl/health/live"
if ($live -and $live.StatusCode -eq 200) { Write-FcPass '/health/live returns 200' }
else { Fail "/health/live returned $(if ($live) { $live.StatusCode } else { 'no response' })" }

$ready = Invoke-Probe "$baseUrl/health/ready"
if ($ready -and $ready.StatusCode -eq 200) {
    Write-FcPass '/health/ready returns 200 (sql, outbox, probes)'
} else {
    Fail "/health/ready returned $(if ($ready) { $ready.StatusCode } else { 'no response' })"
    if ($ready) { Write-FcNote $ready.Content }
    Write-FcNote 'This is the first real exercise of the managed-identity SQL connection.'
    Write-FcNote 'A failure is usually the app identity missing a database user rather than'
    Write-FcNote 'anything wrong with the app — run 04-GrantDatabasePrincipals.sql.'
}

# ── Security headers ───────────────────────────────────────────────────────────────────
Write-FcHeading 'Security headers'

$root = Invoke-Probe "$baseUrl/"
if (-not $root) {
    Fail 'no response from /'
} else {
    function Test-Header {
        param([string]$Name, [string]$MustContain)
        $value = $root.Headers[$Name]
        if (-not $value) { Fail "$Name missing"; return }
        $value = ($value -join ' ')
        if ($MustContain -and $value -notlike "*$MustContain*") {
            Fail "${Name}: '$value' (expected to contain '$MustContain')"
        } else {
            Write-FcPass "${Name}: $value"
        }
    }

    Test-Header 'Strict-Transport-Security' 'max-age'
    Test-Header 'X-Content-Type-Options'    'nosniff'
    Test-Header 'X-Frame-Options'           'DENY'
    Test-Header 'Referrer-Policy'           ''
    Test-Header 'Content-Security-Policy'   "default-src 'self'"

    foreach ($banner in 'Server', 'X-Powered-By', 'X-AspNet-Version') {
        if ($root.Headers[$banner]) { Fail "$banner disclosed — it should have been stripped" }
        else { Write-FcPass "$banner not disclosed" }
    }

    # ── CSP, read properly ─────────────────────────────────────────────────────────────
    #
    # Corrected from an earlier revision. This application renders with InteractiveServer and
    # contains no WebAssembly, so 'wasm-unsafe-eval' must NOT be present — it was in the
    # original policy in error, copied from Blazor WebAssembly guidance where the Mono runtime
    # genuinely requires it. Its presence here would be a needless exception.
    $csp = ($root.Headers['Content-Security-Policy'] -join ' ')

    if ($csp -match "unsafe-inline") { Fail "CSP contains 'unsafe-inline' — most of the protection is gone" }
    else { Write-FcPass "CSP has no 'unsafe-inline'" }

    if ($csp -match "unsafe-eval") { Fail "CSP contains 'unsafe-eval'" }
    else { Write-FcPass "CSP has no 'unsafe-eval'" }

    if ($csp -match "wasm-unsafe-eval") {
        Fail "CSP contains 'wasm-unsafe-eval' but this app has no WebAssembly — remove it"
    } else {
        Write-FcPass "CSP has no 'wasm-unsafe-eval' (correct: server-side rendering only)"
    }

    if ($csp -match "script-src 'self'(\s*;|\s*$)") {
        Write-FcPass "script-src is exactly 'self'"
    } else {
        Write-FcWarn "script-src is: $([regex]::Match($csp, "script-src[^;]*").Value)"
        Write-FcNote "Blazor Server needs only 'self'. Anything more needs a written reason."
    }
}

# ── Anonymous access ───────────────────────────────────────────────────────────────────
Write-FcHeading 'Anonymous access boundary'

foreach ($path in '/', '/locations', '/services', '/vendors') {
    $response = Invoke-Probe "$baseUrl$path"
    $code = if ($response) { $response.StatusCode } else { 0 }
    switch ($code) {
        { $_ -in 302, 301, 401 } { Write-FcPass "$path -> $code (redirected to sign-in)" }
        200 { Fail "$path returned 200 to an anonymous caller — check its [Authorize] attribute" }
        default { Write-FcWarn "$path returned $code" }
    }
}

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($failures -eq 0) { Write-Host 'Smoke test clean.' -ForegroundColor Green }
else { Write-Host "$failures failure(s)." -ForegroundColor Red }

Write-Host @'

NOT covered here, and not skippable:

  Blazor circuit    Sign in and open an interactive page. If it renders but never responds,
                    the CSP connect-src is blocking the WebSocket. Check the browser console
                    for a CSP violation before suspecting the server. This is the one change
                    in the corrected policy that could plausibly break something.

  Authorization     One real account per role. The failure you are hunting is a role seeing
                    something it should not, which needs someone who knows what that role is
                    for. See the runbook, step 6.

  Log redaction     Emit a log event containing a sensitive property and confirm it arrives
                    REDACTED. 07-CryptoCheck.ps1 gives you the Application Insights query.
'@

exit $failures
