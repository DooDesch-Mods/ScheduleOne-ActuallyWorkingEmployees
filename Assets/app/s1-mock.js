// Stand-in for Host/Calls.cs: the same JSON shapes for list, role, set, reset and resetAll. Every name the page
// passes to s1.call needs a case here; a missing one answers '' and the page shows an error. Not embedded in the mod.

const GROUPS = ['General', 'Reliability', 'Botanist', 'Chemist', 'Packager', 'Cleaner', 'Stations', 'Property', 'Diagnostics'];

const B = (group, id, label, help, def, extra = {}) => ({ group, key: (group + '.' + id).toLowerCase(), label, help, kind: 'bool', def: String(def), value: String(def), hostOnly: true, balance: false, ...extra });
const N = (kind, group, id, label, help, def, min, max, step, extra = {}) => ({ group, key: (group + '.' + id).toLowerCase(), label, help, kind, def: String(def), value: String(def), min, max, step, hostOnly: true, balance: true, sentinel: false, ...extra });
const C = (group, id, label, help, def, choices) => ({ group, key: (group + '.' + id).toLowerCase(), label, help, kind: 'choice', def, value: def, choices, hostOnly: true, balance: true });

const OPTIONS = [
  B('General', 'UseVanillaBalance', 'Use vanilla balance', 'Every balance setting acts as in the base game while on. Fixes stay active.', false, { balance: false }),
  B('General', 'DisableEmployeeCollision', 'Employees pass through each other', 'Employees no longer block each other while walking. Vanilla: off.', false),
  B('General', 'VerboseLogging', 'Verbose log', 'Writes detailed debug lines to the log.', false, { hostOnly: false }),
  B('Reliability', 'StuckRescue', 'Rescue stuck employees', 'Teleports an employee that has not moved for a while.', true),
  N('float', 'Botanist', 'WalkSpeed', 'Walk speed', "Botanist walking speed. Vanilla: the game's value.", -1, 0.1, 10, 0.1, { sentinel: true }),
  N('int', 'Botanist', 'DailyWage', 'Daily wage', "Botanist daily wage. Vanilla: the game's value.", -1, 0, 100000, 50, { sentinel: true }),
  N('int', 'Botanist', 'MaxPots', 'Max pots', 'Pots one botanist can be assigned. Vanilla: 8.', 8, 1, 64, 1),
  N('float', 'Botanist', 'WorkSpeed', 'Work speed', 'Multiplier for the speed of every botanist action. Vanilla: 1.', 1, 0.1, 10, 0.1),
  N('float', 'Botanist', 'SoilTime', 'Add soil time', 'Seconds to add soil to a pot. Vanilla: 10.', 10, 0.5, 60, 0.5),
  N('int', 'Chemist', 'MaxStations', 'Max stations', 'Stations one chemist can be assigned. Vanilla: 4.', 4, 1, 32, 1),
  C('Packager', 'Priority', 'Pack order', 'Which station the packager serves first.', 'Nearest', ['Nearest', 'Fullest', 'Oldest']),
  N('float', 'Cleaner', 'TrashRange', 'Trash range', 'Metres a cleaner looks for trash. Vanilla: 20.', 20, 5, 200, 5),
  B('Diagnostics', 'ShowStatus', 'Show status lines', 'Writes one status line per employee to the log.', false, { hostOnly: false }),
];

let role = 'single-player';
let hostValues = null;   // null = none arrived yet; otherwise key -> value

const find = k => OPTIONS.find(o => o.key === k);
const forced = () => find('general.usevanillabalance').value === 'true';
const describe = (o, v) => {
  if (o.kind === 'bool') return v === 'true' ? 'On' : 'Off';
  if (o.kind === 'int' || o.kind === 'float') return o.sentinel && parseFloat(v) === -1 ? 'Vanilla' : String(parseFloat(v));
  return v;
};
const client = () => role === 'client';

function shown(o) {
  const editable = !(o.hostOnly && client());
  const hv = client() && hostValues ? hostValues[o.key] ?? o.value : null;
  const value = hv ?? o.value;
  return {
    ...o, value, text: describe(o, value), defText: describe(o, o.def), isDefault: o.value === o.def,
    editable, waiting: !editable && hv === null,
  };
}

function state() {
  const groups = GROUPS.map((name) => ({ name, options: OPTIONS.filter((o) => o.group === name).map(shown) }));
  return { ok: true, role, locked: client(), vanilla: forced(), groups, notice: client() ? 'Some settings are set by the host.' : '', hints: {} };
}

function diag() {
  const host = !client();
  return {
    ok: true, host,
    badges: { enabled: true, problems: 1, items: host ? [
      { name: 'Anna Berg', role: 'Botanist', sev: 'problem', text: 'No bed or locker assigned.' },
      { name: 'Ben Roth', role: 'Packager', sev: 'ok', text: 'Working.' },
    ] : [] },
    doctor: { enabled: true, ran: host, total: 1, shown: 1, summary: '1 finding(s), 1 shown, 1 propert(ies)', ageSec: 3,
      findings: [{ sev: 'problem', cat: 'Route', subject: 'Bench -> Rack', msg: 'No walkable path to the source.', fix: 'Furniture blocks it.', count: 1 }] },
    analytics: { enabled: true, interval: 5, samples: 12, items: host ? [
      { name: 'Anna Berg', role: 'Botanist', samples: 12, idle: 6, idlePct: 50, reasons: [{ text: 'I haven\'t been assigned a locker', count: 6 }] },
    ] : [] },
  };
}

function store(o, raw) {
  const s = String(raw).trim();
  if (o.kind === 'bool') {
    if (!['true', 'false', 'on', 'off'].includes(s.toLowerCase())) return 'expected on or off';
    o.value = ['true', 'on'].includes(s.toLowerCase()) ? 'true' : 'false';
  } else if (o.kind === 'choice') {
    const c = o.choices.find((x) => x.toLowerCase() === s.toLowerCase());
    if (!c) return 'expected one of ' + o.choices.join(', ');
    o.value = c;
  } else {
    const v = parseFloat(s);
    if (Number.isNaN(v)) return 'expected a number';
    if (!((o.sentinel && v === -1) || (v >= o.min && v <= o.max))) return o.key + ': ' + v + ' is outside ' + o.min + '-' + o.max + '.';
    o.value = String(v);
  }
  return null;
}

export function call(name, arg) {
  const a = arg ? JSON.parse(arg) : {};
  switch (name) {
    case 'list': return JSON.stringify(state());
    case 'role': return JSON.stringify({ ok: true, role, locked: client(), vanilla: forced() });
    case 'set': {
      const o = find(a.key);
      if (!o) return JSON.stringify({ ok: false, msg: 'Unknown setting.' });
      if (o.hostOnly && client()) return JSON.stringify({ ok: false, msg: 'Set by the host.' });
      const err = store(o, a.value);
      if (err) return JSON.stringify({ ok: false, msg: err });
      return JSON.stringify({ ok: true, option: shown(o) });
    }
    case 'reset': {
      const o = find(a.key);
      if (!o) return JSON.stringify({ ok: false, msg: 'Unknown setting.' });
      if (o.hostOnly && client()) return JSON.stringify({ ok: false, msg: 'Set by the host.' });
      o.value = o.def;
      return JSON.stringify({ ok: true, option: shown(o) });
    }
    case 'diag': return JSON.stringify(diag());
    case 'doctorRun': return JSON.stringify(client() ? { ok: false, msg: 'Host only.' } : diag());
    case 'analyticsReset': return JSON.stringify(client() ? { ok: false, msg: 'Host only.' } : diag());
    case 'resetAll': {
      let n = 0;
      for (const o of OPTIONS) if (!(o.hostOnly && client()) && o.value !== o.def) { o.value = o.def; n++; }
      return JSON.stringify({ ...state(), changed: n });
    }
    default:
      console.warn('[preview] no stand-in for s1.call("' + name + '")' + (arg ? ' <- ' + arg : ''));
      return '';
  }
}

export const scenarios = {
  'single-player': (s1) => { role = 'single-player'; hostValues = null; s1.emit('options.changed'); },
  'client, host values in': (s1) => {
    role = 'client';
    hostValues = { 'botanist.workspeed': '2', 'botanist.maxpots': '12', 'general.usevanillabalance': 'false' };
    s1.emit('options.changed');
  },
  'client, waiting for host': (s1) => { role = 'client'; hostValues = null; s1.emit('options.changed'); },
  'vanilla balance on': (s1) => { role = 'single-player'; hostValues = null; find('general.usevanillabalance').value = 'true'; s1.emit('options.changed'); },
  'changed values': (s1) => {
    role = 'single-player'; hostValues = null;
    find('botanist.workspeed').value = '2.5'; find('botanist.maxpots').value = '16'; find('packager.priority').value = 'Fullest';
    s1.emit('options.changed');
  },
};
