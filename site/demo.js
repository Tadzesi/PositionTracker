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
  // the elevation of the sample lap (m above sea level) at a fraction of the lap: invented, two long climbs with some rolling on top
  const eleAt = (f) => 310 + 48 * Math.sin(f * Math.PI * 4 + 0.6) + 14 * Math.sin(f * Math.PI * 11);

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

    // ----- elevation profile under the map: where the riders are on the lap. Riders that would overlap are merged into a purple bubble
    // with a count; the selected, followed and alarmed riders and the first three stay single (the same rules as the application) -----
    const prof = document.createElement('div');
    prof.className = 'prof';
    prof.innerHTML = '<h3></h3><svg role="img"></svg><p class="pnote"></p>';
    prof.querySelector('h3').textContent = t('Jazdci na výškovom profile trate', 'Riders on the elevation profile of the lap');
    prof.querySelector('.pnote').textContent = t('Modrá značka = štartové číslo jazdca, fialová bublina = skupina jazdcov (počet). Kliknutím vyberiete jazdca.',
      'Blue mark = the rider\'s bib, purple bubble = a group of riders (their number). Click to select a rider.');
    root.appendChild(prof);
    const profSvg = prof.querySelector('svg');
    profSvg.setAttribute('aria-label', t('Poloha jazdcov na výškovom profile jedného kola', 'Riders on the elevation profile of one lap'));
    const PROF = { h: 150, l: 40, r: 10, t: 48, b: 26, rows: 2, rowH: 22, gap: 24 };
    const profSamples = Array.from({ length: 161 }, (_, i) => [(i / 160) * lap, eleAt(i / 160)]);
    const eMin = Math.min(...profSamples.map((p) => p[1])), eMax = Math.max(...profSamples.map((p) => p[1]));
    let profStatic = '', profW = 0;
    function drawProfile(ord) {
      const W = Math.max(280, Math.round(profSvg.clientWidth || prof.clientWidth || 600));
      // a narrow screen has less room side by side, so the marks get a third row
      PROF.rows = W < 600 ? 3 : 2; PROF.t = 14 + PROF.rows * PROF.rowH + 12; PROF.h = PROF.t + 100 + PROF.b - 26;
      const iw = W - PROF.l - PROF.r, ih = PROF.h - PROF.t - PROF.b;
      const x = (dd) => PROF.l + (dd / lap) * iw, y = (e) => PROF.t + (1 - (e - eMin) / (eMax - eMin)) * ih;
      if (W !== profW) {
        profW = W;
        const path = profSamples.map((p, i) => (i ? 'L' : 'M') + x(p[0]).toFixed(1) + ',' + y(p[1]).toFixed(1)).join('');
        profStatic = '<path class="parea" d="' + path + 'L' + x(lap).toFixed(1) + ',' + (PROF.t + ih) + 'L' + x(0).toFixed(1) + ',' + (PROF.t + ih) + 'Z"/>'
          + '<path class="pline" d="' + path + '"/>'
          + '<text class="pax" x="' + (PROF.l - 6) + '" y="' + (PROF.t + 4) + '" text-anchor="end">' + Math.round(eMax) + ' m</text>'
          + '<text class="pax" x="' + (PROF.l - 6) + '" y="' + (PROF.t + ih) + '" text-anchor="end">' + Math.round(eMin) + ' m</text>'
          + [0, 0.25, 0.5, 0.75, 1].map((f) => '<text class="pax" x="' + x(lap * f).toFixed(1) + '" y="' + (PROF.h - 7) + '" text-anchor="'
            + (f === 0 ? 'start' : f === 1 ? 'end' : 'middle') + '">' + ((lap * f) / 1000).toFixed(1) + ' km</text>').join('')
          + CHECKPOINTS.map((f) => '<line class="pcp" x1="' + x(lap * f).toFixed(1) + '" x2="' + x(lap * f).toFixed(1) + '" y1="' + PROF.t + '" y2="' + (PROF.t + ih) + '"/>').join('');
        profSvg.setAttribute('viewBox', '0 0 ' + W + ' ' + PROF.h);
      }
      const eleHere = (dd) => eleAt(dd / lap);
      // distance in the current lap; a finished rider stands at the end
      const items = ord.map(({ r, d: dd }, pos) => {
        const lapD = r.finishT !== null ? lap : Math.max(0, dd) % lap;
        const sel = r.i === selected, fol = view === 'spectator' && followed.has(r.i);
        const warn = view === 'organizer' && (r.alarms.stopped || r.alarms.offCourse);
        const dim = view === 'spectator' && followed.size > 0 && !fol && !sel;
        return { r, pos, lapD, sel, fol, warn, dim, pinned: sel || fol || warn || pos < 3 };
      });
      const free = items.filter((it) => !it.pinned).sort((a, b) => a.lapD - b.lapD);
      const groups = [];
      let cur = [];
      const flush = () => { if (cur.length) { groups.push({ members: cur, d: cur.reduce((s, m) => s + m.lapD, 0) / cur.length }); cur = []; } };
      free.forEach((it) => { if (cur.length && x(it.lapD) - x(cur[cur.length - 1].lapD) >= PROF.gap) flush(); cur.push(it); });
      flush();
      items.filter((it) => it.pinned).forEach((it) => groups.push({ members: [it], d: it.lapD }));
      groups.sort((a, b) => a.d - b.d);
      const right = Array(PROF.rows).fill(-Infinity);
      let marks = '';
      groups.forEach((g) => {
        const gx = x(g.d), single = g.members.length === 1, m = g.members[0];
        const w = single ? 24 : 30, left = gx - w / 2;
        let row = right.findIndex((rr) => left >= rr + 2);
        if (row < 0) row = right.indexOf(Math.min(...right));
        right[row] = gx + w / 2;
        const cy = 14 + row * PROF.rowH + PROF.rowH / 2 - 2, dotY = y(eleHere(g.d));
        const names = g.members.map((it) => '#' + (it.r.i + 1) + ' ' + shownName(it.r)).join(', ');
        const cls = 'pm' + (single ? (m.sel ? ' sel' : '') + (m.fol ? ' fol' : '') + (m.warn ? ' warn' : '') + (m.dim ? ' dim' : '') : ' grp');
        marks += '<g class="' + cls + '" role="button" tabindex="0" data-i="' + g.members.map((it) => it.r.i).join(',') + '" aria-label="' + names.replace(/"/g, '') + '">'
          + '<title>' + names.replace(/</g, '') + '</title>'
          + '<line class="stem" x1="' + gx.toFixed(1) + '" x2="' + gx.toFixed(1) + '" y1="' + (cy + 9) + '" y2="' + dotY.toFixed(1) + '"/>'
          + '<circle class="pin" cx="' + gx.toFixed(1) + '" cy="' + dotY.toFixed(1) + '" r="3.5"/>'
          + '<rect class="tag" x="' + left.toFixed(1) + '" y="' + (cy - 10) + '" width="' + w + '" height="20" rx="10"/>'
          + '<text x="' + gx.toFixed(1) + '" y="' + (cy + 4) + '" text-anchor="middle">' + (single ? m.r.i + 1 : g.members.length) + '</text></g>';
      });
      profSvg.innerHTML = profStatic + marks;
    }
    // a click or Enter on a mark selects the rider (a bubble: the best placed one inside it)
    const pickMark = (e) => {
      const g = e.target.closest && e.target.closest('[data-i]');
      if (!g) return false;
      select(Number(g.dataset.i.split(',')[0]));
      return true;
    };
    profSvg.addEventListener('click', pickMark);
    profSvg.addEventListener('keydown', (e) => { if ((e.key === 'Enter' || e.key === ' ') && pickMark(e)) e.preventDefault(); });

    const map = L.map($('.map'), { zoomControl: true, scrollWheelZoom: false, dragging: !L.Browser.mobile, tap: false, zoomSnap: 0.25 });
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(map);
    const line = L.polyline(pts, { color: '#FF1E3C', weight: 4, opacity: 0.85 }).addTo(map);
    CHECKPOINTS.forEach((f) => L.circleMarker(at(lap * f), { radius: 5, color: '#FFD60A', weight: 2, fillOpacity: 0.9 }).addTo(map));
    const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const fit = () => { map.invalidateSize(); map.fitBounds(line.getBounds(), { paddingTopLeft: [28, 28], paddingBottomRight: [28, 120], animate: !reduceMotion }); };
    map.fitBounds(line.getBounds(), { paddingTopLeft: [28, 28], paddingBottomRight: [28, 120] });

    let sport = 'cycling', view = 'organizer', speedMul = 12, selected = 0, simT = 0, running = true, paused = false, finished = false, lastTs = 0;
    const followed = new Set(); // spectator view: the riders the visitor follows (in memory only; the real app keeps them in localStorage)

    // ----- following: the map keeps the followed riders in view, with the best zoom, until the visitor moves the map himself -----
    // Every few hundred ms it looks where the followed riders are and where they will be in AHEAD_S seconds (so a fast rider does not
    // outrun the map); if any of those points leaves the middle of the map (or hides under the callout) or the zoom could be better, it
    // zooms out at once, zooms in only when the riders are a whole zoom level closer together, and otherwise just pans.
    let auto = false, followBtn = null, lastMove = 0;
    const AHEAD_S = 1.5, MOVE_EVERY_MS = 350, FOLLOW_ZOOM = 15, MAX_FOLLOW_ZOOM = 16;
    const mapEl = $('.map');
    const active = () => view === 'spectator' && followed.size > 0;
    const followPoints = () => riders.filter((r) => followed.has(r.i)).map((r) => {
      const d = seen(r), cur = L.latLng(pointFor(r, d));
      return [cur, r.finishT === null ? L.latLng(pointFor(r, d + r.v * speedMul * AHEAD_S)) : cur]; // r.v m/s of race time, speedMul race s per real s
    });
    const underCallout = (p) => {
      if (getComputedStyle(callout).position !== 'absolute') return false; // on narrow screens the callout is under the map
      const c = callout.getBoundingClientRect(), m = mapEl.getBoundingClientRect();
      return p.x > c.left - m.left - 20 && p.x < c.right - m.left + 20 && p.y > c.top - m.top - 20 && p.y < c.bottom - m.top + 20;
    };
    function followView(force) {
      if (!active() || !auto) return;
      const now = Date.now();
      if (!force && now - lastMove < MOVE_EVERY_MS) return;
      const pairs = followPoints();
      if (!pairs.length) return;
      const all = pairs.flat(), bounds = L.latLngBounds(all), multi = pairs.length > 1;
      const size = map.getSize(), zoom = map.getZoom();
      const hidden = (ll) => {
        const p = map.latLngToContainerPoint(ll);
        return p.x < size.x * 0.15 || p.x > size.x * 0.85 || p.y < size.y * 0.15 || p.y > size.y * 0.85 || underCallout(p);
      };
      // the zoom at which all of them (and where they are heading) sit comfortably inside the middle of the map, so a zoom-in leaves room
      const best = Math.min(MAX_FOLLOW_ZOOM, map.getBoundsZoom(bounds, false, L.point(size.x * 0.4, size.y * 0.4 + 60)));
      const zoomChange = best < zoom - 0.01 || (multi && best >= zoom + 1);
      if (!force && !zoomChange && !all.some(hidden)) return;
      lastMove = now;
      const animate = !reduceMotion;
      if (force && multi) map.fitBounds(bounds, { paddingTopLeft: [40, 40], paddingBottomRight: [40, 140], maxZoom: MAX_FOLLOW_ZOOM, animate });
      else if (zoomChange && multi) map.setView(bounds.getCenter(), best, { animate });
      else if (force) map.setView(bounds.getCenter(), Math.max(zoom, FOLLOW_ZOOM), { animate });
      else map.panTo(bounds.getCenter(), { animate });
    }
    function updateFollowBtn() {
      if (!followBtn) return;
      followBtn.hidden = !active();
      followBtn.textContent = auto ? t('Celá trať', 'Whole course') : t('Sledovať', 'Follow');
    }
    function stopAuto() { if (auto) { auto = false; updateFollowBtn(); } }
    // only the visitor's own gestures stop it (not map.on('zoomstart'): that also fires for our zooms)
    const box = map.getContainer();
    map.on('dragstart', stopAuto);
    map.on('dblclick', stopAuto);
    box.addEventListener('touchstart', (e) => { if (e.touches.length > 1) stopAuto(); }, { passive: true }); // a pinch
    box.addEventListener('click', (e) => { if (e.target.closest && e.target.closest('.leaflet-control-zoom')) stopAuto(); }, true);
    box.addEventListener('keydown', (e) => { if (/^(\+|-|=|_|Arrow\w+)$/.test(e.key)) stopAuto(); });
    const FollowControl = L.Control.extend({
      options: { position: 'topleft' },
      onAdd() {
        followBtn = L.DomUtil.create('button', 'mapbtn leaflet-control');
        followBtn.type = 'button';
        L.DomEvent.disableClickPropagation(followBtn);
        L.DomEvent.on(followBtn, 'click', () => {
          if (auto) { auto = false; fit(); } else { auto = true; followView(true); }
          updateFollowBtn();
        });
        updateFollowBtn();
        return followBtn;
      },
    });
    new FollowControl().addTo(map);
    // the stage grows when the standings are filled in and when the window changes: keep the map fitted to its box (or to the followed riders)
    const onResize = () => { if (!followed.size) fit(); else { map.invalidateSize(); followView(true); } };
    if (window.ResizeObserver) new ResizeObserver(onResize).observe($('.map')); else window.addEventListener('resize', onResize);
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
        li.innerHTML = '<span class="pos"></span><span class="bib">' + (r.i + 1) + '</span><span class="name"></span><span class="gap"></span><span class="sectors"></span><button class="star" type="button"></button>';
        li.addEventListener('click', () => select(r.i));
        li.querySelector('.star').addEventListener('click', (e) => { e.stopPropagation(); toggleFollow(r.i); });
        tower.appendChild(li); r.row = li;
      });
      draw();
    }

    function select(i) { selected = i; draw(); }
    // like the application: following a rider selects him, and once someone is followed the others shrink on the map
    function toggleFollow(i) {
      const had = followed.size;
      if (followed.has(i)) followed.delete(i); else { followed.add(i); selected = i; }
      draw();
      if (followed.size > had || (followed.size && auto)) { auto = true; followView(true); } // a new rider: zoom to the followed ones
      else if (!followed.size) { auto = false; fit(); } // nobody followed any more: the whole course again
      updateFollowBtn();
    }

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
      root.dataset.view = view;
      clockEl.textContent = clock(view === 'spectator' ? Math.max(0, simT - DELAY) : simT);
      riders.forEach((r) => {
        r.marker.setLatLng(pointFor(r, seen(r)));
        const warn = view === 'organizer' && (r.alarms.stopped || r.alarms.offCourse);
        const sel = r.i === selected;
        const fol = view === 'spectator' && followed.has(r.i);
        const dim = view === 'spectator' && followed.size > 0 && !fol && !sel;
        const key = (sel ? 's' : '') + (warn ? 'w' : '') + (fol ? 'f' : '') + (dim ? 'd' : '');
        if (key !== r.iconKey) {
          r.iconKey = key;
          const size = sel ? 34 : dim ? 18 : 26;
          r.marker.setIcon(L.divIcon({ className: '', iconSize: [size, size], html: '<div class="mk ' + (warn ? 'warn ' : '') + (fol ? 'fol ' : '') + (dim ? 'dim ' : '') + (sel ? 'sel' : '') + '" style="width:' + size + 'px;height:' + size + 'px">' + (r.i + 1) + '</div>' }));
          r.marker.setZIndexOffset(sel ? 1000 : fol ? 500 : 0);
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
        const star = li.querySelector('.star'), on = followed.has(r.i), sname = shownName(r);
        if (star.dataset.on !== String(on) || star.dataset.n !== sname) {
          star.dataset.on = String(on); star.dataset.n = sname;
          star.textContent = on ? '★' : '☆';
          star.setAttribute('aria-pressed', String(on));
          star.setAttribute('aria-label', t('Sledovať: ', 'Follow: ') + sname);
        }
        const sectors = r.secColors.join('|');
        if (li.dataset.sectors !== sectors) {
          li.dataset.sectors = sectors;
          li.querySelector('.sectors').innerHTML = r.secColors.map((c) => '<i class="sec ' + c + '"></i>').join('');
        }
        if (tower.children[pos] !== li) tower.insertBefore(li, tower.children[pos] || null);
      });
      drawProfile(ord);
      followView(false);
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
      if (active()) { auto = true; followView(true); } else if (followed.size) { auto = false; fit(); }
      updateFollowBtn();
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
