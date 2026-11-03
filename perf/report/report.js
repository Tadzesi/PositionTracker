/* Renders the load-test report from the embedded JSON (perf/analysis/analyze.py). Every number on the page comes from it. */
(function () {
  'use strict';
  const D = JSON.parse(document.getElementById('data').textContent);
  const { Plot, Heat, auto, clock, num } = window.PTC;
  const root = document.documentElement;
  const $ = (s) => document.querySelector(s);
  const S = D.series || {};
  const T = S.t || [];

  // ------------------------------------------------------------------ theme
  const mq = matchMedia('(prefers-color-scheme: dark)');
  function applyTheme() {
    const t = root.getAttribute('data-theme');
    root.dataset.themeActive = t || (mq.matches ? 'dark' : 'light');
    document.querySelectorAll('.seg button').forEach((b) => b.setAttribute('aria-pressed', String((b.dataset.t === 'auto' && !t) || b.dataset.t === t)));
    window.PTC.redrawAll();
  }
  document.querySelectorAll('.seg button').forEach((b) => b.addEventListener('click', () => {
    const t = b.dataset.t;
    if (t === 'auto') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', t);
    try { t === 'auto' ? localStorage.removeItem('pt-site-theme') : localStorage.setItem('pt-site-theme', t); } catch (e) { /* private mode */ }
    applyTheme();
  }));
  mq.addEventListener('change', applyTheme);
  root.dataset.themeActive = root.getAttribute('data-theme') || (mq.matches ? 'dark' : 'light');

  // ------------------------------------------------------------------ helpers
  const get = (path) => path.split('.').reduce((o, k) => (o == null ? o : o[k]), D);
  const f = (d, unit) => (v) => num(v, d) + (unit ? ' ' + unit : '');
  const ms = (v) => (v == null ? '–' : v >= 1000 ? num(v / 1000, 2) + ' s' : num(v, v < 10 ? 2 : v < 100 ? 1 : 0) + ' ms');
  document.querySelectorAll('[data-v]').forEach((el) => {
    const v = get(el.dataset.v);
    const d = el.dataset.d != null ? +el.dataset.d : null;
    el.textContent = v == null ? '–' : typeof v === 'number' ? (el.dataset.ms != null ? ms(v) : d != null ? num(v, d) : auto(v)) : v;
  });
  const PS = Object.fromEntries((D.phase_stats || []).map((p) => [p.name, p]));
  const short = (n) => n.replace('noise floor', 'kľud').replace(/^E1 field /, 'E1 ').replace(/^E3 step /, 'E3 ').replace(/^E6 spectators /, 'E6 ')
    .replace('step up', '↑').replace('step down', '↓').replace('pre-step', '·').replace(/^E5 impulse /, 'E5 #').replace(' cooldown', ' ⌁').replace(' chirp', ' chirp').replace(' soak', ' pretek');
  const bands = (D.phases || []).map((p) => ({ x0: p.t0, x1: p.t1, label: short(p.name), full: p.name }));
  const fullBands = bands.map((b) => Object.assign({}, b, { label: b.full }));
  const has = (sel) => !!$(sel);

  // ------------------------------------------------------------------ side meta and hero
  if (has('#runid')) $('#runid').textContent = `beh ${D.generated ? D.generated.slice(0, 10) : ''} · ${num(D.kpi?.duration_min, 0)} min`;
  if (has('#sidemeta')) $('#sidemeta').innerHTML = `<b>Hardvér</b><br>Intel NUC11ATKC2<br>Celeron N4505 · 2C/2T<br>15 GiB RAM · NVMe<br><br><b>Softvér</b><br>.NET 10 · PostgreSQL 17<br>nginx · Docker (37 kontajnerov)<br><br>Vygenerované ${D.generated?.replace('T', ' ').replace('+00:00', ' UTC') || ''}`;

  // ------------------------------------------------------------------ 01 strip chart (the DAQ recorder view)
  const strip = $('#strip');
  if (strip && T.length) {
    const panels = [
      { title: 'Záťaž [req/s]', series: [['target', 'cieľ (generátor)', '--c-ink2', { dash: true, width: 1.2 }], ['ok', '202 prijaté', '--s1'], ['e503', '503 odmietnuté', '--crit']] },
      { title: 'Latencia servera, POST /positions [ms]', y: { log: true, min: 0.5 }, series: [['p50', 'p50', '--s1'], ['p99', 'p99', '--s2']], tipFmt: ms },
      { title: 'CPU podľa procesu [jadrá z 2]', y: { min: 0, max: 2.05 }, series: [['app', 'aplikácia (.NET)', '--s1', { stack: true }], ['db', 'PostgreSQL', '--s2', { stack: true }], ['nginx', 'nginx', '--s3', { stack: true }], ['other', 'ostatné služby servera', '--s4', { stack: true }]] },
      { title: 'Takt CPU [GHz]', y: { min: 0.6, max: 3.0, zero: false }, series: [['freq', 'priemer jadier', '--s7']] },
      { title: 'Teplota [°C]', y: { zero: false }, series: [['temp', 'CPU package', '--s2'], ['temp_nvme', 'NVMe', '--s3']] },
      { title: 'Príkon (RAPL) [W]', series: [['p_psys', 'platforma (psys)', '--s1'], ['p_pkg', 'CPU package', '--s2'], ['p_core', 'jadrá', '--s3']] },
      { title: 'Perióda broadcastu [s]', y: { zero: false, min: 1 }, series: [['bc_period', 'interval medzi LiveUpdate', '--s7']], hlines: [{ y: 1.5, label: 'nominálne 1,5 s' }] },
      { title: 'LiveUpdate správa [kB]', series: [['bc_kb', 'veľkosť JSON', '--s1']] },
      { title: '.NET GC pauza [ms/s]', series: [['gc_pause_ms', 'čas v GC', '--s5']] },
      { title: 'Tlak na CPU – PSI some [%]', series: [['psi_cpu', 'čas, keď niečo čakalo na CPU', '--s2']] },
      { title: 'Susedia a prístroj [ms]', y: { log: true, min: 0.05 }, series: [['dns_ms', 'DNS dotaz (Technitium)', '--s1'], ['jit_p99', 'jitter plánovača p99', '--s2'], ['wifi_ms', 'ping cez Wi-Fi', '--s3']] },
      { title: 'Odchádzajúce dáta eno1 [MB/s]', series: [['net_tx', 'TX', '--s3']] },
      { title: 'Pripojení diváci (SignalR)', series: [['spectators', 'diváci', '--s7', { step: true }]] },
    ];
    panels.forEach((p, i) => {
      const el = document.createElement('div'); strip.appendChild(el);
      new Plot(el, {
        title: p.title, group: 'strip', height: i === 0 ? 120 : 92, bands, bandLabels: i === 0, peerTips: true, forceLegend: false,
        pad: { l: 56, r: 14, t: i === 0 ? 16 : 6, b: i === panels.length - 1 ? 24 : 6 },
        x: { time: true, fmt: i === panels.length - 1 ? clock : () => '' , tipFmt: clock },
        y: Object.assign({ fmt: auto, tipFmt: p.tipFmt }, p.y || {}),
        series: p.series.map(([k, name, color, extra]) => Object.assign({ name, color, x: T, y: extra?.scale ? (S[k] || []).map((v) => v == null ? v : v * extra.scale) : (S[k] || []), width: 1.3 }, extra || {})),
        hlines: p.hlines,
      });
      el.querySelector('.ptc-legend').style.display = 'flex';
      if (p.series.length < 2) el.querySelector('.ptc-legend').style.display = 'none';
    });
    // zoom presets
    const zb = $('#zoombar');
    const groupsE = {};
    (D.phases || []).forEach((p) => { const k = p.name.slice(0, 2); (groupsE[k] = groupsE[k] || [Infinity, -Infinity]); groupsE[k][0] = Math.min(groupsE[k][0], p.t0); groupsE[k][1] = Math.max(groupsE[k][1], p.t1); });
    const names = { E0: 'E0 kľud', E1: 'E1 pole', E2: 'E2 skok', E3: 'E3 schody', E4: 'E4 chirp', E5: 'E5 impulz', E6: 'E6 diváci', E7: 'E7 pretek' };
    const mk = (label, z) => { const b = document.createElement('button'); b.className = 'chip'; b.type = 'button'; b.textContent = label; b.onclick = () => { window.PTC.groups.strip[0].setZoom(z); zb.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-pressed', String(c === b))); }; zb.appendChild(b); return b; };
    mk('Celý beh', null).setAttribute('aria-pressed', 'true');
    Object.keys(groupsE).sort().forEach((k) => mk(names[k] || k, [groupsE[k][0] - 20, groupsE[k][1] + 20]));
  }

  // ------------------------------------------------------------------ 02 phase-resolved latency (the lock)
  if (D.sync && D.sync.length && has('#sync')) {
    const sy = D.sync.find((x) => x.name === 'E7 soak') || D.sync[D.sync.length - 1];
    new Plot('#sync', { title: `Latencia POST vs. čas od začiatku zostavovania pohľadu (${sy.name}, ${num(sy.n, 0)} požiadaviek)`, height: 330,
      x: { label: 's od začiatku zostavovania', min: 0, max: 1.6, fmt: f(1) }, y: { label: 'ms', tipFmt: ms, min: 0, max: 1600 },
      series: [{ name: 'jednotlivé požiadavky', color: '--s1', x: sy.d, y: sy.lat, points: true, r: 1.6, noTip: true },
        { name: 'medián (50 ms biny)', color: '--c-ink', x: sy.bins, y: sy.p50, width: 2.2, fmt: ms },
        { name: 'p90', color: '--s2', x: sy.bins, y: sy.p90, width: 1.6, fmt: ms }],
      vlines: sy.B ? [{ x: sy.B, label: `B ≈ ${num(sy.B, 2)} s` }] : [], annotations: [],
      hlines: [] });
    D.sync.forEach((x) => { const el = document.querySelector('[data-sync="' + x.name + '"]'); if (el) el.textContent = x.B == null ? '–' : num(x.B, 2) + ' s'; });
  }

  // ------------------------------------------------------------------ E1 field size
  if ((D.field || D.big?.field) && has('#field-cpu')) {
    // two races: 400 registered (this run) and 1 500 registered (the first run, until it saturated)
    const sets = [[D.field?.rows, 'pretek 400 prihlásených', '--s1'], [D.big?.field?.rows, 'pretek 1 500 prihlásených', '--s2']].filter((s) => s[0]);
    const pts = (k, fmt) => sets.map(([R, name, color]) => ({ name, color, x: R.map((r) => r.n), y: R.map((r) => r[k]), points: true, connect: true, r: 4, fmt }));
    new Plot('#field-cpu', { title: 'CPU aplikácie vs. aktívni bežci', height: 230, x: { label: 'aktívni bežci', min: 0 }, y: { label: 'jadrá' }, series: pts('app', f(2, 'jadra')) });
    new Plot('#field-p99', { title: 'p99 latencia POST /positions', height: 230, x: { label: 'aktívni bežci', min: 0 }, y: { log: true, label: 'ms', tipFmt: ms }, hlines: [{ y: 1500, label: 'hranica 1,5 s' }], series: pts('p99', ms) });
    new Plot('#field-bc', { title: 'Perióda broadcastu vs. aktívni bežci', height: 230, x: { label: 'aktívni bežci', min: 0 }, y: { label: 's', min: 0 }, hlines: [{ y: 1.5, label: 'nominálne 1,5 s' }], series: pts('bc_period', f(2, 's')) });
    new Plot('#field-kb', { title: 'Veľkosť LiveUpdate správy pre organizátora', height: 230, x: { label: 'aktívni bežci', min: 0 }, y: { label: 'kB' }, series: pts('bc_kb', f(0, 'kB')) });
    const rows = sets.flatMap(([R, name]) => R.map((r) => Object.assign({ race: name.replace('pretek ', '') }, r)));
    table('#field-tbl', rows, [['race', 'pretek'], ['n', 'aktívni', 0], ['target', 'req/s', 0], ['achieved', 'prijaté/s', 0], ['app', 'CPU app', 2], ['cpu', 'CPU spolu', 2], ['bc_period', 'perióda s', 2], ['bc_kb', 'kB', 0], ['p50', 'p50 ms', 1], ['p99', 'p99 ms', 0], ['err_rate', 'chyby %', 1, (v) => num(v * 100, 1)], ['psys', 'W', 2], ['temp', '°C', 1]], (r) => r.p99 > 1500 || r.err_rate > 0.02);
  }

  // ------------------------------------------------------------------ E2 step response
  if (D.step && has('#step-cpu')) {
    const s = D.step;
    const sm = (a, n) => movAvg(a, n);
    new Plot('#step-cpu', { title: 'CPU (10 Hz surové, čiary = kĺzavý priemer 5 s)', height: 250, x: { label: 's od skoku', fmt: f(0) }, y: { label: 'jadrá', max: 2.05 }, vlines: [{ x: 0, label: 'skok' }],
      series: [{ name: 'aplikácia 10 Hz', color: '--s5', x: s.t, y: s.app, width: 0.6, noTip: true },
        { name: 'celý server', color: '--s2', x: s.t, y: sm(s.cpu, 50), width: 2 }, { name: 'aplikácia', color: '--s1', x: s.t, y: sm(s.app, 50), width: 2 }, { name: 'PostgreSQL', color: '--s3', x: s.t, y: sm(s.db, 50), width: 2 }] });
    new Plot('#step-lat', { title: 'Latencia servera (1 s surové, čiary = kĺzavý priemer 10 s)', height: 250, x: { label: 's od skoku', fmt: f(0) }, y: { log: true, label: 'ms', tipFmt: ms }, vlines: [{ x: 0, label: 'skok' }],
      series: [{ name: 'p99 za 1 s', color: '--s5', x: s.s_t, y: s.p99, width: 0.6, noTip: true }, { name: 'p50', color: '--s1', x: s.s_t, y: sm(s.p50, 10), width: 2, fmt: ms }, { name: 'p99', color: '--s2', x: s.s_t, y: sm(s.p99, 10), width: 2, fmt: ms }] });
    const fit = s.temp_fit;
    const fitY = fit ? s.t.map((t) => (t < 0 ? fit.T0 : fit.T0 + fit.dT * (1 - Math.exp(-t / fit.tau)))) : [];
    new Plot('#step-temp', { title: 'Teplota CPU a fit 1. rádu', height: 250, x: { label: 's od skoku', fmt: f(0) }, y: { label: '°C', zero: false }, vlines: [{ x: 0 }],
      series: [{ name: 'meranie 10 Hz', color: '--s2', x: s.t, y: s.temp, width: 1 }].concat(fit ? [{ name: `T₀ + ΔT·(1 − e^(−t/τ)), τ = ${num(fit.tau, 0)} s`, color: '--c-ink', x: s.t, y: fitY, dash: true, width: 1.6 }] : []) });
    new Plot('#step-freq', { title: 'Príkon platformy (psys), 10 Hz a kĺzavý priemer 5 s', height: 250, x: { label: 's od skoku', fmt: f(0) }, y: { label: 'W', zero: false }, vlines: [{ x: 0, label: 'skok' }],
      series: [{ name: 'psys 10 Hz', color: '--s5', x: s.t, y: s.psys, width: 0.6, noTip: true }, { name: 'psys 5 s', color: '--s1', x: s.t, y: movAvg(s.psys, 50), width: 2, fmt: f(2, 'W') }] });
  }

  // ------------------------------------------------------------------ E3 staircase
  if (D.stairs && has('#cap-lat')) {
    const R = D.stairs.rows, X = R.map((r) => r.achieved);
    const pts = (k, name, color) => ({ name, color, x: X, y: R.map((r) => r[k]), points: true, connect: true, r: 4, fmt: ms });
    new Plot('#cap-lat', { title: 'Priepustnosť vs. latencia servera (percentily)', height: 300, x: { label: 'dosiahnuté req/s', min: 0 }, y: { log: true, label: 'ms', tipFmt: ms },
      hlines: [{ y: 1500, label: 'hranica 1,5 s' }],
      series: [pts('p50', 'p50', '--s1'), pts('p90', 'p90', '--s3'), pts('p99', 'p99', '--s2'), pts('p999', 'p99,9', '--s7')]
        .concat(D.field ? [{ name: 'p99 v E1 (mobily po 5 s)', color: '--c-ink2', x: D.field.rows.map((r) => r.achieved), y: D.field.rows.map((r) => r.p99), points: true, r: 4, fmt: ms }] : []) });
    new Plot('#cap-cpu', { title: 'Využitie CPU', height: 300, x: { label: 'dosiahnuté req/s', min: 0 }, y: { label: 'jadrá z 2', min: 0, max: 2 },
      series: [{ name: 'CPU aplikácia', color: '--s1', x: X, y: R.map((r) => r.app), points: true, connect: true, r: 4 }, { name: 'CPU celý server', color: '--s2', x: X, y: R.map((r) => r.cpu), points: true, connect: true, r: 4 }] });
    new Plot('#cap-mj', { title: 'Energia na jeden request (nad kľudovým príkonom)', height: 300, x: { label: 'dosiahnuté req/s', min: 0 }, y: { label: 'mJ/req', min: 0 },
      series: [{ name: 'mJ na request', color: '--s4', x: X, y: R.map((r) => r.mj_per_req), points: true, connect: true, r: 4 }] });
    table('#cap-tbl', R, [['rps', 'cieľ', 0], ['achieved', 'dosiahnuté', 1], ['err_rate', 'chyby %', 2, (v) => num(v * 100, 2)], ['n503', '503', 0], ['p50', 'p50 ms', 1], ['p90', 'p90 ms', 1], ['p99', 'p99 ms', 0], ['p999', 'p99,9 ms', 0], ['c_p99', 'klient p99', 0], ['app', 'CPU app', 2], ['cpu', 'CPU', 2], ['psi', 'PSI %', 0], ['bc_period', 'broadcast s', 2], ['psys', 'W', 2], ['mj_per_req', 'mJ/req', 1], ['temp', '°C', 1]], (r) => r.p99 > 1500 || r.err_rate > 0.02);
  }

  // ------------------------------------------------------------------ E4 chirp / Bode
  if (D.bode && has('#bode-trace')) {
    const b = D.bode, tr = b.trace;
    new Plot('#bode-trace', { title: 'Budenie (chirp) a odozva v čase', height: 220, x: { label: 's', fmt: clock, tipFmt: clock }, y: { label: 'req/s' },
      series: [{ name: 'prijaté 202/s (1 s)', color: '--s5', x: tr.t, y: tr.ok, width: 0.8 }, { name: 'prijaté, kĺzavý priemer 10 s', color: '--s1', x: tr.t, y: movAvg(tr.ok, 10), width: 2 },
        { name: 'cieľ', color: '--c-ink', x: tr.t, y: tr.target, dash: true, width: 1.4 }] });
    new Plot('#bode-trace2', { height: 160, x: { label: 's', fmt: clock, tipFmt: clock }, y: { label: '°C', zero: false }, series: [{ name: 'teplota CPU', color: '--s2', x: tr.t, y: tr.temp, width: 1.3 }] });
    const tau = D.step?.temp_fit?.tau;
    const chans = [['app', 'Aplikácia: CPU [jadrá / (req/s)]', '--s1', (v) => num(v * 1000, 2) + ' ms CPU/req'], ['psys', 'Príkon platformy [W / (req/s)]', '--s3', (v) => num(v * 1000, 1) + ' mJ/req'], ['temp', 'Teplota CPU [°C / (req/s)]', '--s2', (v) => num(v, 4) + ' °C/(req/s)']];
    const yr = { app: [1e-4, 0.03], psys: [1e-3, 0.3], temp: [1e-3, 0.3] };
    chans.forEach(([k, title, color, tip]) => {
      const m = b[k], [ymin, ymax] = yr[k];
      // where the 2-sigma interval reaches below zero the lower bound is unbounded: draw it to the axis floor
      const ser = [{ name: '±2σ', color, x: b.f, y: m.lo.map((v, i) => (v == null || v < ymin || m.mag[i] - 2 * (m.hi[i] - m.mag[i]) / 2 <= 0 ? ymin : v)), y2: m.hi.map((v) => Math.min(v, ymax)), band: true, noLegend: true, noTip: true },
        { name: '|H(f)|', color, x: b.f, y: m.mag, points: true, connect: true, r: 3, fmt: tip }];
      if (k === 'temp' && tau) {
        const lowf = m.mag.slice(0, 6).filter((v) => v != null).sort((p, q) => p - q);
        const K = lowf[Math.floor(lowf.length / 2)];
        ser.push({ name: `model 1. rádu, τ = ${num(tau, 0)} s zo skoku`, color: '--c-ink', dash: true, x: b.f, y: b.f.map((fr) => K / Math.sqrt(1 + Math.pow(2 * Math.PI * fr * tau, 2))), fmt: tip });
      }
      new Plot('#bode-' + k + '-m', { title, height: 200, x: { log: true, label: 'Hz', fmt: hz }, y: { log: true, fmt: auto, min: ymin, max: ymax }, series: ser, forceLegend: k === 'temp',
        vlines: k === 'temp' && tau ? [{ x: 1 / (2 * Math.PI * tau), label: 'f_c' }] : [] });
      new Plot('#bode-' + k + '-p', { title: 'fáza [°]', height: 140, x: { log: true, label: 'Hz', fmt: hz }, y: { zero: false, fmt: f(0) }, series: [{ name: 'fáza', color, x: b.f, y: m.phase, points: true, connect: true, r: 2.5, fmt: f(0, '°') }] });
      new Plot('#bode-' + k + '-c', { title: 'R² lock-in fitu (podiel vysvetleného rozptylu)', height: 110, x: { log: true, label: 'Hz', fmt: hz }, y: { min: 0, max: 1, fmt: f(1) }, series: [{ name: 'R²', color: '--c-ink2', x: b.f, y: m.coh, width: 1.2, area: true }] });
    });
  }
  function movAvg(a, n) { const h = Math.floor(n / 2); return a.map((_, i) => { let sum = 0, c = 0; for (let j = i - h; j <= i + h; j++) { const v = a[j]; if (v != null) { sum += v; c++; } } return c ? sum / c : null; }); }
  function hz(v) { return v >= 1 ? num(v, 1) : v >= 0.01 ? num(v, 2) : num(v, 3); }

  if (D.portrait && has('#portrait')) {
    const p = D.portrait, n = p.t.length;
    const ramp = () => (root.dataset.themeActive === 'dark' ? ['#104281', '#1c5cab', '#2a78d6', '#5598e7', '#86b6ef', '#b7d3f6'] : ['#b7d3f6', '#86b6ef', '#5598e7', '#2a78d6', '#1c5cab', '#104281']);
    new Plot('#portrait', { title: 'Fázový portrét: príkon → teplota počas chirpu (farba = čas)', height: 320, x: { label: 'príkon psys [W]', zero: false }, y: { label: '°C', zero: false },
      series: [{ name: 'trajektória', color: '--s1', x: p.psys, y: p.temp, points: true, r: 2.2, colorFn: (i) => { const r = ramp(); return r[Math.min(r.length - 1, Math.floor(i / n * r.length))]; } }] });
  }

  // ------------------------------------------------------------------ E5 impulse
  if (D.impulse && has('#imp-rate')) {
    const im = D.impulse;
    const each = im.each_ok.map((y, i) => ({ name: `impulz ${i + 1}`, color: ['--s3', '--s4', '--s5'][i], x: im.t, y, width: 1, noTip: false }));
    new Plot('#imp-rate', { title: `Odpovede za sekundu po impulze ${num(im.rows[0]?.sent, 0)} požiadaviek (bin 0,5 s)`, height: 260, x: { label: 's od impulzu', fmt: f(0), min: -3, max: 15 }, y: { label: 'odpovede/s' }, vlines: [{ x: 0, label: 'impulz' }],
      series: [{ name: '202 priemer', color: '--s1', x: im.t, y: im.ok, width: 2 }, { name: '503 priemer', color: '--crit', x: im.t, y: im.e503, width: 2 }].concat(each) });
    new Plot('#imp-cpu', { title: 'CPU servera (priemer troch impulzov)', height: 260, x: { label: 's od impulzu', fmt: f(0), min: -10, max: 40 }, y: { label: 'jadrá', max: 2.05 }, vlines: [{ x: 0 }],
      series: [{ name: 'CPU', color: '--s2', x: im.t, y: im.cpu, area: true }] });
    table('#imp-tbl', im.rows.map((r) => Object.assign({}, r, { name: r.name.replace('E5 impulse', 'Impulz') })), [['name', 'impulz'], ['sent', 'odoslané', 0], ['ok', '202', 0], ['e503', '503', 0], ['other', 'iné', 0], ['c_p50', 'p50 ms', 0], ['c_p99', 'p99 ms', 0], ['c_max', 'max ms', 0], ['drain_s', 'vyprázdnenie s', 2]]);
  }

  // ------------------------------------------------------------------ E6 fan-out
  if (D.fanout && has('#fan-bw')) {
    const R = D.fanout.rows, X = R.map((r) => r.n);
    new Plot('#fan-bw', { title: 'Dátový tok k divákom', height: 250, x: { label: 'diváci', min: 0 }, y: { label: 'MB/s' },
      series: [{ name: 'prijaté klientom', color: '--s1', x: X, y: R.map((r) => r.spec_mbps), points: true, connect: true }, { name: 'eno1 TX (server)', color: '--s3', x: X, y: R.map((r) => r.tx), points: true, connect: true }] });
    new Plot('#fan-cpu', { title: 'CPU', height: 250, x: { label: 'diváci', min: 0 }, y: { label: 'jadrá z 2', min: 0, max: 2 },
      series: [{ name: 'CPU aplikácia', color: '--s1', x: X, y: R.map((r) => r.app), points: true, connect: true, r: 4 }, { name: 'CPU celý server', color: '--s2', x: X, y: R.map((r) => r.cpu), points: true, connect: true, r: 4 }] });
    const delay = 30; // the event's configured spectator delay (seed: SpectatorDelaySeconds 30)
    new Plot('#fan-lag', { title: 'Doručenie divákom nad nastavené oneskorenie 30 s', height: 250, x: { label: 'diváci', min: 0 }, y: { label: 's', min: 0 },
      series: [{ name: 'p50', color: '--s1', x: X, y: R.map((r) => (r.spec_lag_p50 == null ? null : r.spec_lag_p50 - delay)), points: true, connect: true, r: 4, fmt: f(2, 's') },
        { name: 'p99', color: '--s2', x: X, y: R.map((r) => (r.spec_lag_p99 == null ? null : r.spec_lag_p99 - delay)), points: true, connect: true, r: 4, fmt: f(2, 's') }] });
    table('#fan-tbl', R, [['n', 'diváci', 0], ['spec_kb', 'kB/správa', 0], ['spec_rate', 'správ/s na diváka', 3], ['spec_mbps', 'MB/s', 2], ['tx', 'TX MB/s', 2], ['app', 'CPU app', 2], ['cpu', 'CPU', 2], ['bc_period', 'broadcast s', 2], ['spec_lag_p50', 'lag p50 s', 2], ['spec_lag_p99', 'lag p99 s', 2], ['p99', 'POST p99 ms', 0]]);
  }

  // ------------------------------------------------------------------ A1 spectra
  if (D.spectra && has('#psd')) {
    const sp = D.spectra, ser = [], ann = [];
    [['E0 noise floor', 'kľud (E0)', '--s3'], ['E7 soak', 'pretek (E7)', '--s1']].forEach(([k, name, color]) => {
      if (!sp[k]) return;
      ser.push({ name, color, x: sp[k].f, y: sp[k].cpu, width: 1.2, fmt: f(1, 'dB') });
      ann.length = 0; // label only the last (loaded) spectrum: two sets of labels collide
      const placed = [];
      (sp[k].peaks || []).slice().sort((a, b) => b.prom - a.prom).forEach((pk) => {
        if (placed.some((x) => Math.abs(Math.log10(x) - Math.log10(pk.f)) < 0.14) || placed.length >= 6) return; // keep labels apart
        placed.push(pk.f);
        ann.push({ x: pk.f, y: pk.db, text: pk.id || (pk.period_s >= 1 ? num(pk.period_s, 1) + ' s' : num(pk.f, 2) + ' Hz') });
      });
    });
    new Plot('#psd', { title: 'Výkonová spektrálna hustota CPU (Welch, 10 Hz, okno 204,8 s, prekryv 50 %)', height: 300, x: { log: true, label: 'Hz', fmt: hz, min: 0.003, max: 5 }, y: { label: 'dB (jadrá²/Hz)', zero: false }, series: ser, annotations: ann });
    const late = [];
    [['E0 noise floor', 'kľud', '--s3'], ['E7 soak', 'pretek', '--s1']].forEach(([k, name, color]) => sp[k] && late.push({ name, color, x: sp[k].f, y: sp[k].p_psys, width: 1.2, fmt: f(1, 'dB') }));
    new Plot('#psd-w', { title: 'PSD príkonu platformy (psys)', height: 220, x: { log: true, label: 'Hz', fmt: hz, min: 0.003, max: 5 }, y: { label: 'dB (W²/Hz)', zero: false }, series: late });
  }
  if (D.spectrogram && has('#sgram')) {
    const g = D.spectrogram, cw = g.t.length > 1 ? g.t[1] - g.t[0] : 100;
    const ye = g.f.map((v) => v - g.df / 2).concat([g.f[g.f.length - 1] + g.df / 2]);
    const flat = g.z.flat().filter((v) => v != null).sort((a, b) => a - b);
    new Heat('#sgram', { title: 'Spektrogram CPU celého behu (okno 204,8 s, krok 102,4 s)', height: 300, x: g.t.map((t) => t - cw / 2), colW: cw, yEdges: ye, y: { log: false, label: 'Hz', fmt: hz, min: 0, max: 2 }, z: g.z,
      zmin: flat[Math.floor(flat.length * 0.6)], zmax: flat[Math.floor(flat.length * 0.999)], zFmt: f(1), zUnit: 'dB', yUnit: 'Hz', bands, marks: [{ y: 1 / 1.5, label: 'broadcast 0,667 Hz' }, { y: 2 / 1.5, label: '2×' }] });
  }

  // ------------------------------------------------------------------ A2 latency
  if (D.heatmap && has('#heat')) {
    const h = D.heatmap;
    new Heat('#heat', { title: 'Rozdelenie latencie servera v čase (stĺpec 10 s, logaritmické biny)', height: 300, x: h.t, colW: h.col_s, yEdges: h.edges, y: { log: true, label: 'ms', fmt: auto, min: 0.3, max: 30000 }, z: h.z, zlog: true,
      zUnit: 'požiadaviek', yUnit: 'ms', bands });
  }
  if (D.e2e && has('#e2e')) {
    const cols = ['--s3', '--s7', '--s1', '--s4', '--s2'];
    const q = Array.from({ length: 101 }, (_, i) => i);
    new Plot('#e2e', { title: 'Mobil → organizátor: CDF latencie end-to-end', height: 280, x: { label: 's', min: 0 }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
      series: D.e2e.phases.map((p, i) => ({ name: p.name.replace('noise floor', 'kľud').replace('step up', 'skok').replace('soak', 'pretek').replace('field', 'pole'), color: cols[i % cols.length], x: p.cdf_x, y: q, width: 1.8, fmt: f(0, '%') })),
      x2: null });
  }
  if (D.wifi && has('#wifi')) {
    const q = Array.from({ length: 51 }, (_, i) => i * 2);
    new Plot('#wifi', { title: 'Wi-Fi RTT (ping 10 Hz počas celého behu) – šum prístroja', height: 220, x: { log: true, label: 'ms', fmt: auto }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
      series: [{ name: 'CDF', color: '--s3', x: D.wifi.q, y: q, width: 1.8, fmt: f(0, '%') }] });
  }

  // ------------------------------------------------------------------ A3 jitter & neighbours
  if (D.jitter && has('#jit')) {
    const bins = D.jitter.bins_us, X = bins.map((_, i) => i);
    new Plot('#jit', { title: 'Oneskorenie zobudenia 1 kHz slučky (podiel zobudení, log₂ biny)', height: 280, x: { label: 'µs', fmt: (i) => (Number.isInteger(i) && bins[i] != null ? auto(bins[i]) : ''), min: -0.6, max: bins.length - 0.4 }, y: { log: true, label: '%', min: 1e-5, max: 100 },
      series: D.jitter.regimes.map((r, i) => { const tot = r.hist.reduce((a, b) => a + b, 0) || 1; return { name: r.name, color: ['--s3', '--s2', '--s1'][i], x: X, y: r.hist.map((v) => (v ? v / tot * 100 : null)), bars: true, barWidth: 30, fmt: (v) => num(v, v < 0.01 ? 5 : 3) + ' %' }; }), xgrid: false });
  }
  if (D.dns && has('#dns')) {
    const q = Array.from({ length: 51 }, (_, i) => i * 2);
    new Plot('#dns', { title: 'DNS (Technitium) – CDF času odpovede', height: 280, x: { log: true, label: 'ms', fmt: auto }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
      series: D.dns.map((r, i) => ({ name: r.name, color: ['--s3', '--s2', '--s1'][i], x: r.q, y: q, width: 1.8, fmt: f(0, '%') })) });
    table('#dns-tbl', D.dns, [['name', 'režim'], ['n', 'dotazov', 0], ['p50', 'p50 ms', 2], ['p99', 'p99 ms', 2], ['max', 'max ms', 1], ['lost', 'stratené', 0]]);
    table('#jit-tbl', D.jitter.regimes, [['name', 'režim'], ['mean', 'priemer µs', 0], ['p99', 'p99 µs', 0], ['p999', 'p99,9 µs', 0], ['max', 'max µs', 0]]);
  }

  // ------------------------------------------------------------------ A4 correlations
  if (D.xcorr && has('#xcorr')) {
    const x = D.xcorr;
    new Plot('#xcorr', { title: `Krížová korelácia s p99 latenciou (${x.phase}, 1 s)`, height: 260, x: { label: 'posun [s] (+ = latencia nasleduje)', fmt: f(0) }, y: { label: 'r', min: -0.3, max: 1 },
      series: [{ name: 'GC pauza → p99', color: '--s5', x: x.lags, y: x.gc_p99, points: true, connect: true }, { name: 'perióda broadcastu → p99', color: '--s7', x: x.lags, y: x.bc_p99, points: true, connect: true }] });
  }
  if (D.clock && has('#clk')) {
    new Plot('#clk', { title: 'Odchýlka hodín PC voči serveru počas behu (po odčítaní strednej hodnoty)', height: 200, x: { time: true, label: 'čas behu' }, y: { label: 'ms', zero: false },
      series: [{ name: 'offset', color: '--s1', x: D.clock.drift.t, y: D.clock.drift.off, points: true, connect: true, fmt: f(2, 'ms') }] });
  }

  // ------------------------------------------------------------------ table helper
  function table(sel, rows, cols, bad) {
    const el = $(sel); if (!el) return;
    const th = cols.map((c) => `<th>${c[1]}</th>`).join('');
    const tr = rows.map((r) => `<tr>${cols.map((c) => { const v = r[c[0]]; const s = c[3] ? c[3](v) : typeof v === 'number' ? num(v, c[2] ?? 2) : (v ?? '–'); return `<td${bad && bad(r) && c === cols[0] ? ' class="bad"' : ''}>${s}</td>`; }).join('')}</tr>`).join('');
    el.innerHTML = `<div class="tblwrap"><table class="tbl"><thead><tr>${th}</tr></thead><tbody>${tr}</tbody></table></div>`;
  }

  document.querySelectorAll('[data-zoom]').forEach((a) => a.addEventListener('click', () => {
    const chip = [...document.querySelectorAll('#zoombar .chip')].find((c) => c.textContent.startsWith(a.dataset.zoom));
    if (chip) setTimeout(() => chip.click(), 50);
  }));

  // ------------------------------------------------------------------ nav highlight
  const links = [...document.querySelectorAll('#nav a')];
  const io = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) links.forEach((a) => a.classList.toggle('on', a.getAttribute('href') === '#' + e.target.id)); }), { rootMargin: '-30% 0px -65% 0px' });
  document.querySelectorAll('section[id]').forEach((s) => io.observe(s));
  applyTheme();
})();
