/* Actually Working Employees - the settings page.
 *
 * Groups on the left (a separate screen in portrait), the options of one group on the right. The list comes from the
 * host (Host/Calls.cs) and is never hard-coded here, so an option added in C# shows up without a change to this file.
 * Every change goes through "set", which answers with the option as it now stands; the page never keeps a value the
 * host did not confirm. A locked option (a client in a host's session) has no controls.
 *
 * Written against the Sideload engine, not a browser: every box is a column flexbox, s1.call is synchronous and
 * returns a string, and only Latin-1 glyphs draw.
 */

const host = (typeof window !== 'undefined' && window.aweMock) ? window.aweMock : s1;
const $ = id => document.getElementById(id);

const KNOWN_ICONS = ['general', 'reliability', 'botanist', 'chemist', 'packager', 'cleaner', 'stations', 'property', 'diagnostics'];

const app = {
  raw: '',          // the last "list" answer as text, to skip a redraw when nothing changed
  groups: [],
  locked: false,    // a client in a host's session
  vanilla: false,
  role: '',
  group: 0,
  confirmAll: false,
  confirmFast: false,
  notice: '',       // the host-controlled text shown on a client, empty when it is off
  hints: {},        // group name -> hint text shown above that group's options
};

function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined && text !== null) n.textContent = String(text);
  return n;
}

function icon(name, cls) {
  const i = el('img', 'ic' + (cls ? ' ' + cls : ''));
  i.setAttribute('src', name + '.png');
  return i;
}

function button(cls, iconName, text, onClick) {
  const b = el('button', 'btn' + (cls ? ' ' + cls : ''));
  if (iconName) b.appendChild(icon(iconName));
  if (text) b.appendChild(el('span', null, text));
  b.addEventListener('click', () => onClick());
  return b;
}

// The host's change event carries the field text in `value`; an event the page raises itself carries only its type,
// so an empty `value` falls back to the field's own text.
const valueOf = e => (e && e.value ? e.value : e && e.target && e.target.value ? e.target.value : '');

// ---- the host ---------------------------------------------------------------------------------------------------

function callRaw(name, arg) {
  let raw = '';
  try { raw = host.call(name, arg === undefined ? '' : JSON.stringify(arg)); }
  catch (e) { console.error('[awe] call ' + name + ' threw: ' + (e && e.message)); }
  return raw || '';
}

function call(name, arg) {
  const raw = callRaw(name, arg);
  if (!raw) { console.error('[awe] ' + name + ' answered nothing'); return { ok: false, msg: 'Something went wrong.' }; }
  try { return JSON.parse(raw); }
  catch (e) { console.error('[awe] ' + name + ' answered something that is not JSON: ' + e.message); return { ok: false, msg: 'Something went wrong.' }; }
}

// A note under the panel: red for an error, green ('info') for a change the app made on its own.
function say(msg, kind) {
  const n = $('note');
  if (!msg) { n.className = 'note hidden'; return; }
  n.textContent = msg;
  n.className = kind === 'info' ? 'note info' : 'note';
}

// ---- helpers on an option ---------------------------------------------------------------------------------------

function isNumber(o) { return o.kind === 'int' || o.kind === 'float'; }

function round(v) { return Math.round(v * 1000) / 1000; }

// The value one step up or down. A vanilla option sits at -1, one step below its minimum.
function stepped(o, dir) {
  const cur = parseFloat(o.value);
  const step = o.step || 1;
  if (o.sentinel && cur === -1) return dir > 0 ? o.min : -1;
  let next = round(cur + dir * step);
  if (next < o.min) next = o.sentinel ? -1 : o.min;
  if (next > o.max) next = o.max;
  return next;
}

// True when a step in that direction would not change the value, so its button is shown as disabled.
function atLimit(o, dir) {
  const cur = parseFloat(o.value);
  if (dir < 0) return o.sentinel ? cur === -1 : cur <= o.min;
  return cur >= o.max;
}

// What the field shows: "Vanilla" for the sentinel, the number otherwise.
function fieldText(o) { return o.sentinel && parseFloat(o.value) === -1 ? String(o.text) : String(o.value); }

// The allowed range, shown in the help line so nobody has to find it by typing a wrong number.
function rangeText(o) { return 'Range: ' + o.min + '-' + o.max + '.'; }

// A typed number outside the range is set to the nearest limit, with a note naming it. Text that is no number goes
// to the host unchanged, which answers with its own error. "Vanilla" means -1 where the option has that value.
function clampTyped(o, v) {
  if (o.sentinel && v.toLowerCase() === String(o.text).toLowerCase()) return { value: '-1', note: '' };
  const n = parseFloat(v);
  if (isNaN(n) || (o.sentinel && n === -1)) return { value: v, note: '' };
  if (n < o.min) return { value: String(o.min), note: 'Set to ' + o.min + ', the minimum.' };
  if (n > o.max) return { value: String(o.max), note: 'Set to ' + o.max + ', the maximum.' };
  return { value: v, note: '' };
}

function changedCount(g) { return g.options.filter(o => !o.isDefault).length; }

// ---- rows -------------------------------------------------------------------------------------------------------

function commit(o, raw, rowNode, note) {
  const r = call('set', { key: o.key, value: String(raw) });
  if (!r.ok) {
    say(r.msg || 'Something went wrong.');
    redrawRow(o, rowNode);
    return;
  }
  say(note || '', 'info');
  refreshOne(o, r.option, rowNode);
}

function reset(o, rowNode) {
  const r = call('reset', { key: o.key });
  if (!r.ok) { say(r.msg || 'Something went wrong.'); return; }
  say('');
  refreshOne(o, r.option, rowNode);
}

// Replaces the stored option with the host's answer and rebuilds only its row, so the list keeps its scroll position.
function refreshOne(o, fresh, rowNode) {
  Object.assign(o, fresh);
  redrawRow(o, rowNode);
  drawGroups();
  drawPaneSub();
  app.raw = callRaw('list');
  if (String(o.key).indexOf('diagnostics.') === 0) renderDiag();
  if (String(o.key).indexOf('stations.') === 0) drawHint();
}

// Option keys are "group.name"; a row id keeps the dot out of the selector.
function rowId(o) { return 'opt-' + String(o.key).replace('.', '-'); }

function redrawRow(o, rowNode) {
  const parent = rowNode.parentNode;
  if (!parent) return;
  // A field removed while it has the caret never hands the keyboard back: the game keeps treating every key as
  // typing, and ESC no longer closes the app. Release the caret before the row with the field goes.
  const focused = document.activeElement;
  for (let n = focused; n; n = n.parentNode) {
    if (n === rowNode) { focused.blur(); break; }
  }
  parent.replaceChild(buildRow(o), rowNode);
}

function stepButton(o, dir, rowNode) {
  const cls = 'ghost small square ' + (dir < 0 ? 'opt-minus' : 'opt-plus') + (atLimit(o, dir) ? ' disabled' : '');
  return button(cls, dir < 0 ? 'i-minus' : 'i-plus', null, () => { if (!atLimit(o, dir)) commit(o, stepped(o, dir), rowNode); });
}

function controls(o, rowNode) {
  const ctl = el('div', 'ctl');
  // Portrait shows the default here, beside the controls, so a changed value adds no line and the buttons stay put.
  if (!o.isDefault) ctl.appendChild(el('div', 'sub default ctl-default', 'Default: ' + o.defText));

  if (o.kind === 'bool') {
    const on = o.value === 'true';
    const sw = el('button', 'switch opt-switch' + (on ? ' on' : ''));
    sw.appendChild(el('div', 'knob'));
    sw.addEventListener('click', () => commit(o, on ? 'false' : 'true', rowNode));
    ctl.appendChild(sw);
  } else if (o.kind === 'choice') {
    const seg = el('div', 'seg');
    for (const c of o.choices || []) {
      const picked = c.toLowerCase() === String(o.value).toLowerCase();
      seg.appendChild(button('small opt-choice' + (picked ? ' on' : ''), null, c, () => { if (!picked) commit(o, c, rowNode); }));
    }
    ctl.appendChild(seg);
  } else if (isNumber(o)) {
    ctl.appendChild(stepButton(o, -1, rowNode));
    const f = el('input', 'field opt-field');
    f.setAttribute('type', 'text');
    f.value = fieldText(o);
    let done = f.value;
    const send = e => {
      const v = String(valueOf(e) || '').trim();
      if (v === done) return;
      done = v;
      const c = clampTyped(o, v);
      commit(o, c.value, rowNode, c.note);
    };
    f.addEventListener('change', send);
    f.addEventListener('keydown', e => { if (e.key === 'Enter') send(f); });
    ctl.appendChild(f);
    ctl.appendChild(stepButton(o, 1, rowNode));
  }

  // The reset slot is always there, empty at the default, so the controls never move when a value changes and
  // a repeated click on + keeps landing on +.
  if (!o.isDefault) {
    ctl.appendChild(button('ghost small square opt-reset', 'i-reset', null, () => reset(o, rowNode)));
  } else {
    ctl.appendChild(el('div', 'reset-slot'));
  }
  return ctl;
}

function buildRow(o) {
  const changed = !o.isDefault && o.editable;
  const row = el('div', 'row kind-' + o.kind + (changed ? ' changed' : '') + (app.vanilla && o.balance ? ' dim' : ''));
  row.setAttribute('id', rowId(o));
  const text = el('div', 'text');
  text.appendChild(el('div', 'label', o.label));
  // The range leads the help line: at its end the text breaks after the hyphen ("1-" / "32"), and Latin-1 has no
  // non-breaking hyphen.
  const help = isNumber(o) && o.editable ? rangeText(o) + (o.help ? ' ' + o.help : '') : o.help;
  if (help) text.appendChild(el('div', 'help', help));
  if (changed) text.appendChild(el('div', 'sub default', 'Default: ' + o.defText));
  row.appendChild(text);

  if (o.editable) {
    row.appendChild(controls(o, row));
    return row;
  }

  // Locked: the host's value when it has arrived, otherwise a wait note. Never a control.
  const ctl = el('div', 'ctl');
  ctl.appendChild(icon('i-lock', 'dim'));
  ctl.appendChild(el('div', 'value locked', o.waiting ? 'Waiting for the host.' : o.text));
  row.appendChild(ctl);
  if (!o.waiting && app.notice) text.appendChild(el('div', 'sub', 'Set by the host.'));
  return row;
}

// ---- groups and panes -------------------------------------------------------------------------------------------

function groupIcon(name) {
  const k = String(name).toLowerCase();
  return 'i-' + (KNOWN_ICONS.indexOf(k) >= 0 ? k : 'other');
}

function drawGroups() {
  const nav = $('groups');
  nav.replaceChildren();
  app.groups.forEach((g, i) => {
    const b = el('div', 'group' + (i === app.group ? ' on' : ''));
    b.setAttribute('id', 'group-' + String(g.name).toLowerCase());
    b.appendChild(icon(groupIcon(g.name)));
    b.appendChild(el('div', 'name', g.name));
    // The number of changed options, so all nine groups keep one line and fit without a scrollbar.
    const n = changedCount(g);
    if (n > 0) b.appendChild(el('div', 'count', n));
    b.appendChild(icon('i-next', 'dim next'));
    b.addEventListener('click', () => openGroup(i));
    nav.appendChild(b);
  });
}

function drawPane() {
  const g = app.groups[app.group];
  const head = $('pane-head');
  const rows = $('rows');
  head.replaceChildren();
  rows.replaceChildren();
  if (!g) { rows.appendChild(el('div', 'empty', 'No settings.')); return; }

  head.appendChild(el('div', 'pane-title', g.name));
  const changed = changedCount(g);
  if (changed > 0 && g.name !== 'Diagnostics') {
    const sub = el('div', 'pane-sub', changed + ' changed');
    sub.setAttribute('id', 'pane-changed');
    head.appendChild(sub);
  }
  if (g.name === 'Diagnostics') {
    const b = button('ghost small', null, 'Refresh', () => renderDiag());
    b.setAttribute('id', 'diag-refresh');
    head.appendChild(b);
    const run = button('small disabled', null, 'Run check', () => runDoctor());
    run.setAttribute('id', 'diag-doctor-run');
    head.appendChild(run);
  }
  if (g.options.some(o => !o.isDefault && o.editable)) {
    const b = button('ghost small', 'i-reset', 'Reset group', () => {
      for (const o of g.options) if (!o.isDefault && o.editable) call('reset', { key: o.key });
      refresh(true);
    });
    b.setAttribute('id', 'reset-group');
    head.appendChild(b);
  }
  const hint = el('div', 'hint hidden');
  hint.setAttribute('id', 'group-hint');
  rows.appendChild(hint);
  if (g.name === 'Diagnostics') {
    const box = el('div', 'diag');
    box.setAttribute('id', 'diag');
    rows.appendChild(box);
  }
  if (!g.options.length) { rows.appendChild(el('div', 'empty', 'No settings.')); return; }
  const list = el('div', 'list');
  list.setAttribute('id', 'list');
  for (const o of g.options) list.appendChild(buildRow(o));
  rows.appendChild(list);
  drawHint();
  if (g.name === 'Diagnostics') renderDiag();
}

// The "N changed" note in the pane head, kept current after a single row changed.
function drawPaneSub() {
  const g = app.groups[app.group];
  const old = $('pane-changed');
  if (!g) return;
  const n = changedCount(g);
  if (old && n > 0) { old.textContent = n + ' changed'; return; }
  // Appearing or vanishing also adds or drops Reset group, so the whole pane is rebuilt.
  if (!!old !== n > 0) drawPane();
}

// The hint of the open group, from the last list answer.
function drawHint() {
  const g = app.groups[app.group];
  const node = $('group-hint');
  if (!g || !node) return;
  let hints = app.hints;
  try { hints = JSON.parse(app.raw).hints || {}; } catch (e) { console.error('[awe] hint: the list answer is not JSON: ' + e.message); }
  const text = hints[g.name] || '';
  node.textContent = text;
  node.className = text ? 'hint' : 'hint hidden';
}

// ---- the Diagnostics panels --------------------------------------------------------------------------------------

function card(id, title) {
  const c = el('div', 'card');
  c.setAttribute('id', id);
  const head = el('div', 'card-head');
  head.appendChild(el('div', 'card-title', title));
  c.appendChild(head);
  return { c, head };
}

function note(id, text) {
  const n = el('div', 'card-note', text);
  n.setAttribute('id', id);
  return n;
}

function sevLabel(s) { return s === 'problem' ? 'Problem' : s === 'warning' ? 'Warning' : s === 'ok' ? 'OK' : 'Info'; }

function badgesCard(d) {
  const b = d.badges;
  const { c, head } = card('diag-badges', 'Employee badges');
  if (!d.host) { c.appendChild(note('diag-badges-note', 'Host only.')); return c; }
  if (!b.enabled) { c.appendChild(note('diag-badges-note', 'Off. Turn on Show problem badges.')); return c; }
  const count = el('div', 'card-sub', b.problems + ' of ' + b.items.length + ' with a problem');
  count.setAttribute('id', 'diag-badges-count');
  head.appendChild(count);
  if (!b.items.length) { c.appendChild(note('diag-badges-note', 'No employees.')); return c; }
  for (const it of b.items) {
    const row = el('div', 'badge-row sev-' + it.sev);
    const chip = el('div', 'chip ' + it.sev, sevLabel(it.sev));
    row.appendChild(chip);
    const t = el('div', 'badge-text');
    t.appendChild(el('div', 'badge-name', it.name + ' - ' + it.role));
    t.appendChild(el('div', 'badge-msg', it.text));
    row.appendChild(t);
    c.appendChild(row);
  }
  return c;
}

function doctorCard(d) {
  const x = d.doctor;
  const { c } = card('diag-doctor', 'Setup Doctor');
  if (!d.host) { c.appendChild(note('diag-doctor-note', 'Host only.')); return c; }
  if (!x.enabled) { c.appendChild(note('diag-doctor-note', 'Off. Turn on Enable Setup Doctor.')); return c; }
  if (!x.ran) { c.appendChild(note('diag-doctor-summary', 'Not run yet.')); return c; }
  c.appendChild(note('diag-doctor-summary', x.summary + ', ' + x.ageSec + ' s ago'));
  if (x.total === 0) { c.appendChild(note('diag-doctor-empty', 'No problems found.')); return c; }
  for (const f of x.findings) {
    const row = el('div', 'finding sev-' + f.sev);
    row.appendChild(el('div', 'chip ' + f.sev, sevLabel(f.sev)));
    const t = el('div', 'badge-text');
    t.appendChild(el('div', 'badge-name', (f.count > 1 ? f.count + 'x ' : '') + f.msg));
    t.appendChild(el('div', 'badge-msg', f.cat + ': ' + f.subject));
    if (f.fix) t.appendChild(el('div', 'badge-fix', f.fix));
    row.appendChild(t);
    c.appendChild(row);
  }
  if (x.total > x.shown) c.appendChild(note('diag-doctor-more', (x.total - x.shown) + ' more not shown.'));
  return c;
}

function analyticsCard(d) {
  const a = d.analytics;
  const { c, head } = card('diag-analytics', 'Idle time');
  const clear = button('ghost small' + (a.enabled && d.host ? '' : ' disabled'), null, 'Clear', () => {
    if (!a.enabled || !d.host) return;
    const r = call('analyticsReset');
    if (!r.ok) { say(r.msg || 'Something went wrong.'); return; }
    say('');
    renderDiag(r);
  });
  clear.setAttribute('id', 'diag-analytics-reset');
  head.appendChild(clear);
  if (!d.host) { c.appendChild(note('diag-analytics-status', 'Host only.')); return c; }
  if (!a.enabled) { c.appendChild(note('diag-analytics-status', 'Off. Turn on Track idle time and reason.')); return c; }
  c.appendChild(note('diag-analytics-status', 'Every ' + a.interval + ' s, ' + a.samples + ' samples'));
  for (const it of a.items) {
    const row = el('div', 'an-row');
    row.appendChild(el('div', 'badge-name', it.name + ' - ' + it.role));
    row.appendChild(el('div', 'badge-msg', 'Idle ' + it.idlePct + '% (' + it.idle + ' of ' + it.samples + ')'));
    for (const r of it.reasons) row.appendChild(el('div', 'an-reason', r.count + 'x ' + r.text));
    c.appendChild(row);
  }
  return c;
}

function canRunDoctor(d) { return !!(d && d.ok && d.host && d.doctor && d.doctor.enabled); }

// "Run check" sits in the pane head beside "Refresh", so it stays in reach above a long badge list.
function runDoctor() {
  if (!canRunDoctor(app.diag)) return;
  const r = call('doctorRun');
  if (!r.ok) { say(r.msg || 'Something went wrong.'); return; }
  say('');
  renderDiag(r);
}

// Rebuilds the three panels from the host. A call that already answered with the state passes it in.
function renderDiag(answer) {
  const box = $('diag');
  if (!box) return;
  const d = answer || call('diag');
  app.diag = d;
  const run = $('diag-doctor-run');
  if (run) run.className = 'btn small' + (canRunDoctor(d) ? '' : ' disabled');
  box.replaceChildren();
  if (!d.ok) { box.appendChild(note('diag-error', d.msg || 'Something went wrong.')); return; }
  box.appendChild(badgesCard(d));
  box.appendChild(doctorCard(d));
  box.appendChild(analyticsCard(d));
}

function drawBar() {
  $('role').textContent = app.role === 'client' ? 'Client' : app.role === 'host' ? 'Host' : '';
  const end = $('bar-end');
  end.replaceChildren();
  // A client resets its own local options; the host's values stay as they are.
  if (app.confirmFast) {
    const yes = button('small', 'i-fast', 'Apply fast preset?', () => { app.confirmFast = false; doPreset('fast'); });
    yes.setAttribute('id', 'preset-fast-confirm');
    const no = button('ghost small', null, 'Cancel', () => { app.confirmFast = false; drawBar(); });
    no.setAttribute('id', 'preset-fast-cancel');
    end.appendChild(yes);
    end.appendChild(no);
  } else if (app.confirmAll) {
    const yes = button('danger small', null, 'Reset all?', () => { app.confirmAll = false; doResetAll(); });
    yes.setAttribute('id', 'reset-all-confirm');
    const no = button('ghost small', null, 'Cancel', () => { app.confirmAll = false; drawBar(); });
    no.setAttribute('id', 'reset-all-cancel');
    end.appendChild(yes);
    end.appendChild(no);
  } else {
    // The preset writes host-controlled options only, so a client has nothing it could change with it.
    if (!app.locked) {
      const f = button('ghost small', 'i-fast', 'Fast', () => { app.confirmFast = true; app.confirmAll = false; drawBar(); });
      f.setAttribute('id', 'preset-fast');
      end.appendChild(f);
    }
    const b = button('ghost small', 'i-reset', 'Reset all', () => { app.confirmAll = true; app.confirmFast = false; drawBar(); });
    b.setAttribute('id', 'reset-all');
    end.appendChild(b);
  }
}

function drawBand() {
  const band = $('band');
  let msg = '';
  if (app.locked) msg = app.notice;
  else if (app.vanilla) msg = 'Vanilla balance is on.';
  if (!msg) { band.className = 'band hidden'; return; }
  $('band-text').textContent = msg;
  band.className = 'band';
}

function drawAll() {
  if (app.group >= app.groups.length) app.group = 0;
  drawBar();
  drawBand();
  drawGroups();
  drawPane();
}

function openGroup(i) {
  app.group = i;
  $('screen').className = 'screen on-pane';
  drawGroups();
  drawPane();
}

function backToList() { $('screen').className = 'screen on-list'; }

function doResetAll() {
  const r = call('resetAll');
  if (!r.ok) { say(r.msg || 'Something went wrong.'); drawBar(); return; }
  say('');
  apply(r, null);
}

// Writes the preset's values once; afterwards they are ordinary settings.
function doPreset(name) {
  const r = call('preset', { name });
  if (!r.ok) { say(r.msg || 'Something went wrong.'); drawBar(); return; }
  say('');
  apply(r, null);
}

// ---- state ------------------------------------------------------------------------------------------------------

function apply(state, raw) {
  app.groups = state.groups || [];
  app.locked = !!state.locked;
  app.vanilla = !!state.vanilla;
  app.role = state.role || '';
  app.notice = state.notice || '';
  app.hints = state.hints || {};
  app.raw = raw === null ? callRaw('list') : raw;
  drawAll();
}

// Loads the list and redraws, unless it is the text already on screen.
function refresh(force) {
  const raw = callRaw('list');
  if (!raw) { console.error('[awe] list answered nothing'); say('Something went wrong.'); return; }
  if (!force && raw === app.raw) return;
  let state;
  try { state = JSON.parse(raw); }
  catch (e) { console.error('[awe] list answered something that is not JSON: ' + e.message); say('Something went wrong.'); return; }
  if (!state.ok) { say(state.msg || 'Something went wrong.'); return; }
  say('');
  apply(state, raw);
}

// ---- back: the phone's back gesture ------------------------------------------------------------------------------

// Landscape shows both columns, so there is nothing to go back to and the press closes the app.
document.addEventListener('back', e => {
  if (host.orientation !== 'portrait') return;
  if ($('screen').className.indexOf('on-pane') < 0) return;
  e.preventDefault();
  backToList();
});

$('back').addEventListener('click', backToList);

host.on('options.changed', () => refresh(false));

refresh(true);
