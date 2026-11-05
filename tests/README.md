# AWE tests

Re-runnable PASS/FAIL scripts for Actually Working Employees. They drive a running Debug build through the dev console
(`awe ...`) and the bridge; they never start or stop a game. Run from the repo root of the worktree whose DLL is deployed:
the expected option counts are read from `Config/` of the checkout the script sits in.

## Start a slot

```
pwsh -File Workspace/tools/session/session.ps1 -Build ActuallyWorkingEmployees -App <app>      # single-player, save 5
pwsh -File Workspace/tools/session/session.ps1 -Coop -Build ActuallyWorkingEmployees          # host + client pair
pwsh -File Workspace/tools/session/session.ps1 -Profile main -Build ActuallyWorkingEmployees   # mods like D:, the final run
```

`python Workspace/tools/slots/slots.py` shows the slot names. Use the default Debug config: Release has no dev console.

## Run

```
pwsh -File tests/awe-check.ps1 -Slot mptest2                      # all single-player checks
pwsh -File tests/awe-check.ps1 -Slot mptest2 -Only 'opt-*,fix1-*'  # wildcards, comma separated
pwsh -File tests/awe-check.ps1 -List                               # check names
pwsh -File tests/awe-coop.ps1 -HostSlot mptest2 -ClientSlot mptest3
```

One `PASS <name>` / `FAIL <name>: <reason>` line per check (`SKIP` for an unfilled placeholder), then `SUMMARY`.
The exit code is the number of FAILs. Waits are on log lines, `/health` and `wait-ready`, each poll step at most 5-10 s.

## Files

- `awe-check.ps1` - the check table (one `New-Row` per check) and the runner.
- `awe-coop.ps1` - host/client sync checks.
- `awe-lib.ps1` - helpers: `Invoke-Awe` (s1.py run), log reads, bridge calls, background waits.
- `awe_bridge.py` - bridge calls s1.py has no verb for (`health`, `save_now`, `return_to_menu`, `load_save`).

## Adding a check

```powershell
$rows.Add((New-Row 'fix1-example' -Finally 'awe reset some.key' -Steps @(
    New-Step -Cmd 'awe set some.key 5' -Expect 'some\.key = 5'                 # command, regex that must appear
    New-Step -Cmd 'awe fixdump' -Until 'done' -Expect 'stuck=0' -Forbid 'stuck=[1-9]'
    New-Step -Log '[FixOne]' -Expect 'warped'                                  # grep the slot log instead
)))
```

A step fails when a wanted regex is missing, a forbidden one is present, or the mod logged an error line while it ran.
`-Check { param($result, $texts) ... }` returns a reason string for anything a regex cannot say. `-Run { param($Slot) }`
takes a free-form body that returns `$null` or a reason. Replace a placeholder by swapping `-Pending` for `-Steps`.

## awe-check.ps1 checks

| Check | Proves |
|---|---|
| `mod-loaded` | boot log has "Actually Working Employees v<ver> ... N options" and N equals the options declared in `Config/` |
| `options-registered` | `[Options] N options in 9 groups` with no unknown group, duplicate key or missing entry |
| `features-boot` | `[Features] N active: ...` line exists |
| `awehelp`, `awe-help` | both list every base command and all group names |
| `list-count` | `awe list` prints as many option lines as the `N option(s).` footer and the source declare |
| `list-group-<group>` | each of the 9 groups lists exactly its source count, only its own keys |
| `list-bad-group`, `console-errors`, `key-ignores-case` | unknown group/key/subcommand and missing arguments warn instead of failing silently; keys ignore case |
| `opt-bool`, `opt-int`, `opt-int-vanilla`, `opt-float`, `opt-choice` | get/set/reset round trip per type; out-of-range, wrong-type and bad-choice values are rejected and leave the value; boundary values and the -1 vanilla sentinel are accepted; aliases (`on/off/yes`, `vanilla`, any choice case) work |
| `reset-all` | `awe reset all` resets what was changed and `awe status` then shows `changed=0` |
| `preset-fast` | `awe preset fast` sets the four work speeds to 2 and the four walk speeds to 6 and leaves the wage at vanilla; a second run changes nothing; an unknown preset warns; `awe reset all` takes all eight back to vanilla |
| `status` | the header's feature count equals the feature lines, the boot log's feature names, and `-ExpectFeatures` |
| `persist-reload` | a set value survives `save_now`, return to menu, `load_save` and `wait-ready` in the same game |
| `fix1-todo` .. `fix6-todo`, `fix7-packager-routes`, `fix8-harvest-time`, `bal-todo`, `coop-sync`, `app-todo` | placeholders (SKIP) for the feature checks |
| `log-no-failed` | no `[Failed]` line and no failed option reset in the whole log, so run it last |

`persist-reload` does not cover a restart; `awe-prefs-persist.ps1` does.

## Scripts that start the game themselves

| Script | Proves |
|---|---|
| `awe-prefs-persist.ps1 -Slot <slot> [-Build <name>]` | a changed option survives a restart while `MelonPreferences.cfg` cannot be saved (read-only, the state another mod's failed save leaves): AWE writes `UserData/ActuallyWorkingEmployees.cfg`, takes values over from the shared file on the first start, and the game is quit by closing its window so MelonLoader's quit save runs |
| `awe-stuckwalk.ps1 -Slot <slot> [-Build <name>]` | a walk that never advances (`awe standstill freeze`: the agent keeps its path, `IsMoving` stays true) stays stuck with Fix 1 off and is stopped by Fix 1 on, after which the employee moves on |
| `python -I awe-warpfloor.py --slot <slot>` | in the reporter's Sweatshop save (template `mrmonkey-dock`, with Based and MapExpansions): the old warp target of a floor spot under furniture is the roof, the new one stays on the floor, and an employee put on the roof is warped back down by Fix 1 |

`-Build` takes a catalog name (main checkout) or the worktree folder name, not a full path.

## awe-coop.ps1 checks

Start the pair with `session.ps1 -Coop`, load the world on both, join the lobby. The rows share state, so run them in
order (or all).

| Check | Proves |
|---|---|
| `roles` | `awe status` shows `role=host` and `role=client` |
| `sync-host-sets-client-locked` | host values of a Float, Int, Choice and Bool option reach the client as `host=<value>` and `read-only`; log has a new `[CoopSync] published` on the host and `applied the host's options` on the client |
| `sync-client-cannot-set` | the client's `awe set` and `awe reset` are refused and the host value stays |
| `sync-readback-and-hash` | `awe sync` on both: `readback_fail=0`, `inLobby=True`, equal hashes, the keys in the decoded line |
| `sync-host-reset-client-follows` | after the host's reset the client drops the host values, the log shows `cleared`/`applied`, and the hashes match again |
| `sync-no-failures-in-log` | no `[Failed]` line and no `[CoopSync]` problem line on either side |
| `sync-feature-todo` | placeholder |
