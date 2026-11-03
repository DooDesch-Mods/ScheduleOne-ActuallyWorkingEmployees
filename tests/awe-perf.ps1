<#
Snitch measurement of one running game: samples for -Seconds without moving the player or camera, then logs Snitch's
standings, sections, unattributed time and (with -Patches) the cost of every mod's Harmony patch, and exports a report.

    pwsh -File tests/awe-perf.ps1 -Slot mptest6 -Label with-awe -Patches
    pwsh -File tests/awe-perf.ps1 -Slot mptest6 -Label without-awe

Needs Snitch in the slot (session.ps1 -Mods Snitch). The output of every command goes to -OutDir\<Label>.txt; the
script prints one PASS/FAIL line per step and exits with the number of FAILs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][string]$Label,
    [int]$Seconds = 60,
    [switch]$Patches,
    [string]$OutDir = (Join-Path $PSScriptRoot 'out')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$s1 = Join-Path $root 'Workspace\tools\bridge\s1.py'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path $OutDir "$Label.txt"
Set-Content -Path $out -Value "# $Label, slot $Slot, $Seconds s, patches=$($Patches.IsPresent), $(Get-Date -Format s)" -Encoding utf8
$fails = 0

function Run([string]$Cmd) {
    $text = & python $s1 --slot $Slot run $Cmd 2>&1 | Out-String
    $code = $LASTEXITCODE
    Add-Content -Path $out -Value "`n## $Cmd (exit $code)`n$text" -Encoding utf8
    if ($code -ne 0 -or $text -match 'MAIN_THREAD_TIMEOUT|unknown command') {
        Write-Host "FAIL $Cmd : exit $code $($text.Trim() -replace '\s+', ' ')"
        $script:fails++
    } else { Write-Host "PASS $Cmd" }
    return $text
}

if ($Patches) { $null = Run 'snitch patches on' }
$null = Run 'snitch start'
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
foreach ($c in @('snitch top', 'snitch sections', 'snitch unattributed', 'snitch counters')) { $null = Run $c }
if ($Patches) { $null = Run 'snitch patches list' }
$null = Run 'snitch report md'
$null = Run 'snitch stop'
Write-Host "SUMMARY label=$Label fails=$fails out=$out"
exit $fails
