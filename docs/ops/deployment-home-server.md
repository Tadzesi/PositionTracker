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

How the pieces fit: nginx sends the client address as `X-Forwarded-For` and `X-Forwarded-Proto: https`. The address is
`CF-Connecting-IP` only for requests that come from the Cloudflare tunnel (docker network `frontend`, `172.30.0.0/24`, minus the
gateway `.1`); `lab463-web` also publishes port 8081 on the host, reachable from the LAN, and there the header would be whatever the
client sends, so it is ignored and the real peer address is used (`$pt_client_ip`, `geo`/`map` in the http context, see the top of
`deploy/home-server/nginx-positiontracker.conf`). On top of the app's own limits nginx allows 50 requests a second (burst 100) and
100 open connections per client address to the app, and caches the anonymous public answers of an event (`/public`, `/course`,
`/info`, `/public-results`) for 2 s, keyed without the query string; the app shares only the small network `pt-edge`
(`docker network create --subnet 172.30.77.0/29 pt-edge`, with the nginx container attached; compose of the web stack lists it) with that nginx and trusts
only it (`Proxy__KnownNetworks__0=172.30.77.0/29`), so the per-IP limits see real visitors and no other stack can forge a client address. The nginx
access log masks every `token=` value and the token in `/api/p/<token>/` (`map` blocks before `log_format`). The session cookie is rewritten to path `/tracker/app/`
(`proxy_cookie_path`). The app sets its own CSP, `X-Frame-Options` and cookie flags; the location defines an `add_header` so the server-level headers
are not added twice. The app location sends `X-Robots-Tag: noindex` (participant links carry credentials), the static site is indexable.

## Security hardening of 2026-11-03 (one-time steps on the server)

1. nginx (`~/server/stacks/web/nginx.conf`): back it up to `~/backups/lab463-web-nginx.conf.bak.<date>`, add the http-context lines
   listed at the top of the snippet (`geo $pt_via_tunnel`, `map ... $pt_client_ip`, `limit_req_zone`, `limit_conn_zone`,
   `limit_*_status`, the new `map $uri $pt_nocache` replacing the old `map $request_uri $pt_nocache`), replace the PositionTracker
   locations with the snippet, then `docker exec lab463-web nginx -t && docker exec lab463-web nginx -s reload`. If the frontend
   network's subnet ever changes, update the `geo` block.
2. The stack: copy `deploy/home-server/docker-compose.yml` over `~/server/stacks/positiontracker/docker-compose.yml` (limits,
   read-only root file system, `cap_drop`, pinned postgres digest), then update the application as below. The migration
   `OrganizerCodeFailures` runs at start. `ORGANIZER_API_KEY` must have at least 32 characters (startup fails otherwise).
3. Codes mailed before the update stop working (their hash is now keyed by `RATE_LIMIT_SALT`); sign-in links keep working.

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
