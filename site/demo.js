/* Live race demo: runs entirely in the browser, no server, and looks like the real application: OpenStreetMap, the course as a
   red line with yellow checkpoints, numbered circles for riders (yellow ring = selected), the same standings and ETA.
   - organizer view: current positions, sector colours, gaps, race-control messages, battery and GPS of the selected rider;
   - spectator view: positions and standings delayed by 15 s, no alarms and no device data, names only where the rider agreed.
   SOS is deliberately absent: with OwnTracks the participant has no SOS button (an SOS is a phone call to the organizer).
   Exposed as window.PTDemo.init(rootElement, t) where t(sk, en) returns the text in the current language. Needs Leaflet (L). */
(function () {
  // name, nickname, relative speed, agreed to show the name to spectators
  const RIDERS = [
    ['Peter Horváth', 'Rocket', 1.0, true], ['Lucia Šimková', 'Gazela', 0.95, true], ['Marek Baláž', 'Tank', 0.98, true],
    ['Tomáš Novotný', '', 0.9, false], ['Eva Kráľová', '', 0.88, true], ['Jakub Mráz', 'Junior', 0.84, false],
    ['Oliver Smith', '', 0.8, true], ['Adriana Pavlíková', '', 0.76, true], ['Filip Urban', '', 0.72, true],
  ];
  const LAPS = 3, SECTORS = 6, DELAY = 15, CHECKPOINTS = [0.4, 0.8];

  // the sample course of the screenshots: a wavy loop of about 5.5 km
  function courseLatLng() {
    const pts = [];
    for (let i = 0; i <= 180; i++) {
      const a = (Math.PI * 2 * i) / 180;
      const r = 1 + 0.28 * Math.sin(3 * a) + 0.12 * Math.cos(2 * a);
      pts.push([48.7285 + 0.0065 * r * Math.sin(a), 18.87 + 0.0105 * r * Math.cos(a)]);
    }
    return pts;
  }
  function haversine(a, b) {
    const R = 6371000, rad = (x) => (x * Math.PI) / 180;
    const dLat = rad(b[0] - a[0]), dLon = rad(b[1] - a[1]);
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a[0])) * Math.cos(rad(b[0])) * Math.sin(dLon / 2) ** 2;
    return 2 * R * Math.asin(Math.sqrt(h));
  }

  const fmt = (sec) => {
    sec = Math.max(0, Math.round(sec));
    return Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
  };
  const clock = (sec) => {
    const s = Math.round(sec), h = 13 + Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
    return h + ':' + String(m).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0');
  };

  function init(root, t) {
    const $ = (s) => root.querySelector(s);
    const tower = $('.tower ol'), clockEl = $('.clock'), flagsEl = $('.flags'), callout = $('.callout'), winner = $('.winner');

    const pts = courseLatLng();
    const cum = [0];
    for (let i = 1; i < pts.length; i++) cum.push(cum[i - 1] + haversine(pts[i - 1], pts[i]));
    const lap = cum[cum.length - 1], total = lap * LAPS;

    function at(dist) {
      const d = ((dist % lap) + lap) % lap;
      let lo = 0, hi = cum.length - 1;
      while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (cum[mid] <= d) lo = mid; else hi = mid; }
      const f = (d - cum[lo]) / (cum[hi] - cum[lo] || 1);
      return [pts[lo][0] + (pts[hi][0] - pts[lo][0]) * f, pts[lo][1] + (pts[hi][1] - pts[lo][1]) * f];
    }

    const map = L.map($('.map'), { zoomControl: true, scrollWheelZoom: false, dragging: !L.Browser.mobile, tap: false, zoomSnap: 0.25 });
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(map);
    const line = L.polyline(pts, { color: '#FF1E3C', weight: 4, opacity: 0.85 }).addTo(map);
    CHECKPOINTS.forEach((f) => L.circleMarker(at(lap * f), { radius: 5, color: '#FFD60A', weight: 2, fillOpacity: 0.9 }).addTo(map));
    const fit = () => { map.invalidateSize(); map.fitBounds(line.getBounds(), { paddingTopLeft: [28, 28], paddingBottomRight: [28, 120] }); };
    fit();
    // the stage grows when the standings are filled in and when the window changes: keep the map fitted to its box
    if (window.ResizeObserver) new ResizeObserver(fit).observe($('.map')); else window.addEventListener('resize', fit);

    let sport = 'cycling', view = 'organizer', speedMul = 12, selected = 0, simT = 0, running = true, paused = false, finished = false, lastTs = 0;
    let riders = [];
    const bestSector = Array(SECTORS).fill(Infinity);

    function reset() {
      simT = 0; finished = false; bestSector.fill(Infinity);
      winner.classList.remove('on'); flagsEl.innerHTML = '';
      riders.forEach((r) => r.marker.remove());
      tower.innerHTML = '';
      riders = RIDERS.map((r, i) => ({
        i, name: r[0], nick: r[1], base: r[2], show: r[3], d: -i * 12, v: 0, battery: 82 - i * 4, finishT: null,
        alarms: { stopped: false, offCourse: false, lowBattery: false },
        secStart: 0, secColors: Array(SECTORS).fill(''), best: Array(SECTORS).fill(Infinity), lastSec: 0, hist: [], marker: null, row: null, iconKey: null,
      }));
      riders.forEach((r) => {
        r.marker = L.marker(at(0), { icon: L.divIcon({ className: '', iconSize: [26, 26], html: '' }), keyboard: false }).addTo(map);
        r.marker.on('click', () => select(r.i));
        const li = document.createElement('li');
        li.innerHTML = '<span class="pos"></span><span class="bib">' + (r.i + 1) + '</span><span class="name"></span><span class="gap"></span><span class="sectors"></span>';
        li.addEventListener('click', () => select(r.i));
        tower.appendChild(li); r.row = li;
      });
      draw();
    }

    function select(i) { selected = i; draw(); }

    function flag(cls, text) {
      const el = document.createElement('div');
      el.className = 'flag ' + cls; el.textContent = text;
      flagsEl.prepend(el);
      while (flagsEl.children.length > 3) flagsEl.lastChild.remove();
    }

    const label = (r) => (r.nick || r.name);
    const shownName = (r) => (view === 'spectator' ? (r.show ? label(r) : '#' + (r.i + 1)) : label(r));

    function step(dt) {
      simT += dt;
      riders.forEach((r) => {
        if (r.finishT !== null) return;
        const wave = 1 + 0.12 * Math.sin(simT * 0.035 + r.i * 1.7) + 0.05 * Math.sin(simT * 0.13 + r.i);
        r.v = r.alarms.stopped ? 0 : 9.5 * r.base * wave; // m/s
        r.d += r.v * dt;
        r.hist.push([simT, r.d]);
        while (r.hist.length > 2 && r.hist[1][0] < simT - DELAY - 5) r.hist.shift();
        const lapD = Math.max(0, r.d) % lap, lapNo = Math.floor(Math.max(0, r.d) / lap);
        const sec = Math.min(SECTORS - 1, Math.floor((lapD / lap) * SECTORS));
        const idx = lapNo * SECTORS + sec;
        if (idx !== r.lastSec && r.d > 0) {
          const k = r.lastSec % SECTORS, time = simT - r.secStart;
          r.secStart = simT;
          if (r.lastSec < LAPS * SECTORS) {
            let c = 'y';
            if (time < r.best[k]) { r.best[k] = time; c = 'g'; }
            if (time < bestSector[k]) { bestSector[k] = time; c = 'p'; }
            r.secColors[k] = c;
          }
          r.lastSec = idx;
        }
        if (r.d >= total) {
          r.finishT = simT; r.d = total;
          if (!finished) {
            finished = true;
            $('.winner .who2').textContent = shownName(r);
            $('.winner .time').textContent = fmt(simT);
            winner.classList.add('on');
          }
          if (view === 'organizer') flag('fin', '🏁 ' + label(r) + ' · ' + fmt(simT));
        }
      });
      if (riders.every((r) => r.finishT !== null)) running = false;
    }

    // the distance a viewer sees: current for the organizer, DELAY seconds old for spectators
    function seen(r) {
      if (view === 'organizer') return r.d;
      const target = simT - DELAY;
      if (target < 0) return Math.min(0, r.d);
      let last = r.hist.length ? r.hist[0][1] : r.d;
      for (const [ts, dd] of r.hist) { if (ts > target) break; last = dd; }
      return last;
    }

    function pointFor(r, dist) {
      const p = at(dist);
      if (view === 'organizer' && r.alarms.offCourse) {
        const q = at(dist + 8);
        const nx = -(q[0] - p[0]), ny = q[1] - p[1], len = Math.hypot(nx, ny) || 1;
        return [p[0] + (nx / len) * 0.0011, p[1] + (ny / len) * 0.0011];
      }
      return p;
    }

    function draw() {
      const ord = riders.map((r) => ({ r, d: seen(r) })).sort((a, b) => b.d - a.d);
      const lead = ord[0];
      clockEl.textContent = clock(view === 'spectator' ? Math.max(0, simT - DELAY) : simT);
      riders.forEach((r) => {
        r.marker.setLatLng(pointFor(r, seen(r)));
        const warn = view === 'organizer' && (r.alarms.stopped || r.alarms.offCourse);
        const sel = r.i === selected;
        const key = (sel ? 's' : '') + (warn ? 'w' : '');
        if (key !== r.iconKey) {
          r.iconKey = key;
          const size = sel ? 34 : 26;
          r.marker.setIcon(L.divIcon({ className: '', iconSize: [size, size], html: '<div class="mk ' + (warn ? 'warn ' : '') + (sel ? 'sel' : '') + '" style="width:' + size + 'px;height:' + size + 'px">' + (r.i + 1) + '</div>' }));
          r.marker.setZIndexOffset(sel ? 1000 : 0);
        }
      });
      // persistent rows (a click must land on the same element it started on); reorder only when the order changes
      ord.forEach(({ r, d: dd }, pos) => {
        const li = r.row;
        li.classList.toggle('sel', r.i === selected);
        const gap = pos === 0 ? t('Líder', 'Leader') : r.finishT !== null && lead.r.finishT !== null ? '+' + fmt(r.finishT - lead.r.finishT)
          : '+' + fmt(Math.max(0, (lead.d - dd) / Math.max(1, lead.r.v || 9)));
        const warn = view === 'organizer' && (r.alarms.stopped || r.alarms.offCourse || r.alarms.lowBattery) ? ' ⚠' : '';
        const set = (sel, text) => { const el = li.querySelector(sel); if (el.textContent !== text) el.textContent = text; };
        set('.pos', String(pos + 1)); set('.gap', gap); set('.name', shownName(r) + warn);
        const sectors = r.secColors.join('|');
        if (li.dataset.sectors !== sectors) {
          li.dataset.sectors = sectors;
          li.querySelector('.sectors').innerHTML = r.secColors.map((c) => '<i class="sec ' + c + '"></i>').join('');
        }
        if (tower.children[pos] !== li) tower.insertBefore(li, tower.children[pos] || null);
      });
      if (view === 'spectator' && !flagsEl.querySelector('.note')) {
        flagsEl.innerHTML = '';
        const n = document.createElement('div');
        n.className = 'flag note';
        n.textContent = t('Polohy sú oneskorené o ' + DELAY + ' s a poradie je orientačné.', 'Positions are delayed by ' + DELAY + ' s and the ranking is indicative.');
        flagsEl.appendChild(n);
      }
      const s = riders[selected];
      const sd = seen(s);
      const remainKm = Math.max(0, (total - Math.max(0, sd)) / 1000);
      const kmh = s.v * 3.6 * (sport === 'cycling' ? 1 : 0.34);
      const rem = s.finishT !== null ? 0 : s.v > 0.1 ? (total - sd) / s.v : Infinity;
      const base = view === 'spectator' ? simT - DELAY : simT;
      const speedTxt = sport === 'cycling' ? kmh.toFixed(1) + ' km/h' : (kmh > 1 ? fmt(3600 / kmh) : '–') + ' /km';
      let eta = '–', range = '';
      if (s.finishT !== null && simT - s.finishT >= (view === 'spectator' ? DELAY : 0)) eta = '🏁 ' + fmt(s.finishT);
      else if (isFinite(rem)) {
        eta = clock(base + rem);
        range = clock(base + rem * 0.92) + ' – ' + clock(base + rem * 1.08) + ' ≈';
      }
      const lines = view === 'organizer'
        ? [speedTxt, remainKm.toFixed(1) + ' km', '🔋 ' + Math.round(s.battery) + ' %', 'GPS ±' + (4 + (s.i % 4)) + ' m']
        : [speedTxt, remainKm.toFixed(1) + ' km ' + t('do cieľa', 'to go'), (ord.findIndex((x) => x.r === s) + 1) + '. ' + t('miesto', 'place')];
      callout.innerHTML = '<b></b><span class="eta">' + eta + '</span><small>' + (range ? range + ' · ' : '') + lines.join(' · ') + '</small>';
      const nm = shownName(s);
      callout.querySelector('b').textContent = nm.startsWith('#') ? nm : '#' + (s.i + 1) + ' ' + nm;
    }

    function loop(ts) {
      if (!lastTs) lastTs = ts;
      const dt = Math.min(0.1, (ts - lastTs) / 1000);
      lastTs = ts;
      if (running && !paused && !document.hidden) {
        for (let k = 0; k < speedMul; k++) step(dt);
        draw();
      }
      requestAnimationFrame(loop);
    }

    const setPressed = (sel, el) => root.querySelectorAll(sel).forEach((x) => x.setAttribute('aria-pressed', String(x === el)));
    root.querySelectorAll('[data-speed]').forEach((b) => b.addEventListener('click', () => { speedMul = Number(b.dataset.speed); setPressed('[data-speed]', b); }));
    root.querySelectorAll('[data-sport]').forEach((b) => b.addEventListener('click', () => { sport = b.dataset.sport; setPressed('[data-sport]', b); draw(); }));
    root.querySelectorAll('[data-view]').forEach((b) => b.addEventListener('click', () => {
      view = b.dataset.view; setPressed('[data-view]', b); flagsEl.innerHTML = '';
      draw();
    }));
    root.querySelectorAll('[data-alarm]').forEach((b) => b.addEventListener('click', () => {
      const r = riders[selected], k = b.dataset.alarm;
      if (r.finishT !== null) return;
      r.alarms[k] = !r.alarms[k];
      if (k === 'lowBattery') r.battery = r.alarms[k] ? 12 : 60;
      const names = {
        stopped: [t('Stojí', 'Stopped'), t('Opäť v pohybe', 'Moving again')],
        offCourse: [t('Mimo trasy', 'Off course'), t('Späť na trase', 'Back on course')],
        lowBattery: [t('Slabá batéria', 'Low battery'), t('Batéria v poriadku', 'Battery fine')],
      };
      if (view === 'organizer') flag(r.alarms[k] && k !== 'lowBattery' ? 'sos' : '', names[k][r.alarms[k] ? 0 : 1] + ' · #' + (r.i + 1) + ' ' + label(r));
      running = true; draw();
    }));
    const pauseBtn = $('[data-act="pause"]');
    const setPaused = (p) => {
      paused = p;
      pauseBtn.setAttribute('aria-pressed', String(p));
      pauseBtn.textContent = p ? t('Pokračovať', 'Resume') : t('Pozastaviť', 'Pause');
    };
    pauseBtn.addEventListener('click', () => setPaused(!paused));
    // moving content can be stopped at any time; with "reduce motion" the demo starts stopped
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) setPaused(true);
    $('[data-act="restart"]').addEventListener('click', () => { setPaused(false); running = true; reset(); });
    $('[data-act="again"]').addEventListener('click', () => { running = true; reset(); });
    reset();
    for (let k = 0; k < 2000; k++) step(0.1); // 200 s into the race, so the first impression already has a field spread out
    selected = 0; draw();
    requestAnimationFrame(loop);
    // the visitor asked for the demo (the "Spustiť živé demo" links): a paused demo (reduced motion, or paused by hand) resumes, a finished one
    // starts again, a running one just keeps running
    function play() {
      setPaused(false);
      if (!running) { running = true; reset(); }
    }
    return { redraw: draw, play };
  }

  window.PTDemo = { init };
})();
