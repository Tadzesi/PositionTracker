/* PT charts — a small canvas chart library for the load-test report (no dependencies).
   Line/area/stacked/step/points/bars on linear or log axes, heatmaps, min/max envelope decimation (every spike survives
   at any zoom, like an oscilloscope), synchronised crosshair and drag-to-zoom inside a group, theme-aware via CSS variables. */
(function () {
  'use strict';
  const DPR = () => Math.max(1, window.devicePixelRatio || 1);
  const root = document.documentElement;
  const cv = (name) => getComputedStyle(root).getPropertyValue(name).trim();
  const all = [];
  const groups = {};

  // ------------------------------------------------------------------ formatting
  // number format and the few words drawn on the canvas follow the page language (<html lang>, the report's PTC_T translations)
  const EN = root.lang === 'en';
  const LOCALE = EN ? 'en-US' : 'sk-SK';
  const tr = (s) => (window.PTC_T ? window.PTC_T(s) : s);
  const num = (v, d) => (v == null || !isFinite(v)) ? '–' : v.toLocaleString(LOCALE, { maximumFractionDigits: d, minimumFractionDigits: 0 });
  function auto(v) {
    if (v == null || !isFinite(v)) return '–';
    const a = Math.abs(v);
    if (a === 0) return '0';
    if (a >= 1e6) return num(v / 1e6, 1) + ' M';
    if (a >= 1e4) return num(v / 1e3, 0) + ' k';
    if (a >= 100) return num(v, 0);
    if (a >= 10) return num(v, 1);
    if (a >= 1) return num(v, 2);
    if (a >= 0.01) return num(v, 3);
    if (a >= 0.001) return num(v, 4);
    if (a >= 0.0001) return num(v, 5);
    return EN ? v.toExponential(1) : v.toExponential(1).replace('.', ',');
  }
  function clock(s) {
    if (s == null || !isFinite(s)) return '–';
    const neg = s < 0; s = Math.abs(s);
    const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), ss = Math.floor(s % 60);
    return (neg ? '−' : '') + (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(ss).padStart(2, '0');
  }

  // ------------------------------------------------------------------ scales and ticks
  function niceStep(span, n) {
    const raw = span / Math.max(1, n), p = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / p;
    return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p;
  }
  function linTicks(a, b, n) {
    const s = niceStep(b - a, n), out = [];
    for (let v = Math.ceil(a / s) * s; v <= b + s * 1e-9; v += s) out.push(+v.toPrecision(12));
    return out;
  }
  function timeTicks(a, b, n) {
    const steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 3600, 7200];
    const raw = (b - a) / Math.max(1, n);
    const s = steps.find((x) => x >= raw) || 7200, out = [];
    for (let v = Math.ceil(a / s) * s; v <= b; v += s) out.push(v);
    return out;
  }
  function logTicks(a, b) {
    const out = [], lo = Math.floor(Math.log10(a)), hi = Math.ceil(Math.log10(b));
    const mult = hi - lo > 4 ? [1] : hi - lo > 2 ? [1, 3] : [1, 2, 5];
    for (let e = lo; e <= hi; e++) for (const m of mult) { const v = m * Math.pow(10, e); if (v >= a * 0.999 && v <= b * 1.001) out.push(v); }
    return out;
  }
  function makeScale(o, lo, hi, p0, p1) {
    if (o.log) {
      const a = Math.log10(lo), b = Math.log10(hi);
      return { f: (v) => p0 + (Math.log10(Math.max(v, lo * 1e-6)) - a) / (b - a) * (p1 - p0), inv: (px) => Math.pow(10, a + (px - p0) / (p1 - p0) * (b - a)), lo, hi };
    }
    return { f: (v) => p0 + (v - lo) / (hi - lo || 1) * (p1 - p0), inv: (px) => lo + (px - p0) / (p1 - p0) * (hi - lo), lo, hi };
  }

  // ------------------------------------------------------------------ base chart
  class Chart {
    constructor(el, o) {
      this.el = typeof el === 'string' ? document.querySelector(el) : el;
      this.o = o;
      this.el.classList.add('ptc');
      this.el.innerHTML = '';
      if (o.title) { const t = document.createElement('div'); t.className = 'ptc-title'; t.textContent = o.title; this.el.appendChild(t); }
      this.legendEl = document.createElement('div'); this.legendEl.className = 'ptc-legend'; this.el.appendChild(this.legendEl);
      this.box = document.createElement('div'); this.box.className = 'ptc-box'; this.box.style.height = (o.height || 220) + 'px'; this.el.appendChild(this.box);
      this.cv = document.createElement('canvas'); this.box.appendChild(this.cv);
      this.ov = document.createElement('canvas'); this.ov.className = 'ptc-ov'; this.box.appendChild(this.ov);
      this.tip = document.createElement('div'); this.tip.className = 'ptc-tip'; this.box.appendChild(this.tip);
      this.cv.setAttribute('role', 'img');
      this.cv.setAttribute('aria-label', o.aria || o.title || tr('graf'));
      this.hidden = new Set();
      this.pad = Object.assign({ l: 56, r: 14, t: 10, b: 30 }, o.pad || {});
      if (o.group) (groups[o.group] = groups[o.group] || []).push(this);
      all.push(this);
      new ResizeObserver(() => this.draw()).observe(this.box);
      this.bind();
      this.legend();
    }
    get W() { return this.box.clientWidth; }
    get H() { return this.box.clientHeight; }
    legend() {
      const s = (this.o.series || []).filter((x) => !x.noLegend);
      if (s.length < 2 && !this.o.forceLegend) { this.legendEl.style.display = 'none'; return; }
      this.legendEl.innerHTML = '';
      s.forEach((x) => {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'ptc-li';
        b.innerHTML = `<i style="--c:var(${x.color})" class="${x.dash ? 'dash' : x.area || x.stack || x.bars ? 'fill' : ''}"></i>${x.name}`;
        b.onclick = () => { this.hidden.has(x.name) ? this.hidden.delete(x.name) : this.hidden.add(x.name); b.classList.toggle('off'); this.draw(); };
        this.legendEl.appendChild(b);
      });
    }
    ctx(c) {
      const d = DPR(), w = this.W, h = this.H;
      if (c.width !== Math.round(w * d) || c.height !== Math.round(h * d)) { c.width = Math.round(w * d); c.height = Math.round(h * d); c.style.width = w + 'px'; c.style.height = h + 'px'; }
      const g = c.getContext('2d'); g.setTransform(d, 0, 0, d, 0, 0); g.clearRect(0, 0, w, h); return g;
    }
    xDomain() {
      if (this.zoom) return this.zoom;
      const o = this.o.x || {};
      if (o.min != null && o.max != null) return [o.min, o.max];
      let lo = Infinity, hi = -Infinity;
      this.eachX((v) => { if (v < lo) lo = v; if (v > hi) hi = v; });
      return [o.min ?? lo, o.max ?? hi];
    }
    eachX(fn) { (this.o.series || []).forEach((s) => s.x.forEach((v) => v != null && isFinite(v) && (!this.o.x?.log || v > 0) && fn(v))); }
    axes(g, xs, ys, xo, yo) {
      const { l, r, t, b } = this.pad, w = this.W, h = this.H;
      g.font = '11px ' + cv('--font-num');
      g.lineWidth = 1;
      const xt = xo.log ? logTicks(xs.lo, xs.hi) : xo.time ? timeTicks(xs.lo, xs.hi, Math.max(2, (w - l - r) / 90)) : linTicks(xs.lo, xs.hi, Math.max(2, (w - l - r) / 80));
      const yt = yo.log ? logTicks(ys.lo, ys.hi) : linTicks(ys.lo, ys.hi, Math.max(2, (h - t - b) / 34));
      g.strokeStyle = cv('--c-grid');
      g.beginPath();
      yt.forEach((v) => { const y = Math.round(ys.f(v)) + 0.5; g.moveTo(l, y); g.lineTo(w - r, y); });
      if (this.o.xgrid !== false) xt.forEach((v) => { const x = Math.round(xs.f(v)) + 0.5; g.moveTo(x, t); g.lineTo(x, h - b); });
      g.stroke();
      g.strokeStyle = cv('--c-axis');
      g.beginPath(); g.moveTo(l, h - b + 0.5); g.lineTo(w - r, h - b + 0.5); g.stroke();
      g.fillStyle = cv('--c-muted');
      g.textAlign = 'right'; g.textBaseline = 'middle';
      const yf = yo.fmt || auto;
      yt.forEach((v) => g.fillText(yf(v), l - 6, ys.f(v)));
      g.textAlign = 'center'; g.textBaseline = 'top';
      const xf = xo.fmt || (xo.time ? clock : auto);
      let lastX = -1e9;
      xt.forEach((v) => { const x = xs.f(v); const s = xf(v); const tw = g.measureText(s).width; if (x - tw / 2 > lastX + 6 && x + tw / 2 <= w) { g.fillText(s, x, h - b + 6); lastX = x + tw / 2; } });
      if (yo.label) { g.save(); g.textAlign = 'left'; g.textBaseline = 'top'; g.fillStyle = cv('--c-muted'); g.fillText(yo.label, l + 4, t + 1); g.restore(); }
      if (xo.label) { g.textAlign = 'right'; g.textBaseline = 'bottom'; g.fillText(xo.label, w - r, h - b - 3); }
    }
    bands(g, xs) {
      const { t, b } = this.pad, h = this.H;
      (this.o.bands || []).forEach((bd, i) => {
        const x0 = Math.max(this.pad.l, xs.f(bd.x0)), x1 = Math.min(this.W - this.pad.r, xs.f(bd.x1));
        if (x1 <= x0) return;
        if (bd.shade !== false && i % 2 === 0) { g.fillStyle = cv('--c-band'); g.fillRect(x0, t, x1 - x0, h - t - b); }
        if (this.o.bandLabels && x1 - x0 > 26) {
          g.font = '600 10px ' + cv('--font-ui'); g.fillStyle = cv('--c-muted'); g.textAlign = 'left'; g.textBaseline = 'top';
          let s = bd.label; while (s.length > 1 && g.measureText(s).width > x1 - x0 - 6) s = s.slice(0, -1);
          g.fillText(s, x0 + 3, t + 2);
        }
      });
      (this.o.vlines || []).forEach((v) => {
        const x = Math.round(xs.f(v.x)) + 0.5; if (x < this.pad.l || x > this.W - this.pad.r) return;
        g.strokeStyle = cv(v.color || '--c-ink2'); g.setLineDash([3, 3]); g.beginPath(); g.moveTo(x, t); g.lineTo(x, h - b); g.stroke(); g.setLineDash([]);
        if (v.label) { g.font = '10px ' + cv('--font-ui'); g.fillStyle = cv('--c-ink2'); g.textAlign = 'left'; g.textBaseline = 'top'; g.fillText(v.label, x + 4, h - b - 16); }
      });
    }
    bind() {
      let drag = null;
      const pos = (e) => { const r = this.box.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
      this.box.addEventListener('pointermove', (e) => {
        const p = pos(e);
        if (drag) { drag.x1 = p.x; this.overlayAll(null, drag); return; }
        if (!this.xs) return;
        const xv = this.xs.inv(p.x);
        if (p.x < this.pad.l || p.x > this.W - this.pad.r) { this.hover(null); return; }
        const peers = this.o.group ? groups[this.o.group] : [this];
        peers.forEach((c) => c.hover(c === this ? { xv, px: p.x, py: p.y } : { xv, peer: true }));
      });
      this.box.addEventListener('pointerleave', () => { const peers = this.o.group ? groups[this.o.group] : [this]; peers.forEach((c) => c.hover(null)); });
      if (this.o.zoom === false) return;
      this.box.addEventListener('pointerdown', (e) => { if (e.button !== 0) return; const p = pos(e); drag = { x0: p.x, x1: p.x }; this.box.setPointerCapture(e.pointerId); });
      this.box.addEventListener('pointerup', () => {
        if (!drag) return;
        const a = Math.min(drag.x0, drag.x1), b = Math.max(drag.x0, drag.x1); drag = null;
        if (b - a > 8 && this.xs) this.setZoom([this.xs.inv(a), this.xs.inv(b)]);
        else this.overlayAll(null, null);
      });
      this.box.addEventListener('dblclick', () => this.setZoom(null));
    }
    setZoom(z) { const peers = this.o.group ? groups[this.o.group] : [this]; peers.forEach((c) => { c.zoom = z; c.draw(); }); document.dispatchEvent(new CustomEvent('ptc-zoom', { detail: { group: this.o.group, zoom: z } })); }
    overlayAll(h, drag) { const g = this.ctx(this.ov); if (drag) { g.fillStyle = cv('--c-sel'); g.fillRect(Math.min(drag.x0, drag.x1), this.pad.t, Math.abs(drag.x1 - drag.x0), this.H - this.pad.t - this.pad.b); } }
    hover() {}
  }

  // ------------------------------------------------------------------ XY plot
  class Plot extends Chart {
    constructor(el, o) { super(el, o); this.draw(); }
    yDomain(xr) {
      const yo = this.o.y || {};
      if (yo.min != null && yo.max != null) return [yo.min, yo.max];
      let lo = Infinity, hi = -Infinity;
      const ser = (this.o.series || []).filter((s) => !this.hidden.has(s.name));
      const stacks = ser.filter((s) => s.stack);
      if (stacks.length) {
        const n = stacks[0].y.length;
        for (let i = 0; i < n; i++) { const xv = stacks[0].x[i]; if (xv < xr[0] || xv > xr[1]) continue; let sum = 0; stacks.forEach((s) => { sum += s.y[i] || 0; }); hi = Math.max(hi, sum); lo = Math.min(lo, 0); }
      }
      ser.filter((s) => !s.stack).flatMap((s) => (s.y2 ? [s, Object.assign({}, s, { y: s.y2 })] : [s])).forEach((s) => s.y.forEach((v, i) => {
        const xv = s.x[i];
        if (v == null || !isFinite(v) || xv < xr[0] || xv > xr[1] || (yo.log && v <= 0)) return;
        if (v < lo) lo = v; if (v > hi) hi = v;
      }));
      (this.o.hlines || []).forEach((h) => { lo = Math.min(lo, h.y); hi = Math.max(hi, h.y); });
      if (!isFinite(lo)) { lo = yo.log ? 1 : 0; hi = yo.log ? 10 : 1; }
      if (yo.log) { lo = yo.min ?? Math.pow(10, Math.floor(Math.log10(lo))); hi = yo.max ?? Math.pow(10, Math.ceil(Math.log10(hi))); return [lo, hi]; }
      if (yo.zero !== false) lo = Math.min(0, lo);
      if (hi === lo) hi = lo + 1;
      const padv = (hi - lo) * 0.06;
      return [yo.min ?? lo, yo.max ?? (hi + padv)];
    }
    draw() {
      if (!this.W) return;
      const g = this.ctx(this.cv), o = this.o, xo = o.x || {}, yo = o.y || {};
      const { l, r, t, b } = this.pad, w = this.W, h = this.H;
      const xr = this.xDomain();
      const yr = this.yDomain(xr);
      const xs = this.xs = makeScale(xo, xr[0], xr[1], l, w - r);
      const ys = this.ys = makeScale(yo, yr[0], yr[1], h - b, t);
      g.fillStyle = cv('--c-surface'); g.fillRect(0, 0, w, h);
      this.bands(g, xs);
      this.axes(g, xs, ys, xo, yo);
      g.save(); g.beginPath(); g.rect(l, t - 1, w - l - r, h - t - b + 2); g.clip();
      const ser = (o.series || []).filter((s) => !this.hidden.has(s.name));
      // stacked areas first (2 px surface gap between the layers)
      const stacks = ser.filter((s) => s.stack);
      if (stacks.length) {
        const n = stacks[0].x.length, base = new Float64Array(n);
        stacks.forEach((s) => {
          const top = new Float64Array(n);
          for (let i = 0; i < n; i++) top[i] = base[i] + (isFinite(s.y[i]) && s.y[i] != null ? Math.max(0, s.y[i]) : 0);
          const pts = this.envelopeIdx(s.x, top, xs, xr);
          const ptsB = this.envelopeIdx(s.x, base, xs, xr);
          g.beginPath();
          pts.forEach(([x, y], i) => (i ? g.lineTo(x, ys.f(y)) : g.moveTo(x, ys.f(y))));
          for (let i = ptsB.length - 1; i >= 0; i--) g.lineTo(ptsB[i][0], ys.f(ptsB[i][1]));
          g.closePath(); g.fillStyle = cv(s.color); g.globalAlpha = 0.85; g.fill(); g.globalAlpha = 1;
          g.beginPath(); pts.forEach(([x, y], i) => (i ? g.lineTo(x, ys.f(y)) : g.moveTo(x, ys.f(y)))); g.strokeStyle = cv('--c-surface'); g.lineWidth = 1.5; g.stroke();
          base.set(top);
        });
      }
      ser.filter((s) => s.bars).forEach((s, k, arr) => this.drawBars(g, s, xs, ys, k, arr.length));
      ser.filter((s) => !s.stack && !s.bars).forEach((s) => {
        const col = cv(s.color);
        if (s.band) { // a min..max band (s.y = lower, s.y2 = upper)
          g.beginPath(); let started = false;
          for (let i = 0; i < s.x.length; i++) { if (s.y[i] == null) continue; const X = xs.f(s.x[i]); started ? g.lineTo(X, ys.f(s.y2[i])) : g.moveTo(X, ys.f(s.y2[i])); started = true; }
          for (let i = s.x.length - 1; i >= 0; i--) { if (s.y[i] == null) continue; g.lineTo(xs.f(s.x[i]), ys.f(s.y[i])); }
          g.closePath(); g.fillStyle = col; g.globalAlpha = 0.18; g.fill(); g.globalAlpha = 1; return;
        }
        const pts = s.points ? null : this.envelopeIdx(s.x, s.y, xs, xr, s.step, yo.log);
        if (pts) {
          if (s.area) {
            g.beginPath(); let first = null, last = null;
            pts.forEach(([x, y]) => { if (y == null) return; if (!first) { g.moveTo(x, ys.f(y)); first = x; } else g.lineTo(x, ys.f(y)); last = x; });
            if (first != null) { g.lineTo(last, ys.f(Math.max(yr[0], yo.log ? yr[0] : 0))); g.lineTo(first, ys.f(Math.max(yr[0], yo.log ? yr[0] : 0))); g.closePath(); g.fillStyle = col; g.globalAlpha = 0.16; g.fill(); g.globalAlpha = 1; }
          }
          g.beginPath(); let pen = false;
          pts.forEach(([x, y]) => { if (y == null || !isFinite(y) || (yo.log && y <= 0)) { pen = false; return; } const Y = ys.f(y); pen ? g.lineTo(x, Y) : g.moveTo(x, Y); pen = true; });
          g.strokeStyle = col; g.lineWidth = s.width || 1.6; g.lineJoin = 'round'; g.setLineDash(s.dash ? [5, 4] : []); g.stroke(); g.setLineDash([]);
        }
        if (s.points) {
          const rr = s.r || 3;
          for (let i = 0; i < s.x.length; i++) {
            const v = s.y[i]; if (v == null || !isFinite(v) || s.x[i] < xr[0] || s.x[i] > xr[1]) continue;
            g.beginPath(); g.arc(xs.f(s.x[i]), ys.f(v), rr, 0, 7);
            g.fillStyle = s.colorFn ? s.colorFn(i) : col; g.fill(); g.lineWidth = 1; g.strokeStyle = cv('--c-surface'); g.stroke();
          }
          if (s.connect) { g.beginPath(); let pen = false; s.x.forEach((xv, i) => { const v = s.y[i]; if (v == null || !isFinite(v)) { pen = false; return; } const Y = ys.f(v); pen ? g.lineTo(xs.f(xv), Y) : g.moveTo(xs.f(xv), Y); pen = true; }); g.strokeStyle = col; g.lineWidth = 1.2; g.globalAlpha = 0.5; g.stroke(); g.globalAlpha = 1; }
        }
        if (s.labels) s.labels.forEach((lb) => { if (!lb) return; const i = lb.i; g.font = '600 10.5px ' + cv('--font-ui'); g.fillStyle = cv('--c-ink'); g.textAlign = lb.align || 'left'; g.textBaseline = 'bottom'; g.fillText(lb.text, xs.f(s.x[i]) + (lb.dx ?? 6), ys.f(s.y[i]) - (lb.dy ?? 4)); });
      });
      (o.hlines || []).forEach((hl) => {
        const y = Math.round(ys.f(hl.y)) + 0.5; g.strokeStyle = cv(hl.color || '--c-ink2'); g.setLineDash([4, 4]); g.beginPath(); g.moveTo(l, y); g.lineTo(w - r, y); g.stroke(); g.setLineDash([]);
        if (hl.label) { g.font = '10.5px ' + cv('--font-ui'); g.fillStyle = cv('--c-ink2'); g.textAlign = 'right'; g.textBaseline = 'bottom'; g.fillText(hl.label, w - r - 4, y - 3); }
      });
      (o.annotations || []).forEach((an) => {
        const X = xs.f(an.x), Y = ys.f(an.y); if (X < l || X > w - r) return;
        g.fillStyle = cv('--c-ink'); g.beginPath(); g.arc(X, Y, 3, 0, 7); g.fill();
        g.font = '600 10.5px ' + cv('--font-ui'); g.textAlign = an.align || 'center'; g.textBaseline = 'bottom'; g.fillText(an.text, X + (an.dx || 0), Y - 6 + (an.dy || 0));
      });
      g.restore();
      if (this.zoom) { g.font = '10px ' + cv('--font-ui'); g.fillStyle = cv('--c-muted'); g.textAlign = 'right'; g.textBaseline = 'top'; g.fillText(tr('zoom · dvojklik = späť'), w - r - 2, t + 2); }
      this.ctx(this.ov);
    }
    /** min/max envelope per pixel column: [[px, value], ...] in x order */
    envelopeIdx(X, Y, xs, xr, step, log) {
      const out = [], n = X.length;
      let col = null, mn = 0, mx = 0, fi = 0, la = 0, imn = 0, imx = 0, gap = false;
      const flush = () => {
        if (col == null) return;
        const seq = imn < imx ? [mn, mx] : [mx, mn];
        out.push([col, fi]); if (seq[0] !== fi) out.push([col, seq[0]]); if (seq[1] !== seq[0]) out.push([col, seq[1]]); if (la !== seq[1]) out.push([col, la]);
      };
      let prevY = null;
      for (let i = 0; i < n; i++) {
        const xv = X[i], yv = Y[i];
        if (xv < xr[0] - (xr[1] - xr[0]) * 0.02 || xv > xr[1] + (xr[1] - xr[0]) * 0.02) continue;
        if (yv == null || !isFinite(yv) || (log && yv <= 0)) { flush(); col = null; out.push([xs.f(xv), null]); continue; }
        const c = Math.round(xs.f(xv) * 2) / 2;
        if (step && prevY != null) { flush(); col = null; out.push([xs.f(xv), prevY]); }
        if (c !== col) { flush(); col = c; mn = mx = fi = la = yv; imn = imx = i; }
        else { if (yv < mn) { mn = yv; imn = i; } if (yv > mx) { mx = yv; imx = i; } la = yv; }
        prevY = yv;
      }
      flush();
      return out;
    }
    drawBars(g, s, xs, ys, k, n) {
      const xsArr = s.x, ws = s.barWidth || (xsArr.length > 1 ? Math.abs(xs.f(xsArr[1]) - xs.f(xsArr[0])) * 0.8 : 20);
      const bw = Math.max(2, ws / n - 2);
      const y0 = ys.f(this.o.y?.log ? ys.lo : Math.max(0, ys.lo));
      g.fillStyle = cv(s.color);
      xsArr.forEach((xv, i) => {
        const v = s.y[i]; if (v == null || !isFinite(v) || v <= (this.o.y?.log ? 0 : -Infinity)) return;
        const x = xs.f(xv) - ws / 2 + k * (bw + 2) + 1, y = ys.f(v), hgt = y0 - y;
        if (hgt <= 0) return;
        const rr = Math.min(3, bw / 2, hgt);
        g.beginPath(); g.moveTo(x, y0); g.lineTo(x, y + rr); g.quadraticCurveTo(x, y, x + rr, y); g.lineTo(x + bw - rr, y); g.quadraticCurveTo(x + bw, y, x + bw, y + rr); g.lineTo(x + bw, y0); g.closePath(); g.fill();
      });
    }
    hover(hv) {
      const g = this.ctx(this.ov);
      if (!hv || !this.xs) { this.tip.style.display = 'none'; return; }
      const px = this.xs.f(hv.xv);
      if (px < this.pad.l || px > this.W - this.pad.r) { this.tip.style.display = 'none'; return; }
      g.strokeStyle = cv('--c-cross'); g.lineWidth = 1; g.beginPath(); g.moveTo(Math.round(px) + 0.5, this.pad.t); g.lineTo(Math.round(px) + 0.5, this.H - this.pad.b); g.stroke();
      if (hv.peer && !this.o.peerTips) { this.tip.style.display = 'none'; return; }
      const ser = (this.o.series || []).filter((s) => !this.hidden.has(s.name) && !s.noTip);
      const rows = [];
      let xlab = null;
      ser.forEach((s) => {
        const i = nearest(s.x, hv.xv); if (i < 0) return;
        const v = s.y[i]; if (v == null) return;
        if (xlab == null) xlab = (this.o.x?.tipFmt || this.o.x?.fmt || (this.o.x?.time ? clock : auto))(s.x[i]);
        if (Math.abs(this.xs.f(s.x[i]) - px) > 40) return;
        const Y = this.ys.f(v);
        if (!s.bars) { g.beginPath(); g.arc(this.xs.f(s.x[i]), Y, 3.5, 0, 7); g.fillStyle = cv(s.color); g.fill(); g.strokeStyle = cv('--c-surface'); g.lineWidth = 2; g.stroke(); }
        rows.push(`<div><i style="--c:var(${s.color})"></i><span>${s.name}</span><b>${(s.fmt || this.o.y?.tipFmt || this.o.y?.fmt || auto)(v)}${s.unit ? ' ' + s.unit : ''}</b></div>`);
      });
      if (!rows.length) { this.tip.style.display = 'none'; return; }
      const ph = phaseAt(this.o.bands, hv.xv);
      this.tip.innerHTML = `<div class="ptc-tip-h">${(this.o.x?.label ? '' : '')}${xlab ?? ''}${ph ? ' · ' + ph : ''}</div>` + rows.join('');
      this.tip.style.display = 'block';
      const tw = this.tip.offsetWidth, th = this.tip.offsetHeight;
      let x = px + 14; if (x + tw > this.W - 4) x = px - tw - 14;
      let y = (hv.py ?? this.pad.t) - th / 2; y = Math.max(2, Math.min(this.H - th - 2, y));
      this.tip.style.transform = `translate(${x}px,${y}px)`;
    }
  }
  function phaseAt(bands, x) { if (!bands) return null; const b = bands.find((b) => x >= b.x0 && x < b.x1); return b ? b.label : null; }
  function nearest(X, v) {
    let lo = 0, hi = X.length - 1; if (hi < 0) return -1;
    if (X[0] > X[hi]) { let best = -1, bd = Infinity; X.forEach((x, i) => { const d = Math.abs(x - v); if (d < bd) { bd = d; best = i; } }); return best; }
    while (hi - lo > 1) { const m = (lo + hi) >> 1; if (X[m] < v) lo = m; else hi = m; }
    return Math.abs(X[lo] - v) <= Math.abs(X[hi] - v) ? lo : hi;
  }

  // ------------------------------------------------------------------ heatmap (x columns x y bins)
  class Heat extends Chart {
    constructor(el, o) { super(el, o); this.draw(); }
    eachX(fn) { fn(this.o.x[0]); fn(this.o.x[this.o.x.length - 1] + (this.o.colW || 0)); }
    draw() {
      if (!this.W) return;
      const o = this.o, g = this.ctx(this.cv), { l, r, t, b } = this.pad, w = this.W, h = this.H;
      const xr = this.xDomain();
      const ye = o.yEdges;
      const yo = Object.assign({ log: true }, o.y || {});
      const xs = this.xs = makeScale(o.xo || { time: true }, xr[0], xr[1], l, w - r);
      const ys = this.ys = makeScale(yo, yo.min ?? ye[0], yo.max ?? ye[ye.length - 1], h - b, t);
      g.fillStyle = cv('--c-surface'); g.fillRect(0, 0, w, h);
      const ramp = (o.ramp || 'seq').split(',').length > 1 ? o.ramp.split(',') : rampColors();
      let zmax = o.zmax ?? 0, zmin = o.zmin ?? Infinity;
      if (o.zmax == null) o.z.forEach((row) => row.forEach((v) => { if (v != null && v > zmax) zmax = v; if (v != null && v < zmin) zmin = v; }));
      if (o.zmin != null) zmin = o.zmin;
      const tf = o.zlog ? (v) => Math.log10(1 + v) / Math.log10(1 + zmax) : (v) => (v - zmin) / (zmax - zmin || 1);
      const cw = o.colW || (o.x[1] - o.x[0]);
      for (let yi = 0; yi < o.z.length; yi++) {
        const y0 = ys.f(ye[yi]), y1 = ys.f(ye[yi + 1]);
        for (let xi = 0; xi < o.x.length; xi++) {
          const v = o.z[yi][xi]; if (v == null || (o.zlog && v <= 0)) continue;
          const x0 = xs.f(o.x[xi]), x1 = xs.f(o.x[xi] + cw); if (x1 < l || x0 > w - r) continue;
          const k = Math.max(0, Math.min(1, tf(v)));
          g.fillStyle = ramp[Math.min(ramp.length - 1, Math.floor(k * ramp.length))];
          g.fillRect(Math.floor(x0), Math.floor(y1), Math.ceil(x1 - x0) + 0.5, Math.ceil(y0 - y1) + 0.5);
        }
      }
      this.bands(g, xs);
      const xo = o.xo || { time: true };
      this.axes(g, xs, ys, Object.assign({}, xo), yo);
      (o.lines || []).forEach((ln) => {
        g.beginPath(); let pen = false;
        ln.x.forEach((xv, i) => { const v = ln.y[i]; if (v == null) { pen = false; return; } const X = xs.f(xv), Y = ys.f(v); pen ? g.lineTo(X, Y) : g.moveTo(X, Y); pen = true; });
        g.strokeStyle = cv(ln.color); g.lineWidth = 1.6; g.setLineDash(ln.dash ? [4, 3] : []); g.stroke(); g.setLineDash([]);
      });
      (o.marks || []).forEach((m) => {
        const Y = ys.f(m.y); g.strokeStyle = cv('--c-ink2'); g.setLineDash([2, 3]); g.beginPath(); g.moveTo(l, Y); g.lineTo(w - r, Y); g.stroke(); g.setLineDash([]);
        g.font = '600 10px ' + cv('--font-ui'); g.fillStyle = cv('--c-ink'); g.textAlign = 'right'; g.textBaseline = 'bottom'; g.fillText(m.label, w - r - 4, Y - 2);
      });
      this.ctx(this.ov);
    }
    hover(hv) {
      const g = this.ctx(this.ov);
      if (!hv || !this.xs) { this.tip.style.display = 'none'; return; }
      const px = this.xs.f(hv.xv);
      g.strokeStyle = cv('--c-cross'); g.beginPath(); g.moveTo(Math.round(px) + 0.5, this.pad.t); g.lineTo(Math.round(px) + 0.5, this.H - this.pad.b); g.stroke();
      if (hv.peer || hv.py == null) { this.tip.style.display = 'none'; return; }
      const o = this.o, cw = o.colW || (o.x[1] - o.x[0]);
      const xi = Math.floor((hv.xv - o.x[0]) / cw); const yv = this.ys.inv(hv.py);
      const yi = o.yEdges.findIndex((e, i) => i < o.yEdges.length - 1 && yv >= e && yv < o.yEdges[i + 1]);
      if (xi < 0 || xi >= o.x.length || yi < 0) { this.tip.style.display = 'none'; return; }
      const yf = o.y?.fmt || auto;
      this.tip.innerHTML = `<div class="ptc-tip-h">${(o.xo?.fmt || clock)(o.x[xi])}${phaseAt(o.bands, hv.xv) ? ' · ' + phaseAt(o.bands, hv.xv) : ''}</div><div><span>${yf(o.yEdges[yi])} – ${yf(o.yEdges[yi + 1])} ${o.yUnit || ''}</span><b>${(o.zFmt || auto)(o.z[yi][xi])} ${o.zUnit || ''}</b></div>`;
      this.tip.style.display = 'block';
      const tw = this.tip.offsetWidth; let x = px + 14; if (x + tw > this.W) x = px - tw - 14;
      this.tip.style.transform = `translate(${x}px,${Math.max(2, hv.py - 30)}px)`;
    }
  }
  function rampColors() {
    const dark = root.dataset.themeActive === 'dark';
    const steps = ['#cde2fb', '#b7d3f6', '#9ec5f4', '#86b6ef', '#6da7ec', '#5598e7', '#3987e5', '#2a78d6', '#256abf', '#1c5cab', '#184f95', '#104281', '#0d366b'];
    return dark ? steps.slice().reverse() : steps;
  }

  function redrawAll() { all.forEach((c) => { c.draw(); }); }
  window.PTC = { Plot, Heat, redrawAll, auto, clock, num, groups };
})();

/* Renders the load-test report from two data files (perf/report/build.py): core (the summary, the comparison and every value the
   text binds to; enough for the first screen) and data (the long time series), both preloaded by the page. Every number on the
   page comes from them. Text goes through t(): the key is the Slovak text, the English page carries the translations (#i18n);
   the build fails when a key has none. Sections are drawn when they come near the viewport. */
(function () {
  'use strict';
  const root = document.documentElement;
  const I18N = JSON.parse(document.getElementById('i18n')?.textContent || '{}');
  const t = (s, ...a) => (I18N[s] ?? s).replace(/\{(\d)\}/g, (_, i) => a[+i]);
  window.PTC_T = t;
  const { Plot, Heat, auto, clock, num } = window.PTC;
  const $ = (s) => document.querySelector(s);
  const has = (sel) => !!$(sel);
  const D = {};
  let S = {}, T = [], bands = [];

  // ------------------------------------------------------------------ theme and language
  const mq = matchMedia('(prefers-color-scheme: dark)');
  function applyTheme() {
    const th = root.getAttribute('data-theme');
    root.dataset.themeActive = th || (mq.matches ? 'dark' : 'light');
    document.querySelectorAll('.seg button[data-t]').forEach((b) => b.setAttribute('aria-pressed', String((b.dataset.t === 'auto' && !th) || b.dataset.t === th)));
    window.PTC.redrawAll();
  }
  document.querySelectorAll('.seg button[data-t]').forEach((b) => b.addEventListener('click', () => {
    const th = b.dataset.t;
    if (th === 'auto') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', th);
    try { th === 'auto' ? localStorage.removeItem('pt-site-theme') : localStorage.setItem('pt-site-theme', th); } catch (e) { /* private mode */ }
    applyTheme();
  }));
  mq.addEventListener('change', applyTheme);
  root.dataset.themeActive = root.getAttribute('data-theme') || (mq.matches ? 'dark' : 'light');
  // the language switch: the choice is a cookie the web server reads before the browser's Accept-Language
  document.querySelectorAll('a[data-lang]').forEach((a) => a.addEventListener('click', () => {
    document.cookie = `pt_lang=${a.dataset.lang}; path=/tracker/perf/; max-age=31536000; samesite=lax`;
  }));

  // ------------------------------------------------------------------ helpers
  const get = (path) => path.split('.').reduce((o, k) => (o == null ? o : o[k]), D);
  const f = (d, unit) => (v) => num(v, d) + (unit ? ' ' + unit : '');
  const ms = (v) => (v == null ? '–' : v >= 1000 ? num(v / 1000, 2) + ' s' : num(v, v < 10 ? 2 : v < 100 ? 1 : 0) + ' ms');
  const short = (n) => n.replace('noise floor', t('kľud')).replace(/^E1 field /, 'E1 ').replace(/^E3 step /, 'E3 ').replace(/^E6 spectators /, 'E6 ')
    .replace('step up', '↑').replace('step down', '↓').replace('pre-step', '·').replace(/^E5 impulse /, 'E5 #').replace(' cooldown', ' ⌁').replace(' soak', ' ' + t('pretek'));
  // names that come from the analysis in Slovak (regimes, identified spectral lines)
  const trName = (n) => { const m = /^Saturácia \((.+)\)$/.exec(n || ''); return m ? t('Saturácia ({0})', m[1]) : t(n); };
  const trPeak = (id) => { const m = /^(.*?) (\d+×)$/.exec(id || ''); return m ? t(m[1]) + ' ' + m[2] : t(id); };
  function movAvg(a, n) { const h = Math.floor(n / 2); return a.map((_, i) => { let sum = 0, c = 0; for (let j = i - h; j <= i + h; j++) { const v = a[j]; if (v != null) { sum += v; c++; } } return c ? sum / c : null; }); }
  function hz(v) { return v >= 1 ? num(v, 1) : v >= 0.01 ? num(v, 2) : num(v, 3); }
  function table(sel, rows, cols, bad) {
    const el = $(sel); if (!el) return;
    const th = cols.map((c) => `<th>${c[1]}</th>`).join('');
    const tr = rows.map((r) => `<tr>${cols.map((c) => { const v = r[c[0]]; const s = c[3] ? c[3](v) : typeof v === 'number' ? num(v, c[2] ?? 2) : (v ?? '–'); return `<td${bad && bad(r) && c === cols[0] ? ' class="bad"' : ''}>${s}</td>`; }).join('')}</tr>`).join('');
    el.innerHTML = `<div class="tblwrap"><table class="tbl"><thead><tr>${th}</tr></thead><tbody>${tr}</tbody></table></div>`;
  }
  function merge(a, b) { // deep merge of the data parts (core holds the scalars and short arrays, data the long arrays)
    Object.keys(b).forEach((k) => {
      if (Array.isArray(b[k]) && Array.isArray(a[k]) && b[k].length === a[k].length && b[k].every((x) => x && typeof x === 'object')) b[k].forEach((x, i) => merge(a[k][i], x));
      else if (b[k] && typeof b[k] === 'object' && !Array.isArray(b[k]) && a[k] && typeof a[k] === 'object') merge(a[k], b[k]);
      else a[k] = b[k];
    });
  }

  // ------------------------------------------------------------------ lazy sections: drawn once near the viewport and once their data is there
  const sections = [];
  let full = false;
  const io = new IntersectionObserver((es) => es.forEach((e) => { const s = sections.find((x) => x.el === e.target); if (s && e.isIntersecting) { s.near = true; draw(s); } }), { rootMargin: '900px 0px' });
  function sec(sel, needsFull, fn) { const el = $(sel); if (!el) return; const s = { el, needsFull, fn, near: false, done: false }; sections.push(s); io.observe(el); }
  function draw(s) {
    if (s.done || !s.near || (s.needsFull && !full)) return;
    s.done = true; io.unobserve(s.el);
    try { s.fn(); } catch (e) { console.error('[report]', e); }
  }
  window.addEventListener('beforeprint', () => sections.forEach((s) => { s.near = true; draw(s); }));

  // ------------------------------------------------------------------ data
  const urls = (document.querySelector('meta[name="pt-data"]')?.content || '').split(' ');
  const load = (u) => fetch(u, { credentials: 'same-origin' }).then((r) => { if (!r.ok) throw new Error(u + ' ' + r.status); return r.json(); });
  const fullP = load(urls[1]);
  load(urls[0]).then((core) => {
    merge(D, core);
    bind();
    register();
    return fullP;
  }).then((rest) => {
    merge(D, rest);
    S = D.series || {}; T = S.t || [];
    full = true;
    sections.forEach(draw);
  }).catch((e) => console.error('[report]', e));

  // ------------------------------------------------------------------ text bound to values, side meta
  function bind() {
    document.querySelectorAll('[data-v]').forEach((el) => {
      const v = get(el.dataset.v);
      const d = el.dataset.d != null ? +el.dataset.d : null;
      el.textContent = v == null ? '–' : typeof v === 'number' ? (el.dataset.ms != null ? ms(v) : d != null ? num(v, d) : auto(v)) : v;
    });
    bands = (D.phases || []).map((p) => ({ x0: p.t0, x1: p.t1, label: short(p.name), full: p.name }));
    if (has('#runid')) $('#runid').textContent = t('beh {0} · {1} min', D.generated ? D.generated.slice(0, 10) : '', num(D.kpi?.duration_min, 0));
    if (has('#sidemeta')) $('#sidemeta').innerHTML = `<b>${t('Hardvér')}</b><br>Intel NUC11ATKC2<br>Celeron N4505 · 2C/2T<br>15 GiB RAM · NVMe<br><br><b>${t('Softvér')}</b><br>.NET 10 · PostgreSQL 17<br>nginx · Docker (${t('37 kontajnerov')})<br><br>${t('Vygenerované')} ${D.generated?.replace('T', ' ').replace('+00:00', ' UTC') || ''}`;
    (D.sync || []).forEach((x) => { const el = document.querySelector('[data-sync="' + x.name + '"]'); if (el) el.textContent = x.B == null ? '–' : num(x.B, 2) + ' s'; });
  }

  function register() {
    // ---------------------------------------------------------------- 00b before/after (perf/analysis/compare.py): one colour per version
    const C = D.cmp;
    if (C) sec('#zmena', false, () => {
      const B = C.before, A = C.after, BEF = '--c-muted', AFT = '--s1';
      const before = t('pred'), after = t('po');
      const txt = (sel, v) => { const el = $(sel); if (el) el.textContent = v; };
      const txtB = (sel) => $(sel)?.textContent || '–';
      const phones = (r) => (r.capacity_rps == null ? null : r.capacity_rps * 5);
      const capTxt = (r) => (phones(r) == null ? '–' : (r.capacity_limited ? '' : '≥ ') + num(phones(r), 0));
      const times = (a, b) => (a && b ? '×' + num(b / a, b / a >= 10 ? 0 : 1) : '');
      txt('#cmp-cap-b', capTxt(B)); txt('#cmp-cap-a', capTxt(A)); txt('#cmp-cap-x', times(phones(B), phones(A)) + (A.capacity_limited ? '' : ' ' + t('a viac')));
      txt('#cmp-p99-b', ms(B.same_load.p99)); txt('#cmp-p99-a', ms(A.same_load.p99)); txt('#cmp-p99-x', times(A.same_load.p99, B.same_load.p99) + ' ' + t('rýchlejšie'));
      txt('#cmp-cpu-b', num(B.same_load.app, 2)); txt('#cmp-cpu-a', num(A.same_load.app, 2));
      txt('#cmp-cpu-x', t('−{0} % · uvoľnené pre ostatné služby servera', num((1 - A.same_load.app / B.same_load.app) * 100, 0)));

      const sync = (r, name, color) => [
        { name: name + ' · request', color, x: r.sync.d, y: r.sync.lat, points: true, r: 2.2, noLegend: true, noTip: true },
        { name: name + ' · ' + t('medián'), color, x: r.sync.bins, y: r.sync.p50, width: 2.4, fmt: ms },
      ];
      new Plot('#cmp-sync', { title: t('Latencia príjmu polohy vs. čas od začiatku zostavovania živého pohľadu (200 mobilov, čiarkovane = ďalší takt)'), height: 340,
        x: { label: t('s od začiatku zostavovania'), min: 0, max: B.sync.period * 1.3, fmt: f(1) }, y: { log: true, label: 'ms', min: 0.5, tipFmt: ms },
        vlines: [{ x: B.sync.period }], series: [...sync(B, before, BEF), ...sync(A, after, AFT)], forceLegend: true });
      txt('#cmp-B-b', B.sync.B == null ? '–' : num(B.sync.B * 1000, 0) + ' ms');
      txt('#cmp-B-a', A.sync.B == null || A.sync.B < 0.05 ? '< 50 ms' : num(A.sync.B * 1000, 0) + ' ms');

      new Plot('#cmp-psd', { title: t('Spektrum latencie (Welch, 20 Hz) · čiary = harmonické taktu'), height: 260,
        x: { label: 'Hz', min: 0.05, max: 3, fmt: f(2) }, y: { label: 'dB', zero: false },
        vlines: B.psd.lines.filter((v) => v <= 3).map((v, i) => ({ x: v, label: i ? '' : num(v, 2) + ' Hz' })),
        series: [{ name: before, color: BEF, x: B.psd.f, y: B.psd.db, width: 1.6, fmt: f(1, 'dB') }, { name: after, color: AFT, x: A.psd.f, y: A.psd.db, width: 1.6, fmt: f(1, 'dB') }] });
      txt('#cmp-line-b', B.psd.line_db[0] == null ? '–' : '+' + num(B.psd.line_db[0], 0) + ' dB');
      txt('#cmp-line-a', A.psd.line_db[0] == null ? '–' : (A.psd.line_db[0] >= 0 ? '+' : '') + num(A.psd.line_db[0], 0) + ' dB');

      const cap = (r, name, color) => ({ name, color, x: r.stairs.map((s) => s.rps * 5), y: r.stairs.map((s) => s.p99), points: true, connect: true, r: 4, fmt: ms });
      const bandsCap = phones(B) && phones(A) && phones(A) > phones(B) ? [{ x0: phones(B), x1: phones(A), label: t('získaná kapacita') }] : [];
      new Plot('#cmp-cap', { title: t('Záťaž vs. p99 latencia servera (schody po 90 s)'), height: 320, bands: bandsCap, bandLabels: true,
        x: { label: t('mobilov po 5 s (= req/s × 5)'), min: 0 }, y: { log: true, label: 'ms', tipFmt: ms }, hlines: [{ y: 1500, label: t('kritérium p99 1,5 s') }],
        series: [cap(B, before, BEF), cap(A, after, AFT)] });

      const I = (r) => r.impulse;
      new Plot('#cmp-imp-ok', { title: t('Hromadný štart 1 000 mobilov: prijaté polohy [req/s]'), height: 230, group: 'cmp-imp',
        x: { label: t('s od štartu'), min: -1, max: 8, fmt: f(0) }, y: { label: 'req/s' }, vlines: [{ x: 0, label: t('štart') }],
        series: [{ name: before, color: BEF, x: I(B).t, y: I(B).ok, width: 1.6, fmt: f(0, 'req/s') }, { name: after, color: AFT, x: I(A).t, y: I(A).ok, width: 1.6, fmt: f(0, 'req/s') }] });
      new Plot('#cmp-imp-lat', { title: t('Hromadný štart: najdlhšia odozva servera v okne 0,25 s'), height: 230, group: 'cmp-imp',
        x: { label: t('s od štartu'), min: -1, max: 8, fmt: f(0) }, y: { log: true, label: 'ms', tipFmt: ms }, vlines: [{ x: 0 }],
        series: [{ name: before, color: BEF, x: I(B).t, y: I(B).lat_max, width: 1.6, fmt: ms }, { name: after, color: AFT, x: I(A).t, y: I(A).lat_max, width: 1.6, fmt: ms }] });
      txt('#cmp-ip99-b', ms(I(B).c_p99)); txt('#cmp-ip99-a', ms(I(A).c_p99)); txt('#cmp-ip99-x', times(I(A).c_p99, I(B).c_p99));
      const CO = C.cold;
      if (CO) {
        txt('#cmp-cold-b', ms(CO.without.p99)); txt('#cmp-cold-a', ms(CO.with.p99)); txt('#cmp-cold-x', times(CO.with.p99, CO.without.p99) + ' ' + t('rýchlejšie'));
        txt('#cmp-colddb-b', num(CO.without.db_pending_max, 0)); txt('#cmp-colddb-a', num(CO.with.db_pending_max, 0));
        txt('#cmp-colderr-b', num(CO.without.err, 0)); txt('#cmp-colderr-a', num(CO.with.err, 0));
      }

      const why = [
        [t('Zámok drží príjem polôh'), txtB('#cmp-B-b'), txtB('#cmp-B-a'), t('dĺžka rampy v grafe vyššie')],
        [t('Čiara taktu v spektre latencie'), txtB('#cmp-line-b'), txtB('#cmp-line-a'), t('výška nad šumom pri 0,67 Hz')],
        [t('Čakanie na DB pripojenie na schodoch'), num(B.db_pending_stairs, 0), num(A.db_pending_stairs, 0), t('špička fronty Npgsql (pôvodná verzia pri {0} req/s)', num(B.db_pending_at, 0))],
        [t('CPU aplikácie pri 200 mobiloch'), num(B.same_load.app, 2) + ' ' + t('jadra'), num(A.same_load.app, 2) + ' ' + t('jadra'), t('z 2 jadier NUC')],
      ];
      $('#cmp-why').innerHTML = `<div class="tblwrap"><table class="tbl"><thead><tr><th style="text-align:left">${t('Príčina')}</th><th>${before}</th><th>${after}</th><th style="text-align:left">${t('čo to je')}</th></tr></thead><tbody>`
        + why.map((r) => `<tr><td style="text-align:left">${r[0]}</td><td>${r[1]}</td><td><b>${r[2]}</b></td><td style="text-align:left;color:var(--muted);white-space:normal;font-family:var(--font-ui);min-width:180px">${r[3]}</td></tr>`).join('') + '</tbody></table></div>';
    });

    // ---------------------------------------------------------------- 01 strip chart (the DAQ recorder view)
    sec('#strip', true, () => {
      const strip = $('#strip');
      if (!T.length) return;
      const panels = [
        { title: t('Záťaž [req/s]'), series: [['target', t('cieľ (generátor)'), '--c-ink2', { dash: true, width: 1.2 }], ['ok', t('202 prijaté'), '--s1'], ['e503', t('503 odmietnuté'), '--crit']] },
        { title: t('Latencia servera, POST /positions [ms]'), y: { log: true, min: 0.5 }, series: [['p50', 'p50', '--s1'], ['p99', 'p99', '--s2']], tipFmt: ms },
        { title: t('CPU podľa procesu [jadrá z 2]'), y: { min: 0, max: 2.05 }, series: [['app', t('aplikácia (.NET)'), '--s1', { stack: true }], ['db', 'PostgreSQL', '--s2', { stack: true }], ['nginx', 'nginx', '--s3', { stack: true }], ['other', t('ostatné služby servera'), '--s4', { stack: true }]] },
        { title: t('Takt CPU [GHz]'), y: { min: 0.6, max: 3.0, zero: false }, series: [['freq', t('priemer jadier'), '--s7']] },
        { title: t('Teplota [°C]'), y: { zero: false }, series: [['temp', 'CPU package', '--s2'], ['temp_nvme', 'NVMe', '--s3']] },
        { title: t('Príkon (RAPL) [W]'), series: [['p_psys', t('platforma (psys)'), '--s1'], ['p_pkg', 'CPU package', '--s2'], ['p_core', t('jadrá'), '--s3']] },
        { title: t('Perióda broadcastu [s]'), y: { zero: false, min: 1 }, series: [['bc_period', t('interval medzi LiveUpdate'), '--s7']], hlines: [{ y: 1.5, label: t('nominálne 1,5 s') }] },
        { title: t('LiveUpdate správa [kB]'), series: [['bc_kb', t('veľkosť JSON'), '--s1']] },
        { title: t('.NET GC pauza [ms/s]'), series: [['gc_pause_ms', t('čas v GC'), '--s5']] },
        { title: t('Tlak na CPU – PSI some [%]'), series: [['psi_cpu', t('čas, keď niečo čakalo na CPU'), '--s2']] },
        { title: t('Susedia a prístroj [ms]'), y: { log: true, min: 0.05 }, series: [['dns_ms', t('DNS dotaz (Technitium)'), '--s1'], ['jit_p99', t('jitter plánovača p99'), '--s2'], ['wifi_ms', t('ping cez Wi-Fi'), '--s3']] },
        { title: t('Odchádzajúce dáta eno1 [MB/s]'), series: [['net_tx', 'TX', '--s3']] },
        { title: t('Pripojení diváci (SignalR)'), series: [['spectators', t('diváci'), '--s7', { step: true }]] },
      ];
      panels.forEach((p, i) => {
        const el = document.createElement('div'); strip.appendChild(el);
        new Plot(el, {
          title: p.title, group: 'strip', height: i === 0 ? 120 : 92, bands, bandLabels: i === 0, peerTips: true, forceLegend: false,
          pad: { l: 56, r: 14, t: i === 0 ? 16 : 6, b: i === panels.length - 1 ? 24 : 6 },
          x: { time: true, fmt: i === panels.length - 1 ? clock : () => '', tipFmt: clock },
          y: Object.assign({ fmt: auto, tipFmt: p.tipFmt }, p.y || {}),
          series: p.series.map(([k, name, color, extra]) => Object.assign({ name, color, x: T, y: S[k] || [], width: 1.3 }, extra || {})),
          hlines: p.hlines,
        });
        el.querySelector('.ptc-legend').style.display = p.series.length < 2 ? 'none' : 'flex';
      });
      const zb = $('#zoombar');
      const groupsE = {};
      (D.phases || []).forEach((p) => { const k = p.name.slice(0, 2); (groupsE[k] = groupsE[k] || [Infinity, -Infinity]); groupsE[k][0] = Math.min(groupsE[k][0], p.t0); groupsE[k][1] = Math.max(groupsE[k][1], p.t1); });
      const names = { E0: t('E0 kľud'), E1: t('E1 pole'), E2: t('E2 skok'), E3: t('E3 schody'), E4: t('E4 chirp'), E5: t('E5 impulz'), E6: t('E6 diváci'), E7: t('E7 pretek') };
      const mk = (label, z) => { const b = document.createElement('button'); b.className = 'chip'; b.type = 'button'; b.textContent = label; b.onclick = () => { window.PTC.groups.strip[0].setZoom(z); zb.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-pressed', String(c === b))); }; zb.appendChild(b); return b; };
      mk(t('Celý beh'), null).setAttribute('aria-pressed', 'true');
      Object.keys(groupsE).sort().forEach((k) => mk(names[k] || k, [groupsE[k][0] - 20, groupsE[k][1] + 20]));
    });

    // ---------------------------------------------------------------- 02 phase-resolved latency (the lock)
    if (D.sync && D.sync.length) sec('#sync', true, () => {
      const sy = D.sync.find((x) => x.name === 'E7 soak') || D.sync[D.sync.length - 1];
      new Plot('#sync', { title: t('Latencia POST vs. čas od začiatku zostavovania pohľadu ({0}, {1} požiadaviek)', sy.name, num(sy.n, 0)), height: 330,
        x: { label: t('s od začiatku zostavovania'), min: 0, max: 1.6, fmt: f(1) }, y: { label: 'ms', tipFmt: ms, min: 0, max: 1600 },
        series: [{ name: t('jednotlivé požiadavky'), color: '--s1', x: sy.d, y: sy.lat, points: true, r: 1.6, noTip: true },
          { name: t('medián (50 ms biny)'), color: '--c-ink', x: sy.bins, y: sy.p50, width: 2.2, fmt: ms },
          { name: 'p90', color: '--s2', x: sy.bins, y: sy.p90, width: 1.6, fmt: ms }],
        vlines: sy.B ? [{ x: sy.B, label: `B ≈ ${num(sy.B, 2)} s` }] : [] });
    });

    // ---------------------------------------------------------------- E1 field size
    if (D.field || D.big?.field) sec('#pole', false, () => {
      const sets = [[D.field?.rows, t('pretek 400 prihlásených'), '--s1'], [D.big?.field?.rows, t('pretek 1 500 prihlásených'), '--s2']].filter((s) => s[0]);
      const pts = (k, fmt) => sets.map(([R, name, color]) => ({ name, color, x: R.map((r) => r.n), y: R.map((r) => r[k]), points: true, connect: true, r: 4, fmt }));
      const xl = { label: t('aktívni bežci'), min: 0 };
      new Plot('#field-cpu', { title: t('CPU aplikácie vs. aktívni bežci'), height: 230, x: xl, y: { label: t('jadrá') }, series: pts('app', f(2, t('jadra'))) });
      new Plot('#field-p99', { title: t('p99 latencia POST /positions'), height: 230, x: xl, y: { log: true, label: 'ms', tipFmt: ms }, hlines: [{ y: 1500, label: t('hranica 1,5 s') }], series: pts('p99', ms) });
      new Plot('#field-bc', { title: t('Perióda broadcastu vs. aktívni bežci'), height: 230, x: xl, y: { label: 's', min: 0 }, hlines: [{ y: 1.5, label: t('nominálne 1,5 s') }], series: pts('bc_period', f(2, 's')) });
      new Plot('#field-kb', { title: t('Veľkosť LiveUpdate správy pre organizátora'), height: 230, x: xl, y: { label: 'kB' }, series: pts('bc_kb', f(0, 'kB')) });
      const rows = sets.flatMap(([R, name]) => R.map((r) => Object.assign({ race: name.replace(t('pretek') + ' ', '') }, r)));
      table('#field-tbl', rows, [['race', t('pretek')], ['n', t('aktívni'), 0], ['target', 'req/s', 0], ['achieved', t('prijaté/s'), 0], ['app', t('CPU app'), 2], ['cpu', t('CPU spolu'), 2], ['bc_period', t('perióda s'), 2], ['bc_kb', 'kB', 0], ['p50', 'p50 ms', 1], ['p99', 'p99 ms', 0], ['err_rate', t('chyby %'), 1, (v) => num(v * 100, 1)], ['psys', 'W', 2], ['temp', '°C', 1]], (r) => r.p99 > 1500 || r.err_rate > 0.02);
    });

    // ---------------------------------------------------------------- E2 step response
    if (D.step) sec('#skok', true, () => {
      const s = D.step;
      const xl = { label: t('s od skoku'), fmt: f(0) };
      new Plot('#step-cpu', { title: t('CPU (10 Hz surové, čiary = kĺzavý priemer 5 s)'), height: 250, x: xl, y: { label: t('jadrá'), max: 2.05 }, vlines: [{ x: 0, label: t('skok') }],
        series: [{ name: t('aplikácia 10 Hz'), color: '--s5', x: s.t, y: s.app, width: 0.6, noTip: true },
          { name: t('celý server'), color: '--s2', x: s.t, y: movAvg(s.cpu, 50), width: 2 }, { name: t('aplikácia'), color: '--s1', x: s.t, y: movAvg(s.app, 50), width: 2 }, { name: 'PostgreSQL', color: '--s3', x: s.t, y: movAvg(s.db, 50), width: 2 }] });
      new Plot('#step-lat', { title: t('Latencia servera (1 s surové, čiary = kĺzavý priemer 10 s)'), height: 250, x: xl, y: { log: true, label: 'ms', tipFmt: ms }, vlines: [{ x: 0, label: t('skok') }],
        series: [{ name: t('p99 za 1 s'), color: '--s5', x: s.s_t, y: s.p99, width: 0.6, noTip: true }, { name: 'p50', color: '--s1', x: s.s_t, y: movAvg(s.p50, 10), width: 2, fmt: ms }, { name: 'p99', color: '--s2', x: s.s_t, y: movAvg(s.p99, 10), width: 2, fmt: ms }] });
      const fit = s.temp_fit;
      const fitY = fit ? s.t.map((x) => (x < 0 ? fit.T0 : fit.T0 + fit.dT * (1 - Math.exp(-x / fit.tau)))) : [];
      new Plot('#step-temp', { title: t('Teplota CPU a fit 1. rádu'), height: 250, x: xl, y: { label: '°C', zero: false }, vlines: [{ x: 0 }],
        series: [{ name: t('meranie 10 Hz'), color: '--s2', x: s.t, y: s.temp, width: 1 }].concat(fit ? [{ name: `T₀ + ΔT·(1 − e^(−t/τ)), τ = ${num(fit.tau, 0)} s`, color: '--c-ink', x: s.t, y: fitY, dash: true, width: 1.6 }] : []) });
      new Plot('#step-freq', { title: t('Príkon platformy (psys), 10 Hz a kĺzavý priemer 5 s'), height: 250, x: xl, y: { label: 'W', zero: false }, vlines: [{ x: 0, label: t('skok') }],
        series: [{ name: 'psys 10 Hz', color: '--s5', x: s.t, y: s.psys, width: 0.6, noTip: true }, { name: 'psys 5 s', color: '--s1', x: s.t, y: movAvg(s.psys, 50), width: 2, fmt: f(2, 'W') }] });
    });

    // ---------------------------------------------------------------- E3 staircase
    if (D.stairs) sec('#kapacita', false, () => {
      const R = D.stairs.rows, X = R.map((r) => r.achieved);
      const xl = { label: t('dosiahnuté req/s'), min: 0 };
      const pts = (k, name, color) => ({ name, color, x: X, y: R.map((r) => r[k]), points: true, connect: true, r: 4, fmt: ms });
      new Plot('#cap-lat', { title: t('Priepustnosť vs. latencia servera (percentily)'), height: 300, x: xl, y: { log: true, label: 'ms', tipFmt: ms },
        hlines: [{ y: 1500, label: t('hranica 1,5 s') }],
        series: [pts('p50', 'p50', '--s1'), pts('p90', 'p90', '--s3'), pts('p99', 'p99', '--s2'), pts('p999', t('p99,9'), '--s7')]
          .concat(D.field ? [{ name: t('p99 v E1 (mobily po 5 s)'), color: '--c-ink2', x: D.field.rows.map((r) => r.achieved), y: D.field.rows.map((r) => r.p99), points: true, r: 4, fmt: ms }] : []) });
      new Plot('#cap-cpu', { title: t('Využitie CPU'), height: 300, x: xl, y: { label: t('jadrá z 2'), min: 0, max: 2 },
        series: [{ name: t('CPU aplikácia'), color: '--s1', x: X, y: R.map((r) => r.app), points: true, connect: true, r: 4 }, { name: t('CPU celý server'), color: '--s2', x: X, y: R.map((r) => r.cpu), points: true, connect: true, r: 4 }] });
      new Plot('#cap-mj', { title: t('Energia na jeden request (nad kľudovým príkonom)'), height: 300, x: xl, y: { label: 'mJ/req', min: 0 },
        series: [{ name: t('mJ na request'), color: '--s4', x: X, y: R.map((r) => r.mj_per_req), points: true, connect: true, r: 4 }] });
      table('#cap-tbl', R, [['rps', t('cieľ'), 0], ['achieved', t('dosiahnuté'), 1], ['err_rate', t('chyby %'), 2, (v) => num(v * 100, 2)], ['n503', '503', 0], ['p50', 'p50 ms', 1], ['p90', 'p90 ms', 1], ['p99', 'p99 ms', 0], ['p999', t('p99,9 ms'), 0], ['c_p99', t('klient p99'), 0], ['app', t('CPU app'), 2], ['cpu', 'CPU', 2], ['psi', 'PSI %', 0], ['bc_period', t('broadcast s'), 2], ['psys', 'W', 2], ['mj_per_req', 'mJ/req', 1], ['temp', '°C', 1]], (r) => r.p99 > 1500 || r.err_rate > 0.02);
    });

    // ---------------------------------------------------------------- E4 chirp / Bode
    if (D.bode) sec('#bode', true, () => {
      const b = D.bode, tr = b.trace;
      new Plot('#bode-trace', { title: t('Budenie (chirp) a odozva v čase'), height: 220, x: { label: 's', fmt: clock, tipFmt: clock }, y: { label: 'req/s' },
        series: [{ name: t('prijaté 202/s (1 s)'), color: '--s5', x: tr.t, y: tr.ok, width: 0.8 }, { name: t('prijaté, kĺzavý priemer 10 s'), color: '--s1', x: tr.t, y: movAvg(tr.ok, 10), width: 2 },
          { name: t('cieľ'), color: '--c-ink', x: tr.t, y: tr.target, dash: true, width: 1.4 }] });
      new Plot('#bode-trace2', { height: 160, x: { label: 's', fmt: clock, tipFmt: clock }, y: { label: '°C', zero: false }, series: [{ name: t('teplota CPU'), color: '--s2', x: tr.t, y: tr.temp, width: 1.3 }] });
      const tau = D.step?.temp_fit?.tau;
      const chans = [['app', t('Aplikácia: CPU [jadrá / (req/s)]'), '--s1', (v) => num(v * 1000, 2) + ' ' + t('ms CPU/req')], ['psys', t('Príkon platformy [W / (req/s)]'), '--s3', (v) => num(v * 1000, 1) + ' mJ/req'], ['temp', t('Teplota CPU [°C / (req/s)]'), '--s2', (v) => num(v, 4) + ' °C/(req/s)']];
      const yr = { app: [1e-4, 0.03], psys: [1e-3, 0.3], temp: [1e-3, 0.3] };
      chans.forEach(([k, title, color, tip]) => {
        const m = b[k], [ymin, ymax] = yr[k];
        // where the 2-sigma interval reaches below zero the lower bound is unbounded: draw it to the axis floor
        const ser = [{ name: '±2σ', color, x: b.f, y: m.lo.map((v, i) => (v == null || v < ymin || m.mag[i] - 2 * (m.hi[i] - m.mag[i]) / 2 <= 0 ? ymin : v)), y2: m.hi.map((v) => Math.min(v, ymax)), band: true, noLegend: true, noTip: true },
          { name: '|H(f)|', color, x: b.f, y: m.mag, points: true, connect: true, r: 3, fmt: tip }];
        if (k === 'temp' && tau) {
          const lowf = m.mag.slice(0, 6).filter((v) => v != null).sort((p, q) => p - q);
          const K = lowf[Math.floor(lowf.length / 2)];
          ser.push({ name: t('model 1. rádu, τ = {0} s zo skoku', num(tau, 0)), color: '--c-ink', dash: true, x: b.f, y: b.f.map((fr) => K / Math.sqrt(1 + Math.pow(2 * Math.PI * fr * tau, 2))), fmt: tip });
        }
        new Plot('#bode-' + k + '-m', { title, height: 200, x: { log: true, label: 'Hz', fmt: hz }, y: { log: true, fmt: auto, min: ymin, max: ymax }, series: ser, forceLegend: k === 'temp',
          vlines: k === 'temp' && tau ? [{ x: 1 / (2 * Math.PI * tau), label: 'f_c' }] : [] });
        new Plot('#bode-' + k + '-p', { title: t('fáza [°]'), height: 140, x: { log: true, label: 'Hz', fmt: hz }, y: { zero: false, fmt: f(0) }, series: [{ name: t('fáza'), color, x: b.f, y: m.phase, points: true, connect: true, r: 2.5, fmt: f(0, '°') }] });
        new Plot('#bode-' + k + '-c', { title: t('R² lock-in fitu (podiel vysvetleného rozptylu)'), height: 110, x: { log: true, label: 'Hz', fmt: hz }, y: { min: 0, max: 1, fmt: f(1) }, series: [{ name: 'R²', color: '--c-ink2', x: b.f, y: m.coh, width: 1.2, area: true }] });
      });
      if (D.portrait && has('#portrait')) {
        const p = D.portrait, n = p.t.length;
        const ramp = () => (root.dataset.themeActive === 'dark' ? ['#104281', '#1c5cab', '#2a78d6', '#5598e7', '#86b6ef', '#b7d3f6'] : ['#b7d3f6', '#86b6ef', '#5598e7', '#2a78d6', '#1c5cab', '#104281']);
        new Plot('#portrait', { title: t('Fázový portrét: príkon → teplota počas chirpu (farba = čas)'), height: 320, x: { label: t('príkon psys [W]'), zero: false }, y: { label: '°C', zero: false },
          series: [{ name: t('trajektória'), color: '--s1', x: p.psys, y: p.temp, points: true, r: 2.2, colorFn: (i) => { const r = ramp(); return r[Math.min(r.length - 1, Math.floor(i / n * r.length))]; } }] });
      }
    });

    // ---------------------------------------------------------------- E5 impulse
    if (D.impulse) sec('#impulz', true, () => {
      const im = D.impulse;
      const each = im.each_ok.map((y, i) => ({ name: t('impulz {0}', i + 1), color: ['--s3', '--s4', '--s5'][i], x: im.t, y, width: 1 }));
      new Plot('#imp-rate', { title: t('Odpovede za sekundu po impulze {0} požiadaviek (bin 0,5 s)', num(im.rows[0]?.sent, 0)), height: 260, x: { label: t('s od impulzu'), fmt: f(0), min: -3, max: 15 }, y: { label: t('odpovede/s') }, vlines: [{ x: 0, label: t('impulz') }],
        series: [{ name: t('202 priemer'), color: '--s1', x: im.t, y: im.ok, width: 2 }, { name: t('503 priemer'), color: '--crit', x: im.t, y: im.e503, width: 2 }].concat(each) });
      new Plot('#imp-cpu', { title: t('CPU servera (priemer troch impulzov)'), height: 260, x: { label: t('s od impulzu'), fmt: f(0), min: -10, max: 40 }, y: { label: t('jadrá'), max: 2.05 }, vlines: [{ x: 0 }],
        series: [{ name: 'CPU', color: '--s2', x: im.t, y: im.cpu, area: true }] });
      table('#imp-tbl', im.rows.map((r) => Object.assign({}, r, { name: r.name.replace('E5 impulse', t('Impulz')) })), [['name', t('impulz')], ['sent', t('odoslané'), 0], ['ok', '202', 0], ['e503', '503', 0], ['other', t('iné'), 0], ['c_p50', 'p50 ms', 0], ['c_p99', 'p99 ms', 0], ['c_max', 'max ms', 0], ['drain_s', t('vyprázdnenie s'), 2]]);
    });

    // ---------------------------------------------------------------- E6 fan-out
    if (D.fanout) sec('#fanout', false, () => {
      const R = D.fanout.rows, X = R.map((r) => r.n);
      const xl = { label: t('diváci'), min: 0 };
      new Plot('#fan-bw', { title: t('Dátový tok k divákom'), height: 250, x: xl, y: { label: 'MB/s' },
        series: [{ name: t('prijaté klientom'), color: '--s1', x: X, y: R.map((r) => r.spec_mbps), points: true, connect: true }, { name: t('eno1 TX (server)'), color: '--s3', x: X, y: R.map((r) => r.tx), points: true, connect: true }] });
      new Plot('#fan-cpu', { title: 'CPU', height: 250, x: xl, y: { label: t('jadrá z 2'), min: 0, max: 2 },
        series: [{ name: t('CPU aplikácia'), color: '--s1', x: X, y: R.map((r) => r.app), points: true, connect: true, r: 4 }, { name: t('CPU celý server'), color: '--s2', x: X, y: R.map((r) => r.cpu), points: true, connect: true, r: 4 }] });
      const delay = 30; // the event's configured spectator delay (seed: SpectatorDelaySeconds 30)
      new Plot('#fan-lag', { title: t('Doručenie divákom nad nastavené oneskorenie 30 s'), height: 250, x: xl, y: { label: 's', min: 0 },
        series: [{ name: 'p50', color: '--s1', x: X, y: R.map((r) => (r.spec_lag_p50 == null ? null : r.spec_lag_p50 - delay)), points: true, connect: true, r: 4, fmt: f(2, 's') },
          { name: 'p99', color: '--s2', x: X, y: R.map((r) => (r.spec_lag_p99 == null ? null : r.spec_lag_p99 - delay)), points: true, connect: true, r: 4, fmt: f(2, 's') }] });
      table('#fan-tbl', R, [['n', t('diváci'), 0], ['spec_kb', t('kB/správa'), 0], ['spec_rate', t('správ/s na diváka'), 3], ['spec_mbps', 'MB/s', 2], ['tx', 'TX MB/s', 2], ['app', t('CPU app'), 2], ['cpu', 'CPU', 2], ['bc_period', t('broadcast s'), 2], ['spec_lag_p50', t('lag p50 s'), 2], ['spec_lag_p99', t('lag p99 s'), 2], ['p99', 'POST p99 ms', 0]]);
    });

    // ---------------------------------------------------------------- A1 spectra
    if (D.spectra) sec('#spektra', true, () => {
      const sp = D.spectra, ser = [], ann = [];
      [['E0 noise floor', t('kľud (E0)'), '--s3'], ['E7 soak', t('pretek (E7)'), '--s1']].forEach(([k, name, color]) => {
        if (!sp[k]) return;
        ser.push({ name, color, x: sp[k].f, y: sp[k].cpu, width: 1.2, fmt: f(1, 'dB') });
        ann.length = 0; // label only the last (loaded) spectrum: two sets of labels collide
        const placed = [];
        (sp[k].peaks || []).slice().sort((a, b) => b.prom - a.prom).forEach((pk) => {
          if (placed.some((x) => Math.abs(Math.log10(x) - Math.log10(pk.f)) < 0.14) || placed.length >= 6) return; // keep labels apart
          placed.push(pk.f);
          ann.push({ x: pk.f, y: pk.db, text: pk.id ? trPeak(pk.id) : (pk.period_s >= 1 ? num(pk.period_s, 1) + ' s' : num(pk.f, 2) + ' Hz') });
        });
      });
      new Plot('#psd', { title: t('Výkonová spektrálna hustota CPU (Welch, 10 Hz, okno 204,8 s, prekryv 50 %)'), height: 300, x: { log: true, label: 'Hz', fmt: hz, min: 0.003, max: 5 }, y: { label: t('dB (jadrá²/Hz)'), zero: false }, series: ser, annotations: ann });
      const late = [];
      [['E0 noise floor', t('kľud'), '--s3'], ['E7 soak', t('pretek'), '--s1']].forEach(([k, name, color]) => sp[k] && late.push({ name, color, x: sp[k].f, y: sp[k].p_psys, width: 1.2, fmt: f(1, 'dB') }));
      new Plot('#psd-w', { title: t('PSD príkonu platformy (psys)'), height: 220, x: { log: true, label: 'Hz', fmt: hz, min: 0.003, max: 5 }, y: { label: 'dB (W²/Hz)', zero: false }, series: late });
      if (D.spectrogram && has('#sgram')) {
        const g = D.spectrogram, cw = g.t.length > 1 ? g.t[1] - g.t[0] : 100;
        const ye = g.f.map((v) => v - g.df / 2).concat([g.f[g.f.length - 1] + g.df / 2]);
        const flat = g.z.flat().filter((v) => v != null).sort((a, b) => a - b);
        new Heat('#sgram', { title: t('Spektrogram CPU celého behu (okno 204,8 s, krok 102,4 s)'), height: 300, x: g.t.map((x) => x - cw / 2), colW: cw, yEdges: ye, y: { log: false, label: 'Hz', fmt: hz, min: 0, max: 2 }, z: g.z,
          zmin: flat[Math.floor(flat.length * 0.6)], zmax: flat[Math.floor(flat.length * 0.999)], zFmt: f(1), zUnit: 'dB', yUnit: 'Hz', bands, marks: [{ y: 1 / 1.5, label: t('broadcast 0,667 Hz') }, { y: 2 / 1.5, label: '2×' }] });
      }
    });

    // ---------------------------------------------------------------- A2 latency
    sec('#latencia', true, () => {
      if (D.heatmap && has('#heat')) {
        const h = D.heatmap;
        new Heat('#heat', { title: t('Rozdelenie latencie servera v čase (stĺpec 10 s, logaritmické biny)'), height: 300, x: h.t, colW: h.col_s, yEdges: h.edges, y: { log: true, label: 'ms', fmt: auto, min: 0.3, max: 30000 }, z: h.z, zlog: true,
          zUnit: t('požiadaviek'), yUnit: 'ms', bands });
      }
      if (D.e2e && has('#e2e')) {
        const cols = ['--s3', '--s7', '--s1', '--s4', '--s2'];
        const q = Array.from({ length: 101 }, (_, i) => i);
        new Plot('#e2e', { title: t('Mobil → organizátor: CDF latencie end-to-end'), height: 280, x: { label: 's', min: 0 }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
          series: D.e2e.phases.map((p, i) => ({ name: p.name.replace('noise floor', t('kľud')).replace('step up', t('skok')).replace('soak', t('pretek')).replace('field', t('pole')), color: cols[i % cols.length], x: p.cdf_x, y: q, width: 1.8, fmt: f(0, '%') })) });
      }
      if (D.wifi && has('#wifi')) {
        const q = Array.from({ length: 51 }, (_, i) => i * 2);
        new Plot('#wifi', { title: t('Wi-Fi RTT (ping 10 Hz počas celého behu) – šum prístroja'), height: 220, x: { log: true, label: 'ms', fmt: auto }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
          series: [{ name: 'CDF', color: '--s3', x: D.wifi.q, y: q, width: 1.8, fmt: f(0, '%') }] });
      }
    });

    // ---------------------------------------------------------------- A3 jitter & neighbours
    if (D.jitter) sec('#jitter', false, () => {
      const bins = D.jitter.bins_us, X = bins.map((_, i) => i);
      new Plot('#jit', { title: t('Oneskorenie zobudenia 1 kHz slučky (podiel zobudení, log₂ biny)'), height: 280, x: { label: 'µs', fmt: (i) => (Number.isInteger(i) && bins[i] != null ? auto(bins[i]) : ''), min: -0.6, max: bins.length - 0.4 }, y: { log: true, label: '%', min: 1e-5, max: 100 },
        series: D.jitter.regimes.map((r, i) => { const tot = r.hist.reduce((a, b) => a + b, 0) || 1; return { name: trName(r.name), color: ['--s3', '--s2', '--s1'][i], x: X, y: r.hist.map((v) => (v ? v / tot * 100 : null)), bars: true, barWidth: 30, fmt: (v) => num(v, v < 0.01 ? 5 : 3) + ' %' }; }), xgrid: false });
      if (D.dns && has('#dns')) {
        const q = Array.from({ length: 51 }, (_, i) => i * 2);
        new Plot('#dns', { title: t('DNS (Technitium) – CDF času odpovede'), height: 280, x: { log: true, label: 'ms', fmt: auto }, y: { label: '%', min: 0, max: 100, fmt: f(0) },
          series: D.dns.map((r, i) => ({ name: trName(r.name), color: ['--s3', '--s2', '--s1'][i], x: r.q, y: q, width: 1.8, fmt: f(0, '%') })) });
        table('#dns-tbl', D.dns.map((r) => Object.assign({}, r, { name: trName(r.name) })), [['name', t('režim')], ['n', t('dotazov'), 0], ['p50', 'p50 ms', 2], ['p99', 'p99 ms', 2], ['max', 'max ms', 1], ['lost', t('stratené'), 0]]);
        table('#jit-tbl', D.jitter.regimes.map((r) => Object.assign({}, r, { name: trName(r.name) })), [['name', t('režim')], ['mean', t('priemer µs'), 0], ['p99', 'p99 µs', 0], ['p999', t('p99,9 µs'), 0], ['max', 'max µs', 0]]);
      }
    });

    // ---------------------------------------------------------------- A4 correlations, M clock
    if (D.xcorr) sec('#gc', false, () => {
      const x = D.xcorr;
      new Plot('#xcorr', { title: t('Krížová korelácia s p99 latenciou ({0}, 1 s)', x.phase), height: 260, x: { label: t('posun [s] (+ = latencia nasleduje)'), fmt: f(0) }, y: { label: 'r', min: -0.3, max: 1 },
        series: [{ name: t('GC pauza → p99'), color: '--s5', x: x.lags, y: x.gc_p99, points: true, connect: true }, { name: t('perióda broadcastu → p99'), color: '--s7', x: x.lags, y: x.bc_p99, points: true, connect: true }] });
    });
    if (D.clock) sec('#metodika', false, () => {
      new Plot('#clk', { title: t('Odchýlka hodín PC voči serveru počas behu (po odčítaní strednej hodnoty)'), height: 200, x: { time: true, label: t('čas behu') }, y: { label: 'ms', zero: false },
        series: [{ name: 'offset', color: '--s1', x: D.clock.drift.t, y: D.clock.drift.off, points: true, connect: true, fmt: f(2, 'ms') }] });
    });
  }

  // ------------------------------------------------------------------ links that zoom the recorder, nav highlight
  document.querySelectorAll('[data-zoom]').forEach((a) => a.addEventListener('click', () => {
    const chip = [...document.querySelectorAll('#zoombar .chip')].find((c) => c.textContent.startsWith(a.dataset.zoom));
    if (chip) setTimeout(() => chip.click(), 50);
  }));
  const links = [...document.querySelectorAll('#nav a')];
  const nio = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) links.forEach((a) => a.classList.toggle('on', a.getAttribute('href') === '#' + e.target.id)); }), { rootMargin: '-30% 0px -65% 0px' });
  document.querySelectorAll('section[id]').forEach((s) => nio.observe(s));
  applyTheme();
})();
