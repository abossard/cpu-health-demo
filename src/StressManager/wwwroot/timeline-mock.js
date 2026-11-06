// Local mock of GET /api/timeline and the SSE stream. Loaded by timeline.html only with ?mock=1.
// Data is a deterministic function of time, so reloads agree with the IndexedDB cache.
// Optional: &drop=<seconds> drops each stream after that many seconds to exercise reconnects.
(() => {
  const STEP = 10e3, WINDOW = 30, MIN = 60e3, DAY = 1440 * MIN, PERIOD = 30 * MIN;
  const TH = { degraded: 90, unhealthy: 98 };
  const ROOT = 'hm-aks-cpu-health', FLOW = 'cpu-stress-flow';
  const NODES = ['aks-stress-45060198-vmss000000', 'aks-stress-45060198-vmss000001', 'aks-stress-45060198-vmss000002'];
  const ENTITIES = [
    { name: ROOT, displayName: 'hm-aks-cpu-health', kind: 'root', parent: null },
    { name: FLOW, displayName: 'CPU stress flow', kind: 'flow', parent: ROOT },
    ...NODES.map(n => ({ name: n, displayName: `AKS node: ${n}`, kind: 'node', parent: FLOW })),
  ];

  const hash = (...k) => {
    let h = 0x9e3779b9;
    for (const n of k) { h = Math.imul(h ^ (n | 0), 0x85ebca6b); h ^= h >>> 13; h = Math.imul(h, 0xc2b2ae35); h ^= h >>> 16; }
    return (h >>> 0) / 4294967296;
  };

  // Run k starts 1 min into its 30 min period; ~15 % of periods are breaks.
  function run(k) {
    if (hash(k, 3) < 0.15) return null;
    const start = k * PERIOD + MIN;
    const minutes = [8, 12, 15, 20][Math.floor(hash(k, 1) * 4)];
    const load = hash(k, 2) < 0.35 ? 95 : 100;
    return {
      k, runId: `run-${k}`, start, end: start + minutes * MIN, minutes, load, source: `rhythm:${k % 4}`,
      delays: NODES.map((_, n) => n * 45e3 + hash(k, 10 + n) * 90e3),
      tails: NODES.map((_, n) => hash(k, 20 + n) * 30e3),
    };
  }

  function raw(n, i) {
    const t = i * STEP, r = run(Math.floor(t / PERIOD)), noise = hash(n, i);
    if (r && t >= r.start + r.delays[n] && t < r.end + r.tails[n]) return Math.min(100, r.load - 1.4 + noise * 2.6);
    return 3 + noise * 5 + n;
  }

  const memo = NODES.map(() => new Map());
  function avg(n, i) {
    let v = memo[n].get(i);
    if (v === undefined) {
      v = 0;
      for (let j = i - WINDOW + 1; j <= i; j++) v += raw(n, j);
      v = Math.round(v / WINDOW * 100) / 100;
      memo[n].set(i, v);
    }
    return v;
  }

  const isGap = (n, t) => hash(n, 99, Math.floor(t / (12 * MIN))) < 0.03 && t % (12 * MIN) < 6 * MIN;
  const stateOf = v => v > TH.unhealthy ? 3 : v > TH.degraded ? 2 : 1;
  const signalPoint = (n, i) => {
    const t = i * STEP;
    if (isGap(n, t)) return [t, null, 0];
    const v = avg(n, i);
    return [t, v, stateOf(v)];
  };
  const worst = states => states.includes(3) ? 3 : states.includes(2) ? 2 : states.includes(1) ? 1 : 0;

  function build(since, now) {
    const last = Math.floor(now / STEP), first = Math.ceil((now - DAY) / STEP);
    const series = NODES.map((_, n) => { const a = []; for (let i = first; i <= last; i++) a.push(signalPoint(n, i)); return a; });
    const newer = p => p[0] > since;

    const entityStates = [];
    const transitions = (name, stateAt) => {
      const pts = [];
      let prev = -1;
      for (let j = 0; j < series[0].length; j++) {
        const s = stateAt(j);
        if (s !== prev) pts.push([series[0][j][0], s]);
        prev = s;
      }
      entityStates.push({ entity: name, points: pts.filter(newer) });
    };
    NODES.forEach((name, n) => transitions(name, j => series[n][j][2]));
    const rollup = j => worst(series.map(s => s[j][2]));
    transitions(FLOW, rollup);
    transitions(ROOT, rollup);

    const annotations = [];
    const runs = [];
    for (let k = Math.floor((now - DAY) / PERIOD); k <= Math.floor(now / PERIOD); k++) {
      const r = run(k);
      if (!r || r.start > now || r.start < now - DAY) continue;
      NODES.forEach((name, n) => {
        for (const [phase, t] of [['start', r.start + r.delays[n]], ['end', r.end + r.tails[n]]]) {
          if (t > now) continue;
          annotations.push({
            entity: name, id: `${r.runId}-${n}-${phase}`, t: Math.round(t), phase, runId: r.runId, minutes: r.minutes,
            load: r.load, source: r.source, description: `Stress ${r.load}% for ${r.minutes} min (${phase})`,
          });
        }
      });
      const nodes = NODES.map((name, n) => {
        const out = { entity: name, degradedAt: null, unhealthyAt: null, recoveredAt: null };
        for (const p of series[n]) {
          if (p[0] < r.start || p[0] >= r.start + PERIOD) continue;
          if (out.degradedAt == null && p[2] >= 2) out.degradedAt = p[0];
          if (out.unhealthyAt == null && p[2] === 3) out.unhealthyAt = p[0];
          if (out.degradedAt != null && out.recoveredAt == null && p[2] === 1) out.recoveredAt = p[0];
        }
        return out;
      });
      const spread = key => { const v = nodes.map(x => x[key]).filter(x => x != null); return v.length < 2 ? null : (Math.max(...v) - Math.min(...v)) / 1000; };
      runs.push({ runId: r.runId, startedAt: r.start, endedAt: r.end <= now ? r.end : null, nodes, spreadDegradedSec: spread('degradedAt'), spreadUnhealthySec: spread('unhealthyAt') });
    }

    const disagreements = [];
    for (let m = Math.ceil((now - DAY) / MIN) * MIN; m <= now; m += MIN) {
      const states = NODES.map((_, n) => signalPoint(n, Math.floor(m / STEP))[2]);
      if (states.every(s => s === states[0])) continue;
      const lastIv = disagreements[disagreements.length - 1];
      if (lastIv && lastIv[1] === m) lastIv[1] = m + MIN; else disagreements.push([m, m + MIN]);
    }

    return {
      cursor: new Date(last * STEP - 2 * MIN).toISOString(), // trails the newest point, so deltas repeat a little
      thresholds: TH,
      entities: ENTITIES,
      entityStates,
      signals: NODES.map((name, n) => ({ entity: name, signal: 'average-cpu', points: series[n].filter(newer) })),
      annotations: annotations.filter(a => a.t > since),
      runs,
      disagreements,
    };
  }

  const realFetch = window.fetch.bind(window);
  window.fetch = async (url, opts = {}) => {
    const u = new URL(url, location.href);
    if (u.pathname !== '/api/timeline') return realFetch(url, opts);
    const since = u.searchParams.get('since');
    const body = build(since ? Date.parse(since) : -Infinity, Date.now());
    await new Promise(r => setTimeout(r, 150));
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  };

  const dropAfter = Number(new URLSearchParams(location.search).get('drop')) * 1000 || 0;
  class MockEventSource extends EventTarget {
    constructor(url) {
      super();
      this.url = url;
      this.readyState = 0;
      this.onopen = this.onerror = this.onmessage = null;
      this.sent = Math.floor(Date.now() / STEP) * STEP - 2 * MIN;
      this.grid = Math.floor(Date.now() / STEP);
      const base = build(Infinity, Date.now()); // what the client already has from GET
      this.runs = new Map(base.runs.map(r => [r.runId, JSON.stringify(r)]));
      this.disagreements = JSON.stringify(base.disagreements);
      this.opened = Date.now();
      setTimeout(() => { if (this.readyState !== 2) { this.readyState = 1; this.emit('open'); } }, 100);
      this.timer = setInterval(() => this.tick(), 2000);
      this.pinger = setInterval(() => this.emit('ping', JSON.stringify({ t: Date.now() })), 30e3);
    }
    emit(type, data) {
      const ev = new MessageEvent(type, { data });
      this.dispatchEvent(ev);
      if (typeof this['on' + type] === 'function') this['on' + type](ev);
    }
    tick() {
      if (dropAfter && Date.now() - this.opened > dropAfter) { this.readyState = 2; this.stop(); this.emit('error'); return; }
      const now = Date.now();
      if (Math.floor(now / STEP) <= this.grid) return;
      this.grid = Math.floor(now / STEP);
      const delta = build(this.sent, now);
      this.sent = Date.parse(delta.cursor);
      // Only new or changed runs; null when nothing changed. Disagreements only when changed.
      const prev = this.runs;
      this.runs = new Map(delta.runs.map(r => [r.runId, JSON.stringify(r)]));
      const changed = delta.runs.filter(r => prev.get(r.runId) !== this.runs.get(r.runId));
      delta.runs = changed.length ? changed : null;
      const dis = JSON.stringify(delta.disagreements);
      if (dis === this.disagreements) delta.disagreements = null; else this.disagreements = dis;
      this.emit('delta', JSON.stringify(delta));
    }
    stop() { clearInterval(this.timer); clearInterval(this.pinger); }
    close() { this.readyState = 2; this.stop(); }
  }
  window.EventSource = MockEventSource;
  console.info('timeline-mock.js: /api/timeline and its stream are mocked');
})();
