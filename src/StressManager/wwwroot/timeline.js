// Health timeline: every entity's state, signal and CPU on one shared time axis, with difference views.
// Data: GET /api/timeline?since=, SSE /api/timeline/stream, cached in IndexedDB. Contract: docs/aks/timeline.md.
'use strict';

/* ---------- Calculations: pure functions over plain data, no DOM ---------- */
const TL = (() => {
  const MIN = 60e3, HOUR = 60 * MIN;
  const KIND_ORDER = { root: 0, flow: 1, node: 2 };
  const TICK_STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720].map(m => m * MIN);
  const BUCKETS = [MIN, 5 * MIN, 15 * MIN];
  const FRICTION_MS = 325, BOUNCE_MS = 110;

  const toMs = iso => (iso ? Date.parse(iso) : NaN);
  const stateOf = p => p[p.length - 1];

  // First index whose time is > t (points sorted by time).
  function indexAfter(points, t) {
    let lo = 0, hi = points.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (points[m][0] <= t) lo = m + 1; else hi = m; }
    return lo;
  }

  // First index whose .t is >= t (items sorted by .t).
  function indexFromT(items, t) {
    let lo = 0, hi = items.length;
    while (lo < hi) { const m = (lo + hi) >> 1; if (items[m].t < t) lo = m + 1; else hi = m; }
    return lo;
  }

  // Merge two time-sorted point arrays; on equal time the incoming point wins.
  function mergePoints(a, b) {
    if (!b || !b.length) return a || [];
    const inc = b.slice().sort((p, q) => p[0] - q[0]);
    if (!a || !a.length) return inc;
    if (inc[0][0] > a[a.length - 1][0]) return a.concat(inc);
    const out = [];
    let i = 0, j = 0;
    while (i < a.length || j < inc.length) {
      if (j >= inc.length || (i < a.length && a[i][0] < inc[j][0])) out.push(a[i++]);
      else { if (i < a.length && a[i][0] === inc[j][0]) i++; out.push(inc[j++]); }
    }
    return out;
  }

  const emptyStore = () => ({
    v: 1, cursor: null, thresholds: { degraded: 90, unhealthy: 98 }, entities: [],
    states: {}, signals: {}, annotations: {}, runs: [], disagreements: [],
  });

  // Runs merge by runId (a delta carries only new or changed runs), sorted by start.
  function mergeRuns(a, b) {
    if (!b || !b.length) return a;
    const byId = new Map(a.map(r => [r.runId, r]));
    for (const r of b) byId.set(r.runId, r);
    return [...byId.values()].sort((x, y) => x.startedAt - y.startedAt);
  }

  // Store + API delta -> new store. Points merge by (entity, time), annotations by id, runs by runId;
  // entities and disagreements are replaced when present (null means unchanged).
  function mergeDelta(store, d) {
    const states = { ...store.states };
    for (const s of d.entityStates || []) states[s.entity] = mergePoints(states[s.entity], s.points);
    const signals = { ...store.signals };
    for (const s of d.signals || []) {
      const key = `${s.entity}|${s.signal}`;
      signals[key] = { entity: s.entity, signal: s.signal, points: mergePoints(signals[key] && signals[key].points, s.points) };
    }
    const annotations = { ...store.annotations };
    for (const a of d.annotations || []) annotations[a.id] = a;
    const cursor = !store.cursor || toMs(d.cursor) > toMs(store.cursor) ? d.cursor || store.cursor : store.cursor;
    return {
      ...store, cursor, states, signals, annotations,
      thresholds: d.thresholds || store.thresholds,
      entities: Array.isArray(d.entities) ? d.entities : store.entities,
      runs: Array.isArray(d.runs) ? mergeRuns(store.runs, d.runs) : store.runs,
      disagreements: Array.isArray(d.disagreements) ? d.disagreements : store.disagreements,
    };
  }

  // Drop data older than minT; keeps the last point before minT so bands start filled.
  function prune(store, minT) {
    const keep = pts => { const i = indexAfter(pts, minT); return i > 1 ? pts.slice(i - 1) : pts; };
    const states = {}, signals = {}, annotations = {};
    for (const [k, pts] of Object.entries(store.states)) states[k] = keep(pts);
    for (const [k, s] of Object.entries(store.signals)) signals[k] = { ...s, points: keep(s.points) };
    for (const [k, a] of Object.entries(store.annotations)) if (a.t >= minT) annotations[k] = a;
    return {
      ...store, states, signals, annotations,
      runs: store.runs.filter(r => r.startedAt >= minT),
      disagreements: store.disagreements.filter(iv => iv[1] >= minT),
    };
  }

  // Last point at or before t, or null when older than maxAge.
  function pointAt(points, t, maxAge = Infinity) {
    if (!points) return null;
    const i = indexAfter(points, t) - 1;
    return i >= 0 && t - points[i][0] <= maxAge ? points[i] : null;
  }

  // Calls fn(a, b, state) for each state interval clipped to [t0, t1], merging equal neighbours.
  // A point holds until the next one, at most maxHold; the last one holds until end.
  function forEachSegment(points, t0, t1, end, maxHold, fn) {
    if (!points || !points.length) return;
    let open = false, ca = 0, cb = 0, cs = 0;
    for (let i = Math.max(0, indexAfter(points, t0) - 1); i < points.length && points[i][0] < t1; i++) {
      const p = points[i], next = i + 1 < points.length ? points[i + 1][0] : end;
      const a = Math.max(p[0], t0), b = Math.min(next, p[0] + maxHold, t1), s = stateOf(p);
      if (b <= a) continue;
      if (open && cs === s && cb >= a) cb = b;
      else { if (open) fn(ca, cb, cs); ca = a; cb = b; cs = s; open = true; }
    }
    if (open) fn(ca, cb, cs);
  }

  function segments(points, t0, t1, end, maxHold = Infinity) {
    const out = [];
    forEachSegment(points, t0, t1, end, maxHold, (a, b, s) => out.push({ a, b, s }));
    return out;
  }

  // Calls fn(t, v, startsRun) for visible non-null values (one point either side); runs break on nulls and gaps over maxGap.
  function forEachPoint(points, t0, t1, maxGap, fn) {
    if (!points) return;
    let prevT = -Infinity, fresh = true;
    const to = Math.min(points.length, indexAfter(points, t1) + 1);
    for (let i = Math.max(0, indexAfter(points, t0) - 1); i < to; i++) {
      const p = points[i];
      if (p[1] == null || p[0] - prevT > maxGap) fresh = true;
      if (p[1] != null) { fn(p[0], p[1], fresh); fresh = false; }
      prevT = p[0];
    }
  }

  // Downsampled copies of a [t, v, s] series: per bucket the max value and the worst state.
  function buildLevels(points) {
    const levels = [{ size: 0, points }];
    for (const size of BUCKETS) {
      const out = [];
      let cur = null;
      for (const p of points) {
        const b = Math.floor(p[0] / size) * size;
        if (!cur || cur[0] !== b) { cur = [b, null, 0]; out.push(cur); }
        if (p[1] != null && (cur[1] == null || p[1] > cur[1])) cur[1] = p[1];
        if (p[2] > cur[2]) cur[2] = p[2];
      }
      levels.push({ size, points: out });
    }
    return levels;
  }

  // Coarsest level with at most about one point per two pixels... finer when zoomed in.
  function pickLevel(levels, msPerPx) {
    let best = levels[0];
    for (const l of levels) if (l.size && l.size <= msPerPx * 2) best = l;
    return best;
  }

  function scale(d0, d1, r0, r1) {
    const k = (r1 - r0) / ((d1 - d0) || 1);
    const f = v => r0 + (v - d0) * k;
    f.invert = x => d0 + (x - r0) / k;
    return f;
  }

  // Tick spacing so labels are at least minPx apart.
  const tickStep = (span, width, minPx) => TICK_STEPS.find(s => width * s / span >= minPx) || TICK_STEPS[TICK_STEPS.length - 1];
  // First tick at or after t0, on local clock boundaries.
  const firstTick = (t0, step, tzOffsetMin) => Math.ceil((t0 - tzOffsetMin * MIN) / step) * step + tzOffsetMin * MIN;

  function timeTicks(t0, t1, width, minPx, tzOffsetMin) {
    const step = tickStep(t1 - t0, width, minPx), out = [];
    for (let t = firstTick(t0, step, tzOffsetMin); t <= t1; t += step) out.push(t);
    return out;
  }

  // Markers sorted by x -> clusters; a marker joins the previous cluster when closer than minPx to its last member.
  function cluster(markers, minPx) {
    const out = [];
    for (const m of markers) {
      const c = out[out.length - 1];
      if (c && m.x - c.lastX < minPx) { c.items.push(m.item); c.lastX = m.x; c.x = (c.firstX + m.x) / 2; }
      else out.push({ x: m.x, firstX: m.x, lastX: m.x, items: [m.item] });
    }
    return out.map(c => ({ x: c.x, items: c.items }));
  }

  // Gaps of sorted intervals within [t0, t1].
  function complement(intervals, t0, t1) {
    const out = [];
    let cur = t0;
    for (const [a, b] of intervals) {
      if (b <= cur) continue;
      if (a >= t1) break;
      if (a > cur) out.push([cur, a]);
      cur = Math.max(cur, b);
    }
    if (cur < t1) out.push([cur, t1]);
    return out;
  }

  /* View model: target window = center c, span s (ms); velocity v in ms of time per ms. */

  // How far the window [c - s/2, c + s/2] sticks out of [lo, hi]; signed, 0 when inside.
  function overshoot(c, s, lo, hi) {
    if (s >= hi - lo) return c - (lo + hi) / 2;
    if (c + s / 2 > hi) return c + s / 2 - hi;
    if (c - s / 2 < lo) return c - s / 2 - lo;
    return 0;
  }

  // Drag resistance past an edge: 1 inside, falling towards 0 the further out.
  const rubberFactor = (over, s) => 1 / (1 + Math.abs(over) / (0.08 * s)) ** 2;

  // Zoom by factor keeping anchorT at the same screen position.
  function zoomAt(c, s, anchorT, factor, minS, maxS) {
    const s2 = Math.min(maxS, Math.max(minS, s * factor));
    const t0 = anchorT - (anchorT - (c - s / 2)) / s * s2;
    return { c: t0 + s2 / 2, s: s2 };
  }

  // One momentum step: inertia with friction, and a critically damped spring back when past an edge.
  function momentumStep(c, v, s, dt, lo, hi) {
    c += v * dt;
    v *= Math.exp(-dt / FRICTION_MS);
    const over = overshoot(c, s, lo, hi);
    if (over) { const w = 1 / BOUNCE_MS; v += (-w * w * over - 2 * w * v) * dt; }
    return { c, v };
  }

  // Release velocity from recent {t, c} samples (event time, center); 0 when the pointer rested before release.
  function releaseVelocity(samples, releaseT, windowMs = 100, restMs = 60) {
    if (samples.length < 2) return 0;
    const last = samples[samples.length - 1];
    if (releaseT - last.t > restMs) return 0;
    let first = last;
    for (let i = samples.length - 2; i >= 0 && last.t - samples[i].t <= windowMs; i--) first = samples[i];
    return last.t > first.t ? (last.c - first.c) / (last.t - first.t) : 0;
  }

  // Model order: root, flow, then nodes by name, depth-first along parent links.
  function orderEntities(entities) {
    const names = new Set(entities.map(e => e.name)), children = new Map(), seen = new Set(), out = [];
    for (const e of entities) {
      const p = e.parent && names.has(e.parent) ? e.parent : null;
      if (!children.has(p)) children.set(p, []);
      children.get(p).push(e);
    }
    const cmp = (a, b) => ((KIND_ORDER[a.kind] ?? 3) - (KIND_ORDER[b.kind] ?? 3)) || a.name.localeCompare(b.name);
    const walk = (p, depth) => {
      for (const e of (children.get(p) || []).slice().sort(cmp)) {
        if (seen.has(e.name)) continue;
        seen.add(e.name);
        out.push({ entity: e, depth });
        walk(e.name, depth + 1);
      }
    };
    walk(null, 0);
    for (const e of entities) if (!seen.has(e.name)) out.push({ entity: e, depth: 0 });
    return out;
  }

  const nodeNames = entities => entities.filter(e => e.kind === 'node').map(e => e.name).sort((a, b) => a.localeCompare(b));

  // First signal key of an entity (by signal name), or null.
  const signalKey = (signals, entity) => Object.keys(signals).filter(k => signals[k].entity === entity).sort()[0] || null;

  // Onset skew table model for runs that overlap [t0, t1], newest first.
  function skewRows(runs, nodes, annotations, range, flagSec) {
    const meta = {};
    for (const a of annotations) if (a.phase === 'start' && !meta[a.runId]) meta[a.runId] = a;
    return runs
      .filter(r => r.startedAt <= range.t1 && (r.endedAt ?? range.t1) + 15 * MIN >= range.t0)
      .sort((a, b) => b.startedAt - a.startedAt)
      .map(r => {
        const byNode = new Map((r.nodes || []).map(n => [n.entity, n]));
        const sec = t => (t == null ? null : Math.round((t - r.startedAt) / 1000));
        const m = meta[r.runId];
        return {
          runId: r.runId, startedAt: r.startedAt, load: m ? m.load : null, minutes: m ? m.minutes : null,
          cells: nodes.map(n => { const x = byNode.get(n); return { degraded: x ? sec(x.degradedAt) : null, unhealthy: x ? sec(x.unhealthyAt) : null }; }),
          spreadDegraded: r.spreadDegradedSec, spreadUnhealthy: r.spreadUnhealthySec,
          flagDegraded: r.spreadDegradedSec > flagSec, flagUnhealthy: r.spreadUnhealthySec > flagSec,
        };
      });
  }

  // Time up to which data is known: the cursor, or the newest point when the cursor trails it.
  function dataEnd(store) {
    let end = toMs(store.cursor);
    if (!Number.isFinite(end)) end = -Infinity;
    for (const pts of Object.values(store.states)) if (pts.length) end = Math.max(end, pts[pts.length - 1][0]);
    for (const s of Object.values(store.signals)) if (s.points.length) end = Math.max(end, s.points[s.points.length - 1][0]);
    return end;
  }

  // Every node's value and states at time t; entity states only up to end.
  function snapshot(store, nodes, t, signalHold, end) {
    return nodes.map(n => {
      const key = signalKey(store.signals, n);
      const sp = key ? pointAt(store.signals[key].points, t, signalHold) : null;
      const ep = t <= end ? pointAt(store.states[n], t) : null;
      return { entity: n, value: sp ? sp[1] : null, signalState: sp ? sp[2] : null, entityState: ep ? ep[1] : null };
    });
  }

  return {
    MIN, HOUR, toMs, indexAfter, indexFromT, mergePoints, mergeRuns, emptyStore, mergeDelta, dataEnd, prune, pointAt, forEachSegment, segments,
    forEachPoint, buildLevels, pickLevel, scale, tickStep, firstTick, timeTicks, cluster, complement, overshoot, rubberFactor,
    zoomAt, momentumStep, releaseVelocity, orderEntities, nodeNames, signalKey, skewRows, snapshot,
  };
})();

/* ---------- Page ---------- */
(() => {
  const { MIN, HOUR } = TL;
  const RETAIN = 24 * HOUR, SIGNAL_HOLD = 5 * MIN, SKEW_FLAG_SEC = 120;
  const MIN_SPAN = 2 * MIN, MAX_SPAN = RETAIN, EASE_MS = 70, TIP_MS = 100, KEY_ZOOM = 1.5, KEY_PAN = 0.2;
  const BUBBLE_W = 14, CLUSTER_PX = BUBBLE_W + 8; // markers cluster when the bubbles would be closer than 8 px
  const STATE_NAMES = ['Unknown', 'Healthy', 'Degraded', 'Unhealthy'];
  const HEIGHTS = { axis: 28, lane: 14, overlay: 150, band: 28, signal: 28, chart: 72 };
  const SIGNAL_LABELS = { 'average-cpu': 'Average CPU utilization' };
  const MOCK = new URLSearchParams(location.search).get('mock') === '1';
  const DB_NAME = MOCK ? 'stress-timeline-mock' : 'stress-timeline';
  const PREFS_KEY = 'timeline-prefs';
  const FONT = '11px system-ui, -apple-system, Segoe UI, sans-serif';
  const BUBBLE_FONT = '600 10px system-ui, -apple-system, Segoe UI, sans-serif';
  const $ = id => document.getElementById(id);

  const start0 = Date.now();
  const app = {
    store: TL.emptyStore(),
    derived: null,
    windowMs: 3 * HOUR,
    live: true,
    view: { c: start0 - 1.5 * HOUR, s: 3 * HOUR, v: 0 }, // target
    disp: { t0: start0 - 3 * HOUR, t1: start0 },          // what is drawn, eased towards the target
    toggles: { lane: true, highlight: false, overlay: false, skew: true },
    pending: [], frame: 0, lastTs: 0, dirty: true, sizeDirty: true,
    rows: new Map(), rowSig: '', skewSig: '', ui: {},
    hover: null, gesture: null, pointers: new Map(), tipAt: 0,
    conn: 'loading', lastUpdate: null, retries: 0, es: null,
    colors: null,
  };

  /* ----- formatting ----- */
  // All times in UTC, 24 h, like the rhythm page.
  const iso = t => new Date(t).toISOString();
  const fmtClock = t => iso(t).slice(11, 16);
  const fmtClockSec = t => `${iso(t).slice(11, 19)} UTC`;
  const fmtDay = t => iso(t).slice(5, 16).replace('T', ' ');
  const fmtFull = t => `${iso(t).slice(0, 16).replace('T', ' ')} UTC`;
  const fmtSec = s => (s == null ? '–' : `${s} s`);
  const shortName = n => { const i = n.lastIndexOf('-'); return i > 0 && n.length - i > 3 ? n.slice(i + 1) : n; };

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  /* ----- prefs (per-viewer convenience) ----- */
  function loadPrefs() {
    try {
      const p = JSON.parse(localStorage.getItem(PREFS_KEY) || 'null');
      if (p && p.windowMs) app.windowMs = p.windowMs;
      if (p && p.toggles) Object.assign(app.toggles, p.toggles);
    } catch { /* no storage */ }
    app.view.s = app.windowMs;
  }
  function savePrefs() {
    try { localStorage.setItem(PREFS_KEY, JSON.stringify({ windowMs: app.windowMs, toggles: app.toggles })); } catch { /* no storage */ }
  }

  /* ----- IndexedDB cache ----- */
  let dbPromise = null;
  function db() {
    if (!dbPromise) {
      dbPromise = new Promise((resolve, reject) => {
        const r = indexedDB.open(DB_NAME, 1);
        r.onupgradeneeded = () => r.result.createObjectStore('kv');
        r.onsuccess = () => resolve(r.result);
        r.onerror = () => reject(r.error);
      });
    }
    return dbPromise;
  }
  async function cacheLoad() {
    try {
      const d = await db();
      return await new Promise((resolve, reject) => {
        const q = d.transaction('kv').objectStore('kv').get('store');
        q.onsuccess = () => resolve(q.result || null);
        q.onerror = () => reject(q.error);
      });
    } catch { return null; }
  }
  let saveTimer = 0;
  function scheduleSave() {
    if (saveTimer) return;
    saveTimer = setTimeout(async () => {
      saveTimer = 0;
      try { (await db()).transaction('kv', 'readwrite').objectStore('kv').put(app.store, 'store'); } catch { /* cache is best effort */ }
    }, 3000);
  }

  /* ----- network ----- */
  function enqueue(delta) { app.pending.push(delta); requestFrame(); }

  async function catchUp() {
    const since = app.store.cursor;
    const res = await fetch('/api/timeline' + (since ? `?since=${encodeURIComponent(since)}` : ''), { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(`GET /api/timeline: HTTP ${res.status}`);
    enqueue(await res.json());
  }

  function openStream() {
    const es = new EventSource('/api/timeline/stream');
    app.es = es;
    es.addEventListener('open', () => { app.retries = 0; setConn('live'); });
    es.addEventListener('delta', ev => {
      try { enqueue(JSON.parse(ev.data)); } catch (e) { showError(`Bad delta: ${e.message}`); }
    });
    es.onerror = () => { es.close(); setConn('reconnecting'); reconnect(); };
  }

  function reconnect() {
    const delay = Math.min(30e3, 1000 * 2 ** app.retries++);
    setTimeout(async () => {
      try { await catchUp(); showError(''); openStream(); } catch (e) { showError(e.message); setConn('offline'); reconnect(); }
    }, delay);
  }

  /* ----- status / errors ----- */
  function setConn(c) { app.conn = c; renderStatus(); }
  function showError(msg) { const b = $('error'); b.textContent = msg || ''; b.hidden = !msg; }
  function renderStatus() {
    const s = $('status');
    s.dataset.conn = app.conn;
    const conn = { loading: 'Loading', cached: 'Cached', live: 'Connected', reconnecting: 'Reconnecting', offline: 'Offline' }[app.conn];
    s.textContent = app.lastUpdate ? `${conn} · updated ${fmtClockSec(app.lastUpdate)}` : conn;
  }

  /* ----- data derived once per merge, so frames only read ----- */
  function derive(store) {
    const nodes = TL.nodeNames(store.entities);
    const sigKey = {}, levels = {}, ann = {};
    for (const e of store.entities) sigKey[e.name] = TL.signalKey(store.signals, e.name);
    for (const [k, s] of Object.entries(store.signals)) levels[k] = TL.buildLevels(s.points);
    for (const a of Object.values(store.annotations)) (ann[a.entity] = ann[a.entity] || []).push(a);
    for (const k of Object.keys(ann)) ann[k].sort((a, b) => a.t - b.t);
    return { nodes, sigKey, levels, ann, end: TL.dataEnd(store) };
  }

  /* ----- frame loop: merge queued deltas, step the view, draw once ----- */
  // redraw=false: only the crosshair/tooltip need an update.
  function requestFrame(redraw = true) {
    if (redraw) app.dirty = true;
    if (!app.frame) app.frame = requestAnimationFrame(frame);
  }

  const bounds = now => ({ lo: now - RETAIN, hi: now });
  const trackWidth = () => { const a = app.rows.get('axis'); return (a && a.w) || 1; };
  const targetEdges = () => ({ t0: app.view.c - app.view.s / 2, t1: app.view.c + app.view.s / 2 });

  function frame(ts) {
    app.frame = 0;
    const dt = app.lastTs ? Math.min(50, ts - app.lastTs) : 16;
    app.lastTs = ts;
    if (app.pending.length) {
      app.store = TL.prune(app.pending.reduce(TL.mergeDelta, app.store), Date.now() - RETAIN);
      app.pending = [];
      app.derived = derive(app.store);
      app.lastUpdate = Date.now();
      app.dirty = true;
      scheduleSave();
      renderStatus();
    }
    if (app.sizeDirty) resizeRows();

    const now = Date.now(), { lo, hi } = bounds(now), v = app.view, d = app.disp;
    const pxPerMs = trackWidth() / (d.t1 - d.t0);
    let springing = false;
    if (app.live) { v.c = hi - v.s / 2; v.v = 0; }
    else if (!app.gesture) {
      const m = TL.momentumStep(v.c, v.v, v.s, dt, lo, hi);
      v.c = m.c; v.v = m.v;
      const over = TL.overshoot(v.c, v.s, lo, hi);
      if (Math.abs(v.v) * pxPerMs < 0.002 && Math.abs(over) * pxPerMs < 0.5) {
        v.c -= over;
        v.v = 0;
        if (over > 0 && app.panned) app.live = true; // bounced back to "now": follow it again
        app.panned = false;
      } else springing = true;
    }

    const t = targetEdges(), a = 1 - Math.exp(-dt / EASE_MS);
    d.t0 += (t.t0 - d.t0) * a;
    d.t1 += (t.t1 - d.t1) * a;
    const easing = (Math.abs(t.t0 - d.t0) + Math.abs(t.t1 - d.t1)) * pxPerMs > 0.2;
    if (!easing) { d.t0 = t.t0; d.t1 = t.t1; }
    const moving = easing || springing || !!app.gesture;

    if (moving || app.dirty) {
      app.dirty = false;
      draw(now, lo, hi);
      syncControls();
      if (!moving) renderSettled();
    }
    if (app.hover) updateHover(ts, moving);
    if (moving) app.frame = requestAnimationFrame(frame);
    else app.lastTs = 0;
  }

  /* ----- colors from CSS tokens ----- */
  // Tokens use light-dark() and color-mix(), which canvas cannot parse; a probe element resolves them to colors.
  function readColors() {
    const probe = $('color-probe');
    const v = name => { probe.style.color = `var(${name})`; return getComputedStyle(probe).color; };
    return {
      state: [v('--color-unknown'), v('--color-healthy'), v('--color-degraded'), v('--color-unhealthy')],
      shade: [null, null, v('--tl-shade-degraded'), v('--tl-shade-unhealthy')],
      line: v('--color-accent'), fill: v('--tl-fill'), grid: v('--color-border'), muted: v('--color-text-muted'), track: v('--color-surface-2'),
      bubble: v('--color-accent'), diff: v('--tl-diff'), dim: v('--tl-dim'), edge: v('--tl-edge'),
      series: [v('--tl-n1'), v('--tl-n2'), v('--tl-n3'), v('--tl-n4'), v('--tl-n5')],
    };
  }

  /* ----- rows ----- */
  function rowModel() {
    const rows = [{ key: 'axis', kind: 'axis', label: 'Time (UTC)' }];
    if (app.toggles.lane) rows.push({ key: 'lane', kind: 'lane', label: 'Disagreement' });
    if (app.toggles.overlay) rows.push({ key: 'overlay', kind: 'overlay', label: 'All nodes · CPU' });
    for (const { entity: e, depth } of TL.orderEntities(app.store.entities)) {
      rows.push({ key: `e:${e.name}`, kind: 'band', entity: e.name, label: e.displayName || e.name, tag: e.kind, depth });
      if (e.kind !== 'node') continue;
      const sk = app.derived.sigKey[e.name];
      const sig = sk ? app.store.signals[sk].signal : 'average-cpu';
      rows.push({ key: `s:${e.name}`, kind: 'signal', entity: e.name, label: 'Signals', depth: depth + 1 });
      rows.push({ key: `c:${e.name}`, kind: 'chart', entity: e.name, label: SIGNAL_LABELS[sig] || sig, depth: depth + 2 });
    }
    return rows;
  }

  function buildRows(model) {
    const container = $('rows');
    container.replaceChildren();
    app.rows.clear();
    const nodes = app.derived.nodes;
    for (const m of model) {
      const row = el('div', `row row-${m.kind}`);
      const label = el('div', 'label');
      label.style.paddingLeft = `${(m.depth || 0) * 14 + 4}px`;
      if (m.kind === 'band') label.append(el('span', `kind kind-${m.tag}`, m.tag));
      // Long names keep their distinguishing suffix visible: "AKS node: aks-stress-…-vmss000001".
      const dash = m.label.lastIndexOf('-');
      const cut = m.label.length > 24 && dash > 0 && !/\s/.test(m.label.slice(dash)) ? dash : -1;
      const name = el('span', 'name');
      if (cut > 0) name.append(el('span', 'head', m.label.slice(0, cut)), el('span', 'tail', m.label.slice(cut)));
      else name.append(el('span', 'head', m.label));
      name.title = m.label;
      label.append(name);
      if (m.kind === 'overlay') {
        const legend = el('div', 'series-legend');
        nodes.forEach((n, i) => {
          const item = el('span', 'series-item', shortName(n));
          item.title = n;
          item.style.setProperty('--c', `var(--tl-n${(i % 5) + 1})`);
          legend.append(item);
        });
        label.append(legend);
      }
      const track = el('div', 'track');
      if (m.kind !== 'chart' && m.kind !== 'overlay') track.style.height = `${HEIGHTS[m.kind]}px`; // charts grow to fill the card
      const canvas = el('canvas');
      const cross = el('div', 'cross'), sel = el('div', 'sel');
      track.append(canvas, cross, sel);
      row.append(label, track);
      container.append(row);
      app.rows.set(m.key, { model: m, canvas, ctx: canvas.getContext('2d'), cross, sel, track, w: 0, h: HEIGHTS[m.kind], crossX: null });
    }
    app.sizeDirty = true;
  }

  function resizeRows() {
    app.sizeDirty = false;
    const dpr = window.devicePixelRatio || 1;
    for (const row of app.rows.values()) {
      row.w = row.track.clientWidth;
      row.h = row.track.clientHeight || HEIGHTS[row.model.kind];
      const bw = Math.round(row.w * dpr), bh = Math.round(row.h * dpr);
      if (row.canvas.width !== bw || row.canvas.height !== bh) { row.canvas.width = bw; row.canvas.height = bh; }
      row.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
  }

  function ensureRows() {
    const model = rowModel();
    const sig = model.map(m => `${m.key}=${m.label}`).join('\n');
    if (sig !== app.rowSig) { app.rowSig = sig; buildRows(model); resizeRows(); }
  }

  /* ----- canvas drawing (per frame: no per-point allocations) ----- */
  let T0 = 0, K = 1;
  const X = t => (t - T0) * K;

  function draw(now, lo, hi) {
    if (!app.derived) app.derived = derive(app.store);
    ensureRows();
    if (!app.colors) app.colors = readColors();
    const d = app.disp, w = trackWidth();
    T0 = d.t0;
    K = w / (d.t1 - d.t0);
    const end = app.derived.end;
    const f = { t0: d.t0, t1: d.t1, msPerPx: 1 / K, now, lo, hi, end: Number.isFinite(end) ? end : now };
    for (const row of app.rows.values()) drawRow(row, f);
  }

  function drawRow(row, f) {
    const { ctx, model: m, w, h } = row;
    if (!w) return;
    const c = app.colors, store = app.store, dv = app.derived;
    ctx.clearRect(0, 0, w, h);
    if (m.kind === 'axis') drawAxis(ctx, w, h, f);
    else if (m.kind === 'lane') {
      ctx.fillStyle = c.track;
      ctx.fillRect(0, 2, w, h - 4);
      ctx.fillStyle = c.diff;
      for (const iv of store.disagreements) if (iv[1] > f.t0 && iv[0] < f.t1) ctx.fillRect(X(iv[0]), 2, Math.max(1, X(iv[1]) - X(iv[0])), h - 4);
    } else if (m.kind === 'overlay') {
      drawThresholds(ctx, w, h);
      for (let i = 0; i < dv.nodes.length; i++) {
        const key = dv.sigKey[dv.nodes[i]];
        if (key) drawSeries(ctx, TL.pickLevel(dv.levels[key], f.msPerPx), f, h, c.series[i % c.series.length], false);
      }
    } else if (m.kind === 'band') {
      drawStates(ctx, store.states[m.entity], f, Infinity, w, h, false);
      drawMarkers(ctx, dv.ann[m.entity], f, h);
    } else if (m.kind === 'signal') {
      const key = dv.sigKey[m.entity];
      drawStates(ctx, key ? TL.pickLevel(dv.levels[key], f.msPerPx).points : null, f, SIGNAL_HOLD, w, h, false);
    } else if (m.kind === 'chart') {
      const key = dv.sigKey[m.entity];
      if (key) {
        const level = TL.pickLevel(dv.levels[key], f.msPerPx);
        drawSeries(ctx, level, f, h, null, true);
        drawStates(ctx, level.points, f, Math.max(SIGNAL_HOLD, level.size), w, h, true);
        drawThresholds(ctx, w, h);
        drawSeries(ctx, level, f, h, c.line, false);
      } else drawThresholds(ctx, w, h);
    }
    if (app.toggles.highlight && m.kind !== 'axis') drawAgreementDim(ctx, f, h);
    // Outside the 24 h of data: tinted, so the elastic edge is visible while it stretches.
    ctx.fillStyle = c.edge;
    if (f.lo > f.t0) ctx.fillRect(0, 0, X(f.lo), h);
    if (f.hi < f.t1) ctx.fillRect(X(f.hi), 0, w - X(f.hi), h);
  }

  function drawAxis(ctx, w, h, f) {
    const c = app.colors, span = f.t1 - f.t0;
    ctx.font = FONT;
    ctx.fillStyle = c.muted;
    ctx.strokeStyle = c.grid;
    ctx.lineWidth = 1;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const step = TL.tickStep(span, w, w < 500 ? 64 : 84);
    const tz = 0; // UTC
    ctx.beginPath();
    let lastRight = -Infinity;
    for (let t = TL.firstTick(f.t0, step, tz); t <= f.t1; t += step) {
      const px = Math.round(X(t)) + 0.5;
      ctx.moveTo(px, h - 7); ctx.lineTo(px, h);
      const midnight = step >= HOUR && (t - tz * MIN) % (24 * HOUR) === 0;
      const label = midnight && span > 6 * HOUR ? fmtDay(t) : fmtClock(t);
      const tw = ctx.measureText(label).width / 2;
      // Only labels that fit centred on their tick; while panning they slide in and out at the edges.
      if (px - tw < 0 || px + tw > w || px - tw < lastRight + 6) continue;
      ctx.fillText(label, px, 4);
      lastRight = px + tw;
    }
    ctx.stroke();
  }

  const yOf = (v, h) => h - 2 - v * (h - 6) / 100;

  function drawThresholds(ctx, w, h) {
    const c = app.colors, th = app.store.thresholds;
    ctx.setLineDash([4, 4]);
    ctx.lineWidth = 1;
    ctx.strokeStyle = c.state[2];
    ctx.beginPath(); ctx.moveTo(0, Math.round(yOf(th.degraded, h)) + 0.5); ctx.lineTo(w, Math.round(yOf(th.degraded, h)) + 0.5); ctx.stroke();
    ctx.strokeStyle = c.state[3];
    ctx.beginPath(); ctx.moveTo(0, Math.round(yOf(th.unhealthy, h)) + 0.5); ctx.lineTo(w, Math.round(yOf(th.unhealthy, h)) + 0.5); ctx.stroke();
    ctx.setLineDash([]);
  }

  // State band (shade=false) or chart shading for Degraded/Unhealthy (shade=true).
  function drawStates(ctx, points, f, maxHold, w, h, shade) {
    const c = app.colors, bh = 8, top = h / 2 - bh / 2;
    if (!shade) {
      ctx.fillStyle = c.track;
      ctx.beginPath();
      if (ctx.roundRect) ctx.roundRect(0, top, w, bh, bh / 2); else ctx.rect(0, top, w, bh);
      ctx.fill();
    }
    TL.forEachSegment(points, f.t0, f.t1, f.end, maxHold, (a, b, s) => {
      if (shade && s < 2) return;
      ctx.fillStyle = shade ? c.shade[s] : c.state[s] || c.state[0];
      const xa = X(a);
      if (shade) ctx.fillRect(xa, 0, X(b) - xa, h);
      else ctx.fillRect(xa, top, Math.max(1, X(b) - xa), bh);
    });
  }

  // Line (fill=false) or area (fill=true) of a series level.
  function drawSeries(ctx, level, f, h, color, fill) {
    const maxGap = Math.max(SIGNAL_HOLD, 2 * level.size);
    let startX = 0, lastX = 0, open = false;
    ctx.beginPath();
    TL.forEachPoint(level.points, f.t0, f.t1, maxGap, (t, v, fresh) => {
      const x = X(t), y = yOf(v, h);
      if (fresh) {
        if (fill && open) { ctx.lineTo(lastX, h); ctx.lineTo(startX, h); ctx.closePath(); }
        if (fill) { ctx.moveTo(x, h); ctx.lineTo(x, y); } else ctx.moveTo(x, y);
        startX = x;
        open = true;
      } else ctx.lineTo(x, y);
      lastX = x;
    });
    if (fill) {
      if (open) { ctx.lineTo(lastX, h); ctx.lineTo(startX, h); ctx.closePath(); }
      ctx.fillStyle = app.colors.fill;
      ctx.fill();
    } else {
      ctx.lineWidth = 1.5;
      ctx.lineJoin = 'round';
      ctx.strokeStyle = color;
      ctx.stroke();
    }
  }

  function drawAgreementDim(ctx, f, h) {
    ctx.fillStyle = app.colors.dim;
    let cur = f.t0;
    for (const iv of app.store.disagreements) {
      if (iv[1] <= cur) continue;
      if (iv[0] >= f.t1) break;
      if (iv[0] > cur) ctx.fillRect(X(cur), 0, X(iv[0]) - X(cur), h);
      cur = Math.max(cur, iv[1]);
    }
    if (cur < f.t1) ctx.fillRect(X(cur), 0, X(f.t1) - X(cur), h);
  }

  // Annotation bubbles, clustered inline (same rule as TL.cluster, used for the tooltip).
  function drawMarkers(ctx, list, f, h) {
    if (!list || !list.length) return;
    ctx.font = BUBBLE_FONT;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    let count = 0, firstX = 0, lastX = 0, phase = '';
    for (let i = TL.indexFromT(list, f.t0); i < list.length && list[i].t <= f.t1; i++) {
      const x = X(list[i].t);
      if (count && x - lastX < CLUSTER_PX) { count++; lastX = x; continue; }
      if (count) drawBubble(ctx, (firstX + lastX) / 2, count, phase, h);
      count = 1; firstX = lastX = x; phase = list[i].phase;
    }
    if (count) drawBubble(ctx, (firstX + lastX) / 2, count, phase, h);
  }

  function drawBubble(ctx, x, n, phase, h) {
    const bh = 14, top = h / 2 - bh / 2 - 2, cy = top + bh / 2;
    const label = n > 1 ? String(n) : '';
    const bw = n > 1 ? Math.max(BUBBLE_W, ctx.measureText(label).width + 8) : BUBBLE_W, left = x - bw / 2;
    ctx.fillStyle = app.colors.bubble;
    ctx.beginPath();
    if (ctx.roundRect) ctx.roundRect(left, top, bw, bh, 3); else ctx.rect(left, top, bw, bh);
    ctx.moveTo(left + 2, top + bh - 1); ctx.lineTo(left + 2, top + bh + 4); ctx.lineTo(left + 7, top + bh - 1);
    ctx.fill();
    ctx.fillStyle = '#fff';
    if (n > 1) ctx.fillText(label, x, cy + 0.5);
    else if (phase === 'start') { ctx.beginPath(); ctx.moveTo(x - 2.5, cy - 3.5); ctx.lineTo(x + 3.5, cy); ctx.lineTo(x - 2.5, cy + 3.5); ctx.fill(); }
    else ctx.fillRect(x - 3, cy - 3, 6, 6);
  }

  /* ----- DOM bits that change rarely ----- */
  function syncControls() {
    const ui = app.ui, pressed = Math.abs(app.view.s - app.windowMs) / app.windowMs < 0.01;
    if (ui.live !== app.live) {
      ui.live = app.live;
      $('live').checked = app.live;
      $('back-live').hidden = app.live;
    }
    const key = pressed ? app.windowMs : 0;
    if (ui.pressed !== key) {
      ui.pressed = key;
      for (const b of document.querySelectorAll('#windows button')) b.setAttribute('aria-pressed', String(Number(b.dataset.ms) === key));
    }
  }

  function renderSettled() {
    $('empty').hidden = app.store.entities.length > 0;
    $('skew-section').hidden = !app.toggles.skew;
    if (app.toggles.skew && app.derived) renderSkew({ t0: app.disp.t0, t1: app.disp.t1 }, app.derived.nodes);
  }

  /* ----- onset skew table ----- */
  function renderSkew(range, nodes) {
    const rows = TL.skewRows(app.store.runs, nodes, Object.values(app.store.annotations), range, SKEW_FLAG_SEC);
    const sig = JSON.stringify([nodes, rows]);
    if (sig === app.skewSig) return;
    app.skewSig = sig;
    const table = $('skew');
    const head = el('tr');
    head.append(el('th', null, 'Run start (UTC)'), el('th', null, 'Load'));
    for (const n of nodes) { const th = el('th', 'num', shortName(n)); th.title = n; head.append(th); }
    head.append(el('th', 'num', 'Spread D'), el('th', 'num', 'Spread U'));
    const body = el('tbody');
    for (const r of rows) {
      const tr = el('tr');
      const start = el('td', null, fmtClock(r.startedAt));
      start.title = `${r.runId} · ${fmtFull(r.startedAt)}`;
      tr.append(start, el('td', 'muted', r.load != null ? `${r.load}% · ${r.minutes} min` : '–'));
      for (const cell of r.cells) {
        const td = el('td', 'num');
        td.append(el('span', 'onset d', `D ${fmtSec(cell.degraded)}`), el('span', 'onset u', `U ${fmtSec(cell.unhealthy)}`));
        tr.append(td);
      }
      const spread = (v, flag) => { const td = el('td', `num${flag ? ' flag' : ''}`, fmtSec(v)); if (flag) td.title = `Spread over ${SKEW_FLAG_SEC} s`; return td; };
      tr.append(spread(r.spreadDegraded, r.flagDegraded), spread(r.spreadUnhealthy, r.flagUnhealthy));
      body.append(tr);
    }
    if (!rows.length) { const tr = el('tr'); const td = el('td', 'muted', 'No runs in view'); td.colSpan = nodes.length + 4; tr.append(td); body.append(tr); }
    const thead = el('thead');
    thead.append(head);
    table.replaceChildren(thead, body);
  }

  /* ----- crosshair and tooltip ----- */
  function rowKeyOf(track) {
    for (const [key, row] of app.rows) if (row.track === track) return key;
    return null;
  }

  function setHover(e, track) {
    const r = track.getBoundingClientRect();
    app.hover = { x: e.clientX - r.left, y: e.clientY - r.top, clientX: e.clientX, clientY: e.clientY, key: rowKeyOf(track) };
    app.tipAt = 0;
    requestFrame(false);
  }

  function hideHover() {
    app.hover = null;
    for (const row of app.rows.values()) { row.cross.style.display = 'none'; row.crossX = null; }
    $('tooltip').hidden = true;
  }

  // Crosshair every frame (cheap); tooltip content at most every TIP_MS while moving.
  function updateHover(ts, moving) {
    const h = app.hover;
    const x = Math.round(h.x);
    for (const row of app.rows.values()) {
      if (row.crossX === x) continue;
      row.crossX = x;
      row.cross.style.display = 'block';
      row.cross.style.transform = `translateX(${x}px)`;
    }
    if (moving && ts - app.tipAt < TIP_MS) return;
    app.tipAt = ts;
    const t = T0 + h.x / K;
    const row = h.key ? app.rows.get(h.key) : null;
    let cluster = null;
    if (row && row.model.kind === 'band' && Math.abs(h.y - HEIGHTS.band / 2) < 12) {
      const list = (app.derived.ann[row.model.entity] || []).filter(a => a.t >= app.disp.t0 && a.t <= app.disp.t1);
      for (const c of TL.cluster(list.map(a => ({ x: X(a.t), item: a })), CLUSTER_PX)) {
        if (Math.abs(c.x - h.x) <= 9 && (!cluster || Math.abs(c.x - h.x) < Math.abs(cluster.x - h.x))) cluster = c;
      }
    }
    renderTooltip(t, cluster);
  }

  const stateDot = s => el('span', `st st-${s == null ? 'none' : s}`, s == null ? '–' : STATE_NAMES[s]);

  function renderTooltip(t, cluster) {
    const tip = $('tooltip');
    const rows = TL.snapshot(app.store, app.derived.nodes, t, SIGNAL_HOLD, app.derived.end);
    const parts = [el('div', 'tt-time', fmtClockSec(t))];
    const table = el('table');
    const head = el('tr');
    head.append(el('th', null, 'Node'), el('th', 'num', 'CPU'), el('th', null, 'Signal'), el('th', null, 'Entity'));
    table.append(head);
    rows.forEach((r, i) => {
      const tr = el('tr');
      const name = el('td', 'tt-node', shortName(r.entity));
      name.style.setProperty('--c', `var(--tl-n${(i % 5) + 1})`);
      const sigTd = el('td'); sigTd.append(stateDot(r.signalState));
      const entTd = el('td'); entTd.append(stateDot(r.entityState));
      tr.append(name, el('td', 'num', r.value == null ? '–' : `${r.value.toFixed(1)} %`), sigTd, entTd);
      table.append(tr);
    });
    parts.push(table);
    if (app.store.disagreements.some(([a, b]) => t >= a && t < b)) parts.push(el('div', 'tt-diff', 'Nodes disagree'));
    if (cluster) {
      const list = el('div', 'tt-ann');
      for (const a of cluster.items) {
        const item = el('div', 'tt-ann-item');
        item.append(
          el('div', 'tt-ann-head', `${a.phase === 'start' ? '▶ start' : '■ end'} · ${fmtClockSec(a.t)} · ${a.runId}`),
          el('div', 'muted', [a.load != null ? `${a.load}%` : null, a.minutes != null ? `${a.minutes} min` : null, a.source].filter(Boolean).join(' · ')),
        );
        if (a.description) item.append(el('div', 'muted', a.description));
        list.append(item);
      }
      parts.push(list);
    }
    tip.replaceChildren(...parts);
    tip.hidden = false;
    const h = app.hover, pad = 12, tw = tip.offsetWidth, th = tip.offsetHeight;
    let left = h.clientX + pad, top = h.clientY + pad;
    if (left + tw > window.innerWidth - 8) left = Math.max(8, h.clientX - pad - tw);
    if (top + th > window.innerHeight - 8) top = Math.max(8, h.clientY - pad - th);
    tip.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
  }

  /* ----- view actions (input -> target view) ----- */
  const clampSpan = s => Math.min(MAX_SPAN, Math.max(MIN_SPAN, s));

  // Pan the target by dt ms of time, with rubber-band resistance when moving further out of range.
  function panBy(dtMs) {
    const v = app.view, { lo, hi } = bounds(Date.now());
    const over = TL.overshoot(v.c, v.s, lo, hi);
    if (over && Math.sign(over) === Math.sign(dtMs)) dtMs *= TL.rubberFactor(over, v.s);
    v.c += dtMs;
    app.live = false;
    app.panned = true;
    requestFrame();
  }

  // Zoom the target by factor; while live the right edge stays on "now", otherwise anchorFrac (0..1 of width) stays put.
  function zoomBy(factor, anchorFrac) {
    const v = app.view;
    const anchorT = app.live ? v.c + v.s / 2 : v.c - v.s / 2 + anchorFrac * v.s;
    const z = TL.zoomAt(v.c, v.s, anchorT, factor, MIN_SPAN, MAX_SPAN);
    v.c = z.c; v.s = z.s; v.v = 0;
    requestFrame();
  }

  function goLive() { app.live = true; app.view.v = 0; requestFrame(); }

  function resetView() { app.view.s = app.windowMs; goLive(); }

  function setWindow(ms) {
    app.windowMs = ms;
    const v = app.view;
    if (!app.live) v.c += (v.s - ms) / 2; // keep the right edge
    v.s = ms;
    v.v = 0;
    savePrefs();
    requestFrame();
  }

  /* ----- pointer input: pan with momentum, pinch, Shift+drag box zoom ----- */
  function trackOf(e) { return e.target.closest && e.target.closest('.track'); }

  function onPointerDown(e) {
    const track = trackOf(e);
    if (!track || (e.pointerType === 'mouse' && e.button !== 0)) return;
    $('rows').setPointerCapture(e.pointerId);
    app.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const r = track.getBoundingClientRect();
    app.trackLeft = r.left;
    app.view.v = 0; // catch a moving view
    if (app.pointers.size === 2) startPinch();
    else if (e.shiftKey && e.pointerType === 'mouse') app.gesture = { kind: 'box', x0: e.clientX - r.left, x1: e.clientX - r.left };
    else app.gesture = { kind: 'pan', lastX: e.clientX, moved: 0, samples: [{ t: e.timeStamp, c: app.view.c }] };
    setHover(e, track);
  }

  function startPinch() {
    const [p, q] = [...app.pointers.values()], v = app.view, w = trackWidth();
    const mid = (p.x + q.x) / 2 - app.trackLeft;
    app.gesture = { kind: 'pinch', d0: Math.max(10, Math.hypot(p.x - q.x, p.y - q.y)), s0: v.s, anchorT: v.c - v.s / 2 + (mid / w) * v.s };
  }

  function onPointerMove(e) {
    const g = app.gesture;
    if (app.pointers.has(e.pointerId)) app.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (g && g.kind === 'pan' && app.pointers.size === 1) {
      const dx = e.clientX - g.lastX;
      g.lastX = e.clientX;
      g.moved += Math.abs(dx);
      if (g.moved > 3 && dx) panBy(-dx * (app.disp.t1 - app.disp.t0) / trackWidth());
      g.samples.push({ t: e.timeStamp, c: app.view.c });
      if (g.samples.length > 8) g.samples.shift();
    } else if (g && g.kind === 'pinch' && app.pointers.size === 2) {
      const [p, q] = [...app.pointers.values()], v = app.view, w = trackWidth();
      const s = clampSpan(g.s0 * g.d0 / Math.max(10, Math.hypot(p.x - q.x, p.y - q.y)));
      if (app.live) v.c = Date.now() - s / 2;
      else v.c = g.anchorT - (((p.x + q.x) / 2 - app.trackLeft) / w) * s + s / 2;
      v.s = s;
      requestFrame();
    } else if (g && g.kind === 'box') {
      g.x1 = Math.max(0, Math.min(trackWidth(), e.clientX - app.trackLeft));
      const a = Math.min(g.x0, g.x1), b = Math.max(g.x0, g.x1);
      for (const row of app.rows.values()) { row.sel.style.display = b - a > 2 ? 'block' : 'none'; row.sel.style.left = `${a}px`; row.sel.style.width = `${b - a}px`; }
    }
    const track = trackOf(e);
    if (track) setHover(e, track);
    else if (app.hover && g) { app.hover.x = e.clientX - app.trackLeft; app.hover.clientX = e.clientX; app.hover.clientY = e.clientY; requestFrame(false); }
    else if (!g && e.pointerType === 'mouse') hideHover();
  }

  function onPointerUp(e, cancelled) {
    const g = app.gesture;
    app.pointers.delete(e.pointerId);
    if (!g) return;
    if (g.kind === 'pinch') {
      // One finger left: continue as a pan from where it is.
      const rest = [...app.pointers.values()][0];
      app.gesture = rest ? { kind: 'pan', lastX: rest.x, moved: 10, samples: [] } : null;
      requestFrame();
      return;
    }
    if (app.pointers.size) return;
    app.gesture = null;
    if (g.kind === 'pan' && g.moved > 3 && !cancelled) app.view.v = TL.releaseVelocity(g.samples, e.timeStamp);
    if (g.kind === 'box') {
      for (const row of app.rows.values()) row.sel.style.display = 'none';
      if (Math.abs(g.x1 - g.x0) >= 6 && !cancelled) {
        const { t0 } = targetEdges(), k = app.view.s / trackWidth();
        const a = t0 + Math.min(g.x0, g.x1) * k, b = t0 + Math.max(g.x0, g.x1) * k;
        app.view.s = clampSpan(b - a);
        app.view.c = (a + b) / 2;
        app.live = false;
      }
    }
    requestFrame();
  }

  function onWheel(e) {
    const track = trackOf(e);
    if (!track) return;
    e.preventDefault();
    const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? window.innerHeight : 1;
    const dx = e.deltaX * unit, dy = e.deltaY * unit;
    if (!e.ctrlKey && Math.abs(dx) > Math.abs(dy)) {
      app.view.v = 0;
      panBy(dx * app.view.s / trackWidth());
    } else {
      const frac = (e.clientX - track.getBoundingClientRect().left) / trackWidth();
      zoomBy(Math.exp(dy * (e.ctrlKey ? 0.01 : 0.002)), frac);
    }
  }

  function onKey(e) {
    if (e.ctrlKey || e.metaKey || e.altKey || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName) && e.target.type !== 'checkbox') return;
    const v = app.view, frac = app.hover ? app.hover.x / trackWidth() : 0.5;
    if (e.key === '+' || e.key === '=') zoomBy(1 / KEY_ZOOM, frac);
    else if (e.key === '-' || e.key === '_') zoomBy(KEY_ZOOM, frac);
    else if (e.key === 'ArrowLeft') { v.v = 0; panBy(-KEY_PAN * v.s); }
    else if (e.key === 'ArrowRight') { v.v = 0; panBy(KEY_PAN * v.s); }
    else if (e.key === '0') resetView();
    else return;
    e.preventDefault();
  }

  /* ----- theme (same toggle and storage key as the rhythm page) ----- */
  const effectiveTheme = () => document.documentElement.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  function renderThemeToggle() {
    const next = effectiveTheme() === 'dark' ? 'light' : 'dark', b = $('theme-toggle');
    b.textContent = next === 'dark' ? '☾' : '☀';
    b.setAttribute('aria-label', `Switch to ${next} theme`);
    b.title = `Switch to ${next} theme`;
  }

  /* ----- wiring ----- */
  function wire() {
    for (const b of document.querySelectorAll('#windows button')) b.addEventListener('click', () => setWindow(Number(b.dataset.ms)));
    $('live').addEventListener('change', e => { if (e.target.checked) goLive(); else { app.live = false; requestFrame(); } });
    $('back-live').addEventListener('click', goLive);
    for (const k of Object.keys(app.toggles)) {
      const box = $(`t-${k}`);
      box.checked = app.toggles[k];
      box.addEventListener('change', () => { app.toggles[k] = box.checked; savePrefs(); requestFrame(); });
    }
    const rows = $('rows');
    rows.addEventListener('pointerdown', onPointerDown);
    rows.addEventListener('pointermove', onPointerMove);
    rows.addEventListener('pointerup', e => onPointerUp(e, false));
    rows.addEventListener('pointercancel', e => onPointerUp(e, true));
    // A tap keeps its tooltip until the next tap outside the rows.
    rows.addEventListener('pointerleave', e => { if (!app.gesture && e.pointerType === 'mouse') hideHover(); });
    document.addEventListener('pointerdown', e => { if (!rows.contains(e.target)) hideHover(); });
    rows.addEventListener('wheel', onWheel, { passive: false });
    rows.addEventListener('dblclick', resetView);
    document.addEventListener('keydown', onKey);
    new ResizeObserver(() => { app.sizeDirty = true; requestFrame(); }).observe(rows);
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { app.colors = null; renderThemeToggle(); requestFrame(); });
    $('theme-toggle').addEventListener('click', () => {
      const next = effectiveTheme() === 'dark' ? 'light' : 'dark';
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem('theme-preference', next); } catch { /* no storage */ }
      app.colors = null;
      renderThemeToggle();
      requestFrame();
    });
    renderThemeToggle();
    // Live: "now" moves about a pixel a second at 1 h on a wide screen.
    setInterval(() => { if (app.live) requestFrame(); }, 1000);
  }

  async function start() {
    loadPrefs();
    app.disp.t0 = start0 - app.view.s;
    app.view.c = start0 - app.view.s / 2;
    wire();
    renderStatus();
    const cached = await cacheLoad();
    if (cached && cached.v === 1) {
      app.store = TL.prune(cached, Date.now() - RETAIN);
      app.derived = derive(app.store);
      setConn('cached');
      requestFrame();
    }
    try { await catchUp(); openStream(); } catch (e) { showError(e.message); setConn('offline'); reconnect(); }
  }

  start();
})();
