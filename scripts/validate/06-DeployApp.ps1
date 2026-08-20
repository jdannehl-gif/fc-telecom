<#
.SYNOPSIS
    Build and deploy the application code, then verify the deployed build is the one you meant.

.EXAMPLE
    ./scripts/validate/06-DeployApp.ps1 -Environment dev -ResourceGroup rg-fctelecom-dev

.DESCRIPTION
    THE STEP THAT WAS MISSING.

    An earlier revision went from "infrastructure deployed" straight to "smoke test the
    application", with nothing in between that put application code on the App Service.
    Creating an App Service does not deploy anything to it: `az deployment group create` gives
    you an empty site that serves a default placeholder page, and a placeholder page will
    happily return 200 from '/' while every real check fails for reasons that have nothing to
    do with the application.

    Two supported paths:

      -UseWorkflow   Trigger .github/workflows/cd.yml (workflow_dispatch). This is what a real
                     deployment does, including migrations, and it is what production will
                     use. Requires the gh CLI and a configured AZURE_CREDENTIALS secret plus a
                     WEB_APP_NAME variable in the repository.

      default        Publish locally and push the package with `az webapp deploy`. Fewer moving
                     parts for a first validation run, and it does not require the pipeline to
                     be wired up yet.

    Either way, this script verifies afterwards that the site is actually running the build you
    just produced, rather than assuming a successful upload means a successful start.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')][string]$Environment = 'dev',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [string]$DeploymentName,
    [switch]$UseWorkflow,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

$outputs    = Get-FcDeploymentOutputs -ResourceGroup $ResourceGroup -DeploymentName $DeploymentName
$webAppName = $outputs.webAppName
$baseUrl    = $outputs.webUrl.TrimEnd('/')

Show-FcContext -Operation "Build and deploy the application ($(if ($UseWorkflow) { 'cd.yml workflow' } else { 'local publish' }))" `
               -Environment $Environment -ResourceGroup $ResourceGroup `
               -WebUrl $baseUrl -Mutating | Out-Null

$commit = (git rev-parse --short HEAD 2>$null)
$branch = (git rev-parse --abbrev-ref HEAD 2>$null)
$dirty  = (git status --porcelain 2>$null)

Write-FcHeading 'What is about to be deployed'
Write-FcNote "branch: $branch"
Write-FcNote "commit: $commit"
if ($dirty) {
    Write-FcWarn 'The working tree has uncommitted changes.'
    Write-FcNote 'A local publish deploys what is on disk, not what is committed. If this'
    Write-FcNote 'environment is meant to reflect a specific commit, stash or commit first.'
}

Confirm-FcMutation -ResourceGroup $ResourceGroup `
    -Summary "deploy application code to '$webAppName' ($branch @ $commit)" -Force:$Force

# ── Path A: the real pipeline ──────────────────────────────────────────────────────────
if ($UseWorkflow) {
    Write-FcHeading 'Triggering the Deploy workflow'

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install it, or omit -UseWorkflow to publish locally."
    }

    gh workflow run cd.yml --field environment=$Environment
    if ($LASTEXITCODE -ne 0) { throw "Failed to trigger cd.yml." }

    Write-FcPass 'workflow dispatched'
    Write-FcNote 'Watch it: gh run watch'
    Write-FcNote ''
    Write-FcNote 'Note that cd.yml ALSO applies migrations, using the service principal behind'
    Write-FcNote 'AZURE_CREDENTIALS. That principal must be a member of FCTelecom-SQL-Migrators'
    Write-FcNote 'or the migration step fails with a permission error rather than a SQL error.'
    Write-FcNote ''
    Write-FcNote 'Re-run this script without -UseWorkflow once it completes to verify the build.'
    exit 0
}

# ── Path B: local publish ──────────────────────────────────────────────────────────────
Write-FcHeading 'Publishing'

$publishDir = Join-Path (New-FcResultsDirectory) 'publish-web'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish src/FcTelecom.Web -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
Write-FcPass "published $fileCount file(s) to $publishDir"

# Stamp the build so the verification below can prove which one is running.
$stamp = [ordered]@{
    commit    = $commit
    branch    = $branch
    publishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    dirty     = [bool]$dirty
}
$stamp | ConvertTo-Json | Out-File (Join-Path $publishDir 'wwwroot/build-info.json') -Encoding utf8

$zipPath = Join-Path (New-FcResultsDirectory) "web-$commit.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
Write-FcPass "packaged $zipPath"

Write-FcHeading 'Deploying'

az webapp deploy `
    --name $webAppName --resource-group $ResourceGroup `
    --src-path $zipPath --type zip `
    --async false -o none

if ($LASTEXITCODE -ne 0) { throw "az webapp deploy failed." }
Write-FcPass 'package uploaded'

# ── Verify it actually started, and that it is the right build ─────────────────────────
#
# A successful upload is not a successful start. The application resolves DemoDataSeeder at
# startup, which constructs FieldEncryptor, which throws if the encryption keys are missing —
# so an app with no keys uploads fine and then never serves a request.
Write-FcHeading 'Verifying the deployed build'

Write-FcNote 'waiting for the site to restart...'
$healthy = $false
foreach ($attempt in 1..20) {
    Start-Sleep -Seconds 15
    try {
        $response = Invoke-WebRequest -Uri "$baseUrl/health/live" -TimeoutSec 30 -SkipHttpErrorCheck -ErrorAction Stop
        if ($response.StatusCode -eq 200) { $healthy = $true; break }
        Write-FcNote "attempt ${attempt}: /health/live returned $($response.StatusCode)"
    } catch {
        Write-FcNote "attempt ${attempt}: no response yet"
    }
}

if ($healthy) {
    Write-FcPass '/health/live returns 200 — the application started'
} else {
    Write-FcFail 'the application did not become healthy within five minutes'
    Write-FcNote ''
    Write-FcNote 'Most likely causes, in order:'
    Write-FcNote '  1. Field-encryption keys missing or unresolvable (step 3). FieldEncryptor'
    Write-FcNote '     throws in its constructor, before the app serves anything.'
    Write-FcNote '  2. Database schema missing (step 5). SeedReferenceDataAsync needs Roles.'
    Write-FcNote '  3. The app identity has no database user (step 5b).'
    Write-FcNote ''
    Write-FcNote 'Read the actual exception rather than guessing:'
    Write-FcNote "  az webapp log tail --name $webAppName --resource-group $ResourceGroup"
    exit 1
}

$deployed = Invoke-WebRequest -Uri "$baseUrl/build-info.json" -TimeoutSec 30 -SkipHttpErrorCheck -ErrorAction SilentlyContinue
if ($deployed -and $deployed.StatusCode -eq 200) {
    $info = $deployed.Content | ConvertFrom-Json
    if ($info.commit -eq $commit) {
        Write-FcPass "running commit $($info.commit), published $($info.publishedUtc)"
    } else {
        Write-FcFail "the site reports commit $($info.commit) but this run published $commit"
        Write-FcNote 'The deployment may not have replaced the previous build.'
    }
} else {
    Write-FcWarn 'build-info.json not served — cannot confirm which build is running'
    Write-FcNote 'Static file serving may be restricted; not fatal, but the check is lost.'
}

Write-Host ''
Write-Host 'Application deployed and healthy.' -ForegroundColor Green
Write-Host 'Next: step 7 — confirm reference data seeded, then the bootstrap admin mapping.'
