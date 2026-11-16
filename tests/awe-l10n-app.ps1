<#
Shot set of the AWE phone app in German and English, plus the language checks that need a running game.

    pwsh -File tests/awe-l10n-app.ps1 -Slot mptest2 -OutDir <folder>

Per language ('awe app lang <code>' switches the running game): every group page (landscape) with the pane title read
back and compared with the group button, the Diagnostics panels with the doctor run, the confirm band of the limits
switch, the portrait group list and one portrait page. Checks per language: the pane titles are the group names of that
language, no text of the page is still an English source text that has a German entry (German run), and no box of the
page sticks out of its parent. The German run also reads the template file the mod writes into UserData.
Prints PASS or FAIL per check and a SUMMARY line; the exit code is the number of FAILs. Needs a Debug build
(the 'awe' console) in a loaded save.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][string]$OutDir
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Js([string]$code) { (S1 eval awe $code).Split("`n")[-1].Trim() }
function Shot([string]$name) {
    $p = (S1 shot).Split("`n")[-1].Trim()
    if (-not (Test-Path $p)) { Write-Host "  shot failed for ${name}: $p"; return $null }
    $dst = Join-Path $OutDir "$name.png"
    Copy-Item $p $dst -Force
    Write-Host "  shot $dst"
    $dst
}
function Settle { $t = (Get-Date).AddMilliseconds(1200); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 200 } }

$deJson = Join-Path $PSScriptRoot '..\Assets\l10n\de.json'
$de = Get-Content $deJson -Raw -Encoding UTF8 | ConvertFrom-Json
# English texts that must not show in the German run: a source text with a different German text and no placeholder.
$sources = @($de.PSObject.Properties | Where-Object { $_.Name -ne $_.Value -and $_.Name -notmatch '\{\d' } | ForEach-Object Name)

# Texts of every box of the page, and the boxes that stick out of their parent.
$textsJs = "JSON.stringify(Array.from(document.querySelectorAll('.label,.help,.btn,.name,.pane-title,.pane-sub,.badge-name,.badge-msg,.badge-fix,.card-title,.card-note,.card-sub,.chip,.limits-label,.default,.hint,.note,.title,.role,#band-text,.value,.empty')).map(function(e){return (e.textContent||'').trim()}).filter(function(t){return t}))"
$overflowJs = "(function(){ var bad=[]; var els=document.querySelectorAll('#screen *'); for (var i=0;i<els.length;i++){ var e=els[i]; var p=e.parentNode; if(!p||!p.getBoundingClientRect||e.className==='knob') continue; var a=e.getBoundingClientRect(), b=p.getBoundingClientRect(); if(a.width===0||b.width===0) continue; if(a.right>b.right+2||a.left<b.left-2) bad.push((e.className||e.tagName)+':'+(e.textContent||'').trim().slice(0,30)); } return bad.length+'|'+bad.slice(0,6).join(' ; '); })()"

foreach ($lang in 'de', 'en') {
    Write-Host "--- $lang"
    $sw = S1 run "awe app lang $lang"
    Result ($sw -match "language switched to '$lang'") "lang-$lang-switch" ($sw.Split("`n")[-1])
    S1 run 'awe reset all' | Out-Null
    $open = S1 run 'awe app open'
    Result ($open -match "app 'awe' opened") "lang-$lang-open" ($open.Split("`n")[-1])
    Settle; Settle

    $ids = Js "Array.from(document.getElementById('groups').children).map(function(e){return e.getAttribute('id')}).join(',')"
    $groups = @($ids.Trim('"').Split(',') | Where-Object { $_ -match '^group-' })
    Result ($groups.Count -ge 5) "lang-$lang-groups" "found $($groups.Count)"

    $seen = New-Object System.Collections.Generic.HashSet[string]
    $overflow = @()
    foreach ($g in $groups) {
        S1 click awe "#$g" | Out-Null
        Settle
        $title = (Js "document.querySelector('.pane-title') ? document.querySelector('.pane-title').textContent : '(none)'").Trim('"')
        $name = (Js "document.querySelector('#$g .name') ? document.querySelector('#$g .name').textContent : '(none)'").Trim('"')
        Shot "l10n-$lang-$($g.Substring(6))" | Out-Null
        Result ($title -eq $name -and $title -ne '(none)') "lang-$lang-title-$($g.Substring(6))" "title='$title' button='$name'"
        $of = Js $overflowJs
        if (-not $of.Trim('"').StartsWith('0|')) { $overflow += "$($g.Substring(6)): $($of.Trim('"'))" }
        foreach ($t in (Js $textsJs | ConvertFrom-Json)) { [void]$seen.Add($t) }
    }

    # Diagnostics with the three panels on.
    $diag = 'diagnostics.enablesetupdoctor', 'diagnostics.showproblembadges', 'diagnostics.enableanalytics'
    S1 run "awe set $($diag[0]) true" | Out-Null
    S1 click awe '#group-diagnostics' | Out-Null
    Settle
    S1 click awe '#diag-doctor-run' | Out-Null
    Settle; Settle
    Shot "l10n-$lang-diagnostics-doctor" | Out-Null
    $sum = (Js "document.getElementById('diag-doctor-summary') ? document.getElementById('diag-doctor-summary').textContent : '(none)'").Trim('"')
    Result ($sum -match '\d+ s (ago|her)$|vor \d+ s$') "lang-$lang-doctor-summary" "summary='$sum'"
    foreach ($o in $diag[1..2]) { S1 run "awe set $o true" | Out-Null }
    S1 click awe '#diag-refresh' | Out-Null
    Settle
    Shot "l10n-$lang-diagnostics-badges" | Out-Null
    $of = Js $overflowJs
    if (-not $of.Trim('"').StartsWith('0|')) { $overflow += "diagnostics: $($of.Trim('"'))" }
    foreach ($t in (Js $textsJs | ConvertFrom-Json)) { [void]$seen.Add($t) }
    foreach ($o in $diag) { S1 run "awe reset $o" | Out-Null }

    # The confirm band of the limits switch, and the Fast preset's confirm pair.
    S1 click awe '#group-botanist' | Out-Null
    Settle
    S1 click awe '#limits-bar .limits-switch' | Out-Null
    Settle
    Shot "l10n-$lang-limits-confirm" | Out-Null
    foreach ($t in (Js $textsJs | ConvertFrom-Json)) { [void]$seen.Add($t) }
    S1 click awe '#limits-cancel' | Out-Null
    Settle
    S1 click awe '#preset-fast' | Out-Null
    Settle
    Shot "l10n-$lang-preset-confirm" | Out-Null
    S1 click awe '#preset-fast-cancel' | Out-Null
    Settle

    # Portrait: the group list, then one page.
    S1 eval awe "s1.setOrientation('portrait'); s1.orientation" | Out-Null
    Settle; Settle
    S1 click awe '#back' | Out-Null
    Settle
    Shot "l10n-$lang-portrait-list" | Out-Null
    $of = Js $overflowJs
    if (-not $of.Trim('"').StartsWith('0|')) { $overflow += "portrait list: $($of.Trim('"'))" }
    S1 click awe '#group-reliability' | Out-Null
    Settle
    Shot "l10n-$lang-portrait-reliability" | Out-Null
    $of = Js $overflowJs
    if (-not $of.Trim('"').StartsWith('0|')) { $overflow += "portrait reliability: $($of.Trim('"'))" }
    S1 click awe '#back' | Out-Null
    S1 eval awe "s1.setOrientation('landscape'); s1.orientation" | Out-Null
    Settle; Settle

    Result ($overflow.Count -eq 0) "lang-$lang-no-box-outside-parent" ($overflow -join ' / ')
    if ($lang -eq 'de') {
        $left = @($sources | Where-Object { $seen.Contains($_) })
        Result ($left.Count -eq 0) 'lang-de-no-english-left' "$($left.Count) of $($seen.Count) texts still English: $(($left | Select-Object -First 6) -join ' | ')"
        Result ($seen.Contains('Mitarbeiter') -or $seen.Count -gt 0) 'lang-de-texts-read' "$($seen.Count) distinct texts read"
    } else {
        $ger = @($seen | Where-Object { $_ -match 'Allgemein|Zuverlässigkeit|Diagnose' })
        Result ($ger.Count -eq 0) 'lang-en-no-german' "$($ger.Count) German texts: $($ger -join ' | ')"
    }
}

# The template file the mod writes for translators.
$n = $Slot -replace '^mptest', ''
$root = Join-Path 'F:\Projects\Mods\Schedule1' ("_MPTest$n" + '\Schedule I')
$tpl = Join-Path $root 'UserData\DooDesch\Localization\ActuallyWorkingEmployees\_template.de.json'
if (Test-Path $tpl) {
    $t = Get-Content $tpl -Raw -Encoding UTF8 | ConvertFrom-Json
    $count = @($t.PSObject.Properties).Count
    Result ($count -ge 300 -and $t.'Run check' -eq $de.'Run check') 'template-written' "$count entries, Run check='$($t.'Run check')' ($tpl)"
} else {
    Result $false 'template-written' "missing: $tpl"
}
$lines = S1 log --grep 'L10n'
Result ($lines -match '\[L10n\] \d+ German texts registered') 'german-table-registered' (($lines -split "`n" | Where-Object { $_ -match '\[L10n\]' } | Select-Object -Last 1))

S1 run 'awe app lang de' | Out-Null
S1 run 'awe reset all' | Out-Null
Write-Host "SUMMARY awe-l10n-app pass=$pass fail=$fail"
exit $fail
