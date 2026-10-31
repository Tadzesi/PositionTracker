# PositionTracker product page (https://www.lab463.com/tracker/)

A static site without a framework. `index.html` is the **template** (Slovak text, `data-i` keys); `i18n.en.json` holds the English text.
`build.py` renders `dist/index.html` (Slovak) and `dist/en/index.html` (English) as complete static pages, so search engines and AI crawlers
read both languages without running JavaScript.

```
python build.py                      # -> site/dist
python -m http.server 8099 --directory dist
python gen_og.py                     # re-creates assets/og-image.jpg (1200x630 link preview) from a screenshot
```

What the build adds: canonical + hreflang (sk, en, x-default), Open Graph and Twitter tags, JSON-LD (WebSite, WebPage, SoftwareApplication,
FAQPage, three HowTo guides), a content hash (`?v=…`) on every local asset (so Cloudflare never serves a stale file), and `dist/seo/` fragments
for the shared sitemap. A missing English string fails the build.

- Page behaviour: `app.js` (theme control, guide tabs with permalinks `#guide-organizer|participant|spectator`, copy-link), `demo.js` (the race demo, needs Leaflet in `vendor/`).
- `config.js`: `window.PT_APP_URL`, the address of the application (shows the "open the app" links).
- Add a text: put the Slovak text in `index.html` with `data-i="key"` (and `data-i-aria` / `data-i-alt` for attributes) and the English one in `i18n.en.json`.
- Screenshots in `assets/shots/` come from the running application with sample data; badges in `assets/badges/` are the official store badges.

Deploy (see `docs/ops/deployment-home-server.md`): build, then replace `~/server/stacks/web/site/tracker/` with `dist/` (without `seo/`).
The shared `sitemap.xml`, `robots.txt`, `llms.txt` and `llms-full.txt` of lab463.com live in `~/server/stacks/web/site/` and already list /tracker/ and /tracker/en/.
