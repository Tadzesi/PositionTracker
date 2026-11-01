# Deployment and pilot checklist (Plan 5)

The API serves the web app (`web/`, built into `wwwroot`) and the JSON API from one process on one port. A reverse proxy terminates
HTTPS in front of it. Nothing here has been deployed yet: the gates at the end are open.

## Build

```
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
```

`.env.prod` (never committed): `DB_PASSWORD`, `ORGANIZER_API_KEY` (operator key for `/api/ops/*`), `PUBLIC_BASE_URL` (the public https
origin, no trailing slash), `MAIL_FROM`, `SMTP_HOST/PORT/USER/PASSWORD`, `RATE_LIMIT_SALT` (random, 32+ characters), optional `ADMIN_EMAILS` (administrators, comma separated; they get the Admin page), optional `PROXY_NETWORK`
(CIDR of the reverse proxy as seen by the app), `APP_PORT`. The key directory volume must be writable by the container user:
`docker run --rm -v positiontracker_pt-keys:/keys busybox chown 1654:1654 /keys` once (the volume name depends on the compose project name).

Without `SMTP_HOST` the app refuses to start unless `ALLOW_SIMULATED_EMAIL=true` (simulated mail: nobody receives anything).

## Reverse proxy

- Route everything (`/`, `/api/`, `/hubs/`) to `http://127.0.0.1:${APP_PORT}`; the SignalR hub needs WebSocket upgrade headers.
- Send `X-Forwarded-For` and `X-Forwarded-Proto` and list the proxy in `Proxy:KnownProxies` / `Proxy:KnownNetworks`
  (`Proxy__KnownNetworks__0` in compose). **Required**: without it every visitor shares the proxy's address and one attacker can use up the
  per-IP sign-up and sign-in budget of everybody (Plan 2 ruling, Task 4 minor 8). With it set, `X-Forwarded-*` is trusted from that proxy only.
- Do not log request paths or query strings at the proxy: `/api/p/{token}` and the e-mail links carry credentials. If they are logged,
  keep those logs short-lived and private.
- Cloudflare Tunnel (the lab463.com setup): point a hostname or a path at the app; set `PUBLIC_BASE_URL` to that origin. A sub-path
  deployment is not supported (the app uses absolute paths).
- The app sets `Content-Security-Policy`, `X-Frame-Options: DENY`, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` and (over
  HTTPS) HSTS itself. The CSP allows OpenStreetMap tile images only; the fonts are self-hosted.

## Operations

- Health: `GET /healthz`. Mail mode and rate-limit hits: `GET /api/ops/email-mode` with `X-Api-Key`.
- Backups: dump the database (`pg_dump`) to a directory outside any web root; positions are deleted 30 days after an event ended
  (`Retention:FixDays`), so a backup keeps personal data for as long as the backup lives.
- Restart: running events are rebuilt from the database (positions, actions, device statuses); device tokens survive.
- Logs: the host does not log request paths. Tokens are never written to the application log.

## Pilot checklist

Before the first real event:

1. [ ] Email gate: Gmail headers show SPF/DKIM/DMARC `pass` and the message is in the inbox; same for an Outlook.com mailbox; the table in
   `docs/ops/email-deliverability.md` is filled; the mailbox password was rotated.
2. [ ] OwnTracks field gate (`docs/ops/owntracks-setup.md`): one Android and one iOS phone, 20 minutes with the locked screen; the answers to the
   UNVERIFIED list (429 handling, base64 deep link, interval, battery, offline catch-up) are recorded and the tuning values adjusted.
3. [ ] HTTPS works end to end, `PUBLIC_BASE_URL` matches, the proxy is in `Proxy:*`, and a sign-up from the public URL arrives by mail.
4. [ ] A rehearsal event with 3+ friends: register, confirm, consent, set up OwnTracks, go live, walk or ride a lap, watch `/e/{id}`
   and race control on a phone; mark one DNF; delete one participant's data; finish the event.
5. [ ] Restart the app during the rehearsal: the views come back identical and the phones keep sending.
6. [ ] Organizer briefing: SOS is by phone, DNF is marked by the organizer, spectators see delayed positions, the organizer issues the
   official results, data is deleted after 30 days.
7. [ ] Known limits accepted: in-memory rate limiters (per process), no CSV import, no push notifications (in-page alert only), no PWA tracker.
