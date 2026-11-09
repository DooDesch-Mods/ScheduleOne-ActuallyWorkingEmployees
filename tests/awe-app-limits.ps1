<#
The "Allow values outside the limits" switch in the header of the AWE phone app, by click.

    pwsh -File tests/awe-app-limits.ps1 -Slot mptest4 -OutDir <folder>

Needs a Debug build and a loaded save, as host or singleplayer.
  confirm-shown   clicking the switch shows Allow and Cancel and the risk notice; nothing is set yet
  cancel          Cancel leaves general.allowoutofrange off
  allow           Allow turns it on; the band says values outside the limits are allowed
  row-outside     botanist.maxpots 40 (above 32): the row is marked as outside and its help names the limits
  plus-free       the plus button of that row is not disabled at 40
  switch-off      clicking the switch again turns it off without a confirm; botanist.maxpots acts as 32
Resets general.allowoutofrange and botanist.maxpots at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][string]$OutDir
)
$s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Js([string]$code) { (S1 eval awe $code).Split("`n")[-1].Trim('"', ' ') }
function Get([string]$key) { $o = S1 run "awe get $key"; if ($o -match "$([regex]::Escape($key)) = (\S+)") { $Matches[1] } else { "?($($o.Split("`n")[-1]))" } }
function Settle { $t = (Get-Date).AddMilliseconds(1200); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 200 } }
function Shot([string]$name) {
    $p = (S1 shot).Split("`n")[-1].Trim()
    if (-not (Test-Path $p)) { Write-Host "  shot failed for ${name}: $p"; return }
    $dst = Join-Path $OutDir "limits-$name.png"
    Copy-Item $p $dst -Force
    Write-Host "  shot $dst"
}

S1 run 'awe reset general.allowoutofrange' | Out-Null
S1 run 'awe reset botanist.maxpots' | Out-Null
$open = S1 run 'awe app open'
if ($open -notmatch "app 'awe' opened") { Write-Host "FAIL app-open $($open.Split("`n")[-1])"; Write-Host 'SUMMARY 0 pass, 1 fail'; exit 1 }
Settle
S1 click awe '#group-botanist' | Out-Null
Settle

S1 click awe '#limits .limits-switch' | Out-Null
Settle
$confirm = Js "(document.getElementById('limits-confirm') ? 'yes' : 'no') + '|' + (document.getElementById('limits-cancel') ? 'yes' : 'no') + '|' + document.getElementById('band').className + '|' + document.getElementById('band-text').textContent"
Shot 'confirm'
$v = Get 'general.allowoutofrange'
Result ($confirm -match '^yes\|yes\|band danger\|Values outside the limits are untested' -and $v -match '^(false|False|off)$') 'confirm-shown' "dom='$confirm' value=$v"

S1 click awe '#limits-cancel' | Out-Null
Settle
$v = Get 'general.allowoutofrange'
$gone = Js "document.getElementById('limits-confirm') ? 'shown' : 'gone'"
Result ($v -match '^(false|False|off)$' -and $gone -eq 'gone') 'cancel' "value=$v confirm=$gone"

S1 click awe '#limits .limits-switch' | Out-Null
Settle
S1 click awe '#limits-confirm' | Out-Null
Settle
$v = Get 'general.allowoutofrange'
$band = Js "document.getElementById('band').className + '|' + document.getElementById('band-text').textContent + '|' + document.querySelector('#limits .limits-switch').className"
Result ($v -match '^(true|True|on)$' -and $band -match '^band\|Values outside the limits are allowed\.\|.*\bon\b') 'allow' "value=$v dom='$band'"

S1 run 'awe set botanist.maxpots 40' | Out-Null
S1 click awe '#group-botanist' | Out-Null
Settle
$row = Js "var r=document.getElementById('opt-botanist-maxpots'); r ? r.className + '|' + r.querySelector('.help').textContent : '(no row)'"
Shot 'outside'
Result ($row -match '\boutside\b' -and $row -match '\|Outside the limits \d+-32\.') 'row-outside' "row='$row'"
$plus = Js "var b=document.querySelector('#opt-botanist-maxpots .opt-plus'); b ? b.className : '(no button)'"
Result ($plus -notmatch 'disabled' -and $plus -ne '(no button)') 'plus-free' "plus='$plus'"

S1 click awe '#limits .limits-switch' | Out-Null
Settle
$v = Get 'general.allowoutofrange'
$pots = Get 'botanist.maxpots'
$confirmNow = Js "document.getElementById('limits-confirm') ? 'shown' : 'none'"
Shot 'off'
Result ($v -match '^(false|False|off)$' -and $pots -eq '32' -and $confirmNow -eq 'none') 'switch-off' "value=$v maxpots=$pots confirm=$confirmNow"

S1 run 'awe reset botanist.maxpots' | Out-Null
S1 run 'awe reset general.allowoutofrange' | Out-Null
Write-Host "SUMMARY $pass pass, $fail fail"
if ($fail -gt 0) { exit 1 }
