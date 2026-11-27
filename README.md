# PositionTracker

[![CI](https://github.com/Tadzesi/PositionTracker/actions/workflows/ci.yml/badge.svg)](https://github.com/Tadzesi/PositionTracker/actions/workflows/ci.yml)

Live demo and guides: <https://www.lab463.com/tracker/>

Live position tracking for cycling and running events. Participants' phones send GPS fixes; the back end
matches them onto the course (laps, checkpoints, shared roads), computes distance, speed, ranking, time gaps,
segment colours, ETA and organizer alarms (SOS, stopped, off course, offline, low battery, laps inferred), and broadcasts an
organizer view and a delayed, privacy-reduced spectator view.

This repository holds the core tracking engine and its HTTP/SignalR back end (Plan 1) and, from Plan 2 on, the
PostgreSQL persistence:

| Project | Purpose |
|---|---|
| `src/PositionTracker.Core` | Course model, GPX parser, course matching, participant tracker, ranking, ETA, event runtime, spectator projection, virtual riders. No package references. |
| `src/PositionTracker.Api` | ASP.NET Core host: device ingest, organizer/spectator views, SignalR hub, demo simulator, persistence of the position history and rebuild of live events at startup. |
| `src/PositionTracker.Data` | EF Core model (`AppDbContext`, entities) and migrations for PostgreSQL. |
| `src/PositionTracker.Email` | Email messages, SK/EN templates, sanitising, `IEmailSender` with the MailKit `SmtpEmailSender` and the `LoggingEmailSender`. The only project that references MailKit. |
| `android/` | The native Android tracker of participants (Kotlin, Compose; self-install APK), see `android/README.md`. |
| `tools/SendTestMail` | Console tool for the deliverability check (`docs/ops/email-deliverability.md`). |
| `tests/*` | xunit tests. |

Design: `docs/superpowers/specs/2026-10-30-possition-tracker-design.md`; mockups: `docs/design/mockups/design-hub.html`
(open it from the file system).

## Build and test

Requires the .NET 10 SDK and, for the persistence tests, a running Docker (the tests start PostgreSQL containers
with Testcontainers).

```powershell
dotnet build
dotnet test
```

## Run in Development

Start the development database first (PostgreSQL 17 in Docker, data in the volume `positiontracker-db`):

```powershell
docker compose up -d
dotnet run --project src/PositionTracker.Api
```

The app listens on `http://localhost:5298` (profile `http`). The organizer key in Development is `dev-key`
(`appsettings.Development.json`, `Organizer:ApiKey`). In Development the app applies pending migrations at startup
(`Persistence:MigrateOnStartup`). The development database password `dev-only-not-a-secret` (in
`docker-compose.yml` and `appsettings.Development.json`) is a deliberately fake, documented value for a database that
listens on `127.0.0.1` only; a deployed database gets its own secret through configuration (for example the
environment variable `ConnectionStrings__Default`), never through a committed file. Without
`ConnectionStrings:Default` the app runs as in Plan 1, in memory only (a warning is logged at startup).

Migrations live in `src/PositionTracker.Data/Migrations` and are made with the EF tools pinned in
`.config/dotnet-tools.json`:

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project src/PositionTracker.Data --startup-project src/PositionTracker.Data
$env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=positiontracker;Username=positiontracker;Password=dev-only-not-a-secret"
dotnet ef database update --project src/PositionTracker.Data --startup-project src/PositionTracker.Data
```

## Demo walkthrough

Start a simulated event (default: 30 riders, 3 laps of a 12.5 km loop, time scale 20):

```powershell
$h = @{ 'X-Api-Key' = 'dev-key' }
$demo = Invoke-RestMethod -Method Post -Uri http://localhost:5298/api/demo/events -Headers $h `
  -ContentType 'application/json' -Body '{"participants":30,"timeScale":20,"laps":3,"sport":"cycling"}'
$demo   # eventId, organizerUrl, publicUrl
```

Optional request fields: `gpxBase64` (a GPX course, at most 1 MB), `sport` (`cycling` or `running`). Limits: 100
participants, time scale 200, 10 laps, 3 simulated events at once (409 beyond).

```powershell
# Organizer view (full data, alarms, device status)
Invoke-RestMethod -Uri "http://localhost:5298/api/events/$($demo.eventId)/live" -Headers $h
# Spectator view (no key; 30 s delayed, no alarms or device data, rounded values)
Invoke-RestMethod -Uri "http://localhost:5298/api/events/$($demo.eventId)/public"
# List and delete simulated events
Invoke-RestMethod -Uri http://localhost:5298/api/demo/events -Headers $h
Invoke-RestMethod -Method Delete -Uri "http://localhost:5298/api/demo/events/$($demo.eventId)" -Headers $h
```

The same with curl:

```bash
curl -X POST http://localhost:5298/api/demo/events -H "X-Api-Key: dev-key" -H "Content-Type: application/json" -d "{}"
curl http://localhost:5298/api/events/<eventId>/live -H "X-Api-Key: dev-key"
curl http://localhost:5298/api/events/<eventId>/public
```

### Live updates (SignalR)

Hub: `/hubs/live`. Server methods:

- `JoinOrganizer(eventId, apiKey)`: receives `LiveUpdate` with the organizer view.
- `JoinSpectator(eventId)`: receives `PublicUpdate` with the delayed spectator view.

Views are pushed every `Live:IntervalMs` (default 1 500 ms). An unknown event or a wrong key fails the call
with a `HubException`. A client address (IPv4 address or IPv6 /64) may hold 50 hub connections, all clients together 5 000
(Kestrel caps upgraded connections at the same number); one connection may join at most 4 groups.

## Device ingest contract

Authenticated with a per-participant bearer token (`Authorization: Bearer <token>`); tokens are issued per
event and participant (in Plan 1 only the demo simulator issues them).

`POST /api/events/{eventId}/positions`

```json
{
  "fixes": [
    { "t": "2026-11-07T10:00:05Z", "lat": 49.0012, "lon": 18.7034, "acc": 5 },
    { "t": "2026-11-07T10:00:10.250Z", "lat": 49.0015, "lon": 18.7041, "acc": 6 }
  ],
  "status": { "battery": 71, "charging": false, "queue": 2, "foreground": true }
}
```

- `t` must be UTC with a `Z` suffix; `acc` (metres) is optional; `status` is optional (`queue` and `foreground`
  required inside it, `battery` outside 0..100 is stored as unknown).
- Answer `202 {"accepted": n, "rejected": m}`. Fixes with invalid coordinates, poor accuracy (over 50 m),
  impossible speed, more than 60 s in the future or more than 1 h before the start, a second fix within the same
  second, or beyond 20 000 stored fixes are counted as rejected, not errors.
- `400` malformed body (the client must drop that batch, it will never succeed), `401` missing/unknown/revoked
  token (with `WWW-Authenticate: Bearer`), `403` token of another event, `404` unknown event or participant,
  `409 {"code":"event_finished",...}` the event has finished (the client stops sending; nothing is stored; also once the
  runtime left the memory), `409 {"code":"event_not_live",...}` a database event that the organizer has not started yet
  (nothing is stored; the client drops the batch and waits),
  `413` body over 256 KB or more than 500 fixes, `429` over 60 requests per minute and token (with
  `Retry-After`), `503` the position store cannot take the batch now (with `Retry-After: 1`; nothing of the batch
  was applied, the client resends it; also for actions).
- Fix budget: at most 600 fixes per minute (fixed one-minute window) and token. A batch is decided all or
  nothing: one that does not fit into the rest of the window is refused with `429` and `Retry-After` (seconds
  until the window ends) and nothing of it, neither fixes nor `status`, is applied. The client keeps the batch
  and resends it after `Retry-After`; since a batch holds at most 500 fixes, it always fits a fresh window. A
  70-minute backlog (840 fixes at 5 s) therefore goes through in two windows.
- The fix-budget `429` (problem details naming the 600 fixes per minute) is distinct from the request-limit
  `429` (60 requests per minute); both carry `Retry-After`. Budget taken by a request that then fails with `500`
  is not refunded.
- Unknown tokens: a token that is not cached is looked up in the database, and a client address (IPv4 address or IPv6
  /64) may have 30 lookups a minute that find nothing; beyond that further uncached tokens from it get `429` with
  `Retry-After: 60` without a query (OwnTracks too). Valid tokens of a live event are cached, so phones are not affected.
  Unknown participant links (`/api/p/{token}`) are limited the same way (30 a minute per address).

`POST /api/events/{eventId}/actions` with `{"action": "sos" | "sosCleared" | "pause" | "resume" | "dnf"}`:
`204`, same authentication and status codes.

`GET /api/device/session` (same bearer token and request budget): what a phone needs with only its token —
`{eventId, event:{name,location,sport,state,startTime}, me:{name,bib,nickname,startTime}, tuning:{intervalSeconds,displacementM,ignoreInaccurateM}, link, serverTime}`;
`state` is `draft|ready|live|finished`, `me.startTime` the own wave start (else the event start), `link` the data-link light
(null once finished or without a runtime). `401` for a missing/unknown/revoked token, `404` for a simulated event, `503` +
`Retry-After: 1` on a database failure. The Android app polls it every minute.

## Configuration

| Key | Meaning |
|---|---|
| `Organizer:ApiKey` | Organizer key for `/live`, the demo endpoints, `/api/ops` and `JoinOrganizer`. Required; startup fails without it, and outside Development also when it is shorter than 32 characters. A client address may present 20 wrong keys an hour; beyond that every key from it is refused for the rest of the hour. |
| `Live:IntervalMs` | Broadcast interval in milliseconds (default 1 500, must be positive). |
| `ConnectionStrings:Default` | PostgreSQL connection string. Without it nothing is persisted or rebuilt. |
| `Persistence:QueueCapacity` | Position history writes (one per ingested batch or action) not yet in the database; default 1 000. |
| `Persistence:EnqueueTimeoutMs` | How long a device request waits for a free write slot before `503`; default 2 000. |
| `Persistence:BatchFixes` / `Persistence:BatchIntervalMs` | The writer writes at 200 fixes or 500 ms after the oldest held write, whichever comes first. |
| `Persistence:MigrateOnStartup` | Apply pending migrations at startup (default false; true in Development). |
| `Email:Smtp:Host` | SMTP server. Empty (the committed default): nothing is sent, the `LoggingEmailSender` logs the outbox id, purpose and recipient domain and the row ends `Simulated`. Real sending is an explicit opt-in. Outside Development an empty host fails startup unless `Email:AllowSimulated=true`. |
| `Email:AllowSimulated` | Allow simulated email outside Development (default false). The mode is reported by `GET /api/ops/email-mode` (operator key): `{"mode": "smtp" or "simulated", "simulatedLast24h": n}`. |
| `Email:Smtp:{Port,UseStartTls,User,Password,TimeoutSeconds}` | SMTP connection (defaults 587, STARTTLS required, no auth, 30 s). |
| `Email:From` / `Email:FromName` / `Email:PublicBaseUrl` | Sender and the base of emailed links; `From` and `PublicBaseUrl` are required once a host is set. `PublicBaseUrl` must be https outside Development, without query or fragment (trailing `/` removed). Port 465 needs `UseStartTls=false`; credentials over a plain connection are rejected. |
| `Email:Outbox:{PollIntervalSeconds,BatchSize,MaxAttempts,RetryBaseSeconds,RetryMaxSeconds,RetentionDays}` | Outbox worker (defaults 5, 20, 6, 60, 3600, 7). |
| `Accounts:{RequestsPerIpPerHour,MailsPerEmailAndIpPerHour,MailsPerEmailPerHour,GlobalMailsPerHour,LinkRedeemsPerIpPerHour,LinkRedeemsPerHourPerPrefix48}` | Organizer account limits per hour (defaults 20, 3, 10, 200, 60, 300; the last per IPv6 /48 on top of the per-/64 one). |
| `Accounts:CodeFailuresBeforeRevoke` | Wrong six-digit codes of an account (counted on the account row, so a flood of other keys cannot reset them) after which all its live codes are revoked (default 5). |
| `Accounts:RateLimitMaxKeys` | Keys each limiter keeps at most (default 100 000; the oldest windows are evicted beyond). |
| `Accounts:MinResponseMilliseconds` | Sign-up, sign-in and code redemption answer no sooner than this (default 400 ms), so timing does not reveal an account. |
| `Accounts:RateLimitSalt` | Secret salt of the per-email limiter keys and the key of the stored code hashes (a six-digit code under a plain hash could be read straight from the table); empty = random limiter salt per process and an unkeyed code hash (Development only; required in the compose files). Changing it invalidates the codes in flight. |
| `Accounts:ConfirmPath` / `Accounts:SignInPath` | Front-end pages the emailed links open (defaults `account/confirm`, `account/signin`, relative to `Email:PublicBaseUrl`). |
| `Accounts:DataProtectionKeysPath` | Directory of the keys that encrypt session cookies. Set a persistent volume in a container, or every restart signs everyone out. |
| `Proxy:{KnownProxies,KnownNetworks}` | Addresses / CIDR ranges of the reverse proxy: only from these `X-Forwarded-For/Proto` is trusted (required behind a proxy, see `docs/ops/deployment.md`). Empty: the headers are ignored. |
| `Retention:{FixDays,CheckIntervalHours}` | Position fixes and device statuses are deleted `FixDays` (default 30) after the event ended; checked every `CheckIntervalHours` (default 24), first check one interval after start. The action log and results are kept. |
| `Registration:{MaxParticipantsPerEvent,InvitationsGlobalPerHour,InvitationsPerOrganizerPerHour,InvitationsPerAddressPerHour,InvitationsPerAddressPerDay,RemindMinAgeHours,InvitePath}` | Participant registration: at most 2 000 participants per event; invitation mails 300 per hour in total (below the mailbox quota, with room for the 200 account mails), 100 per organizer per hour, 5 per recipient address per hour and 10 per day (in memory, per process); without a confirmation deadline reminders only for links older than 24 h; the invitation link opens `{Email:PublicBaseUrl}/p?token=…`. |

Email credentials, real host names and addresses are never committed; `appsettings*.json` keep empty values.
Supply them with `dotnet user-secrets` (the API project has a `UserSecretsId`, shared with `tools/SendTestMail`),
for example `dotnet user-secrets --project src/PositionTracker.Api set "Email:Smtp:Password" "<smtp-password>"`, or
with environment variables (`Email__Smtp__Password`). Details, the DNS checklist and the (still PENDING) manual
delivery gate: `docs/ops/email-deliverability.md`.

## Email

Callers add an `EmailOutboxEntity` (`EmailOutbox.Create(message, participantId, now)`) in the same transaction as
the change that causes the mail; templates are `EmailTemplates.Invitation/SignIn/ConfirmOrganizerEmail`, links come
from `EmailLinks.Build(Email:PublicBaseUrl, path, rawToken)`. The `EmailOutboxWorker` (needs the database) claims due
rows with `FOR UPDATE SKIP LOCKED` and a lease sized for the whole batch, sends them, retries transient failures
(4xx, 5.7.x policy, 554 greeting/blocklist, network) with doubling delay and marks permanent ones (5xx recipient or
message rejection, 5.1.x) and the last failed attempt `Failed`; a failed invitation turns its `Invited` participant
into `EmailFailed`. The event time zone (`Europe/Bratislava`, needs tzdata) is checked at startup.
Delivery is **at least once**: a crash after the SMTP server accepted a message but before the outcome was saved
sends it again after the retry delay, at most `MaxAttempts` times in total (then the row is `Failed`, outcome
unknown). Logs and `LastError` never contain addresses, names, subjects, bodies or links; bodies are cleared when a
row finishes and rows are deleted after `Email:Outbox:RetentionDays`. All user values are made single-line
(CR/LF/control characters removed), length-limited, and HTML-escaped in the HTML body.

## Organizer accounts

`/api/account/*` (`src/PositionTracker.Api/Accounts`). **Every POST needs `X-Requested-With: PositionTracker` and
`Content-Type: application/json`** (missing header: HTTP **400** `csrf_failed`; other content type: HTTP 415
`unsupported_media_type`; checked by the
`BrowserApiHeaders` middleware before any limiter or database work; the same rule covers `/api/p/*` and the organizer
routes under `/api/events/**`, but not the device ingest `/api/events/{id}/positions|actions`).

- `POST signup {name,email,language}` and `POST signin {email}` always answer `202 {"status":"accepted"}` (also over
  a limit, also for an unknown address), never sooner than `Accounts:MinResponseMilliseconds`; a database failure is
  `503 unavailable` after the same delay. Sign-up mails a confirmation to a new or unconfirmed account (an unconfirmed
  account takes over the new name and language; the previous confirmation link is superseded) and a sign-in link to
  a confirmed one (nothing changes). Sign-in mails a link to any existing account and nothing to an unknown address.
- `POST confirm {token}` → `200 {"confirmed":true}` (reopening is fine); `POST signin/redeem {token}` → session
  cookie, the profile, and the email counts as confirmed (the link proves the mailbox). Errors
  `400 link_expired|link_used|link_invalid` (superseded, wrong purpose, unknown and missing tokens are `link_invalid`),
  `429 rate_limited` beyond 60 per IP bucket and hour. GET on these routes is `405`.
- Organizer names: trimmed, 1–200 characters; control characters (CR, LF, tab, …), line/paragraph separators and
  bidirectional formatting characters are rejected (`400 validation_failed`, field `name`), never stripped.
- `POST signout` (204) clears the cookie and rotates the security stamp, ending every session of the account.
- `GET me` → `{name,email,language,emailConfirmed,emailMode}` or `401 unauthorized`.
- Errors are `{"code","message":{"sk","en"}}` (+ `field` for `validation_failed`). Policies for later endpoints:
  `OrganizerSession`, `EmailConfirmedOrganizer` (`403 email_not_confirmed`).

Limits (in memory, per process, salted HMAC keys, no address or IP kept): 20 sign-up/sign-in requests per IP bucket
(IPv4 address or IPv6 /64) and, for IPv6, 100 per /48 (`Accounts:RequestsPerHourPerPrefix48`) per hour, taken all or
none; account mails 3 per (address, IP bucket), 10 per address and `Accounts:GlobalMailsPerHour` (200) in total per
hour. Suppressed requests increment `positiontracker.accounts.suppressed` (tag `limit` =
`ip|prefix|email_ip|email|global`); the global cap logs a warning once per hour. `GET /api/ops/email-mode` (operator
key) adds `limitHitsLast24h` and `limitHits` (`global`, `perEmail`, `perEmailAndIp`, `perIp`, `perPrefix`, `total`),
counts only: alert when `global` is above zero, since then nobody gets account mail for the rest of that hour.
Addresses are unique by their normalized form: lower-case local part and the lower-case IDNA ASCII domain
(`x@bücher.de` = `x@xn--bcher-kva.de` = `x@ＢÜＣＨＥＲ．de`); plus-addresses stay distinct; IDNA deviation
characters are mapped nontransitionally (`straße.de` ≠ `strasse.de`). Mail always goes to the delivery address
(local part as entered, `@`, ASCII domain), never to an exotic display form. An unconfirmed account takes over the
display address, name and language of a repeated sign-up; a confirmed one keeps them.

Links are 32 random bytes (base64url); only their SHA-256 is stored; confirmation 14 days (a new one supersedes the
old one, opened or not), sign-in 15 minutes and single use (never superseded: each link lives on its own). The
session cookie `__Secure-pt-session` is `HttpOnly`, `Secure`, `SameSite=Lax`, 30 days sliding, and holds only the
organizer id and security stamp.

**Front-end handoff.** The emailed links open front-end pages `Accounts:ConfirmPath` (`/account/confirm?token=…`)
and `Accounts:SignInPath` (`/account/signin?token=…`). These pages must not call the API on load: they show a
button ("Potvrdiť e-mail" / "Prihlásiť sa") that POSTs `{token}` to `/api/account/confirm` or
`/api/account/signin/redeem` with `X-Requested-With: PositionTracker` and `Content-Type: application/json`, then
remove the token from the address bar (`history.replaceState`). Serve them with `Referrer-Policy: no-referrer` and
keep the token out of analytics. Every other unsafe call of the front end sends the same two headers.

**Deployment.**
- **BLOCKER for Plan 5 (deployment):** behind the reverse proxy the per-IP limits see the proxy's address until
  forwarded headers are configured for that trusted proxy only (`ForwardedHeadersOptions.KnownProxies`); without it
  all users share one budget.
- Persist the data protection keys (`Accounts:DataProtectionKeysPath` on a persistent volume, readable only by the
  app); otherwise every restart or new container signs everyone out.
- Keep `Microsoft.AspNetCore` logging at `Warning` and do not log query strings of the front-end link pages.

## Organizer events

`/api/events` (`src/PositionTracker.Api/Events`), organizer session required (`401 unauthorized`); course,
categories, waves and state also need a confirmed email (`403 email_not_confirmed`). Unsafe requests need the two
browser headers (see above); the device routes `/api/events/{id}/positions|actions` stay exempt. Errors use the
shared `{"code","message":{"sk","en"},"field"?}` shape. An event of **another organizer, an unknown id and a
simulated (demo) event are all `404 not_found`**, for reads and writes alike; the list shows only the organizer's
own database events (demo events live only in memory and never appear). The runtime id of an event is its database
Guid formatted `D` (lower case); only that canonical form reaches an event (upper case, `N` or `{…}` forms are
`404 not_found`).

- `POST /api/events {name, location, startTime, sport}` → `201 {eventId}` (state `draft`, laps 1, spectator delay
  30 s); allowed before the email is confirmed. `name`/`location`: trimmed, 1–120 characters; control characters
  (CR, LF, tab, …), line/paragraph separators and bidirectional formatting characters are **rejected** (not
  stripped), the same rule as for organizer names at sign-up. `startTime`: UTC ISO 8601 with `Z`, strictly in the future (at most 5 years
  ahead). `sport`: `running` or `cycling`. Invalid input: `400 validation_failed` + `field`.
- `GET /api/events` → `{emailMode, events:[{eventId,name,location,startTime,sport,state}]}`;
  `GET /api/events/{id}/manage` → name, location, start, sport, state, laps, `pointToPoint`, spectator delay, `course`
  (`lapLengthM, ascentM, descentM` per lap, `pointCount, closed, uploadedAt, checkpoints`) or null, categories,
  waves, participant counts per status, `running` (runtime registered in this process), `emailMode`
  (`simulated`|`smtp`: whether mails really go out, ruling P8), `createdAt`, `endedAt`.
- `PUT /api/events/{id}/course {gpxBase64, laps?, pointToPoint?, checkpoints?:[{name,lapDistanceM}], spectatorDelaySeconds?}`
  (laps default 1, delay default 30 s). `pointToPoint: true` is a race from A to B without laps: laps are ignored and
  stored as 1, tracking is that of one lap, the organizer UI and the results CSV leave laps out (no `Kolá` column,
  splits named `Úsek N`); the course may be open or closed. The migration `EventPointToPoint` marks existing one-lap
  events on an open course as point-to-point. Everything is validated before the database is touched: `laps_invalid`
  (1–10), `delay_invalid` (5–300 s), `checkpoint_invalid` (name 1–60 characters, names unique ignoring case,
  distances distinct and strictly inside the lap, at most 50), `gpx_invalid` (missing, not base64, not GPX, DTD, fewer
  than 2 points: parsed by the hardened `GpxParser`), `413 gpx_too_large` (over 1 MB decoded; line-wrapped base64 is accepted; the body cap is 2 MB),
  `course_too_short` (lap under 50 m), `course_not_closed` (more than one lap on an open course). Stores the GPX
  bytes, laps, checkpoints and delay on the event and the derived parameters in `Courses`; a new upload replaces
  the old one.
- `PUT /api/events/{id}/categories [{name, gender?, minAge?, maxAge?}]` and `PUT /api/events/{id}/waves
  [{name, startTime}]` replace the whole set in one transaction (at most 50 each). Names 1–60 characters, unique per
  event ignoring case; a kept name keeps its row (participants keep their category). Category gender `female`,
  `male` or none; ages 0–120 with min ≤ max. A wave starts between the event start and 24 h after it (UTC, `Z`).
- `POST /api/events/{id}/state {state}`: only `draft → ready → live → finished`; repeating the current state is a
  success. `ready` needs a course (`409 course_missing`). `live` needs a course and at least one `Confirmed`
  participant (`409 no_confirmed_participants`); other moves are `409 invalid_transition`. Every answer is the
  manage view.
- **live** builds the `EventRuntime` with `RuntimeFactory` (the same code as the startup rebuild:
  `EventSettings.Create`, the stored course, the confirmed participants, the stored history) inside the transaction
  that sets `Live`, registers it in `EventRegistry` and commits; a commit failure unregisters it again, and a
  runtime that cannot be built rolls the transaction back (`500 runtime_failed`, the event stays `ready`). Ingest is
  persisted from then on. `live` on a live event whose runtime is missing (a failed startup rebuild) rebuilds it.
- **Frozen once live:** course, laps, checkpoints, delay, categories and waves answer `409 event_locked` in `live`
  and `finished`.
- **finished** sets `EndedAt` (retention counts from it). The runtime **stays registered, read-only**, so
  `/public` (and the operator's `/live`) keep showing the final state; the device endpoints answer
  `409 event_finished` and nothing more is stored. After a restart, events that finished less than 7 days ago
  (`RuntimeRehydrator.FinishedRuntimeDays`) are rebuilt the same way, still read-only; older ones are not running.
- **Concurrency:** every write of an event sets a new `Events.Version` (optimistic concurrency token). A course,
  category or wave write overtaken by another request is `409 conflict` and changes nothing (the client reloads and
  repeats); state changes re-read and retry, so concurrent identical `live` calls all succeed with one runtime.
- No personal data is logged (event ids and exception types only). A database failure is `503 unavailable`.

## Participant registration

`/api/events/{id}/participants` (`src/PositionTracker.Api/Registration`): organizer session **and confirmed email**
on every route (`401 unauthorized`, `403 email_not_confirmed`), the two browser headers on unsafe requests, the
same `404 not_found` for another organizer's event or participant, unknown, non-canonical and demo ids, `503
unavailable` without or on a failing database. Status values are `Invited`, `Confirmed`, `Declined`, `EmailFailed`.

- `POST {name, email, gender, language, category?, wave?, bib?}` → `201 {participantId, bib, status:"Invited",
  emailMode}` and an invitation in the participant's language (`sk`/`en`) to the delivery address, with a new
  14-day link (`ParticipantInvite`). `name` 1–200 characters (control, line-separator and bidi characters rejected,
  never stripped; unicode and emoji kept), `email` one plain address (display form kept, uniqueness on the normalized
  form: `409 email_duplicate`), `gender` `female|male`, `category`/`wave` by name (ignoring case, `400` if unknown),
  `bib` 1–16 ASCII letters/digits/hyphens (upper-cased; taken: `409 bib_taken`); without `bib` the highest numeric
  bib + 1 (also for an empty `bib`). Writes lock the event row, so bibs, duplicates and the cap are decided serially. `409 participant_limit`
  over `Registration:MaxParticipantsPerEvent`, `409 event_finished` on a finished event. Going live locks the same
  row, so a participant change and `state: live` never interleave. A participant registered
  while the event is live joins the runtime only when they confirm (Task 7).
- `GET ?status=&category=&q=` → `{emailMode, eventState, counts:{all, confirmed, invited, declined, emailFailed},
  participants:[{participantId, bib, name, nickname, nicknameHidden, email, gender, language, category, wave, status,
  isMinor, createdAt}]}`; counts cover the whole event; `q` searches name, email, bib and nickname.
- `PUT /{pid} {name?, email?, gender?, language?, category?, wave?, bib?, nickname?, hideNickname?}`: absent keeps a
  value, `""` clears category, wave and nickname (an empty `bib` keeps the bib). A new address supersedes the old link, withdraws unsent
  invitations, mails a new one and turns `EmailFailed` into `Invited`; a new display form of the same address sends
  nothing. Edits of a participant in a running runtime update it (`UpdateParticipant`; a hidden nickname is not
  shown). Answer: the participant + `emailMode`.
- `DELETE /{pid}` → `204` in `draft`/`ready` (unsent invitations and links go too); `409 event_live` (use DNF) and
  `409 event_finished`.
- `POST /{pid}/resend` → `202 {emailMode}` (new link, `EmailFailed` → `Invited`); `POST /{pid}/reissue-token` →
  `200 {deviceTokenIssued:true, mailQueued, emailMode}`: always revokes the device token in the database at once and
  mails a new link (the participant gets a new device token after consent); over an invitation limit the revocation
  still commits and `mailQueued` is false (resend later). The answer never holds a token. Both are
  `409 participant_declined` for a declined participant.
  `POST /remind` → `202 {sent, limited, emailMode}`. With a confirmation deadline: `409 remind_too_early` before it,
  afterwards a new link once to every `Invited` participant whose link predates the deadline; without one: every
  `Invited` participant whose link is older than `Registration:RemindMinAgeHours`. Runs in chunks of 50 per
  transaction.
- `PUT /api/events/{id}/confirmation-deadline {confirmationDeadline: "…Z" | null}` (draft/ready; `409 event_locked`
  once live) sets or clears the deadline (UTC, in the future, not after the start; else `400 validation_failed`);
  the manage view shows `confirmationDeadline`.
- Abuse limits: every invitation mail takes one unit of the global hourly budget, the organizer's hourly budget and
  the recipient address's hourly and daily budgets, all or none (given back if the change does not commit); over any,
  `429 rate_limited` and nothing changes (`remind` skips the rest and counts them as `limited`). Refusals per limit
  appear in `GET /api/ops/email-mode` as `invitationLimitHits` (`global, perOrganizer, perAddressHour,
  perAddressDay, total`, last 24 h); alert when `global` is above zero. The global cap logs a warning once per hour. A late
  permanent failure of an invitation marks the participant `EmailFailed` only while they still have that address.
- Device tokens (ruling P4): `ITokenService` routes database events to `DbTokenService` (hashed rows in
  `DeviceTokens`) and demo events to the in-memory `TokenService`. The device ingest still resolves through the
  in-memory service; Task 7 switches it.

## Web app

`web/` is a React + Vite + TypeScript single-page app (Slovak/English, light/dark following the OS with a manual toggle, F1-broadcast look from
`docs/design/mockups/`). Pages: landing, organizer sign-up/sign-in/confirm (`/account/*`), organizer events, event setup, participants and race control
(`/org/*`), the participant's personal page from the invitation email (`/p?token=…`: confirm, nickname, consent, OwnTracks setup with deep link and QR) and
the public spectator page (`/e/{eventId}`: search, follow, ETA, in-page finish alert, share). Live data is polled (organizer 2 s, spectators 3 s).

```
cd web
npm install
npm test                      # vitest (unit + page tests with a mocked fetch)
npm run build                 # writes src/PositionTracker.Api/wwwroot (git-ignored); the API serves it
npm run dev                   # Vite dev server on :5173, proxies /api and /hubs to http://localhost:5298
```

Open `http://localhost:5298/` after `dotnet run` (build the web app first). Without an SMTP host the e-mails are simulated; in Development read the
links from the outbox (`select "TextBody" from "Outbox"` before the worker clears the bodies) or configure `Email:Smtp:*` with user-secrets.
Deployment, the reverse proxy requirements and the pilot checklist: `docs/ops/deployment.md`; image: `Dockerfile`, `docker-compose.prod.yml`.

## Participant link and OwnTracks

The invitation link (`/api/p/{linkToken}`, no session, 60 requests/minute per link) lets a participant view their state, `confirm`
(nickname, gender), `decline` (until the start), edit the `profile`, give `consent` (returns the **device token once**; a second consent
re-issues it) and fetch the OwnTracks setup (`POST /owntracks-config {deviceToken}` → `deepLink`, `otrcJson`, `qrPayload`). Errors:
`link_invalid` 404, `link_superseded` 410, `link_expired` 400, `gender_locked`, `decline_locked`, `not_confirmed`, `event_finished` 409.
The host does not log request paths (the link token is in the path); a reverse proxy in front must not either.

On Android the device client is the **PositionTracker app** (`android/`, bearer endpoints above); the participant page offers it
("Open in the app" = `intent://p?token=…#Intent;scheme=positiontracker;…`). On iPhones and as the fallback it is **OwnTracks** in HTTP mode: `POST /api/ingest/owntracks` with Basic auth (user name = bib, password = device
token), see `docs/ops/owntracks-setup.md` (verified protocol facts, UNVERIFIED items, phone setup SK/EN, open field gate). The bearer
endpoints under `/api/events/{id}/positions|actions` remain for other clients and now accept database tokens.

## Organizer operations

All under `/api/events/{eventId}` with the organizer session (own events only, else `404 not_found`; unsafe requests need the browser headers):
`POST /alarms/ack {participantId, kind}` → 204 (the alarm shows `acknowledged:true` in `/live` and the hub push until it clears; a cleared alarm
that returns is new), `POST /participants/{pid}/dnf` → 204 (persisted in the action log), `DELETE /participants/{pid}/data` → 204 (privacy deletion:
fixes, statuses, consents, tokens, links and queued mails are deleted, name/email/nickname anonymised, only the bib stays; a running event keeps the
rider's in-memory track until the next restart). `GET /live` and the hub's `JoinOrganizer(eventId, null)` accept an organizer session for its own
events as well as the operator `X-Api-Key`.

## End to end

`EndToEndRegistrationTests` runs the whole path against PostgreSQL: organizer sign-up → email confirmation → sign-in → event with a 3-lap GPX course →
two participants (SK and EN) confirm through their emailed links, one sets a nickname and shows the name → ready → live → fixes with the device tokens →
`/live` and `/public` (nickname for the consenting rider, `#bib` for the other) → restart on the same database (identical views) → re-issued device token
(old token `401`). Run `docker compose up -d` only for local development; the tests start their own PostgreSQL container (Docker required).

## Persistence and restart

- **What is stored.** For real (non-simulated) events: every fix the tracker stored from a batch
  (`EventRuntime.IngestDetailed(...).Stored`), the batch's device status with the event-clock time it was ingested,
  and every action (SOS, pause, resume, DNF) with the time `EventRuntime.Act` applied it. Fixes the tracker throws
  away (invalid, inaccurate, duplicate, too early or too far ahead, over the cap, losing their second) are not
  stored. Fixes the speed filter skips *for now* are stored, because the tracker keeps them and a late fix can turn
  them into accepted ones on replay. Times of the position history are stored tick-exact (bigint UTC ticks).
  Simulated (demo) events are never persisted.
- **Off the request path.** Ingest takes a slot in a bounded queue, applies the batch, queues the write and answers;
  a background writer writes in batches (200 fixes or 500 ms). If all slots are taken (database slow or down) the
  request waits up to `Persistence:EnqueueTimeoutMs` and is then refused with `503` and `Retry-After: 1` *before*
  anything is applied, so the client resends it and nothing is dropped. Transient database errors are retried with
  backoff while the slots stay taken; a row the database refuses for good (constraint violation) is logged with its
  ids and dropped. On shutdown the queue is closed and drained; only if the host's shutdown timeout runs out are the
  remaining writes lost (logged with their count).
- **Restart.** At startup every event in state `Live` (and every event finished less than 7 days ago, read-only) is rebuilt by `RuntimeFactory`: course from the stored GPX, laps and checkpoints,
  confirmed participants, then the stored actions, fixes and statuses are replayed on a `ReplayTimeProvider` whose
  time is set to each stored time (no wall-clock reads during the replay); the runtime's clock then switches to the
  real clock. The organizer and spectator views after a restart equal those before it. The spectator view starts
  empty and fills once a view is a spectator delay old, as at any start.
- Device tokens are still in memory until Plan 2 Task 7 persists them: after a restart, devices need new tokens.

## Demo mode

The simulator (`SimulationService`) generates deterministic virtual riders (`VirtualRider`) and feeds their
fixes to `EventRuntime.Ingest` in process; it does not post to the HTTP ingest endpoint (controller ruling 11).
The endpoint allows 60 requests per minute and token, which a simulation at time scale 20 or more would exceed,
and after authentication the endpoint calls the same `EventRuntime.Ingest`. Consequently the demo does not
exercise HTTP-level limits, authentication or the per-token fix budget. Each simulated event runs on its own
accelerated clock; by default rider 1 raises SOS at 40 min, rider 2 goes off course at 25 min and riders 3 and 4
lose signal (fixes queued) from 30 to 34 min.

## Rulings that bind later plans

- Alarm acknowledgement must be keyed on (participantId, Kind), not on `Since`; each kind (including
  `LapsInferred`) appears at most once per participant.
- Any future change of participant info (consent, nickname, ...) MUST bump `EventRuntime.ParticipantsVersion`:
  the spectator projection is cached on it, so a missed bump is a privacy leak.
- Non-simulated events must be created with `EventSettings.Create`, which accepts a `SpectatorDelay` from 5 s to
  300 s (`ArgumentOutOfRangeException` otherwise); the unvalidated primary constructor is for tests and the demo
  simulator.
- Participant info changes go through `EventRuntime.UpdateParticipant`, which bumps `ParticipantsVersion`.
- Known limitation: after a long signal gap on a loop shorter than about 2 km the lap count can be wrong. The
  laps ridden unseen are guessed from the expected speed; a rider who stopped or slowed during the gap gets a false
  extra lap and is shown Finished a lap early, one who sped up misses a lap and never finishes. This is not
  corrected; a real correction is planned (Plan 5 pilot item). When laps were guessed, the organizer view shows
  `lapsInferred: true` and a `lapsInferred` alarm (since the fix that guessed them; it stays raised after Finished,
  never shown to spectators) so the organizer checks the rider. Only when the very fix that guessed the lap
  reaches the finish is the finish held back until a later fix confirms it (ETA null, no last segment time); a
  false lap guessed earlier is not caught. A lap wrongly not added raises no alarm.
- `HasParticipant` check-then-act in the ingest endpoint assumes participants are never removed.
- The client must drop a batch on HTTP 400.
- Configuration must be read via options (`IOptions<T>`), not ad hoc.
