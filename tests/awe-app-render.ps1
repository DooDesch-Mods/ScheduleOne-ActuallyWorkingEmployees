<#
Renders every page of the AWE phone app in a running slot and changes one value by a click.

    pwsh -File tests/awe-app-render.ps1 -Slot mptest2 -OutDir <folder>

Per group: click '#group-<name>' through the real raycast, read the pane title back, take a screenshot.
Then the Diagnostics panels (doctor run button) and one bool option switched on by clicking its '.opt-on'
button; 'awe get' must show the new value. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$ClickKey = 'general.disableemployeecollision'
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Js([string]$code) { S1 eval awe $code }
function Shot([string]$name) {
    $p = (S1 shot).Split("`n")[-1].Trim()
    if (-not (Test-Path $p)) { Write-Host "  shot failed for ${name}: $p"; return $null }
    $dst = Join-Path $OutDir "app-$name.png"
    Copy-Item $p $dst -Force
    Write-Host "  shot $dst"
    $dst
}
function Settle { $t = (Get-Date).AddMilliseconds(1200); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 200 } }

$open = S1 run 'awe app open'
Result ($open -match "app 'awe' opened") 'app-open' ($open.Split("`n")[-1])
Settle

$ids = Js "Array.from(document.getElementById('groups').children).map(function(e){return e.getAttribute('id')}).join(',')"
$groups = @($ids.Split("`n")[-1].Trim('"', ' ').Split(',') | Where-Object { $_ -match '^group-' })
Result ($groups.Count -ge 5) 'app-groups' "found $($groups.Count): $($groups -join ' ')"

foreach ($g in $groups) {
    $c = S1 click awe "#$g"
    Settle
    $title = (Js "document.querySelector('.pane-title') ? document.querySelector('.pane-title').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
    $rows = (Js "document.getElementById('rows').children.length").Split("`n")[-1].Trim('"', ' ')
    $want = $g.Substring(6)
    Shot $g | Out-Null
    Result ($title.ToLowerInvariant() -eq $want) "page-$want" "title='$title' rows=$rows click=$($c.Split("`n")[-1])"
}

# Diagnostics: run the doctor from its button and read the summary back.
$diagOpts = 'diagnostics.enablesetupdoctor', 'diagnostics.showproblembadges', 'diagnostics.enableanalytics'
if ($groups -contains 'group-diagnostics') {
    # The doctor runs first, with the badges off: the badge list puts the Run check button below the visible part of
    # the pane, and a raycast click there hits nothing (the page script cannot scroll the pane).
    foreach ($o in $diagOpts) { S1 run "awe reset $o" | Out-Null }
    S1 run "awe set $($diagOpts[0]) true" | Out-Null
    S1 click awe '#group-diagnostics' | Out-Null
    Settle
    S1 click awe '#diag-refresh' | Out-Null
    Settle
    $c = S1 click awe '#diag-doctor-run'
    Settle; Settle
    $sum = (Js "document.getElementById('diag-doctor-summary') ? document.getElementById('diag-doctor-summary').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
    $found = (Js "document.querySelectorAll('.finding').length").Split("`n")[-1].Trim('"', ' ')
    Shot 'diagnostics-doctor' | Out-Null
    # The summary ends with the age of the last run; only a run started by this click is younger than 15 s.
    $age = if ($sum -match '(\d+) s ago$') { [int]$Matches[1] } else { -1 }
    Result ($age -ge 0 -and $age -lt 15) 'diag-doctor-button' "age=${age}s summary='$sum' findings=$found click=$($c.Split("`n")[-1])"

    foreach ($o in $diagOpts[1..2]) { S1 run "awe set $o true" | Out-Null }
    S1 click awe '#diag-refresh' | Out-Null
    Settle
    $badges = (Js "document.getElementById('diag-badges-count') ? document.getElementById('diag-badges-count').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
    Shot 'diagnostics-badges' | Out-Null
    Result ($badges -match '^\d+ of \d+') 'diag-badges' "badges='$badges'"
    # A missing destination needs the player, so its badge is never shown as OK.
    $wrong = (Js "Array.from(document.querySelectorAll('.badge-row')).filter(function(r){ var m=r.querySelector('.badge-msg'); return r.className.indexOf('sev-info')>=0 && m && m.textContent.toLowerCase().indexOf('no destination')>=0 }).length").Split("`n")[-1].Trim('"', ' ')
    Result ($wrong -eq '0') 'diag-badges-no-destination-not-ok' "info rows naming 'no destination': $wrong"
    foreach ($o in $diagOpts) { S1 run "awe reset $o" | Out-Null }
}

# One value changed by a click on the row's On button.
$group = $ClickKey.Split('.')[0]
$row = 'opt-' + $ClickKey.Replace('.', '-')
S1 run "awe reset $ClickKey" | Out-Null
S1 click awe "#group-$group" | Out-Null
Settle
$before = S1 run "awe get $ClickKey"
$c = S1 click awe "#$row .opt-on"
Settle
$after = S1 run "awe get $ClickKey"
Shot "click-$($ClickKey.Replace('.', '-'))" | Out-Null
Result ($before -match '= Off' -and $after -match '= On') 'click-changes-value' "before='$($before.Split("`n")[-1])' after='$($after.Split("`n")[-1])' click=$($c.Split("`n")[-1])"
S1 run "awe reset $ClickKey" | Out-Null

Write-Host "SUMMARY app-render pass=$pass fail=$fail"
