<#
.SYNOPSIS
    Audit the validation scripts for assumptions that only hold on Windows.

.EXAMPLE
    pwsh ./scripts/validate/Test-LinuxCompatibility.ps1

.DESCRIPTION
    The validation pass runs on a headless Ubuntu Server 26.04 host. "It should work on Linux"
    is the kind of claim that is cheap to make and expensive to be wrong about, so this checks
    it mechanically instead.

    Nine categories, each one a way a PowerShell script quietly stops working off Windows:

      Drive letters          C:\ paths, and $env:USERPROFILE / $env:APPDATA / $env:TEMP
      Backslash separators   literal \ inside path strings, which is not a separator on Linux
      Registry               anything touching HKLM:/HKCU: or *-ItemProperty on a registry path
      Windows-only modules   ActiveDirectory, ScheduledTasks, Storage, NetTCPIP, and friends
      Windows-only cmdlets   Get-WmiObject, Get-CimInstance, Get-EventLog, Get-Service,
                             Restart-Computer, Get-LocalUser, Get-ADUser, WinRM/PSSession
      COM and WSH            New-Object -ComObject
      Windows PowerShell     Windows-desktop APIs, WPF, WinForms, System.Drawing
      Interactive browser    az login without --use-device-code, Start-Process on a URL
      Case sensitivity       file references whose case does not match what is on disk

    A finding is not automatically a bug — Get-Service exists on Linux in PowerShell 7, for
    instance, it just does nothing useful. The output names the file and line so a human can
    judge. Exit code is non-zero only for categories that cannot work at all.
#>
[CmdletBinding()]
param(
    [string]$Path = "$PSScriptRoot",
    [switch]$IncludeAdvisory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Id = 'drive-letter';   Severity = 'blocking'; Pattern = '(?<![\w:])[A-Za-z]:\\'
       Why  = 'Drive-letter path. There is no C:\ on Linux.' }

    @{ Id = 'windows-envvar'; Severity = 'blocking'; Pattern = '\$env:(USERPROFILE|APPDATA|LOCALAPPDATA|ProgramFiles|ProgramData|SystemRoot|COMPUTERNAME|USERDOMAIN|windir)\b'
       Why  = 'Windows-only environment variable. Use $HOME, $env:TMPDIR, or [Environment]::GetFolderPath.' }

    @{ Id = 'registry';       Severity = 'blocking'; Pattern = '(HKLM:|HKCU:|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|-Path\s+[''"]?HK)'
       Why  = 'Registry access. No registry provider exists on Linux.' }

    @{ Id = 'com-object';     Severity = 'blocking'; Pattern = 'New-Object\s+-ComObject'
       Why  = 'COM is Windows-only.' }

    @{ Id = 'windows-module'; Severity = 'blocking'; Pattern = 'Import-Module\s+(ActiveDirectory|ScheduledTasks|Storage|NetTCPIP|NetSecurity|DnsClient|Defender|BitLocker|PKI|GroupPolicy|ServerManager|WebAdministration|Hyper-V|AppLocker)\b'
       Why  = 'Windows-only module.' }

    @{ Id = 'windows-cmdlet'; Severity = 'blocking'; Pattern = '\b(Get-WmiObject|Get-CimInstance|New-CimSession|Get-EventLog|Write-EventLog|Get-LocalUser|New-LocalUser|Get-ADUser|Get-ADGroup|Restart-Computer|Get-HotFix|Get-WindowsFeature|Enable-PSRemoting|New-PSSession|Enter-PSSession|Get-Acl|Set-Acl|Get-AuthenticodeSignature)\b'
       Why  = 'Cmdlet is unavailable or non-functional on Linux.' }

    @{ Id = 'windows-ui';     Severity = 'blocking'; Pattern = '(System\.Windows\.Forms|System\.Drawing|PresentationFramework|Add-Type\s+-AssemblyName)'
       Why  = 'Windows desktop assembly.' }

    # The lookahead deliberately scans the whole remaining line rather than requiring the flag
    # to follow immediately. A line that offers both forms — a Windows branch and a Linux
    # branch — is correct, and an earlier version of this rule flagged its own fix.
    @{ Id = 'browser-login';  Severity = 'blocking'; Pattern = '(az\s+login(?![^\r\n]*--use-device-code)(?![^\r\n]*--service-principal)|Start-Process\s+[''"]?https?://)'
       Why  = 'Interactive browser login. The validation host is headless — use az login --use-device-code.' }

    @{ Id = 'backslash-path'; Severity = 'advisory'; Pattern = '[''"][^''"\r\n]*[A-Za-z0-9_)][\\][A-Za-z0-9_$][^''"\r\n]*[''"]'
       Why  = 'Backslash inside a string that looks like a path. Not a separator on Linux.' }

    @{ Id = 'service-cmdlet'; Severity = 'advisory'; Pattern = '\b(Get-Service|Start-Service|Stop-Service|Set-Service)\b'
       Why  = 'Exists on Linux in PowerShell 7 but reports nothing useful.' }
)

$targets = @(Get-ChildItem -Path $Path -Filter '*.ps1' -File) +
           @(Get-ChildItem -Path $Path -Filter '*.psm1' -File) |
           Sort-Object Name

$findings = @()

foreach ($file in $targets) {
    if ($file.Name -eq 'Test-LinuxCompatibility.ps1') { continue }   # the rules are in here

    $lines = Get-Content $file.FullName
    $inBlockComment = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # Skip comments and here-string prose — the scripts explain Windows behaviour in
        # comments, and flagging the explanation as the problem would be unhelpful.
        if ($line -match '<#') { $inBlockComment = $true }
        if ($inBlockComment) { if ($line -match '#>') { $inBlockComment = $false }; continue }
        if ($line -match '^\s*#') { continue }

        $code = $line -replace '(?<!`)#.*$', ''

        # A regex literal is full of backslashes that are not path separators. Without this,
        # every 'CREATE TABLE \[(\w+)\]' pattern in 04-ReviewMigration.ps1 reports as a
        # Windows path, and an audit that cries wolf five times stops being read.
        # Two ways a line is regex rather than paths: it calls something regex-shaped, or it
        # simply contains regex escape classes. The second case catches continuation lines of
        # a multi-line [regex]::Matches(...) call, where the giveaway call is on a line above.
        # A third case: \n, \t and \r are escape sequences in printf formats and in embedded
        # shell, not path separators. Test-DeploymentSequencing.ps1 carries a shell test double
        # whose `printf '%s\n'` was reported as a Windows path.
        $isRegexContext = ($code -match '(\[regex\]::|-match|-notmatch|-replace|-split|Matches\(|Match\()') -or
                          ($code -match '\\[swdbWSDB]|\\\[|\\\(|\\\.|\\\?|\\\+') -or
                          ($code -match '\\[nrt0]')

        foreach ($rule in $rules) {
            if ($isRegexContext -and $rule.Id -eq 'backslash-path') { continue }
            if ($rule.Severity -eq 'advisory' -and -not $IncludeAdvisory) { continue }
            if ($code -match $rule.Pattern) {
                $findings += [pscustomobject]@{
                    File     = $file.Name
                    Line     = $i + 1
                    Rule     = $rule.Id
                    Severity = $rule.Severity
                    Why      = $rule.Why
                    Text     = $code.Trim()
                }
            }
        }
    }
}

# ── Case sensitivity ───────────────────────────────────────────────────────────────────
#
# Linux filesystems are case-sensitive; NTFS is not. A script referring to
# scripts/validate/fcvalidation.psm1 works on Windows and fails here.
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
foreach ($file in $targets) {
    $text = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '(?<![\w./])(scripts|docs|src|infra|tests)/[\w./-]+')) {
        $reference = $match.Value
        $full = Join-Path $repoRoot $reference
        if (Test-Path $full) {
            # Resolve the real name on disk and compare case exactly.
            $leaf = Split-Path $full -Leaf
            $parent = Split-Path $full -Parent
            $actual = Get-ChildItem -Path $parent -Force -ErrorAction SilentlyContinue |
                      Where-Object Name -eq $leaf | Select-Object -First 1
            if ($actual -and $actual.Name -cne $leaf) {
                $findings += [pscustomobject]@{
                    File = $file.Name; Line = 0; Rule = 'path-case'; Severity = 'blocking'
                    Why  = "Case mismatch: referenced '$leaf', on disk '$($actual.Name)'"
                    Text = $reference
                }
            }
        }
    }
}

# ── Report ─────────────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'Linux compatibility audit' -ForegroundColor White
Write-Host ('-' * 25) -ForegroundColor DarkGray
Write-Host "  scanned: $($targets.Count - 1) file(s) in $Path"
Write-Host "  rules:   $($rules.Count)$(if (-not $IncludeAdvisory) { ' (blocking only; -IncludeAdvisory for the rest)' })"

$blocking = @($findings | Where-Object Severity -eq 'blocking')
$advisory = @($findings | Where-Object Severity -eq 'advisory')

if ($blocking.Count -eq 0) {
    Write-Host ''
    Write-Host '  No blocking Windows-only assumptions found.' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host "  $($blocking.Count) blocking finding(s):" -ForegroundColor Red
    foreach ($finding in $blocking) {
        Write-Host ''
        Write-Host ("    {0}:{1}  [{2}]" -f $finding.File, $finding.Line, $finding.Rule) -ForegroundColor Yellow
        Write-Host ("      $($finding.Why)") -ForegroundColor DarkGray
        Write-Host ("      $($finding.Text)")
    }
}

if ($advisory.Count -gt 0) {
    Write-Host ''
    Write-Host "  $($advisory.Count) advisory finding(s):" -ForegroundColor Yellow
    foreach ($finding in $advisory) {
        Write-Host ("    {0}:{1}  [{2}] {3}" -f $finding.File, $finding.Line, $finding.Rule, $finding.Text)
    }
}

Write-Host ''
exit $blocking.Count
