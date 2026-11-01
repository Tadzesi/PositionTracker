/* Runs before the application bundle (a separate file, because the CSP allows no inline scripts). For the two entry links people open
   from a message or an e-mail it starts the work the bundle would only start after it has been downloaded and executed:
   - /e/<id> (spectator): the first two API requests, whose responses lib/api.ts takes over (window.__ptEarly);
   - /e/<id> and /p (participant): the chunks of that route and their CSS (the vite plugin route-preload writes the lists into <meta>). */
(function () {
  try {
    var base = document.currentScript.src.replace(/^[a-z]+:\/\/[^/]+/i, '').replace(/boot\.js(\?.*)?$/, ''); // "/tracker/app/"
    var m = /^(e|p)(?:\/([^/]+))?\/?$/.exec(location.pathname.slice(base.length));
    if (!m) return;
    var route = m[1];
    function list(kind) { var el = document.querySelector('meta[name="pt-' + kind + '-' + route + '"]'); return el && el.content ? el.content.split(' ') : []; }
    function link(rel, href, as) {
      var l = document.createElement('link');
      l.rel = rel; l.href = href; l.crossOrigin = ''; if (as) l.as = as;
      document.head.appendChild(l);
    }
    list('js').forEach(function (f) { link('modulepreload', f); });
    list('css').forEach(function (f) { link('preload', f, 'style'); });
    if (route === 'e' && m[2]) {
      var id = decodeURIComponent(m[2]), early = (window.__ptEarly = {});
      ['course', 'public'].forEach(function (k) {
        var path = '/api/events/' + id + '/' + k;
        var p = fetch(base.replace(/\/$/, '') + path, { credentials: 'same-origin' });
        p.catch(function () { /* api.ts retries with a normal request */ });
        early[path] = { t: Date.now(), p: p };
      });
    }
  } catch (e) { /* the application starts as usual */ }
})();
