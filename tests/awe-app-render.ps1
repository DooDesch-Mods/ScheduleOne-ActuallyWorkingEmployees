<#
Renders every page of the AWE phone app in a running slot and changes one value by a click.

    pwsh -File tests/awe-app-render.ps1 -Slot mptest2 -OutDir <folder>

Per group: click '#group-<name>' through the real raycast, read the pane title back, take a screenshot.
Then the Diagnostics panels (doctor run button), one bool option switched on and off by clicking its
'.opt-switch' ('awe get' must show each value), a vanilla number field showing "Vanilla", plus staying in place
over two clicks, the range in the help line, a typed number below the range set to the minimum, and the Fast preset's
confirm step (Cancel changes nothing, Apply sets the work speeds). Prints PASS or FAIL per check and a SUMMARY line.
The preset check ends with 'awe reset all'.
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

# One value switched on by a click on the row's switch, and off again by a second click.
$group = $ClickKey.Split('.')[0]
$row = 'opt-' + $ClickKey.Replace('.', '-')
S1 run "awe reset $ClickKey" | Out-Null
S1 click awe "#group-$group" | Out-Null
Settle
$before = S1 run "awe get $ClickKey"
$c = S1 click awe "#$row .opt-switch"
Settle
$after = S1 run "awe get $ClickKey"
$cls = (Js "document.querySelector('#$row .opt-switch') ? document.querySelector('#$row .opt-switch').className : '(none)'").Split("`n")[-1].Trim('"', ' ')
$rowCls = (Js "document.getElementById('$row') ? document.getElementById('$row').className : '(none)'").Split("`n")[-1].Trim('"', ' ')
Shot "click-$($ClickKey.Replace('.', '-'))" | Out-Null
Result ($before -match '= Off' -and $after -match '= On') 'click-changes-value' "before='$($before.Split("`n")[-1])' after='$($after.Split("`n")[-1])' click=$($c.Split("`n")[-1])"
Result ($cls -match '\bon\b' -and $rowCls -match '\bchanged\b') 'switch-shows-on' "switch='$cls' row='$rowCls'"
S1 click awe "#$row .opt-switch" | Out-Null
Settle
$back = S1 run "awe get $ClickKey"
Result ($back -match '= Off') 'click-switches-back' "after second click='$($back.Split("`n")[-1])'"
S1 run "awe reset $ClickKey" | Out-Null

# A vanilla number shows the word in its field, and its minus button is disabled (nothing below vanilla).
$vk = 'botanist.walkspeed'
$vrow = 'opt-' + $vk.Replace('.', '-')
S1 run "awe reset $vk" | Out-Null
S1 click awe '#group-botanist' | Out-Null
Settle
$field = (Js "document.querySelector('#$vrow .opt-field') ? document.querySelector('#$vrow .opt-field').value : '(none)'").Split("`n")[-1].Trim('"', ' ')
$minus = (Js "document.querySelector('#$vrow .opt-minus') ? document.querySelector('#$vrow .opt-minus').className : '(none)'").Split("`n")[-1].Trim('"', ' ')
Shot 'botanist-vanilla' | Out-Null
Result ($field -eq 'Vanilla' -and $minus -match '\bdisabled\b') 'vanilla-field' "field='$field' minus='$minus'"

# Plus stays where it is once the value differs from its default (the reset button takes a reserved slot), so two
# clicks in a row both land on plus.
$pk = 'botanist.maxpots'
$prow = 'opt-' + $pk.Replace('.', '-')
$posJs = "(function(){ var b=document.querySelector('#$prow .opt-plus'); if(!b) return '(none)'; var r=b.getBoundingClientRect(); return Math.round(r.left)+','+Math.round(r.top); })()"
S1 run "awe reset $pk" | Out-Null
S1 click awe '#group-property' | Out-Null
Settle
S1 click awe '#group-botanist' | Out-Null
Settle
$p0 = (S1 run "awe get $pk").Split("`n") | Where-Object { $_ -match '=' } | Select-Object -Last 1
$pos0 = (Js $posJs).Split("`n")[-1].Trim('"', ' ')
S1 click awe "#$prow .opt-plus" | Out-Null
Settle
$pos1 = (Js $posJs).Split("`n")[-1].Trim('"', ' ')
S1 click awe "#$prow .opt-plus" | Out-Null
Settle
$p2 = (S1 run "awe get $pk").Split("`n") | Where-Object { $_ -match '=' } | Select-Object -Last 1
Shot 'plus-twice' | Out-Null
$v0 = if ($p0 -match '= (\d+)') { [int]$Matches[1] } else { -99 }
$v2 = if ($p2 -match '= (\d+)') { [int]$Matches[1] } else { -99 }
Result ($pos0 -ne '(none)' -and $pos0 -eq $pos1 -and $v2 -eq $v0 + 2) 'plus-stays-put' "plus at '$pos0' then '$pos1', value '$p0' -> '$p2'"
S1 run "awe reset $pk" | Out-Null

# A change adds "Default: X" on the label line, so neither the row nor the row below it moves. Checked for a number
# row and for a switch row.
function RowBox([string]$row) {
  $js = "(function(){ var r=document.querySelector('#$row'); if(!r) return '(none)'; var a=r.getBoundingClientRect(); var n=r.nextElementSibling; var b=n?n.getBoundingClientRect():null; return Math.round(a.height)+'|'+(b?Math.round(b.top):'end'); })()"
  return (Js $js).Split("`n")[-1].Trim('"', ' ')
}
S1 click awe '#group-property' | Out-Null
Settle
S1 click awe '#group-botanist' | Out-Null
Settle
$h0 = RowBox $prow
S1 click awe "#$prow .opt-plus" | Out-Null
Settle
$h1 = RowBox $prow
$dflt = (Js "document.querySelector('#$prow .head .default') ? document.querySelector('#$prow .head .default').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
Result ($h0 -ne '(none)' -and $h0 -eq $h1 -and $dflt -match '^Default: ') 'number-row-no-jump' "height|next before '$h0' after '$h1', default='$dflt'"
S1 run "awe reset $pk" | Out-Null

$bk = 'general.disableemployeecollision'
$brow = 'opt-' + $bk.Replace('.', '-')
S1 run "awe reset $bk" | Out-Null
S1 click awe '#group-property' | Out-Null
Settle
S1 click awe '#group-general' | Out-Null
Settle
$b0 = RowBox $brow
S1 click awe "#$brow .opt-switch" | Out-Null
Settle
$b1 = RowBox $brow
Result ($b0 -ne '(none)' -and $b0 -eq $b1) 'switch-row-no-jump' "height|next before '$b0' after '$b1'"
S1 run "awe reset $bk" | Out-Null

# Portrait: the label column is narrow there, so a long label is where an added "Default" would wrap.
S1 eval awe "s1.setOrientation('portrait'); s1.orientation" | Out-Null
Settle; Settle
S1 click awe '#back' | Out-Null
Settle
S1 click awe '#group-general' | Out-Null
Settle
$q0 = RowBox $brow
S1 click awe "#$brow .opt-switch" | Out-Null
Settle
$q1 = RowBox $brow
Result ($q0 -ne '(none)' -and $q0 -eq $q1) 'switch-row-no-jump-portrait' "height|next before '$q0' after '$q1'"
S1 run "awe reset $bk" | Out-Null
S1 click awe '#back' | Out-Null
S1 eval awe "s1.setOrientation('landscape'); s1.orientation" | Out-Null
Settle; Settle

# The help line names the range, and a typed number below it is set to the minimum with a green note. The bridge
# cannot type, so the field gets its text and a page-raised change event, which carries only its type (the app then
# reads the field's text).
$ck = 'chemist.chemistrystationtime'
$crow = 'opt-' + $ck.Replace('.', '-')
S1 run "awe reset $ck" | Out-Null
S1 click awe '#group-chemist' | Out-Null
Settle
$help = (Js "document.querySelector('#$crow .help') ? document.querySelector('#$crow .help').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
$typed = (Js "(function(){ var f=document.querySelector('#$crow .opt-field'); if(!f) return 'no field'; f.value='1'; var ev; try { ev=new Event('change'); } catch(e) { ev={type:'change'}; } f.dispatchEvent(ev); return 'sent'; })()").Split("`n")[-1].Trim('"', ' ')
Settle
$cv = (S1 run "awe get $ck").Split("`n") | Where-Object { $_ -match '=' } | Select-Object -Last 1
$note = (Js "document.getElementById('note').className + '|' + document.getElementById('note').textContent").Split("`n")[-1].Trim('"', ' ')
Shot 'typed-below-min' | Out-Null
Result ($help -match '^Range: 2-120\. ') 'range-in-help' "help='$help'"
Result ($cv -match '= 2\b' -and $note -match '^note info\|Set to 2, the minimum\.$') 'typed-clamps-to-min' "typed=$typed value='$cv' note='$note'"
S1 run "awe reset $ck" | Out-Null

# The Fast preset asks first: the first click shows the confirm pair, Cancel leaves every value alone.
S1 run 'awe reset all' | Out-Null
S1 click awe '#group-botanist' | Out-Null
Settle
S1 click awe '#preset-fast' | Out-Null
Settle
$confirm = (Js "!!document.getElementById('preset-fast-confirm') && !!document.getElementById('preset-fast-cancel')").Split("`n")[-1].Trim('"', ' ')
Shot 'preset-fast-confirm' | Out-Null
S1 click awe '#preset-fast-cancel' | Out-Null
Settle
$ws = S1 run 'awe get botanist.workspeed'
$gone = (Js "!document.getElementById('preset-fast-confirm') && !!document.getElementById('preset-fast')").Split("`n")[-1].Trim('"', ' ')
Result ($confirm -eq 'true' -and $gone -eq 'true' -and $ws -notmatch '= 2\b') 'preset-fast-cancel' "confirm shown=$confirm back to Fast=$gone workspeed='$($ws.Split("`n")[-1])'"
S1 click awe '#preset-fast' | Out-Null
Settle
S1 click awe '#preset-fast-confirm' | Out-Null
Settle
$ws = S1 run 'awe get botanist.workspeed'
$count = (Js "document.querySelector('#group-botanist .count') ? document.querySelector('#group-botanist .count').textContent : '(none)'").Split("`n")[-1].Trim('"', ' ')
Shot 'preset-fast-applied' | Out-Null
Result ($ws -match '= 2\b' -and $count -match '^\d+$') 'preset-fast-apply' "workspeed='$($ws.Split("`n")[-1])' botanist count='$count'"
S1 run 'awe reset all' | Out-Null

Write-Host "SUMMARY app-render pass=$pass fail=$fail"
