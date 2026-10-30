# PositionTracker MVP: Design

Date: 2026-10-30
Status: approved for planning (design approved 2026-10-30)

## 1. Intent and why

Race organizers (running and cycling) want to see, live, where every participant is, how they perform, and when something goes wrong. Spectators want to follow a specific person. Participants want certainty that their position is being broadcast.

The MVP exists to **show the potential and range of such an application** to the people who would adopt it. It is a demonstrator, not a certified timing or safety system. It must look and behave convincingly in a live demo and in a small real pilot.

The user asked for the analysis to focus on **problems that appear during real use**. Section 9 is therefore a first-class part of this document.

## 2. Users and what each needs

| Role | Needs |
|---|---|
| Organizer (race control) | Live map of everyone, ranking by position on course, statistics, alarms (SOS, stopped, off course, offline), per-participant detail, event setup |
| Participant | Start broadcasting with minimal effort, see own position and statistics, know the connection state at a glance, send SOS |
| Spectator | Public link, find a person, follow them, see estimated finish time, get notified at the finish |

## 3. Goals, non-goals, anti-goals

### Goals
- Live tracking of phone position for runners and cyclists, delivered as a PWA plus a C# API.
- Three user-facing surfaces: participant, organizer, spectator (plus the participant join flow).
- Course imported from GPX before the event; participants are matched to the course.
- Show **all available data** in the MVP (section 5), with derived outputs such as estimated finish time.
- A built-in **simulator** that replays a GPX as virtual participants, so the product can be demoed without a real race.
- Light and dark mode on every page from the start.
- Slovak and English interface from the start (i18n built in from the first screen).
- Races with **one or more laps**.
- **Registration by the organizer:** the organizer creates an account, creates the event, and registers participants by name and email; participants confirm by email.

### Non-goals (MVP)
- Official timing, results, or any claim that ranking is authoritative.
- Payments, ticketing, and registration forms beyond name, category, bib and wave.
- Native mobile apps. Reliable background tracking with a locked phone.
- In-app course drawing or editing (v2).
- Heart rate and other sensors, power and calorie estimates (need rider weight; later).
- Multi-tenant organizations, billing, user accounts beyond what the MVP needs.
- Chat or two-way messaging with participants (later).

### Anti-goals (a failure even if it technically works)
- Implying that the app guarantees participant safety. SOS and "stopped" alarms are aids only, and the UI and terms must say so.
- Publicly exposing a person's location outside the event window, or without consent.
- A ranking that spectators or participants treat as official and dispute.
- A demo that only works with perfect GPS and connectivity.

## 4. Decisions already made by the user

- Participants broadcast via a **PWA**; this is sufficient for the MVP.
- Server is a **C# API service**; the client is a **web application**.
- Organizer prepares the course in advance. **GPX import is in the MVP**; drawing a course in the app is deferred to the next version because of its complexity.
- Design is inspired by **F1 television graphics** (timing tower, sector colours, flag-style race-control messages).
- **Every page exists in light and dark mode.**
- The MVP shows all data that can be collected (section 5), not a reduced set.
- **Hosting is the existing home server** (`192.168.1.2`) behind the existing reverse proxy, reachable from the internet over HTTPS (the browser Geolocation API and PWA installation require it).
- **The interface is available in Slovak and English** from the start; the user can switch language.
- **Multi-lap races are supported** (reverses the earlier single-pass proposal).
- **In the MVP the organizer registers participants** (manual entry is primary, CSV import is optional); each participant confirms participation by email. Public self-registration by participants is a **later version**.
- **Emails are sent from the project's own server and own domain** (own SMTP), not through an external provider.

## 5. Data

### 5.1 Collected from the phone (PWA)

| Source | Data | Notes |
|---|---|---|
| Geolocation API | latitude, longitude, accuracy, altitude, heading, speed, timestamp | `speed` and `heading` are often null, so they are computed from consecutive fixes |
| App state | foreground/background, Wake Lock active, location permission | drives the "broadcast interrupted" warnings |
| Network | online/offline, queue length of unsent points | connection type only where the browser exposes it |
| Battery Status API | percentage, charging | Chromium/Android only; unavailable on iOS, so shown as unknown |
| User input | SOS, pause, finish, DNF, "I'm OK" | |

Deferred: DeviceMotion cadence (needs permission on iOS, foreground only), Web Bluetooth heart rate (Android only), barometer (not available to browsers).

### 5.2 Derived on the server

- **Movement:** current speed, moving speed, average, maximum, pace (min/km), moving time, stopped time.
- **Course:** lap number, distance along the lap, total distance (laps completed × lap length + progress), remaining distance, percent complete, distance off the course.
- **Elevation:** ascent and descent, current gradient, climb rate. **Elevation is read from the course profile at the matched position, not from GPS altitude** (GPS altitude is too noisy). Off the course, elevation is not shown.
- **Estimates:** ETA to the finish and to the next checkpoint, shown as a range, based on a rolling average of the last 5 to 10 minutes of moving speed adjusted for the gradient of the remaining course. When data is weak the estimate is labelled approximate.
- **Competition:** rank by position on the course, gap to leader and to the participant ahead (time and distance), split times at checkpoints, per-segment performance.
- **Data quality:** GPS accuracy, time since last signal, connection state, battery.

### 5.3 Segment colours

The course is divided into segments between checkpoints (called sectors in the UI). After a participant completes a segment: **purple** = fastest of all participants so far, **green** = personal best, **yellow** = slower. A purple result needs at least two finishers of that segment. A segment is identified by its position in the lap; times from all laps are compared, and the lap number is shown next to each result. A lap summary (lap time, lap rank) is also shown.

### 5.4 Participant states

`Not started` → `Live` (last point within 15 s) → `Delayed` (15 s to 2 min) → `Offline` (over 2 min) → `Stopped` (speed under 1 km/h for X min) → `Off course` → `SOS` → `Finished` / `DNF`

All thresholds are configurable per event.

### 5.5 Visibility by role

| Data | Participant | Organizer | Spectator |
|---|---|---|---|
| Position, speed, distance, ETA | own | all | all, if enabled, delayed by a configured amount |
| Rank, gaps | optional | yes | yes |
| Battery, GPS accuracy, queue length | own | yes | no |
| Alarms, SOS | own | yes | no |

## 6. Surfaces

Mockups (dark and light) are in `.superpowers/brainstorm/488-1793344784/content/`: `design-hub.html` opens all three: `registration-flow-themed.html` (organizer account, event creation, participant management, invitation email, participant confirmation), `broadcast-design-themed.html` (organizer dashboard and participant live screens) and `spectator-join-themed.html` (spectator and join flow). The design was approved by the user on 2026-10-30. The mockups are the visual reference, not pixel specifications.

### 6.0 Organizer account, event creation and participant registration

**Organizer**
1. Registers with name and email and confirms the email address. Sign-in afterwards is by a one-time link sent by email (no passwords in the MVP; the writer's call).
2. Creates an event: **name, location, date and start time, sport** (running or cycling) are required. A draft event can be created before the email is confirmed.
3. After the email is confirmed: uploads the GPX course, sets the number of laps, checkpoints, categories and waves. Course upload and inviting participants are blocked until the email is confirmed (abuse protection).

**Participants are registered by the organizer** (MVP)
4. **Manual entry (form) is the primary way.** Fields: name, email, gender, language (SK/EN), optional category and wave. The bib number is assigned automatically unless provided. Duplicate emails within an event are flagged. **CSV import is an optional feature**: it can be delivered after manual entry or dropped from the MVP without affecting the rest; when present, it reports invalid and duplicate emails before saving.
5. Each participant gets an invitation email in their language with a personal confirmation link. The organizer can send a reminder, resend, and set a confirmation deadline.

**Participant**
6. Opens the link, **confirms or declines** participation, and can change their **nickname** and **gender** (gender is locked when the race starts; nickname can be changed any time).
7. Consent to broadcasting location is a **separate step** in the join flow (6.1) and is not implied by confirming participation.
8. Only a participant who has confirmed can broadcast; the same personal link is the participant's entry into the join flow.

**Organizer view of registrations**
9. Status per participant: `Invited`, `Confirmed`, `Declined`, `Email failed`. Filters by status, counts at the top, actions Resend, Edit, Remove, Re-issue link.
10. The organizer can hide or rewrite a nickname shown to spectators (moderation). For minors only the bib number is shown publicly.

**Email delivery (own server, own domain)**
- Sent through the project's own SMTP server using the project's domain. For this to be deliverable the domain needs SPF, DKIM and DMARC records and a matching reverse DNS entry; many residential networks block outbound port 25.
- **Prerequisite before building registration:** send test messages from the server to Gmail and Outlook addresses and check that they arrive in the inbox, not spam, and that outbound port 25 is open. If not, the sender is swapped for a relay without changing the rest (the email sender is behind an interface).
- Sending runs from a queue with retries; bounces mark the participant `Email failed`.
- Emails exist in Slovak and English. Confirmation and sign-in links are single-purpose, expire (confirmation: 14 days; sign-in link: short), and are invalidated when a new one is issued.
- The participant's email is visible only to the organizer, never to spectators. Participants can request deletion of their data.

**Later version:** public self-registration through an event registration link and QR code, feeding the same confirmation flow.

### 6.1 Participant join flow (4 steps)
1. **Confirm participation:** opened from the link in the invitation email (the link is the participant's personal token; a QR code of the same link is shown on the page so it can be opened on another phone). The participant sees the event, confirms or declines participation, and may set a nickname (and gender until the start). Reopening the link resumes the flow. See section 6.0.
2. **Consent:** who sees the location (organizer exactly; spectators delayed), when broadcasting starts and ends (start of own wave to finish, then it switches off automatically), how long data is kept. Consent to broadcast is required. Showing the name to spectators is optional; otherwise only the bib number is shown.
3. **Phone setup:** checklist with a reason for each item: location permission, add to home screen, keep screen on, battery level, keep the app open.
4. **Ready to start:** countdown to the wave start, GPS quality, connection, battery, distance to the start line. Start broadcasting is enabled once the checks pass.

### 6.2 Participant live screen
- Top status bar: LIVE / Delayed / Offline with time since last successful send.
- Speed as the largest element; rank with gap to the participant ahead.
- Mini map with the course and own position; segment strip with times and sector colours.
- Distance covered, remaining, ascent; ETA with its spread.
- Technical line: GPS accuracy, battery, queue length.
- SOS by drag (not tap), Pause, Finish.
- **Offline state:** the app keeps recording, shows a red bar, queue size, and marks rank and ETA as stale. Points are sent in a batch on reconnect.
- On returning from the background, a prominent warning that broadcasting may have been interrupted.

### 6.3 Organizer: event setup
- Create event, import GPX, set the **number of laps**, place checkpoints by clicking on the course, set categories and wave starts.
- Register participants (section 6.0): manual entry, with optional CSV import; see their confirmation status; resend invitations; edit, remove, or re-issue a participant's token.
- Configure alarm thresholds and what spectators see, including the spectator delay.
- Event states: Draft → Ready → Live → Finished.

### 6.4 Organizer: race control dashboard
- Header with event clock and counts (broadcasting, delayed, offline, DNF).
- Timing tower: rank, name code, sector colours, gap, status dot; filters by category and alarms.
- Map with the course, checkpoints, participants with trails, selected participant callout; elevation profile with participant markers.
- Race control feed: alarms as flag-style messages (red SOS, yellow stopped or off course, chequered offline) with Acknowledge, Show on map, Call actions.
- Selected participant telemetry: speed gauge, distance, ascent, gradient, battery, GPS, last signal, segment splits; notes and manual DNF.
- After the event: CSV/GPX export and static trails (full-field replay is later; see 11a).

### 6.5 Spectator
- Public link, no login. Search by name or bib; follow with a star; filter by category.
- Notice banner: positions are delayed by N seconds and ranking is indicative; the organizer issues official results.
- Map shows followed participants prominently, others as small dots.
- Followed panel: ETA to finish as the largest figure, checkpoint timeline with actual and estimated times, distance, speed, gap.
- "Notify me at the finish" (in-page alert in the MVP; Web Push later) and "Share this participant".
- Mobile: map on top, bottom sheet with the followed participant.

### 6.6 Visual direction
F1 broadcast: italic condensed display type (Barlow Condensed) with Titillium Web for data, angled parallelogram shapes, timing-tower layout, sector colours purple/green/yellow, flag-style race-control messages. Light and dark themes are driven by CSS custom properties, follow the OS setting by default, and have a manual toggle persisted in the browser. All user-facing strings exist in Slovak and English; names such as "Race control" and "P14" are the English variants.

## 7. Course handling

- Import GPX; smooth the elevation profile; compute lap length, ascent, descent; total race distance is lap length × laps.
- Lap counting: a lap is completed when matched progress passes the lap end (the start/finish line for a loop, or the last point for a point-to-point course repeated by the organizer's configuration). A participant finishes after the configured number of laps.
- **Map matching** projects each fix onto the course polyline with a monotonic-progress constraint: search only near the last known progress so that loops and out-and-back sections do not confuse the match.
- Off-course detection from distance to the course over a debounce window.
- Checkpoints are positions along the course; passing a checkpoint is detected from matched progress.

## 8. Suggested architecture (the writer's call; see section 11)

- ASP.NET Core API with SignalR for pushing live updates to organizer and spectator pages; a relational store (PostgreSQL) for events, courses, participants and position history.
- Position ingest endpoint that accepts batches, tolerates late and out-of-order points (ordering by device timestamp), and is authenticated by a per-participant token.
- Server-side computation of derived metrics and alarms; live updates to viewers are batched about every 1 to 2 seconds rather than per point.
- React or similar SPA built as a PWA; MapLibre or Leaflet for maps; OpenStreetMap-based tiles for the MVP.
- Hosted on the existing home server behind its reverse proxy with a valid HTTPS certificate; reachable from the internet. Security of exposing it (rate limiting, no admin surface on the public side) is part of the plan.
- Position source behind a single ingest API, so a native client or an existing tracker app can be added later without changing the server.

## 9. Problems expected in real use (and the MVP response)

| Problem | MVP response |
|---|---|
| A web page cannot track reliably in the background, especially iOS | Wake Lock, loud status, warning on return from background, API independent of source so native/third-party clients can be added later |
| Battery drain | Configurable send interval (about 5 to 10 s), batched sending, battery shown to the organizer, advice in setup |
| Poor or missing coverage on the course | Local buffering (IndexedDB) and batch upload; server accepts late points; viewers see "last known position X min ago" distinct from live |
| Noisy GPS, jumps | Filter by accuracy and by physically possible speed; light smoothing; no GPS altitude |
| Ranking disputes | Ranking marked indicative everywhere; organizer issues official results |
| Course loops, crossings, shortcuts | Windowed matching near last progress; off-course detection |
| Load spikes at the start and for spectators | Batched fan-out, marker clustering on the map, limit on trails drawn |
| Privacy (GDPR), minors | Explicit consent, defined start and end of broadcasting, automatic stop at the finish, retention limit and deletion on request, optional name display (hide for minors) |
| Spoofed location, impersonation | Per-participant signed token, rate limiting, jump detection; full anti-cheat is out of scope |
| Operational: participant forgot to start, permission revoked, phone killed the browser | "Expected but not broadcasting" state, QR-based fast setup at the start, offline/stopped alarms |
| Alarm fatigue | Configurable thresholds, acknowledge and snooze, grouping |
| Data cost, roaming | Small payloads (hundreds of kB per hour) |
| Map tile cost and limits | OSM-based tiles for the MVP; paid providers later |
| Safety misperception | UI and terms state the app is not a safety system |

## 10. Demo and pilot

- **Simulator:** replays an imported GPX as N virtual participants with configurable speeds, pauses, GPS noise, drops in connectivity, an SOS, and an off-course case. It runs through the same ingest API as real devices.
- **Pilot target:** a small real event (10 to 30 people) after testing on foot and by bike with a locked screen and an unlocked screen.

## 11. Left to the builder

- Choice of front-end framework and map library; database schema details; how matching and ETA are implemented, as long as the behaviour above holds.
- Specific default thresholds (stopped, delayed, off-course debounce), subject to being configurable.
- Authentication details for organizers (the MVP may use a simple scheme).
- Deployment details on the home server (container layout, certificate renewal, reverse proxy route), within the hosting decision in section 4.

## 11a. Calls made by the writer after the builder review (not yet confirmed by the user)

- **Multi-lap model (confirmed by the user).** Progress is total distance = laps completed × lap length + progress in the lap, and it is monotonic. Rank, gaps, ETA and finish detection use total distance. Matching is windowed near the last progress so that laps over the same ground do not confuse the match.
- **Time gaps** between participants are computed by interpolation: the time at which the participant ahead was at the same total progress. This requires position history indexed by total progress.
- **Different lap counts per category** are out of scope for the MVP: one lap count per event.
- **Scale target:** up to 100 participants and 500 concurrent spectators. A single server instance with SignalR is enough; no backplane in the MVP.
- **Registration by the organizer (confirmed by the user):** see section 6.0. Self-registration by participants is deferred. Each confirmed participant has one token (link/QR). The organizer can re-issue a token (for example after a phone change), which invalidates the old one. One organizer account per event in the MVP. The spectator link is per event.
- **Languages:** Slovak and English; the language defaults to the browser language and can be switched; the choice is remembered in the browser.
- **Stopped alarm:** default threshold is speed under 1 km/h for 3 minutes, configurable. A manual Pause suppresses the stopped alarm. Paused time is excluded from moving time and from the ETA basis.
- **Spectator consistency:** the spectator view is a delayed view of live data. Late batch uploads may recompute ranks and gaps; this is acceptable and the banner says ranking is indicative. Checkpoint times are corrected silently when a late point shows an earlier crossing.
- **Simulator:** a server-side "demo mode" that creates a separate event flagged as simulated, with virtual participants that use the real ingest API. Simulated and real events are never mixed, and simulated events can be deleted in one action.
- **Consent, terms and the "not a safety system" notice:** placeholder text in the MVP; final legal wording is supplied by the organizer before any real event.
- **Push notifications are out of the MVP.** "Notify me at the finish" uses an in-page alert (sound and title change) while the page is open. Web Push is a later item.
- **Replay is out of the MVP.** The MVP offers CSV and GPX export and static trails per participant. A time-scrubbable replay of the whole field is a later item.

## 12. Assumptions not yet confirmed by the user

These were proposed by the writer and are open to change:
- Spectator delay default of 30 seconds.
- Data retention of 30 days.
- "Elevation gain" is the meaning of the original request's "prevýšenia".
- Wave-based starts with an automatic start of broadcasting at the wave start.
- Passwordless sign-in by emailed link for organizers; confirmation link validity of 14 days.
- Gender is used only to place a participant into a category and is locked at the start.
- Rank is shown to participants optionally (organizer setting).
