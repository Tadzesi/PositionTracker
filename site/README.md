# PositionTracker marketing site

A standalone static site (no build step): `index.html`, `styles.css`, `app.js` (Slovak/English, theme, guide tabs), `demo.js` (the live race demo that
runs in the browser) and the real application screenshots in `assets/shots/`.

```
cd site
python -m http.server 8099      # then open http://localhost:8099
```

- Slovak is the text in the HTML; English lives in the dictionary at the top of `app.js` (keys `data-i`, `data-i-aria`, `data-i-alt`).
- `config.js`: set `window.PT_APP_URL` to the address of a running app to point the final call to action at it.
- Deploy by copying the folder to any static host (the screenshots are about 1 MB in total).
- The screenshots were taken from the running application with sample riders; retake them after UI changes (see `docs/ops/deployment.md` for running it).

- Cloudflare caches CSS, JS and images for hours: after changing any of them bump the `?v=` value in `index.html` (all asset links carry it). HTML is never cached.
