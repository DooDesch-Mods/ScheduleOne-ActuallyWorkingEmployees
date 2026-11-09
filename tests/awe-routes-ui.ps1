<#
The handler route list shows one row per route up to packager.maxroutes, not only the five rows of the prefab.

    pwsh -File tests/awe-routes-ui.ps1 -Slot mptest4 [-OutDir <folder>]

Needs a Debug build of AWE and a save with a hired handler (template 'played'). 'awe routeui <n>' gives the first
handler n routes and opens its clipboard panel; the panel line reports rows (row objects), shown (active rows) and add
(Add button visible).
  rows-grow      packager.maxroutes 16, 14 routes: 16 rows, 14 shown, Add button visible
  scroll-view    the rows sit in a scroll view whose content is taller than the viewport; on open row 1 is the first
                 visible row and the Add button is scrolled out of view
  scroll-bottom  scrolled to the end, row 14 and the Add button lie inside the viewport
  delete-copy    the delete button of row 12 (a copied row) removes one route: 13 shown
  full           16 routes: 16 shown, Add button hidden
Closes the panel and resets packager.maxroutes at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$OutDir
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Run([string]$cmd) { (& python $s1 --slot $Slot run $cmd 2>&1 | Out-String) }
function Panel([string]$out) {
    $m = [regex]::Matches($out, 'routes=(\d+) max=(\d+) rows=(\d+) shown=(\d+) add=(\d)')
    if ($m.Count -eq 0) { return $null }
    $g = $m[$m.Count - 1].Groups
    [pscustomobject]@{ Routes = [int]$g[1].Value; Max = [int]$g[2].Value; Rows = [int]$g[3].Value; Shown = [int]$g[4].Value; Add = [int]$g[5].Value }
}

$null = Run 'awe set packager.maxroutes 16'
# The balance sync writes the limit into the route fields once a second.
$p = $null
for ($i = 0; $i -lt 6; $i++) {
    $out = Run 'awe routeui 14'
    $p = Panel $out
    if ($p -and $p.Max -eq 16) { break }
    Start-Sleep -Milliseconds 700
}
if (-not $p) { Write-Host "FAIL panel no panel line: $out"; Write-Host "SUMMARY 0 pass, 1 fail"; exit 1 }
# A save from an earlier run can hold more than 14 routes; delete the last ones down to 14.
for ($i = 0; $p -and $p.Routes -gt 14 -and $i -lt 8; $i++) { $p = Panel (Run "awe routeui delete $($p.Routes)") }
Result ($p.Rows -ge 16 -and $p.Shown -eq 14 -and $p.Add -eq 1) 'rows-grow' "routes=$($p.Routes) max=$($p.Max) rows=$($p.Rows) shown=$($p.Shown) add=$($p.Add)"
# Routes added while a panel is open scroll its list to the end; a fresh open starts at the top.
$out = Run 'awe routeui'
if ($OutDir) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    Start-Sleep -Milliseconds 500
    $shot = ((& python $s1 --slot $Slot shot 2>&1 | Out-String).Trim()).Split("`n")[-1].Trim()
    if (Test-Path $shot) { Copy-Item $shot (Join-Path $OutDir 'routes-14-of-16.png'); Write-Host "  shot $(Join-Path $OutDir 'routes-14-of-16.png')" }
    else { Write-Host "  shot failed: $shot" }
}

$rx = 'scroll=1 view=(\d+) content=(\d+) pos=([\d.]+) y=(-?\d+) first=(\d+) last=(\d+) addin=(-?\d)'
$m = [regex]::Match($out, $rx)
Result ($m.Success -and [int]$m.Groups[2].Value -gt [int]$m.Groups[1].Value -and $m.Groups[5].Value -eq '1' -and $m.Groups[7].Value -eq '0') 'scroll-view' $m.Value
$out = Run 'awe routeui bottom'
$m = [regex]::Match($out, $rx)
Result ($m.Success -and $m.Groups[6].Value -eq '14' -and $m.Groups[7].Value -eq '1') 'scroll-bottom' $m.Value
if ($OutDir) {
    Start-Sleep -Milliseconds 500
    $shot = ((& python $s1 --slot $Slot shot 2>&1 | Out-String).Trim()).Split("`n")[-1].Trim()
    if (Test-Path $shot) { Copy-Item $shot (Join-Path $OutDir 'routes-bottom.png'); Write-Host "  shot $(Join-Path $OutDir 'routes-bottom.png')" }
    else { Write-Host "  shot failed: $shot" }
}
$null = Run 'awe routeui top'

$out = Run 'awe routeui delete 12'
$p = Panel $out
Result ($p -and $p.Routes -eq 13 -and $p.Shown -eq 13) 'delete-copy' ($out -split "`n" | Where-Object { $_ -match 'delete row' } | Select-Object -Last 1)

$out = Run 'awe routeui 16'
$p = Panel $out
Result ($p -and $p.Shown -eq 16 -and $p.Add -eq 0) 'full' "routes=$($p.Routes) shown=$($p.Shown) add=$($p.Add)"

$null = Run 'awe routeui close'
$null = Run 'awe reset packager.maxroutes'
Write-Host "SUMMARY $pass pass, $fail fail"
if ($fail -gt 0) { exit 1 }
