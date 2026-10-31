/* Live race demo: runs entirely in the browser, no server. A made-up 3-lap race with 9 riders, timing tower, sector colours,
   ETA, SOS and a winner screen. Exposed as window.PTDemo.init(rootElement, t) where t(sk,en) gives the current-language text. */
(function () {
  const RIDERS = [
    ['Peter Horváth', 'Rocket', 1.00], ['Lucia Šimková', 'Gazela', 0.97], ['Marek Baláž', 'Tank', 0.985], ['Tomáš Novotný', '', 0.94],
    ['Eva Kráľová', '', 0.93], ['Jakub Mráz', 'Junior', 0.91], ['Oliver Smith', '', 0.89], ['Adriana Pavlíková', '', 0.87], ['Filip Urban', '', 0.85],
  ];
  const COLORS = ['#FF4B2B', '#22C55E', '#3B82F6', '#F59E0B', '#A855F7', '#06B6D4', '#EC4899', '#84CC16', '#F97316'];
  const LAPS = 3, SECTORS = 6;
  const W = 800, H = 520;

  function trackPath() {
    const pts = [];
    for (let i = 0; i <= 240; i++) {
      const a = (Math.PI * 2 * i) / 240;
      const r = 1 + 0.26 * Math.sin(3 * a + 0.4) + 0.1 * Math.cos(2 * a);
      pts.push([W / 2 + Math.cos(a) * 300 * r * 0.98, H / 2 + Math.sin(a) * 175 * r * 0.98]);
    }
    return 'M' + pts.map((p) => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join('L') + 'Z';
  }

  function fmt(sec) {
    sec = Math.max(0, Math.round(sec));
    const m = Math.floor(sec / 60), s = sec % 60;
    return m + ':' + String(s).padStart(2, '0');
  }

  function init(root, t, getLang) {
    const $ = (s) => root.querySelector(s);
    const svg = $('svg'), course = $('.course'), tower = $('.tower ol'), clockEl = $('.clock'), flagsEl = $('.flags'), callout = $('.callout');
    const winner = $('.winner');
    course.setAttribute('d', trackPath());
    $('.line').setAttribute('d', trackPath());
    const total = course.getTotalLength();
    const lapLen = 12.5; // km, for display only
    const kmPerUnit = (lapLen * LAPS) / (total * LAPS);

    let sport = 'cycling', speedMul = 4, selected = 0, simT = 0, running = true, finished = false, lastTs = 0;
    let riders = [];
    const bestSector = Array(SECTORS).fill(Infinity);

    function reset() {
      simT = 0; finished = false; bestSector.fill(Infinity);
      winner.classList.remove('on'); flagsEl.innerHTML = '';
      riders = RIDERS.map((r, i) => ({
        i, name: r[0], nick: r[1], base: r[2], d: -i * 1.4, v: 0, sos: false, battery: 80 - i * 4, finishT: null, dnf: false,
        secStart: 0, secTimes: Array(SECTORS).fill(null), secColors: Array(SECTORS).fill(''), best: Array(SECTORS).fill(Infinity), lastSec: 0,
        el: null,
      }));
      svg.querySelectorAll('.rider').forEach((n) => n.remove());
      riders.forEach((r) => {
        const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        g.setAttribute('class', 'rider'); g.dataset.rider = r.i;
        g.innerHTML = '<circle r="14" fill="' + COLORS[r.i] + '"/><text>' + (r.i + 1) + '</text>';
        g.addEventListener('click', () => select(r.i));
        svg.appendChild(g); r.el = g;
      });
      draw();
    }

    function select(i) { selected = i; draw(); }

    function flag(cls, text) {
      const d = document.createElement('div');
      d.className = 'flag ' + cls; d.textContent = text;
      flagsEl.prepend(d);
      while (flagsEl.children.length > 3) flagsEl.lastChild.remove();
    }

    function step(dt) {
      simT += dt;
      riders.forEach((r) => {
        if (r.finishT !== null || r.dnf) return;
        const wave = 1 + 0.12 * Math.sin(simT * 0.35 + r.i * 1.7) + 0.05 * Math.sin(simT * 1.3 + r.i);
        let v = 4.2 * r.base * wave; // units of path length per second (before display scaling)
        if (r.sos) v = 0;
        r.v = v;
        r.d += v * dt;
        // sectors
        const lapD = Math.max(0, r.d) % total;
        const lap = Math.floor(Math.max(0, r.d) / total);
        const sec = Math.min(SECTORS - 1, Math.floor((lapD / total) * SECTORS));
        const idx = lap * SECTORS + sec;
        if (idx !== r.lastSec && r.d > 0) {
          const secIdx = r.lastSec % SECTORS;
          const time = simT - r.secStart;
          r.secStart = simT;
          if (r.lastSec < LAPS * SECTORS) {
            let c = 'y';
            if (time < r.best[secIdx]) { r.best[secIdx] = time; c = 'g'; }
            if (time < bestSector[secIdx]) { bestSector[secIdx] = time; c = 'p'; }
            r.secColors[secIdx] = c;
          }
          r.lastSec = idx;
        }
        if (r.d >= total * LAPS) {
          r.finishT = simT; r.d = total * LAPS;
          if (!finished) {
            finished = true;
            $('.winner .who2').textContent = r.nick ? r.name + ' «' + r.nick + '»' : r.name;
            $('.winner .time').textContent = fmt(simT);
            winner.classList.add('on');
            flag('fin', '🏁 ' + t('Víťaz: ', 'Winner: ') + r.name);
          } else flag('fin', '🏁 ' + r.name + ' ' + fmt(simT));
        }
      });
      if (riders.every((r) => r.finishT !== null || r.dnf)) running = false;
    }

    function order() {
      return riders.slice().sort((a, b) => (b.finishT !== null && a.finishT !== null ? a.finishT - b.finishT : b.d - a.d));
    }

    function draw() {
      const ord = order();
      const lead = ord[0];
      clockEl.textContent = fmt(simT);
      riders.forEach((r) => {
        const p = course.getPointAtLength(((Math.max(0, r.d) % total) + total) % total);
        r.el.setAttribute('transform', 'translate(' + p.x.toFixed(1) + ',' + p.y.toFixed(1) + ')');
        r.el.classList.toggle('sel', r.i === selected);
        r.el.classList.toggle('sos', r.sos);
        r.el.querySelector('circle').setAttribute('r', r.i === selected ? 18 : 14);
        if (r.i === selected) svg.appendChild(r.el);
      });
      tower.innerHTML = '';
      ord.forEach((r, pos) => {
        const li = document.createElement('li');
        if (r.i === selected) li.className = 'sel';
        const gap = r === lead ? t('Líder', 'Leader') : r.finishT !== null && lead.finishT !== null ? '+' + fmt(r.finishT - lead.finishT)
          : '+' + fmt(Math.max(0, (lead.d - r.d) / Math.max(1, lead.v || 4)));
        const secs = r.secColors.map((c) => '<i class="sec ' + c + '"></i>').join('');
        li.innerHTML = '<span class="pos">' + (pos + 1) + '</span><span class="bib">' + (r.i + 1) + '</span><span class="name">' +
          (r.nick || r.name) + '</span><span class="gap">' + gap + '</span><span class="sectors">' + secs.slice(0, 0) + '</span>';
        const sectors = li.querySelector('.sectors');
        sectors.innerHTML = r.secColors.map((c) => '<i class="sec ' + c + '"></i>').join('');
        li.addEventListener('click', () => select(r.i));
        tower.appendChild(li);
      });
      // callout for the selected rider
      const s = riders[selected];
      const remainingKm = Math.max(0, (total * LAPS - Math.max(0, s.d)) * kmPerUnit);
      const speedKmh = s.v * 6.6 * (sport === 'cycling' ? 1 : 0.34);
      const remainSim = s.finishT !== null ? 0 : s.v > 0.1 ? (total * LAPS - s.d) / s.v : Infinity;
      const etaTxt = s.finishT !== null ? '🏁 ' + fmt(s.finishT) : s.sos ? 'SOS' : isFinite(remainSim) ? fmt(simT + remainSim) : '—';
      const paceTxt = sport === 'cycling' ? speedKmh.toFixed(1) + ' km/h' : (speedKmh > 1 ? fmt(3600 / speedKmh) : '—') + ' /km';
      callout.innerHTML = '<b>#' + (s.i + 1) + ' ' + (s.nick || s.name) + '</b><span class="eta">' + etaTxt + '</span><small>' +
        t('predpokladaný čas v cieli', 'expected finish time') + ' · ' + paceTxt + ' · ' + remainingKm.toFixed(1) + ' km · ' + Math.round(s.battery) + ' %</small>';
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

    // controls
    root.querySelectorAll('[data-speed]').forEach((b) => b.addEventListener('click', () => {
      speedMul = Number(b.dataset.speed);
      root.querySelectorAll('[data-speed]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
    }));
    root.querySelectorAll('[data-sport]').forEach((b) => b.addEventListener('click', () => {
      sport = b.dataset.sport;
      root.querySelectorAll('[data-sport]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
      draw();
    }));
    $('[data-act="sos"]').addEventListener('click', () => {
      const r = riders[selected];
      if (r.finishT !== null) return;
      r.sos = !r.sos;
      if (r.sos) flag('sos', 'SOS · #' + (r.i + 1) + ' ' + (r.nick || r.name) + ' ' + t('— organizátor je upozornený', '— the organizer is alerted'));
      else flag('', t('SOS zrušené · #', 'SOS cleared · #') + (r.i + 1));
      running = true; draw();
    });
    $('[data-act="restart"]').addEventListener('click', () => { running = true; reset(); });
    $('[data-act="again"]').addEventListener('click', () => { running = true; reset(); });
    // pre-run so the first impression already has a race in progress
    reset();
    for (let k = 0; k < 150 * 10; k++) step(0.1);
    selected = 0; draw();
    requestAnimationFrame(loop);
    return { redraw: draw };
  }

  window.PTDemo = { init };
})();
