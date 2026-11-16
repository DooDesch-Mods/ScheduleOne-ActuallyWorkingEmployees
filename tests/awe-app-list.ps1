<#
In-game check that the phone app's "list" call answers: opens the app in a running slot, reads the groups and rows
back from the page, and checks the log has no "Method not found" (MelonLoader 0.7.0 lacks JToken.ToString(Formatting)).

    pwsh -File tests/awe-app-list.ps1 -Slot <slot> -OutDir <folder>

Prints PASS or FAIL per check; the screenshot of the filled app lands in OutDir. Starts and stops no game.
#>
param([Parameter(Mandatory)][string]$Slot, [Parameter(Mandatory)][string]$OutDir)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$env:PYTHONIOENCODING = 'utf-8'
$script:fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python -I $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Last([string]$t) { $t.Split("`n")[-1].Trim('"', ' ', "`r") }

S1 run 'awe app lang en' | Out-Null   # the checks below read English texts; the OS language may be German
$open = S1 run 'awe app open'
Result ($open -match "app 'awe' opened") 'app-open' (Last $open)
Start-Sleep -Seconds 3

$groups = [int](Last (S1 eval awe "document.getElementById('groups').children.length"))
Result ($groups -ge 5) 'app-groups-filled' "$groups groups"
$null = S1 click awe '#group-general'
Start-Sleep -Seconds 2
$rows = [int](Last (S1 eval awe "document.getElementById('rows').querySelectorAll('.opt-switch, input, .row').length"))
Result ($rows -ge 1) 'app-rows-filled' "$rows controls"
$err = Last (S1 eval awe "document.body.textContent.indexOf('Something went wrong') >= 0 ? 'ERRORTEXT' : 'clean'")
Result ($err -eq 'clean') 'no-something-went-wrong' $err

$log = S1 log --grep 'Method not found' -n 400
Result (-not ($log -match 'Method not found')) 'log-no-method-not-found' $(if ($log -match 'Method not found') { Last $log } else { 'none' })

$p = Last (S1 shot)
if (Test-Path $p) { $dst = Join-Path $OutDir 'awe-app-list.png'; Copy-Item $p $dst -Force; Write-Host "shot $dst" } else { Result $false 'shot' $p }
if ($script:fail) { Write-Host "SUMMARY $script:fail FAIL"; exit 1 }
Write-Host 'SUMMARY all PASS'
