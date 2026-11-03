"""Bridge calls the s1.py command line has no verb for: health, save_now, return_to_menu, load_save.

    python -I tests/awe_bridge.py --slot mptest2 health
    python -I tests/awe_bridge.py --slot mptest2 call save_now
    python -I tests/awe_bridge.py --slot mptest2 call load_save '{"slot": 5}'

Prints the JSON answer. Exit 0 when the bridge answered ok, 1 when it answered with an error or nothing answered
(the reason goes to stderr), so the PowerShell callers can report it as a FAIL with the reason.
"""
import argparse
import json
import os
import sys

# Workspace/tools/bridge/s1.py sits at the repository root, two levels above the worktree's tests folder.
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Workspace", "tools", "bridge"))
from s1 import Game, BridgeDown  # noqa: E402


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--slot", required=True)
    parser.add_argument("--timeout-ms", type=int, default=15000)
    sub = parser.add_subparsers(dest="verb", required=True)
    sub.add_parser("health")
    call = sub.add_parser("call")
    call.add_argument("command")
    call.add_argument("args", nargs="?", default="{}")
    a = parser.parse_args()

    try:
        if a.verb == "health":
            # Reading /health drives nothing, so it needs no lease.
            print(json.dumps(Game(a.slot, check=False).health()))
            return 0
        answer = Game(a.slot).call(a.command, json.loads(a.args), timeout_ms=a.timeout_ms)
    except (BridgeDown, TimeoutError) as e:
        print("bridge: %s" % e, file=sys.stderr)
        return 1
    print(json.dumps(answer))
    if not answer.get("ok"):
        error = answer.get("error") or {}
        print("bridge error %s: %s" % (error.get("code"), error.get("message")), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
