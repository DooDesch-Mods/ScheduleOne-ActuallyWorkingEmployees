<#
The keyboard comes back after a number is changed in the AWE phone app.

    pwsh -File tests/awe-app-typing.ps1 -Slot mptest2 [-OutDir <folder>]

Opens the app, finds the first editable number field, puts the caret in it with a click through the real raycast
('awe app typing' must then read IsTyping=True), and commits a new value through the page's own commit(), which is
what Enter in the field runs. The row is rebuilt and the field is gone; 'awe app typing' must read IsTyping=False
again, because GameInput.HandleExitInputs ignores ESC while IsTyping is true and the player cannot leave the app.
Resets the option at the end. Prints PASS or FAIL per check and a SUMMARY line.
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
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Js([string]$code) { (S1 eval awe $code).Split("`n")[-1].Trim('"', ' ') }
function Typing { $t = S1 run 'awe app typing'; if ($t -match 'IsTyping=(\w+)') { $Matches[1] } else { "unreadable: $($t.Split("`n")[-1])" } }
function Settle { $t = (Get-Date).AddMilliseconds(1200); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 200 } }
function Finish { Write-Host "SUMMARY app-typing pass=$pass fail=$fail"; exit ([int]($fail -gt 0)) }

$open = S1 run 'awe app open'
Result ($open -match "app 'awe' opened") 'app-open' ($open.Split("`n")[-1])
Settle

# The first group whose page shows an editable number field.
$ids = Js "Array.from(document.getElementById('groups').children).map(function(e){return e.getAttribute('id')}).join(',')"
$row = $null; $key = $null
foreach ($g in @($ids.Split(',') | Where-Object { $_ -match '^group-' })) {
    S1 click awe "#$g" | Out-Null
    Settle
    $row = Js "(function(){ var f=document.querySelector('#rows .opt-field'); if(!f) return ''; var n=f; while(n && !/^opt-/.test(n.getAttribute('id')||'')) n=n.parentNode; return n ? n.getAttribute('id') : ''; })()"
    if ($row -match '^opt-') { break }
}
if (-not ($row -match '^opt-')) { Result $false 'number-field' 'no group shows an editable number field'; Finish }
$key = Js "(function(){ for (var g of app.groups) for (var o of g.options) if (rowId(o)==='$row') return o.key; return ''; })()"
Result ($key -ne '') 'number-field' "row=$row key=$key"
S1 run "awe reset $key" | Out-Null
Settle

$before = Typing
Result ($before -eq 'False') 'typing-before' "IsTyping=$before"

$c = S1 click awe "#$row .opt-field"
Settle
$focused = Typing
Result ($focused -eq 'True') 'click-puts-caret-in-field' "IsTyping=$focused click=$($c.Split("`n")[-1])"

# The Enter handler of the field runs commit(o, value, row); call it the same way with the next step up.
$commit = Js "(function(){ var n=document.getElementById('$row'); for (var g of app.groups) for (var o of g.options) if (o.key==='$key') { var v=stepped(o,1); commit(o, v, n); return 'committed '+v; } return 'option not found'; })()"
Settle
$after = Typing
$gone = Js "document.getElementById('$row') ? 'row rebuilt' : 'row missing'"
$value = S1 run "awe get $key"
Result ($commit -match '^committed') 'commit-ran' "$commit; $gone; $($value.Split("`n")[-1])"
Result ($after -eq 'False') 'keyboard-released-after-commit' "IsTyping=$after (True = ESC cannot close the app)"
if ($OutDir) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $p = (S1 shot).Split("`n")[-1].Trim()
    if (Test-Path $p) { $dst = Join-Path $OutDir 'app-typing-after-commit.png'; Copy-Item $p $dst -Force; Write-Host "  shot $dst" }
    else { Write-Host "  shot failed: $p" }
}

S1 run "awe reset $key" | Out-Null
Finish
