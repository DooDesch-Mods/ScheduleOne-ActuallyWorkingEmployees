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

const valueOf = e => (e && e.value !== undefined && e.value !== null ? e.value : e && e.target ? e.target.value : '');

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

function say(msg) {
  const n = $('note');
  if (!msg) { n.className = 'note hidden'; return; }
  n.textContent = msg;
  n.className = 'note';
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

function changedCount(g) { return g.options.filter(o => !o.isDefault).length; }

// ---- rows -------------------------------------------------------------------------------------------------------

function commit(o, raw, rowNode) {
  const r = call('set', { key: o.key, value: String(raw) });
  if (!r.ok) {
    say(r.msg || 'Something went wrong.');
    redrawRow(o, rowNode);
    return;
  }
  say('');
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
  app.raw = callRaw('list');
}

function redrawRow(o, rowNode) {
  const parent = rowNode.parentNode;
  if (!parent) return;
  parent.replaceChild(buildRow(o), rowNode);
}

function controls(o, rowNode) {
  const ctl = el('div', 'ctl');

  if (o.kind === 'bool') {
    ctl.appendChild(button('small' + (o.value === 'true' ? '' : ' off'), null, 'On', () => { if (o.value !== 'true') commit(o, 'true', rowNode); }));
    ctl.appendChild(button('small' + (o.value === 'false' ? '' : ' off'), null, 'Off', () => { if (o.value !== 'false') commit(o, 'false', rowNode); }));
  } else if (o.kind === 'choice') {
    const seg = el('div', 'seg');
    for (const c of o.choices || []) {
      seg.appendChild(button('small' + (c.toLowerCase() === String(o.value).toLowerCase() ? '' : ' off'), null, c, () => commit(o, c, rowNode)));
    }
    ctl.appendChild(seg);
  } else if (isNumber(o)) {
    ctl.appendChild(button('ghost small square', 'i-minus', null, () => commit(o, stepped(o, -1), rowNode)));
    const f = el('input', 'field');
    f.setAttribute('type', 'text');
    f.value = String(o.value);
    let done = o.value;
    const send = e => {
      const v = String(valueOf(e) || '').trim();
      if (v === done) return;
      done = v;
      commit(o, v, rowNode);
    };
    f.addEventListener('change', send);
    f.addEventListener('keydown', e => { if (e.key === 'Enter') send(f); });
    ctl.appendChild(f);
    ctl.appendChild(button('ghost small square', 'i-plus', null, () => commit(o, stepped(o, 1), rowNode)));
  }

  if (!o.isDefault) {
    ctl.appendChild(button('ghost small square', 'i-reset', null, () => reset(o, rowNode)));
  }
  return ctl;
}

function buildRow(o) {
  const row = el('div', 'row' + (app.vanilla && o.balance ? ' dim' : ''));
  const text = el('div', 'text');
  text.appendChild(el('div', 'label', o.label));
  if (o.help) text.appendChild(el('div', 'help', o.help));
  if (isNumber(o) && o.text !== String(o.value)) text.appendChild(el('div', 'sub', o.text));
  if (!o.isDefault && o.editable) text.appendChild(el('div', 'sub', 'Default: ' + o.defText));
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
  if (!o.waiting) text.appendChild(el('div', 'sub', 'Set by the host.'));
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
    b.appendChild(icon(groupIcon(g.name)));
    b.appendChild(el('div', 'name', g.name));
    const n = changedCount(g);
    if (n > 0) b.appendChild(el('div', 'count', n + ' changed'));
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
  if (!app.locked && changedCount(g) > 0) {
    head.appendChild(button('ghost small', 'i-reset', 'Reset group', () => {
      for (const o of g.options) if (!o.isDefault && o.editable) call('reset', { key: o.key });
      refresh(true);
    }));
  }
  if (!g.options.length) { rows.appendChild(el('div', 'empty', 'No settings.')); return; }
  for (const o of g.options) rows.appendChild(buildRow(o));
}

function drawBar() {
  $('role').textContent = app.role === 'client' ? 'Client' : app.role === 'host' ? 'Host' : '';
  const end = $('bar-end');
  end.replaceChildren();
  if (app.locked) return;
  if (app.confirmAll) {
    end.appendChild(button('danger small', null, 'Reset all?', () => { app.confirmAll = false; doResetAll(); }));
    end.appendChild(button('ghost small', null, 'Cancel', () => { app.confirmAll = false; drawBar(); }));
  } else {
    end.appendChild(button('ghost small', 'i-reset', 'Reset all', () => { app.confirmAll = true; drawBar(); }));
  }
}

function drawBand() {
  const band = $('band');
  let msg = '';
  if (app.locked) msg = 'Set by the host.';
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

// ---- state ------------------------------------------------------------------------------------------------------

function apply(state, raw) {
  app.groups = state.groups || [];
  app.locked = !!state.locked;
  app.vanilla = !!state.vanilla;
  app.role = state.role || '';
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
