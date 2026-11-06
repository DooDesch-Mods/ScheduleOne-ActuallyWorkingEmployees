"""With "Work through 4 AM" on, employees keep working while the clock stands at 4:00; with it off they go outside.

Needs a Debug build of AWE and a save with paid employees (template `played`).

    python -I tests/awe-endofday.py --slot mptest [--wait 30]

Rows:
  off-stops        option off, clock at 4:00: at least one employee waits outside, no clock swap happened
  on-swaps         option on: the clock was swapped during employee updates and the "4 AM on day" line was logged
  on-works         option on: fewer employees wait outside than with it off, at least one is kept at work
  clock-stays      the clock still reads 0400 (end of day) afterwards, so plants and the sleep prompt still stop
  no-restore-miss  every swap put 4:00 back
  no-errors        no [Failed] or [Caught] line of work-through-4am in the run

Prints PASS or FAIL per row and exits 1 when one fails.
"""
import argparse
import os
import re
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "Workspace", "tools", "bridge"))
import s1  # noqa: E402

HEAD = re.compile(r"\[awe endofday\] option=(?P<on>\w+) host=(?P<host>\w+) clock=(?P<clock>\d+)(?P<eod> \(end of day\))? swaps=(?P<swaps>\d+) restoreMisses=(?P<miss>\d+)")
ROW = re.compile(r"\[awe endofday\] (?P<n>.+?) \((?P<t>\w+)\): behaviour=(?P<b>.+?) waitOutside=(?P<out>\w+) paid=(?P<paid>\w+) kept=(?P<kept>\w+)")

results = []
seen = []


def check(name, ok, evidence):
    results.append(ok)
    print("%s  %s" % ("PASS" if ok else "FAIL", name))
    for line in evidence if isinstance(evidence, list) else [evidence]:
        print("      " + str(line))


def run(game, command, max_wait=8000):
    answer = game.run(command, max_wait_ms=max_wait, settle_ms=600)
    if not answer.get("ok"):
        raise RuntimeError("%s: %s" % (command, answer))
    lines = [l["text"] for l in answer["result"].get("lines", [])]
    seen.extend(lines)
    if command.startswith("awe set") and any("no option" in t for t in lines):
        raise RuntimeError("%s: %s" % (command, lines))
    return lines


def state(game):
    head, rows = None, []
    for t in run(game, "awe endofday"):
        m = HEAD.search(t)
        if m:
            head = m
            continue
        m = ROW.search(t)
        if m:
            rows.append(m)
    if head is None:
        raise RuntimeError("awe endofday printed no header line (Release build?)")
    return head, rows


def summary(head, rows):
    out = ["clock=%s%s swaps=%s misses=%s" % (head["clock"], head["eod"] or "", head["swaps"], head["miss"])]
    out += ["%s %s behaviour=%s outside=%s paid=%s kept=%s" % (r["n"], r["t"], r["b"], r["out"], r["paid"], r["kept"]) for r in rows]
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--wait", type=int, default=30, help="seconds the employees get after each switch")
    a = ap.parse_args()
    game = s1.Game(a.slot) if a.slot else s1.Game()

    run(game, "awe set general.workthroughendofday false")
    game.world("set_time", {"time": 400})
    time.sleep(a.wait)
    head_off, rows_off = state(game)
    out_off = sum(1 for r in rows_off if r["out"] == "True")
    check("off-stops", head_off["clock"] == "0400" and out_off > 0 and int(head_off["swaps"]) == 0, summary(head_off, rows_off))

    run(game, "awe set general.workthroughendofday true")
    time.sleep(a.wait)
    head_on, rows_on = state(game)
    out_on = sum(1 for r in rows_on if r["out"] == "True")
    kept = sum(1 for r in rows_on if r["kept"] == "True")
    day_line = [t for t in game.log(grep="[work-through-4am] 4 AM on day", lines=5) if t.strip()]
    check("on-swaps", int(head_on["swaps"]) > 0 and len(day_line) > 0, ["swaps=" + head_on["swaps"]] + day_line[-1:])
    check("on-works", out_on < out_off and kept > 0, ["waitOutside off=%d on=%d kept=%d" % (out_off, out_on, kept)] + summary(head_on, rows_on))
    check("clock-stays", head_on["clock"] == "0400" and bool(head_on["eod"]), "clock=" + head_on["clock"] + (head_on["eod"] or ""))
    check("no-restore-miss", head_on["miss"] == "0", "restoreMisses=" + head_on["miss"])

    run(game, "awe set general.workthroughendofday false")
    errs = [t for t in game.log(grep="work-through-4am", lines=200) if "[Failed]" in t or "[Caught]" in t]
    check("no-errors", not errs, errs or "none")

    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
