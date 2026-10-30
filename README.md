# PositionTracker

Live position tracking for cycling and running events. Participants' phones send GPS fixes; the back end
matches them onto the course (laps, checkpoints, shared roads), computes distance, speed, ranking, time gaps,
segment colours, ETA and safety alarms (SOS, stopped, off course, offline, low battery), and broadcasts an
organizer view and a delayed, privacy-reduced spectator view.

This repository currently holds the core tracking engine and its HTTP/SignalR back end (Plan 1):

| Project | Purpose |
|---|---|
| `src/PositionTracker.Core` | Course model, GPX parser, course matching, participant tracker, ranking, ETA, event runtime, spectator projection, virtual riders. No package references. |
| `src/PositionTracker.Api` | ASP.NET Core host: device ingest, organizer/spectator views, SignalR hub, demo simulator. |
| `tests/*` | xunit tests for both. |

Design: `docs/superpowers/specs/2026-10-30-possition-tracker-design.md`; mockups: `docs/design/mockups/design-hub.html`
(open it from the file system).

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test
```

## Run in Development

```powershell
dotnet run --project src/PositionTracker.Api
```

The app listens on `http://localhost:5298` (profile `http`). The organizer key in Development is `dev-key`
(`appsettings.Development.json`, `Organizer:ApiKey`). There is no persistence: events live in memory.

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
  `Retry-After`).
- At most 600 fixes per minute and token are ingested; the excess of a batch is counted as rejected and the
  answer is still 202.

`POST /api/events/{eventId}/actions` with `{"action": "sos" | "sosCleared" | "pause" | "resume" | "dnf"}`:
`204`, same authentication and status codes.

## Configuration

| Key | Meaning |
|---|---|
| `Organizer:ApiKey` | Organizer key for `/live`, the demo endpoints and `JoinOrganizer`. Required; startup fails without it. |
| `Live:IntervalMs` | Broadcast interval in milliseconds (default 1 500, must be positive). |

## Demo mode

The simulator (`SimulationService`) generates deterministic virtual riders (`VirtualRider`) and feeds their
fixes to `EventRuntime.Ingest` in process; it does not post to the HTTP ingest endpoint (controller ruling 11).
The endpoint allows 60 requests per minute and token, which a simulation at time scale 20 or more would exceed,
and after authentication the endpoint calls the same `EventRuntime.Ingest`. Consequently the demo does not
exercise HTTP-level limits, authentication or the per-token fix budget. Each simulated event runs on its own
accelerated clock; by default rider 1 raises SOS at 40 min, rider 2 goes off course at 25 min and riders 3 and 4
lose signal (fixes queued) from 30 to 34 min.

## Rulings that bind later plans

- Alarm acknowledgement must be keyed on (participantId, Kind), not on `Since`.
- Any future change of participant info (consent, nickname, ...) MUST bump `EventRuntime.ParticipantsVersion`:
  the spectator projection is cached on it, so a missed bump is a privacy leak.
- `SpectatorDelay` must be validated (lower and upper bound) at event creation.
- `HasParticipant` check-then-act in the ingest endpoint assumes participants are never removed.
- The client must drop a batch on HTTP 400.
- Configuration must be read via options (`IOptions<T>`), not ad hoc.
