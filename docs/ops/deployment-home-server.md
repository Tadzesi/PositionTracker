# Home server deployment (deployed 2026-10-31)

| What | Where |
|---|---|
| Marketing site | https://www.lab463.com/tracker/ (static files in `~/server/stacks/web/site/tracker/`, source: `site/`) |
| Application | https://www.lab463.com/tracker/app/ (container `positiontracker`, web app built with base `/tracker/app/`) |
| Database | container `positiontracker-db` (PostgreSQL 17, volume `positiontracker_pt-db`, internal network only) |
| Stack | `~/server/stacks/positiontracker/` (`docker-compose.yml`, `.env` chmod 600, `src/` = `git archive` of the repository, `backup.sh`) |
| Routing | the nginx of the web stack (`~/server/stacks/web/nginx.conf`): upstream `positiontracker:8080`, `location ^~ /tracker/app/` (prefix stripped) and `^~ /tracker/` (static); snippet in `deploy/home-server/nginx-positiontracker.conf` |
| Traffic | Internet → Cloudflare → Cloudflare Tunnel → `lab463-web` nginx → `positiontracker:8080` |
| Mail | `possitiontracker@lab463.com` via Websupport SMTP (credentials only in the stack's `.env`) |

How the pieces fit: nginx sends the Cloudflare client address as `X-Forwarded-For` and `X-Forwarded-Proto: https`; the app trusts only the `frontend`
network (`Proxy__KnownNetworks__0=172.30.0.0/24`), so the per-IP limits see real visitors. The session cookie is rewritten to path `/tracker/app/`
(`proxy_cookie_path`). The app sets its own CSP, `X-Frame-Options` and cookie flags; the location defines an `add_header` so the server-level headers
are not added twice. The app location sends `X-Robots-Tag: noindex` (participant links carry credentials), the static site is indexable.

## Update the application

```bash
# on the dev machine, from the repository root (commit first)
git archive --format=tar HEAD | ssh tadzesi@192.168.1.2 'rm -rf ~/server/stacks/positiontracker/src && mkdir ~/server/stacks/positiontracker/src && tar -x -C ~/server/stacks/positiontracker/src'
ssh tadzesi@192.168.1.2 'cd ~/server/stacks/positiontracker && docker compose build app && docker compose up -d app'
```

Migrations run at start (`Persistence__MigrateOnStartup`). Running events are rebuilt from the database after a restart.

## Update the site

```bash
cd site && python build.py                       # renders dist/ (Slovak /tracker/ and English /tracker/en/)
cd .. && ssh tadzesi@192.168.1.2 'rm -rf ~/server/stacks/web/site/tracker.new && mkdir ~/server/stacks/web/site/tracker.new'
tar -C site/dist -cf - --exclude=seo . | ssh tadzesi@192.168.1.2 'tar -x -C ~/server/stacks/web/site/tracker.new && cd ~/server/stacks/web/site && mv tracker ~/backups/tracker.old-$(date +%Y%m%d-%H%M%S) && mv tracker.new tracker'
```

No nginx reload is needed. Asset URLs carry a content hash, so Cloudflare serves new files at once; the HTML is never cached.
SEO files shared with the whole portal (`sitemap.xml`, `robots.txt`, `llms.txt`, `llms-full.txt`) were extended on 2026-10-31 with the tracker
pages (backups: `~/backups/lab463-<file>.bak.*`).

## Operations

- Logs: `docker logs positiontracker` (no request paths or tokens are logged). Health: `GET /tracker/app/healthz`.
- Operator endpoints need `X-Api-Key` (`ORGANIZER_API_KEY` in the `.env`): `GET /tracker/app/api/ops/email-mode`.
- Backup: `backup.sh` runs daily at 03:17 from cron (gz dump in `~/backups/`, 14 days). Restore: `gunzip -c dump.sql.gz | docker exec -i positiontracker-db psql -U positiontracker -d positiontracker`.
- Rollback of the routing: restore `~/backups/lab463-web-nginx.conf.bak.*` over `~/server/stacks/web/nginx.conf` and `docker exec lab463-web nginx -s reload`.
- The data-protection keys (session cookies) are in the volume `positiontracker_pt-keys`; they are not encrypted at rest (the container has no certificate). Losing the volume signs everybody out.

## Still open

The pilot checklist in `docs/ops/deployment.md` (email deliverability table, OwnTracks field test, rehearsal event) is not done; the SMTP password has not been rotated.
