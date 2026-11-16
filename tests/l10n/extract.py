"""Collects every player-facing text of Actually Working Employees with where it appears and how much room it has.

    python -I tests/l10n/extract.py [--out <file.json>]

Sources: Config/Options.*.cs (group, label, help, choices), the key lists between the l10n-keys markers in
Host/Loc.cs (grouped by the comment above each block), and the L10n.T("...") calls in the C# files. The output is
the input of the German translation and the key list of tests/awe-l10n-check.py.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')


def unescape(s):
    return s.replace('\\"', '"').replace("\\'", "'").replace('\\\\', '\\')


def read(path):
    with open(os.path.join(ROOT, path), encoding='utf-8') as f:
        return f.read()


def options():
    """One entry per option label and help text, one per choice, plus the group names."""
    out = []
    cfg = os.path.join(ROOT, 'Config')
    for name in sorted(os.listdir(cfg)):
        if not (name.startswith('Options.') and name.endswith('.cs')):
            continue
        text = read(os.path.join('Config', name))
        for m in re.finditer(r'new (Bool|Int|Float|Choice)Option\((.*?)\);', text, re.S):
            kind = m.group(1)
            lits = [unescape(x) for x in LIT.findall(m.group(2))]
            if len(lits) < 4:
                print('WARN ' + name + ': option with fewer than 4 literals: ' + m.group(2)[:60], file=sys.stderr)
                continue
            group, ident, label, help_ = lits[:4]
            out.append({'key': label, 'where': 'setting title in the "%s" group' % group, 'space': 'title',
                        'option': group + '.' + ident, 'kind': kind.lower()})
            if help_.strip():
                out.append({'key': help_, 'where': 'description under the setting "%s" (%s group)' % (label, group),
                            'space': 'description', 'option': group + '.' + ident})
            if kind == 'Choice':
                for c in lits[5:]:
                    out.append({'key': c, 'where': 'choice button of the setting "%s"' % label, 'space': 'button'})
    return out


SPACE = {
    'game': ('The game\'s idle reason or fix, shown in the app badges and the Setup Doctor', 'sentence'),
    'mod': ('Reason or fix this mod writes, shown in the app badges and the Setup Doctor', 'sentence'),
    'badge': ('Employee badge, role name or idle analytics text', 'short'),
    'doctor': ('Setup Doctor category, finding or fix', 'sentence'),
    'answer': ('Answer or hint of the settings app', 'sentence'),
    'word': ('Word for a value or choice, a button or chip', 'button'),
    'group': ('Group tab of the settings app (left column)', 'tab'),
}


def kind_of(comment):
    c = comment.lower()
    if 'game' in c:
        return 'game'
    if 'reasons this mod writes' in c:
        return 'mod'
    if 'badges' in c:
        return 'badge'
    if 'doctor' in c:
        return 'doctor'
    if 'answers' in c:
        return 'answer'
    if 'words' in c:
        return 'word'
    if 'groups' in c:
        return 'group'
    return 'answer'


def loc_keys():
    text = read('Host/Loc.cs')
    block = text.split('// l10n-keys:begin', 1)[1].split('// l10n-keys:end', 1)[0]
    reason, page = block.split('PageKeys', 1)
    out = []
    kind = 'answer'
    for line in reason.splitlines():
        s = line.strip()
        if s.startswith('//'):
            kind = kind_of(s)
            continue
        m = LIT.fullmatch(s.rstrip(','))
        if m:
            where, space = SPACE[kind]
            out.append({'key': unescape(m.group(1)), 'where': where, 'space': space})
    for line in page.splitlines():
        m = LIT.fullmatch(line.strip().rstrip(','))
        if m:
            out.append({'key': unescape(m.group(1)), 'where': 'Button, label or note of the settings app page',
                        'space': 'button'})
    return out


def t_calls():
    """L10n.T("...") literals outside Loc.cs (the key lists there are read separately)."""
    out = []
    for dirpath, _dirs, files in os.walk(ROOT):
        if any(p in dirpath for p in ('\\bin', '\\obj', '\\.git', '\\tests', '/bin', '/obj', '/tests')):
            continue
        for f in files:
            if not f.endswith('.cs') or f == 'Loc.cs':
                continue
            path = os.path.join(dirpath, f)
            for m in re.finditer(r'L10n\.T\(\s*"((?:[^"\\]|\\.)*)"', open(path, encoding='utf-8').read()):
                out.append({'key': unescape(m.group(1)), 'where': 'text in ' + os.path.relpath(path, ROOT),
                            'space': 'sentence'})
    return out


def collect():
    seen = {}
    for e in options() + loc_keys() + t_calls():
        if e['key'] in seen:
            continue
        seen[e['key']] = e
    return list(seen.values())


if __name__ == '__main__':
    entries = collect()
    out = None
    if '--out' in sys.argv:
        out = sys.argv[sys.argv.index('--out') + 1]
    data = json.dumps(entries, ensure_ascii=False, indent=1)
    if out:
        with open(out, 'w', encoding='utf-8') as f:
            f.write(data)
        print('%d texts written to %s' % (len(entries), out))
    else:
        print(data)
