/* Behaviour of the page. The text itself is pre-rendered in Slovak (/tracker/) and English (/tracker/en/) by build.py, so search
   engines and AI crawlers read both; this file only handles the theme control, the guide tabs (with permalinks), the copy-link
   button and the demo. */
(function () {
  const root = document.documentElement;
  const lang = root.lang === 'en' ? 'en' : 'sk';
  const t = (sk, en) => (lang === 'sk' ? sk : en);

  // ----- language: remember the explicit choice for the whole origin (the app under /tracker/app/ reads the same key) -----
  document.querySelectorAll('.seg a[hreflang]').forEach((a) => a.addEventListener('click', () => {
    try { localStorage.setItem('pt-lang', a.hreflang); } catch (e) { /* storage blocked */ }
  }));

  // ----- theme: two explicit choices; without a stored choice the system setting is shown as the active one -----
  const seg = document.getElementById('themeseg');
  const media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  const effective = () => root.getAttribute('data-theme') || (media && media.matches ? 'dark' : 'light');
  const syncTheme = () => seg.querySelectorAll('button').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.theme === effective())));
  seg.querySelectorAll('button').forEach((b) => b.addEventListener('click', () => {
    root.setAttribute('data-theme', b.dataset.theme);
    try { localStorage.setItem('pt-site-theme', b.dataset.theme); } catch (e) { /* storage blocked */ }
    syncTheme();
  }));
  if (media && media.addEventListener) media.addEventListener('change', syncTheme);
  syncTheme();

  // ----- guide tabs with permalinks: #guide-organizer, #guide-participant, #guide-spectator -----
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  const slugs = ['organizer', 'participant', 'spectator'];
  function show(tab, focus) {
    root.removeAttribute('data-guide'); // set by theme.js for a #guide-* link; from here on the real attributes decide
    tabs.forEach((x) => {
      const on = x === tab;
      x.setAttribute('aria-selected', String(on)); x.tabIndex = on ? 0 : -1;
      document.getElementById(x.getAttribute('aria-controls')).hidden = !on;
    });
    if (focus) tab.focus();
  }
  tabs.forEach((tab, i) => {
    tab.addEventListener('click', () => { show(tab); history.replaceState(null, '', '#guide-' + slugs[i]); });
    tab.addEventListener('keydown', (e) => {
      const n = e.key === 'ArrowRight' ? (i + 1) % tabs.length : e.key === 'ArrowLeft' ? (i + tabs.length - 1) % tabs.length : -1;
      if (n >= 0) { e.preventDefault(); show(tabs[n], true); history.replaceState(null, '', '#guide-' + slugs[n]); }
    });
  });
  function fromHash() {
    const m = /^#guide-(organizer|participant|spectator)$/.exec(location.hash);
    if (!m) return;
    show(tabs[slugs.indexOf(m[1])]);
    loadImages(document.getElementById(tabs[slugs.indexOf(m[1])].getAttribute('aria-controls')), 'high');
    scrollToGuides();
  }
  function scrollToGuides() {
    const g = document.getElementById('guides');
    if (g) g.scrollIntoView({ behavior: 'instant' });
  }

  // ----- images: lazy in the markup (so they never compete with the first paint), but ALL of them are fetched right after the page
  // has loaded, also those in hidden guide tabs, so that every part of the page is ready even for a visitor who never scrolls to it -----
  function loadImages(scope, priority) {
    scope.querySelectorAll('img[loading="lazy"]').forEach((img) => {
      img.fetchPriority = priority;
      img.loading = 'eager';
    });
  }
  function loadAllImages() { loadImages(document, 'low'); }
  const afterLoad = (fn) => {
    const run = () => (window.requestIdleCallback ? window.requestIdleCallback(fn, { timeout: 1500 }) : setTimeout(fn, 200));
    if (document.readyState === 'complete') run(); else window.addEventListener('load', run, { once: true });
  };

  fromHash();
  window.addEventListener('hashchange', fromHash);
  // a link to a guide: the fonts and images arriving later move the layout, so jump again once everything settled, unless the visitor already scrolled
  if (/^#guide-/.test(location.hash)) {
    let moved = false;
    const stop = () => { moved = true; };
    ['wheel', 'touchstart', 'keydown', 'mousedown'].forEach((e) => window.addEventListener(e, stop, { once: true, passive: true }));
    const again = () => { if (!moved && /^#guide-/.test(location.hash)) scrollToGuides(); };
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(again);
    afterLoad(again);
  }

  const copy = document.getElementById('copylink');
  copy.addEventListener('click', async () => {
    const i = tabs.findIndex((x) => x.getAttribute('aria-selected') === 'true');
    const url = location.origin + location.pathname + '#guide-' + slugs[Math.max(0, i)];
    try { await navigator.clipboard.writeText(url); } catch (e) { window.prompt(copy.textContent, url); return; }
    const note = document.getElementById('copied');
    note.textContent = t('Odkaz skopírovaný', 'Link copied');
    setTimeout(() => { note.textContent = ''; }, 2500);
  });

  // ----- link to the application (config.js) -----
  if (window.PT_APP_URL) {
    for (const id of ['appcta', 'appnav']) { const a = document.getElementById(id); if (a) { a.href = window.PT_APP_URL; a.hidden = false; } }
  }

  // ----- demo: Leaflet, its stylesheet and demo.js (templates, so nothing is fetched at first) start the moment the page has loaded; the
  // placeholder with the right size is already on screen. The rest of the images follows once the demo runs. -----
  function loadDemo() {
    const tpl = document.getElementById('demo-assets');
    const link = tpl.content.querySelector('link');
    const tiles = document.createElement('link'); // the tile server is a third party: open the connection while Leaflet downloads
    tiles.rel = 'preconnect'; tiles.href = 'https://tile.openstreetmap.org';
    document.head.appendChild(tiles);
    const loaded = (el) => new Promise((ok, fail) => { el.onload = ok; el.onerror = fail; document.head.appendChild(el); });
    const css = document.createElement('link');
    css.rel = 'stylesheet'; css.href = link.getAttribute('href');
    const scripts = [...tpl.content.querySelectorAll('script')].map((x) => {
      const e = document.createElement('script');
      e.src = x.getAttribute('src'); e.async = false; // dynamic scripts are async by default; leaflet.js must run before demo.js
      return e;
    });
    return Promise.all([css, ...scripts].map(loaded));
  }
  const stage = document.getElementById('demo');
  const onLoad = (fn) => { if (document.readyState === 'complete') setTimeout(fn, 0); else window.addEventListener('load', () => setTimeout(fn, 0), { once: true }); };
  onLoad(() => {
    loadDemo().then(() => {
      demo = window.PTDemo.init(stage, t);
      stage.classList.add('ready');
      if (wantPlay) demo.play();
    }, () => {}).then(loadAllImages);
  });
  // "Spustiť živé demo": besides scrolling to the demo the link starts it (with reduced motion it would otherwise stay stopped); a click
  // before the demo has loaded is remembered
  let demo = null, wantPlay = false;
  document.querySelectorAll('a[href="#demo"]').forEach((a) => a.addEventListener('click', () => { wantPlay = true; if (demo) demo.play(); }));
})();
