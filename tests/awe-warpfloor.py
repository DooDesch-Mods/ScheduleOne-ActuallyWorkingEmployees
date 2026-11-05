"""A warp after endless partial walks lands on the floor of the destination, never on a roof above it.

Needs a Debug build of AWE and a save where a roof sits a few metres above a floor (the reporter's Sweatshop save,
template `mrmonkey-dock`, with Based and MapExpansions).

    python -I tests/awe-warpfloor.py --slot mptest [--roof-wait 120]

Rows:
  old-lands-on-roof     the old warp target (nearest point of any NavMesh within 5 m) of a floor spot under furniture
                        lies more than 2 m above it (the reproduction)
  new-stays-on-floor    the new warp target of the same spots lies within 1 m of their height
  put-on-roof           an employee on a work task was moved onto the roof point
  roof-employee-warped  Fix 1 warped that employee away from the roof by itself (partial walks from the roof island)
  roof-employee-down    afterwards the employee stands within 1 m of the height of its destination, not on the roof

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

# Floor spots of the reporter's Sweatshop: the old Based waiting places under a plant pot and a locker. Floor y -0.2,
# roof y 3.05 above them.
SPOTS = [(-58.5, 0.0, 136.9), (-55.2, 0.0, 136.8)]
ROOF = (-58.5, 3.05, 136.9)

PROBE = re.compile(r"\[Fix1\] probe (?P<p>[-\d.,]+): nearest any NavMesh (?P<old>[-\d.,]+|none), warp lands (?P<new>[-\d.,]+|refused \(.*\))")
ROW = re.compile(r"\[Fix1\] (?P<n>[^:]+): behaviour=(?P<b>\S+) work=(?P<w>\S+) moving=\S+ distToDest=(?P<d>[\d.]+) .* pos=(?P<pos>[-\d.,]+) dest=(?P<dest>[-\d.,]+)")
PUT = re.compile(r"\[Fix1\] put (?P<n>.+?) at (?P<at>[-\d.,]+), now (?P<now>[-\d.,]+), destination (?P<dest>[-\d.,]+)")

results = []


def check(name, ok, evidence):
    results.append(ok)
    print("%s  %s" % ("PASS" if ok else "FAIL", name))
    for line in evidence if isinstance(evidence, list) else [evidence]:
        print("      " + str(line))


def run(game, command, max_wait=8000):
    answer = game.run(command, max_wait_ms=max_wait, settle_ms=600)
    if not answer.get("ok"):
        raise RuntimeError("%s: %s" % (command, answer))
    return [l["text"] for l in answer["result"].get("lines", [])]


def vec(text):
    return tuple(float(c) for c in text.split(","))


def rows(game):
    out = {}
    for t in run(game, "awe standstill"):
        m = ROW.search(t)
        if m:
            out[m.group("n")] = (m.group("b"), m.group("w") == "True", vec(m.group("pos")), vec(m.group("dest")), t)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--slot")
    parser.add_argument("--roof-wait", type=int, default=120)
    a = parser.parse_args()
    game = s1.Game(a.slot) if a.slot else s1.Game()

    run(game, "awe set diagnostics.logfixactions true")
    run(game, "awe set reliability.fixpartialwalkloop true")

    old_ok, new_ok, ev = True, True, []
    for x, y, z in SPOTS:
        lines = [t for t in run(game, "awe standstill probe %s %s %s" % (x, y, z)) if PROBE.search(t)]
        if not lines:
            old_ok = new_ok = False
            ev.append("no probe line for %s,%s,%s" % (x, y, z))
            continue
        m = PROBE.search(lines[-1])
        ev.append(lines[-1].split("[Fix1] ")[-1])
        old = m.group("old")
        old_ok &= old != "none" and vec(old)[1] - y > 2.0
        new = m.group("new")
        new_ok &= not new.startswith("refused") and abs(vec(new)[1] - y) <= 1.0
    check("old-lands-on-roof", old_ok, ev)
    check("new-stays-on-floor", new_ok, ev)

    # An employee on the roof island, the state the reporter saved: its walks come back partial and Fix 1 must warp it
    # back to the floor of its destination.
    put = None
    deadline = time.time() + 90
    while time.time() < deadline and put is None:
        lines = run(game, "awe standstill put %s %s %s" % ROOF)
        hits = [t for t in lines if PUT.search(t)]
        if hits:
            put = PUT.search(hits[-1])
            check("put-on-roof", vec(put.group("now"))[1] > 2.0, hits[-1].split("[Fix1] ")[-1])
        else:
            time.sleep(3)
    if put is None:
        check("put-on-roof", False, "no employee was on a work task with a destination for 90 s")
        return finish()

    name = put.group("n")
    seen = set(game.log(grep="[Fix1]", lines=400))
    warped, last = None, None
    deadline = time.time() + a.roof_wait
    put_at = time.time()
    while time.time() < deadline:
        time.sleep(3)
        r = rows(game)
        if name in r:
            last = r[name]
        hit = [l for l in game.log(grep="[Fix1]", lines=80)
               if ("[Fix1] warped " + name in l or "did not warp " + name in l) and l > ""]
        # Only lines written after the put count; the log keeps lines from earlier runs of this script.
        hit = [l for l in hit if l not in seen]
        if hit:
            warped = hit[-1]
            break
    print("      waited %.0f s" % (time.time() - put_at))
    check("roof-employee-warped", warped is not None and "warped " + name in warped,
          warped or "no '[Fix1] warped %s' line within %d s; last row: %s" % (name, a.roof_wait, last[4] if last else "none"))
    time.sleep(2)
    r = rows(game)
    if name in r:
        _, _, pos, dest, line = r[name]
        check("roof-employee-down", pos[1] < 2.0 and abs(pos[1] - dest[1]) <= 1.0, line.split("[Fix1] ")[-1])
    else:
        check("roof-employee-down", False, "%s not in the employee list" % name)
    return finish()


def finish():
    print("SUMMARY %d PASS, %d FAIL" % (results.count(True), results.count(False)))
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
