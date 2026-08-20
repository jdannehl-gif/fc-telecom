<#
.SYNOPSIS
    Generate the migration SQL and check it for the four most likely failure modes. Read-only.

.EXAMPLE
    ./scripts/validate/04-ReviewMigration.ps1

.NOTES
    Never touches a database. "Review the generated migration before applying it" is correct
    advice and almost impossible to follow well on a 54-entity schema; these four checks are
    the ones where a human skimming thousands of lines of DDL reliably misses the problem and
    the consequence is either a failed apply or a successful apply of the wrong thing.

    Heuristics over generated SQL, not a substitute for reading it.
#>
[CmdletBinding()]
param(
    [string]$InfraProject   = 'src/FcTelecom.Infrastructure',
    [string]$StartupProject = 'src/FcTelecom.Web',

    # Skip generation and analyse a script produced earlier.
    [string]$SqlFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/FcValidation.psm1" -Force

Show-FcContext -Operation 'Migration review (read-only, no database contact)' | Out-Null

$results = New-FcResultsDirectory

if (-not $SqlFile) {
    $SqlFile = Join-Path $results 'migration.sql'

    Write-FcHeading 'Generating idempotent migration script'

    if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) {
        throw "dotnet-ef not installed. Run: dotnet tool install --global dotnet-ef"
    }

    # No connection string is needed for script generation, and none should be used. This is a
    # model-to-SQL operation.
    dotnet ef migrations script --idempotent `
        --project $InfraProject --startup-project $StartupProject `
        --output $SqlFile --no-build 2>&1 | Tee-Object -Variable efOutput | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-FcFail 'Script generation failed. This is itself a finding.'
        Write-FcNote 'It is the first time the EF model is genuinely validated — model errors'
        Write-FcNote 'surface here rather than at runtime.'
        $efOutput | Write-Host
        exit 1
    }

    Write-FcPass "wrote $SqlFile ($((Get-Content $SqlFile).Count) lines)"
}

$sql = Get-Content $SqlFile -Raw
$findings = 0
function Flag { param([string]$m) Write-FcFail $m; $script:findings++ }

# ── 1. Cascade paths ───────────────────────────────────────────────────────────────────
Write-FcHeading '1. Cascade paths'

$cascadeMatches = [regex]::Matches(
    $sql,
    'ALTER TABLE \[(\w+)\]\s+ADD CONSTRAINT \[(\w+)\] FOREIGN KEY.*?REFERENCES \[(\w+)\].*?ON DELETE CASCADE',
    'Singleline, IgnoreCase')

$byParent = @{}
foreach ($match in $cascadeMatches) {
    $child = $match.Groups[1].Value; $constraint = $match.Groups[2].Value; $parent = $match.Groups[3].Value
    if (-not $byParent.ContainsKey($parent)) { $byParent[$parent] = @() }
    $byParent[$parent] += [pscustomobject]@{ Child = $child; Constraint = $constraint }
}

if ($cascadeMatches.Count -eq 0) {
    Write-FcPass 'no ON DELETE CASCADE constraints at all'
} else {
    Write-FcPass "$($cascadeMatches.Count) cascading FK(s) across $($byParent.Count) parent table(s)"
    foreach ($parent in ($byParent.Keys | Sort-Object)) {
        if ($byParent[$parent].Count -le 1) { continue }
        Flag "[$parent] is the target of $($byParent[$parent].Count) cascading FKs:"
        foreach ($item in $byParent[$parent]) { Write-FcNote "from [$($item.Child)] via $($item.Constraint)" }
        Write-FcNote 'Two cascade paths into one table is SQL Server error 1785 at apply time.'
        Write-FcNote 'Set the LESS important side to NoAction — never both to Cascade.'
    }
}

# ── 2. Filtered index predicates ───────────────────────────────────────────────────────
Write-FcHeading '2. Filtered index predicates'

$tableColumns = @{}
foreach ($match in [regex]::Matches($sql, 'CREATE TABLE \[(\w+)\] \((.*?)\r?\n\);', 'Singleline')) {
    $table = $match.Groups[1].Value
    $columns = [regex]::Matches($match.Groups[2].Value, '\[(\w+)\]\s+\w') | ForEach-Object { $_.Groups[1].Value }
    $tableColumns[$table] = @($columns)
}

$filtered = [regex]::Matches($sql,
    'CREATE (?:UNIQUE )?INDEX \[(\w+)\]\s+ON \[(\w+)\] \([^)]*\)\s+WHERE (.+?);', 'IgnoreCase')

if ($filtered.Count -eq 0) {
    Write-FcPass 'no filtered indexes'
} else {
    Write-FcPass "$($filtered.Count) filtered index(es)"
    foreach ($match in $filtered) {
        $indexName = $match.Groups[1].Value; $table = $match.Groups[2].Value; $predicate = $match.Groups[3].Value
        if (-not $tableColumns.ContainsKey($table)) {
            Write-FcNote "$indexName : [$table] not created in this script (pre-existing)"
            continue
        }
        $referenced = [regex]::Matches($predicate, '\[(\w+)\]') | ForEach-Object { $_.Groups[1].Value }
        $missing = @($referenced | Where-Object { $_ -notin $tableColumns[$table] })
        if ($missing.Count -gt 0) {
            Flag "$indexName on [$table] filters on column(s) the table does not have: $($missing -join ', ')"
            Write-FcNote "predicate: WHERE $($predicate.Trim())"
        }
    }
}

# ── 3. RowVersion column type ──────────────────────────────────────────────────────────
Write-FcHeading '3. RowVersion column type'

$rowVersions = [regex]::Matches($sql, '\[RowVersion\]\s+(\w+(?:\(\w+\))?)', 'IgnoreCase') |
    ForEach-Object { $_.Groups[1].Value }

if ($rowVersions.Count -eq 0) {
    Flag 'no RowVersion column found in the script at all'
    Write-FcNote 'Every BaseEntity-derived table should have one. Check ApplyRowVersionConvention.'
} else {
    $wrong = @($rowVersions | Where-Object { $_.ToLower() -ne 'rowversion' })
    if ($wrong.Count -gt 0) {
        Flag "$($wrong.Count) of $($rowVersions.Count) RowVersion columns are not 'rowversion': $(($wrong | Select-Object -Unique) -join ', ')"
        Write-FcNote 'As varbinary the column exists but is never populated or compared, so'
        Write-FcNote 'optimistic concurrency silently does nothing — the lost-update defect.'
    } else {
        Write-FcPass "all $($rowVersions.Count) RowVersion columns are 'rowversion'"
    }
}

# ── 4. Optional owned type nullability ─────────────────────────────────────────────────
Write-FcHeading '4. Optional owned type: Location.MailingAddress'

$locations = [regex]::Match($sql, 'CREATE TABLE \[Locations\] \((.*?)\r?\n\);', 'Singleline')
if (-not $locations.Success) {
    Write-FcNote 'Locations table not created in this script — skipping'
} else {
    $owned = [regex]::Matches($locations.Groups[1].Value, '\[(MailingAddress_\w+)\]\s+[\w()]+\s+(NOT NULL|NULL)')
    if ($owned.Count -eq 0) {
        Write-FcNote 'no MailingAddress_* columns found; the owned type may be named differently'
    } else {
        $notNull = @($owned | Where-Object { $_.Groups[2].Value -eq 'NOT NULL' } | ForEach-Object { $_.Groups[1].Value })
        if ($notNull.Count -gt 0) {
            Flag "$($notNull.Count) MailingAddress column(s) are NOT NULL: $($notNull -join ', ')"
            Write-FcNote 'An optional owned type with NOT NULL columns cannot be absent. Any'
            Write-FcNote 'location without a mailing address will fail to save.'
        } else {
            Write-FcPass "all $($owned.Count) MailingAddress columns are nullable"
        }
    }
}

# ── Result ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($findings -eq 0) {
    Write-Host 'No findings from the four automated checks.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Still read the script. Also scan for: unexpected table drops, NVARCHAR(MAX)'
    Write-Host 'where a length was intended, and check constraints that did not make it across.'
    Write-Host ''
    Write-Host 'Apply it as the MIGRATION identity, not the application identity — see'
    Write-Host 'docs/runbooks/azure-validation.md step 4.'
    exit 0
}

Write-Host "$findings finding(s)." -ForegroundColor Red
Write-Host 'Fix the model and regenerate. Do not hand-edit the migration — a hand-edited'
Write-Host 'migration and the model it came from disagree forever.'
exit 1
