# PositionTracker

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
with a `HubException`.

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

`POST /api/events/{eventId}/actions` with `{"action": "sos" | "sosCleared" | "pause" | "resume" | "dnf"}`:
`204`, same authentication and status codes.

## Configuration

| Key | Meaning |
|---|---|
| `Organizer:ApiKey` | Organizer key for `/live`, the demo endpoints and `JoinOrganizer`. Required; startup fails without it. |
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
| `Accounts:{RequestsPerIpPerHour,MailsPerEmailPerHour,LinkRedeemsPerIpPerHour}` | Organizer account limits (defaults 20, 3, 60). |
| `Accounts:MinResponseMilliseconds` | Sign-up and sign-in answer no sooner than this (default 400 ms), so timing does not reveal an account. |
| `Accounts:RateLimitSalt` | Secret salt of the per-email limiter keys; empty = random per process (the counters are in memory). |
| `Accounts:ConfirmPath` / `Accounts:SignInPath` | Front-end pages the emailed links open (defaults `account/confirm`, `account/signin`, relative to `Email:PublicBaseUrl`). |
| `Accounts:DataProtectionKeysPath` | Directory of the keys that encrypt session cookies. Set a persistent volume in a container, or every restart signs everyone out. |

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

`/api/account/*` (`src/PositionTracker.Api/Accounts`): `POST signup {name,email,language}` and `POST signin {email}`
always answer `202 {"status":"accepted"}` (also over a limit, also for an unknown address), never sooner than
`Accounts:MinResponseMilliseconds`. Sign-up mails a confirmation to a new or unconfirmed account (superseding the
previous link; name and language of an existing account stay) and a sign-in link to a confirmed one; sign-in mails a
link to any existing account and nothing to an unknown address. At most 20 such requests per IP and 3 mails per
address per hour; suppressed requests only increment the `positiontracker.accounts.suppressed` counter (tag
`limit`) and a debug log line. `GET confirm?token=` (`200 {"confirmed":true}`, reopening is fine) and
`GET signin/redeem?token=` (sets the session cookie, single use) answer `400 link_expired|link_used|link_invalid`
(superseded, wrong purpose and unknown links are `link_invalid`) and `429 rate_limited` beyond 60 per IP and hour.
`POST signout` (204) clears the cookie and rotates the account's security stamp, ending every session of the account.
`GET me` returns `{name,email,language,emailConfirmed,emailMode}` or `401 unauthorized`. Errors are
`{"code","message":{"sk","en"}}` (+ `field` for `validation_failed`).

Links are 32 random bytes (base64url); only their SHA-256 is stored; confirmation 14 days, sign-in 15 minutes.
They point to front-end pages (`Accounts:ConfirmPath`, `Accounts:SignInPath`) that call the API, so mail scanners
that prefetch links do not burn single-use sign-in links. The session cookie `__Host-pt-session` is `HttpOnly`,
`Secure`, `SameSite=Lax`, 30 days sliding, and holds only the organizer id and security stamp. Every POST, PUT, PATCH
or DELETE under `/api` that carries the cookie must send `X-Requested-With: PositionTracker` (`403 csrf_failed`
otherwise). Policies for later endpoints: `OrganizerSession`, `EmailConfirmedOrganizer` (`403 email_not_confirmed`).

Deployment notes: behind a reverse proxy the per-IP limits need the client address (configure forwarded headers
for the trusted proxy, otherwise all users share one IP budget); keep `Microsoft.AspNetCore` logging at `Warning`
(the hosting request log at `Information` contains query strings, i.e. link tokens); persist the data protection
keys (`Accounts:DataProtectionKeysPath`).

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
- **Restart.** At startup every event in state `Live` is rebuilt: course from the stored GPX, laps and checkpoints,
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
