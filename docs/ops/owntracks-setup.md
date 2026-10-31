# OwnTracks setup / Nastavenie OwnTracks

## Verified against the OwnTracks documentation (2026-10-31)

Sources: <https://owntracks.org/booklet/tech/http/>, <https://owntracks.org/booklet/tech/json/>,
<https://owntracks.org/booklet/features/remoteconfig/>.

| Item | Verified |
|---|---|
| HTTP mode | Apps POST the same JSON as MQTT payloads to one URL. `Content-Type: application/json`; Basic auth (user name + password in the app settings); TLS "very strongly recommended". |
| Response | Any 2xx = posted. A `200` normally has the body `[]` (it may carry `location`/`cmd`/`card`/`transition` objects; we always send `[]`). |
| Offline queue | "If the endpoint is unreachable, the payload is queued and posted later." Covers an unreachable endpoint. |
| Poll | The apps do not poll the endpoint; it is contacted only when a location is published. |
| `location` | `lat`, `lon` (degrees), `tst` (epoch **seconds**), `acc` (m), `alt` (m), `vel` (km/h), `batt` (%), `bs` (0 unknown, 1 unplugged, 2 charging, 3 full), `tid` (required in HTTP mode), `conn` (`w`/`o`/`m`), `t` (trigger). |
| Other types | `transition`, `lwt`, `waypoint(s)`, `status`, `card`, `beacon`, `steps`, `encrypted`, `configuration`, `cmd` — all answered `200 []` and ignored. |
| Config deep link | `owntracks:///config?inline=` + base64 of a one-line `.otrc` JSON (`openssl enc -a`); both apps. `_type: "configuration"` is mandatory. |
| Config keys | `mode` (0 MQTT, 3 HTTP), `url`, `auth`, `username`, `password`, `tid` (two characters), `monitoring` (-1 quiet, 0 manual, 1 significant, 2 move), `locatorInterval` (s), `locatorDisplacement` (m), `ignoreInaccurateLocations` (m, 0 = off), `locatorPriority` (Android only), `cmd`. |

### UNVERIFIED (to check in the field test)

- How the apps react to **4xx/5xx** (the booklet only says 2xx = posted): whether `401` stops sending, whether `429` is retried and
  when (our `Retry-After` may be ignored). The server answers `429` + `Retry-After` for a backlog over 600 fixes a minute.
- Whether the apps accept **percent-encoded standard base64** in `inline=` (we send exactly that; the booklet shows plain base64) and
  whether `base64url` would also be accepted.
- Minimum send interval actually achieved in `monitoring: 2` (move) with the screen locked, per platform; Android battery optimisation
  and iOS "Always" location behaviour; offline catch-up (order and speed of the queued messages); queue size limit.
- Whether `mode`, `url` etc. are applied when the app already has another configuration, and whether a second config link replaces the
  password (re-issue of the device token).
- Whether the app sends a zero-length or non-JSON body (we answer `200 []`).

## Server contract

`POST /api/ingest/owntracks` — HTTP Basic: user name = the **bib** (public code), password = the **device token** (shown once after
consent). Same `401` for every authentication failure. Body ≤ 16 KB (`413`). Location → one fix (`vel` and `alt` ignored; speed and
elevation come from the course matching); everything else, malformed bodies, and messages for events that are not live (or finished)
→ `200 []`. Budget 600 fixes/minute per token; above it `429` + `Retry-After`, nothing of the request is applied.
Tuning derived from the sport: move mode, interval 5 s (cycling) / 10 s (running), displacement 10 m, inaccurate > 50 m not sent.
The page `POST /api/p/{link}/owntracks-config {deviceToken}` returns `{deepLink, otrcJson, qrPayload}` (the token must be the current one;
nothing new is issued).

## Phone setup (SK)

1. Nainštalujte aplikáciu **OwnTracks** (Android: Google Play / F-Droid, iOS: App Store).
2. Na stránke od organizátora potvrďte účasť a udeľte súhlas; stránka zobrazí tlačidlo/QR „Nastaviť OwnTracks“. Otvorte odkaz na telefóne
   (alebo naskenujte QR) a potvrďte import konfigurácie.
3. **Android:** Nastavenia → Aplikácie → OwnTracks → Batéria → *Bez obmedzení* (vypnúť optimalizáciu batérie); Poloha → *Povoliť vždy*;
   v aplikácii nechajte režim **Move**.
4. **iOS:** Nastavenia → OwnTracks → Poloha → **Vždy**, Presná poloha zapnutá, Obnovovanie na pozadí zapnuté; v aplikácii režim **Move**.
5. Pred štartom skontrolujte, že aplikácia po odoslaní polohy nehlási chybu a že organizátor vás vidí. Počas preteku nevypínajte telefón
   ani šetrič batérie. SOS: zavolajte organizátorovi; DNF označuje organizátor.

## Phone setup (EN)

1. Install **OwnTracks** (Android: Google Play / F-Droid, iOS: App Store).
2. On the organizer's page confirm your participation and give consent; the page shows a "Set up OwnTracks" button/QR. Open the link on the
   phone (or scan the QR) and accept the configuration import.
3. **Android:** Settings → Apps → OwnTracks → Battery → *Unrestricted* (no battery optimisation); Location → *Allow all the time*; keep the
   app in **Move** mode.
4. **iOS:** Settings → OwnTracks → Location → **Always**, Precise Location on, Background App Refresh on; keep **Move** mode.
5. Before the start check that the app shows no error after sending and that the organizer sees you. Do not power off the phone or enable
   battery saver during the race. SOS: phone the organizer; DNF is marked by the organizer.

## Field gate (manual, open)

One Android and one iOS phone, 20 minutes walking or riding with the screen locked, using the generated config link. Record here:
send interval achieved, gaps, battery drain per hour, offline catch-up, and the answers to the UNVERIFIED list. The result decides the
default tuning values (`EventTuning`). **Status: not done.**
