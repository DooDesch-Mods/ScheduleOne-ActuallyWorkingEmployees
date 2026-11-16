"""Static check of the German table: PASS or FAIL per row, SUMMARY line at the end, exit 1 on a FAIL.

    python -I tests/awe-l10n-check.py

Rows: every text of the code has a German entry that differs from the English one, placeholders and the plain-text rules
hold, de.json holds no stale key, and the page script has no text that bypasses t().
"""
import importlib.util
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

spec = importlib.util.spec_from_file_location('extract', os.path.join(HERE, 'l10n', 'extract.py'))
extract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(extract)

# English texts that are the same word in German.
IDENTICAL_OK = {'OK', 'Info', 'Problem', 'Host', 'Client', 'Vanilla', 'Station', 'Route', 'Name', 'Status', 'Standard'}

passed = failed = 0


def result(ok, name, detail=''):
    global passed, failed
    if ok:
        passed += 1
    else:
        failed += 1
    print(('PASS ' if ok else 'FAIL ') + name + (' ' + detail if detail else ''))


def placeholders(s):
    return sorted(re.findall(r'\{\d+\}', s))


entries = extract.collect()
keys = [e['key'] for e in entries]
de_path = os.path.join(ROOT, 'Assets', 'l10n', 'de.json')
try:
    with open(de_path, encoding='utf-8') as f:
        raw = f.read()
    de = json.loads(raw)
    result(True, 'de-json-parses', '%d entries' % len(de))
except Exception as e:
    result(False, 'de-json-parses', str(e))
    print('SUMMARY awe-l10n-check pass=%d fail=%d' % (passed, failed))
    sys.exit(1)

result(not raw.startswith('﻿'), 'de-json-no-bom')
result(len(keys) > 300, 'texts-found', '%d texts in the code' % len(keys))

missing = [k for k in keys if k not in de]
result(not missing, 'every-text-has-german', 'missing %d: %s' % (len(missing), ' | '.join(missing[:5])))

same = [k for k in keys if k in de and de[k].strip() == k.strip() and k not in IDENTICAL_OK]
result(not same, 'no-english-left', '%d identical: %s' % (len(same), ' | '.join(same[:5])))

empty = [k for k in keys if k in de and not de[k].strip()]
result(not empty, 'no-empty-value', '%d: %s' % (len(empty), ' | '.join(empty[:5])))

bad_ph = [k for k in keys if k in de and placeholders(k) != placeholders(de[k])]
result(not bad_ph, 'placeholders-match', '%d: %s' % (len(bad_ph), ' | '.join(bad_ph[:5])))

stale = [k for k in de if k not in set(keys)]
result(not stale, 'no-stale-key', '%d: %s' % (len(stale), ' | '.join(stale[:5])))

wide = [k for k in de if any(ord(c) > 255 for c in de[k])]
result(not wide, 'latin1-only', '%d: %s' % (len(wide), ' | '.join(wide[:5])))

dash = [k for k in de if '–' in de[k] or '—' in de[k]]
result(not dash, 'no-dash-variants', '%d: %s' % (len(dash), ' | '.join(dash[:5])))

# A German word spelled with a replacement for an umlaut is nearly always a slip; "Quelle"/"neue" style words match
# "ue" legitimately, so only the clear cases are checked.
ascii_umlaut = [k for k in de if re.search(r'\b(fuer|ueber|koennen|muessen|Moeglich|zurueck|waehrend|Groesse|gross|heisst)\b', de[k], re.I)]
result(not ascii_umlaut, 'real-umlauts', '%d: %s' % (len(ascii_umlaut), ' | '.join(ascii_umlaut[:5])))

with open(os.path.join(ROOT, 'Assets', 'app', 'app.js'), encoding='utf-8') as f:
    js = f.read()
page_keys = [e['key'] for e in entries if e['where'].startswith('Button, label or note')]
unused = [k for k in page_keys if ("t('" + k.replace("'", "\\'")) not in js and k not in ('Off. Turn on {0}.',)]
result(not unused, 'page-keys-used', '%d unused: %s' % (len(unused), ' | '.join(unused[:5])))

# A text literal handed to say(), note() or el() without t() would stay English.
raw_say = re.findall(r"\bsay\('[A-Za-z][^']*'", js)
raw_el = re.findall(r"el\('(?:div|span|button)', '[^']*', '[A-Za-z][^']*'\)", js)
result(not raw_say and not raw_el, 'page-no-raw-text', 'say=%s el=%s' % (raw_say[:3], raw_el[:3]))

print('SUMMARY awe-l10n-check pass=%d fail=%d' % (passed, failed))
sys.exit(1 if failed else 0)
