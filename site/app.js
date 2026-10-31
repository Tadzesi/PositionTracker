/* Behaviour of the page. The text itself is pre-rendered in Slovak (/tracker/) and English (/tracker/en/) by build.py, so search
   engines and AI crawlers read both; this file only handles the theme control, the guide tabs (with permalinks), the copy-link
   button and the demo. */
(function () {
  const root = document.documentElement;
  const lang = root.lang === 'en' ? 'en' : 'sk';
  const t = (sk, en) => (lang === 'sk' ? sk : en);

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
    const g = document.getElementById('guides');
    if (g) g.scrollIntoView();
  }
  fromHash();
  window.addEventListener('hashchange', fromHash);

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

  window.PTDemo.init(document.getElementById('demo'), t);
})();
