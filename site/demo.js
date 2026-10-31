/* Live race demo: runs entirely in the browser, no server. It mirrors what the real application shows:
   - organizer view: current positions, standings with sector colours and gaps, race-control messages (stopped, off course,
     low battery), battery and GPS of the selected rider, ETA with its range;
   - spectator view: positions and standings delayed by 15 s, no alarms and no device data, names only where the rider
     agreed ("#bib" otherwise), the same ETA range.
   SOS is deliberately absent: with OwnTracks the participant has no SOS button (an SOS is a phone call to the organizer).
   Exposed as window.PTDemo.init(rootElement, t) where t(sk, en) returns the text in the current language. */
(function () {
  // name, nickname, relative speed, agreed to show the name to spectators
  const RIDERS = [
    ['Peter Horváth', 'Rocket', 1.0, true], ['Lucia Šimková', 'Gazela', 0.95, true], ['Marek Baláž', 'Tank', 0.98, true],
    ['Tomáš Novotný', '', 0.9, false], ['Eva Kráľová', '', 0.88, true], ['Jakub Mráz', 'Junior', 0.84, false],
    ['Oliver Smith', '', 0.8, true], ['Adriana Pavlíková', '', 0.76, true], ['Filip Urban', '', 0.72, true],
  ];
  const COLORS = ['#FF4B2B', '#22C55E', '#3B82F6', '#F59E0B', '#A855F7', '#06B6D4', '#EC4899', '#84CC16', '#F97316'];
  const LAPS = 3, SECTORS = 6, DELAY = 15, W = 800, H = 520;

  function trackPoints() {
    const pts = [];
    for (let i = 0; i <= 240; i++) {
      const a = (Math.PI * 2 * i) / 240;
      const r = 1 + 0.26 * Math.sin(3 * a + 0.4) + 0.1 * Math.cos(2 * a);
      pts.push([W / 2 + Math.cos(a) * 300 * r * 0.98, H / 2 + Math.sin(a) * 175 * r * 0.98]);
    }
    return pts;
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
    const svg = $('svg'), course = $('.course'), tower = $('.tower ol'), clockEl = $('.clock'), flagsEl = $('.flags'), callout = $('.callout');
    const winner = $('.winner');
    const path = 'M' + trackPoints().map((p) => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join('L') + 'Z';
    course.setAttribute('d', path);
    $('.line').setAttribute('d', path);
    const total = course.getTotalLength();
    const kmPerUnit = 12.5 / total; // one lap is shown as 12.5 km

    let sport = 'cycling', view = 'organizer', speedMul = 4, selected = 0, simT = 0, running = true, finished = false, lastTs = 0;
    let riders = [];
    const bestSector = Array(SECTORS).fill(Infinity);

    function reset() {
      simT = 0; finished = false; bestSector.fill(Infinity);
      winner.classList.remove('on'); flagsEl.innerHTML = '';
      riders = RIDERS.map((r, i) => ({
        i, name: r[0], nick: r[1], base: r[2], show: r[3], d: -i * 1.4, v: 0, battery: 82 - i * 4, finishT: null,
        alarms: { stopped: false, offCourse: false, lowBattery: false },
        secStart: 0, secColors: Array(SECTORS).fill(''), best: Array(SECTORS).fill(Infinity), lastSec: 0, hist: [], el: null,
      }));
      svg.querySelectorAll('.rider').forEach((n) => n.remove());
      tower.innerHTML = '';
      riders.forEach((r) => {
        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('class', 'rider'); g.dataset.rider = r.i;
        g.innerHTML = '<circle r="14" fill="' + COLORS[r.i] + '"/><text>' + (r.i + 1) + '</text>';
        g.addEventListener('click', () => select(r.i));
        svg.appendChild(g); r.el = g;
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
        const wave = 1 + 0.12 * Math.sin(simT * 0.35 + r.i * 1.7) + 0.05 * Math.sin(simT * 1.3 + r.i);
        r.v = r.alarms.stopped ? 0 : 4.2 * r.base * wave;
        r.d += r.v * dt;
        r.hist.push([simT, r.d]);
        while (r.hist.length > 2 && r.hist[1][0] < simT - DELAY - 5) r.hist.shift();
        const lapD = Math.max(0, r.d) % total, lap = Math.floor(Math.max(0, r.d) / total);
        const sec = Math.min(SECTORS - 1, Math.floor((lapD / total) * SECTORS));
        const idx = lap * SECTORS + sec;
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
        if (r.d >= total * LAPS) {
          r.finishT = simT; r.d = total * LAPS;
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

    function ordered() {
      return riders.map((r) => ({ r, d: seen(r) })).sort((a, b) => b.d - a.d);
    }

    function pointFor(r, dist) {
      const at = (x) => course.getPointAtLength(((Math.max(0, x) % total) + total) % total);
      const p = at(dist);
      if (view === 'organizer' && r.alarms.offCourse) {
        const q = at(dist + 2);
        const nx = -(q.y - p.y), ny = q.x - p.x, len = Math.hypot(nx, ny) || 1;
        return { x: p.x + (nx / len) * 34, y: p.y + (ny / len) * 34 };
      }
      return p;
    }

    function draw() {
      const ord = ordered();
      const lead = ord[0];
      clockEl.textContent = clock(view === 'spectator' ? Math.max(0, simT - DELAY) : simT);
      riders.forEach((r) => {
        const p = pointFor(r, seen(r));
        r.el.setAttribute('transform', 'translate(' + p.x.toFixed(1) + ',' + p.y.toFixed(1) + ')');
        r.el.classList.toggle('sel', r.i === selected);
        r.el.classList.toggle('sos', view === 'organizer' && (r.alarms.stopped || r.alarms.offCourse));
        r.el.querySelector('circle').setAttribute('r', r.i === selected ? 18 : 14);
        if (r.i === selected && svg.lastChild !== r.el) svg.appendChild(r.el);
      });
      // persistent rows (a click must land on the same element it started on); reorder only when the order changes
      ord.forEach(({ r, d: dd }, pos) => {
        const li = r.row;
        li.classList.toggle('sel', r.i === selected);
        const gap = pos === 0 ? t('Líder', 'Leader') : r.finishT !== null && lead.r.finishT !== null ? '+' + fmt(r.finishT - lead.r.finishT)
          : '+' + fmt(Math.max(0, (lead.d - dd) / Math.max(1, lead.r.v || 4)));
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
      const remainKm = Math.max(0, (total * LAPS - Math.max(0, sd)) * kmPerUnit);
      const kmh = s.v * 6.6 * (sport === 'cycling' ? 1 : 0.34);
      const rem = s.finishT !== null ? 0 : s.v > 0.1 ? (total * LAPS - sd) / s.v : Infinity;
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
      callout.querySelector('b').textContent = '#' + (s.i + 1) + ' ' + shownName(s);
    }

    function loop(ts) {
      if (!lastTs) lastTs = ts;
      const dt = Math.min(0.1, (ts - lastTs) / 1000);
      lastTs = ts;
      if (running && !document.hidden) {
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
    $('[data-act="restart"]').addEventListener('click', () => { running = true; reset(); });
    $('[data-act="again"]').addEventListener('click', () => { running = true; reset(); });
    reset();
    for (let k = 0; k < 200 * 10; k++) step(0.1);
    selected = 0; draw();
    requestAnimationFrame(loop);
    return { redraw: draw };
  }

  window.PTDemo = { init };
})();
