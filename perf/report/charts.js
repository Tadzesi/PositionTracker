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
  const num = (v, d) => (v == null || !isFinite(v)) ? '–' : v.toLocaleString('sk-SK', { maximumFractionDigits: d, minimumFractionDigits: 0 });
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
    return v.toExponential(1).replace('.', ',');
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
      this.cv.setAttribute('aria-label', o.aria || o.title || 'graf');
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
      if (this.zoom) { g.font = '10px ' + cv('--font-ui'); g.fillStyle = cv('--c-muted'); g.textAlign = 'right'; g.textBaseline = 'top'; g.fillText('zoom · dvojklik = späť', w - r - 2, t + 2); }
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
