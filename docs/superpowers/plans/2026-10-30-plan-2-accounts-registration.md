# Plan 2: Accounts, Events, Registration and Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Organizers sign up and confirm their email, create events, upload the GPX course, register participants (manual entry, optional CSV), and participants confirm by emailed link, set a nickname and gender, and get their device token; everything survives a restart, including the position history.

**Architecture:** PostgreSQL via EF Core (`PositionTracker.Data` project) behind small repository interfaces defined in Core/Api; the existing in-memory `EventRuntime` stays the live engine and is rebuilt from the database at startup (events in `Live` state, confirmed participants, stored fixes replayed in time order). Email goes out through an `IEmailSender` (MailKit over the project's own SMTP server) from a database-backed queue with retries. Organizer authentication is a passwordless emailed sign-in link that creates a cookie session; the Plan 1 `X-Api-Key` remains only for operators (demo endpoints, health tools).

**Tech Stack:** .NET 10, ASP.NET Core, EF Core + Npgsql, MailKit, xunit, Testcontainers for PostgreSQL (Docker is installed), `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-10-30-possition-tracker-design.md` (sections 5.5, 6.0, 9, 11a, 12). Plan 1 outcome and its binding rulings: `docs/superpowers/plans/2026-10-30-plan-1-execution-ledger.md` and the "Rulings" section of `README.md`.

## Global Constraints

- Branch off `feat/core-engine` (Plan 1 head `1efc8c4`); .NET 10, `Nullable` enabled, `TreatWarningsAsErrors` true; Core stays free of package references; all code, comments, commit messages in English.
- **Emails are sent from the project's own SMTP server and own domain** (`lab463.com`), never through an external provider. Before building any email feature, Task 3 verifies deliverability (SPF, DKIM, DMARC, rDNS, outbound port 25) with test mails to Gmail and Outlook; the sender stays behind `IEmailSender` so a relay can replace it without touching callers.
- UI languages Slovak and English: every email and every API error code has both texts; the participant's `language` (`sk`|`en`) chooses the email language; organizer language is set at sign-up.
- Organizer account: name, email, language; email must be confirmed before the course can be uploaded and before participants can be invited; sign-in by emailed one-time link (no passwords); session cookie `HttpOnly`, `Secure`, `SameSite=Lax`. A draft event may be created before the email is confirmed.
- Event required fields: name, location, date and start time, sport (`running`|`cycling`); course, laps, checkpoints, categories and waves come after email confirmation. Event states `Draft → Ready → Live → Finished`.
- Participants are registered by the organizer: **manual entry is primary, CSV import is an optional last task**. Fields: name, email, gender, language, optional category and wave; bib auto-assigned unless provided; duplicate email inside an event is flagged and rejected. Statuses: `Invited`, `Confirmed`, `Declined`, `EmailFailed`.
- Tokens: invitation/confirmation link token valid 14 days; sign-in link valid 15 minutes and single-use; both random 32 bytes, stored only as SHA-256 hash, single-purpose, a newly issued token of the same purpose invalidates the old one. The participant's device token (existing `TokenService`) is issued on confirmation, re-issuable by the organizer (old token revoked at once), and persisted as a hash.
- The participant's email is visible to the organizer only; never in any spectator type, never in logs. Location consent is a separate recorded step from confirming participation; nickname can change any time; gender until the event starts, then it is locked.
- Privacy: retention of stored position fixes 30 days after the event ends (configurable `Retention:FixDays`), then deleted; participant data deletion on request by the organizer; minors (`IsMinor`) never show a name to spectators.
- Rate limits on every unauthenticated endpoint that sends email or reveals existence (sign-up, sign-in request, resend): no account/participant enumeration (always the same answer), per-IP and per-email limits.
- Plan 1 rulings that bind this plan: alarm acknowledgement keyed on `(participantId, kind)`; any change of participant info bumps `EventRuntime.ParticipantsVersion`; `SpectatorDelay` validated at event creation (5 s to 300 s); `HasParticipant` assumes participants are never removed from a live runtime — removal of a participant from a running event is done by marking `Dnf`/withdrawn, not by deleting the runtime entry; the client must drop a batch on HTTP 400; configuration through options.

## Review Focus

1. Sign-up, sign-in and resend endpoints must not reveal whether an email exists and must not be usable to mail-bomb a third party (rate limits, one pending token, constant response). [Tasks 4, 7]
2. Expired, used, wrong-purpose and superseded links must fail with a clear message in both languages; a confirmed participant re-opening the invitation link sees their current state, not an error. [Tasks 4, 7]
3. Re-issuing a device token must revoke the old one immediately in the live runtime, also after a restart. [Task 7]
4. A restart must give identical live and spectator views for an event in progress (fixes replayed in order; `ParticipantsVersion` and nickname/consent changes included). [Task 2]
5. Names and emails with unicode, very long values, header-injection characters (`\r`, `\n`) in names and CSV cells, duplicate emails differing only by case, and CSV with BOM, quotes or semicolons must be handled. [Tasks 6, 8]

---

### Task 1: Core hardening carried over from Plan 1

**Files:**
- Modify: `src/PositionTracker.Core/ParticipantTracker.cs`, `src/PositionTracker.Core/Runtime/EventRuntime.cs`, `src/PositionTracker.Core/Runtime/EventSettings.cs`, `src/PositionTracker.Core/Runtime/SpectatorProjector.cs`, `src/PositionTracker.Api/Endpoints/FixRateLimiter.cs`, `src/PositionTracker.Api/Endpoints/IngestEndpoints.cs`
- Test: `tests/PositionTracker.Core.Tests/*`, `tests/PositionTracker.Api.Tests/IngestEndpointTests.cs`

**Interfaces:**
- Produces:
```csharp
// ParticipantSnapshot gains:  bool LapsInferred   // true when whole laps were chosen by expected advance after a signal gap
// EventRuntime gains:
public void UpdateParticipant(ParticipantInfo info);   // replaces info, bumps ParticipantsVersion; KeyNotFoundException if unknown
public static EventSettings Create(string eventId, string name, Sport sport, DateTimeOffset startTime,
  TimeSpan spectatorDelay, bool isSimulated);          // validates 5 s <= spectatorDelay <= 300 s (ArgumentOutOfRangeException)
```

- [ ] **Step 1: Write failing tests**
  - `Laps_inferred_flag_is_set_after_a_gap_that_chose_whole_laps_and_cleared_when_not_needed`; `Finished_from_an_inferred_lap_waits_for_a_confirming_fix_and_stays_not_finished_until_one_arrives` (2 km × 5 cyclist, 4-minute gap, stopped 180 s inside it: the view shows `LapsInferred = true` and the state is not `Finished` before a fix after the finish line).
  - `Sos_before_the_first_race_fix_does_not_expose_the_warm_up_position_to_spectators` (spectator JSON has null lat/lon).
  - `Fix_budget_overflow_returns_429_with_Retry_After_instead_of_silently_dropping_newest_fixes` (a 840-fix queue after a 70-minute outage is accepted in full across requests when the client honours `Retry-After`).
  - `Repeated_identical_SetPaused_calls_do_not_grow_the_pause_list`.
  - `UpdateParticipant_changes_the_spectator_display_name_and_bumps_ParticipantsVersion`; `EventSettings.Create` rejects 4 s and 301 s delays and accepts 5 s and 300 s.
- [ ] **Step 2:** Run `dotnet test`; expected: the new tests FAIL.
- [ ] **Step 3:** Implement the interfaces above. `LapsInferred` is set in the lap-choice branch of the tracker that selects whole laps from the expected advance; the state evaluation must not return `Finished` while the finish was reached through an inferred lap and no fix after the finish crossing exists (the organizer view still shows the inferred distance). Hide spectator lat/lon when the tracker has no race fix. The rate limiter answers 429 + `Retry-After` for the excess instead of 202.
- [ ] **Step 4:** Run `dotnet test`; expected: all PASS, 0 warnings.
- [ ] **Step 5:** Commit `fix: carry over Plan 1 hardening (inferred laps, spectator warm-up, fix budget)`.

---

### Task 2: Persistence foundation and position history

**Files:**
- Create: `src/PositionTracker.Data/PositionTracker.Data.csproj`, `src/PositionTracker.Data/AppDbContext.cs`, `src/PositionTracker.Data/Entities/*.cs`, `src/PositionTracker.Data/Migrations/*`, `docker-compose.yml` (PostgreSQL for development), `src/PositionTracker.Api/Persistence/RuntimeRehydrator.cs`, `src/PositionTracker.Api/Persistence/FixStore.cs`
- Test: `tests/PositionTracker.Data.Tests/*` (Testcontainers PostgreSQL), `tests/PositionTracker.Api.Tests/RehydrationTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class AppDbContext : DbContext {
  DbSet<OrganizerEntity> Organizers; DbSet<EventEntity> Events; DbSet<CourseEntity> Courses;
  DbSet<CategoryEntity> Categories; DbSet<WaveEntity> Waves; DbSet<ParticipantEntity> Participants;
  DbSet<LinkTokenEntity> LinkTokens; DbSet<DeviceTokenEntity> DeviceTokens; DbSet<FixEntity> Fixes;
  DbSet<EmailOutboxEntity> Outbox; DbSet<AlarmAckEntity> AlarmAcks; DbSet<ConsentEntity> Consents;
}
public interface IFixStore { Task AppendAsync(string eventId, string participantId, IReadOnlyList<PositionFix> accepted, DeviceStatus? status, CancellationToken ct);
  IAsyncEnumerable<StoredFix> ReadAsync(string eventId, CancellationToken ct);   // ordered by device time, then insertion
  Task<int> DeleteOlderThanAsync(DateTimeOffset eventEndedBefore, CancellationToken ct); }
public sealed class RuntimeRehydrator { Task<int> RebuildAsync(CancellationToken ct); }  // returns number of runtimes rebuilt
```
Entity fields follow the spec (event: name, location, start, sport, laps, state, spectator delay, GPX bytes, checkpoints JSON; participant: bib, name, nickname, email, gender, language, category, wave, status, isMinor, showNameToSpectators; fix: event, participant, time, lat, lon, accuracy).

- [ ] **Step 1: Write failing tests**
  - `Migrations_apply_to_an_empty_PostgreSQL_container`.
  - `FixStore_appends_and_reads_in_device_time_order_and_deletes_old_events_only`.
  - **Review Focus 4:** `Restart_gives_identical_live_and_public_views` — drive an event in progress through ingest (including a late batch and a participant nickname change), snapshot `/live` and `/public`, build a fresh host over the same database, rehydrate, and assert equal JSON (same `Participants`, ranks, states, segment colours, `ParticipantsVersion`-driven spectator names).
  - `Fixes_are_written_in_batches_off_the_request_path_and_flushed_on_shutdown`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~Data|FullyQualifiedName~Rehydration"`; expected: FAIL (types missing).
- [ ] **Step 3:** Implement the data project and the two classes. Fix writes go through a bounded channel and a background writer (batching every 500 ms or 200 fixes), so ingest latency does not depend on the database; accepted fixes only. Rehydration runs at startup for events in `Live` state: create the `EventRuntime`, add confirmed participants, `Ingest` stored fixes per participant in time order using the runtime clock set by the stored fix times (no wall-clock dependency in replay), then restore pause/SOS/DNF actions from an `ActionLog` table (add it: participant, action, time) so state survives. Connection string via options `ConnectionStrings:Default`.
- [ ] **Step 4:** Run the same filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: PostgreSQL persistence, fix store and runtime rehydration`.

---

### Task 3: Email infrastructure and deliverability check

**Files:**
- Create: `src/PositionTracker.Api/Email/IEmailSender.cs`, `SmtpEmailSender.cs`, `EmailOutboxWorker.cs`, `EmailTemplates.cs` (SK + EN), `docs/ops/email-deliverability.md`
- Test: `tests/PositionTracker.Api.Tests/EmailTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string HtmlBody, string TextBody, string Language);
public interface IEmailSender { Task SendAsync(EmailMessage message, CancellationToken ct); }  // throws EmailDeliveryException(permanent: bool)
public sealed class EmailOutboxWorker : BackgroundService { /* sends queued rows with retry + backoff, marks Sent/Failed */ }
public static class EmailTemplates {
  public static EmailMessage Invitation(ParticipantEntity p, EventEntity e, string confirmUrl, string language);
  public static EmailMessage SignIn(OrganizerEntity o, string signInUrl);
  public static EmailMessage ConfirmOrganizerEmail(OrganizerEntity o, string confirmUrl);
}
```
Options `Email:Smtp:{Host,Port,UseStartTls,User,Password}`, `Email:From` (`pozvanky@lab463.com`), `Email:PublicBaseUrl`.

- [ ] **Step 1: Write failing tests:** templates render in both languages, contain the link and event data, never contain raw CR/LF in headers/subject (name `Jozef\r\nBcc: x@y.z` is sanitised) (Review Focus 5); the worker retries a transient failure 3 times with growing delay (FakeTimeProvider) and marks a permanent failure `Failed` which sets the participant status to `EmailFailed`; the outbox row is removed or kept per retention option; a fake `IEmailSender` records sends; the SMTP sender is NOT hit in unit tests (use a loopback test SMTP server such as `smtp4dev`-style in-process `SmtpServer` only if a small package exists, otherwise test the message mapping and leave real SMTP to the manual check).
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~EmailTests`; expected: FAIL.
- [ ] **Step 3:** Implement with MailKit. Write `docs/ops/email-deliverability.md`: the checklist (SPF, DKIM, DMARC records for `lab463.com`, matching rDNS, outbound port 25 test) and the manual verification procedure — a small `tools/send-test-mail` script or console command that sends a test message to a list of addresses given on the command line.
- [ ] **Step 4:** Run the filter; expected: PASS.
- [ ] **Step 5 (manual gate, human):** run the send-test command from the home server to a Gmail and an Outlook address; the controller records the result in `docs/ops/email-deliverability.md`. If the mail lands in spam or port 25 is blocked, register that and keep going with a fake sender in development; real sending is switched on only after this gate passes.
- [ ] **Step 6:** Commit `feat: email outbox, SMTP sender, SK/EN templates`.

---

### Task 4: Organizer accounts and sign-in

**Files:**
- Create: `src/PositionTracker.Api/Accounts/AccountEndpoints.cs`, `OrganizerService.cs`, `LinkTokenService.cs`, `CookieAuth.cs`
- Test: `tests/PositionTracker.Api.Tests/AccountTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum LinkPurpose { OrganizerEmailConfirm, OrganizerSignIn, ParticipantInvite }
public sealed class LinkTokenService {
  public Task<string> IssueAsync(LinkPurpose purpose, Guid subjectId, TimeSpan lifetime, CancellationToken ct);  // returns raw token; supersedes older tokens of the same purpose+subject
  public Task<LinkResult> RedeemAsync(LinkPurpose purpose, string rawToken, bool singleUse, CancellationToken ct); // Ok(subjectId) | Expired | Used | Unknown
}
// HTTP (JSON, camelCase; every error body {"code": "...", "message": {"sk": "...", "en": "..."}})
// POST /api/account/signup        {name,email,language}        -> 202 (always)
// POST /api/account/confirm  {token}                              -> 200 {confirmed:true} or 400 code   (POST per ruling P9)
// POST /api/account/signin        {email}                         -> 202 (always)
// POST /api/account/signin/redeem {token}                        -> sets the session cookie, 200       (POST per ruling P9; all unsafe /api/account/* need Content-Type application/json and X-Requested-With: PositionTracker, ruling P10)
// POST /api/account/signout                                       -> 204
// GET  /api/account/me                                            -> 200 {name,email,language,emailConfirmed}
```

- [ ] **Step 1: Write failing tests:** sign-up creates an unconfirmed organizer and enqueues exactly one confirmation email; signing up again with the same email answers the same 202 and does not leak existence (**Review Focus 1**) — a confirmed account gets a sign-in email instead, an unconfirmed one a fresh confirmation; per-email limit (3 emails/hour) and per-IP limit (20/hour) return 202 without sending; expired (>14 days / >15 minutes), used and wrong-purpose tokens → 400 with the codes `link_expired|link_used|link_invalid` and both texts (**Review Focus 2**); sign-in link is single-use; session cookie flags `HttpOnly; Secure; SameSite=Lax`, 30-day sliding expiry; `/me` without cookie 401; sign-out invalidates; emails differing only by case or surrounding whitespace map to one account; unicode names stored intact; names > 200 chars rejected 400.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~AccountTests`; expected: FAIL.
- [ ] **Step 3:** Implement with ASP.NET Core cookie authentication and an authorization policy `OrganizerSession` (+ `EmailConfirmedOrganizer`). Rate limiting via `AddRateLimiter` partitions (IP and a salted hash of the email). Tokens hashed with SHA-256.
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: organizer sign-up, email confirmation and passwordless sign-in`.

---

### Task 5: Events and courses

**Files:**
- Create: `src/PositionTracker.Api/Events/EventManagementEndpoints.cs`, `EventService.cs`
- Test: `tests/PositionTracker.Api.Tests/EventManagementTests.cs`

**Interfaces:**
- Produces (all require the `OrganizerSession` policy; the event belongs to its organizer, other organizers get 404):
```csharp
// POST /api/events            {name,location,startTime,sport}        -> 201 {eventId}; allowed before email confirmation (Draft)
// GET  /api/events            -> the organizer's events;  GET /api/events/{id}/manage -> details
// PUT  /api/events/{id}/course  {gpxBase64, laps, checkpoints:[{name,lapDistanceM}], spectatorDelaySeconds}  // EmailConfirmedOrganizer
// PUT  /api/events/{id}/categories  [{name, gender?, minAge?, maxAge?}]    PUT /api/events/{id}/waves [{name,startTime}]
// POST /api/events/{id}/state   {state: "ready"|"live"|"finished"}      // legal transitions only
```
`live` creates/registers the `EventRuntime` for the event from the stored course and all `Confirmed` participants and starts persisting fixes (Task 2).

- [ ] **Step 1: Write failing tests:** creation validates required fields (name ≤ 120, location ≤ 120, start time in the future for new events); unconfirmed organizer gets 403 `email_not_confirmed` on course upload; invalid GPX → 400 `gpx_invalid`; a course shorter than 50 m, `laps > 1` on an open course, checkpoints outside (0, lap length), duplicate checkpoints and `spectatorDelaySeconds` outside 5–300 → 400 with distinct codes (uses the Plan 1 `Course.Create` rules; surface its errors); state machine: `draft → ready → live → finished` only, `live` requires a course and at least one confirmed participant (409 otherwise), a `live` event cannot get a new course (409); after `live` the runtime is in `EventRegistry` with the confirmed participants and `/api/events/{id}/public` works; another organizer cannot read or modify the event (404); simulated events are never listed.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~EventManagementTests`; expected: FAIL.
- [ ] **Step 3:** Implement endpoints and `EventService`; GPX parsed with the hardened `GpxParser`; store GPX bytes and parsed course parameters; `EventSettings.Create` for the runtime.
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: event management with course upload and state machine`.

---

### Task 6: Participant registration by the organizer

**Files:**
- Create: `src/PositionTracker.Api/Registration/RegistrationEndpoints.cs`, `RegistrationService.cs`
- Test: `tests/PositionTracker.Api.Tests/RegistrationTests.cs`

**Interfaces:**
- Produces (organizer session + event ownership; email confirmed required):
```csharp
// POST   /api/events/{id}/participants      {name,email,gender,language,category?,wave?,bib?}  -> 201 {participantId,bib,status:"Invited"}
// GET    /api/events/{id}/participants?status=&category=&q=  -> list incl. counts {all,confirmed,invited,declined,emailFailed}
// PUT    /api/events/{id}/participants/{pid}   (name,email,gender,language,category,wave,bib)
// DELETE /api/events/{id}/participants/{pid}   (allowed only while the event is not Live; Live → 409 use withdraw)
// POST   /api/events/{id}/participants/{pid}/resend      -> 202   POST .../reissue-token -> 200 {deviceTokenIssued:true}
// POST   /api/events/{id}/participants/remind            -> 202 {sent:n}   (only Invited, only after the deadline option)
```
Creating a participant enqueues the invitation email (Task 3) in the participant's language.

- [ ] **Step 1: Write failing tests:** creation sets `Invited`, auto-assigns the next free bib (explicit bib duplicates → 409 `bib_taken`), invitation enqueued with a valid link; duplicate email in the same event, also differing by case/whitespace → 409 `email_duplicate`; invalid email, empty/oversized name (> 200), unknown category/wave → 400; **Review Focus 5:** names with CR/LF, unicode, emojis and 200-char names are stored and never reach headers unsanitised; list filters and counts; edit of email re-sends the invitation and supersedes the old link; email delivery failure (`EmailFailed`) shows in the list and `resend` after fixing the address works; `reissue-token` revokes the old device token at once (**Review Focus 3**) and returns nothing secret to the organizer (the participant receives the new link by email); delete rules; ownership (404 for other organizers); the event `Live` + new participant → added to the runtime (`AddParticipant`) only after the participant confirms (Task 7).
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~RegistrationTests`; expected: FAIL.
- [ ] **Step 3:** Implement; normalise emails (trim, lowercase for comparison, keep the original for display); auto bib = max + 1 inside a transaction (retry on conflict).
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: participant registration, invitation and status list`.

---

### Task 7: Participant confirmation, nickname, gender, consent and device token

**Files:**
- Create: `src/PositionTracker.Api/Participants/ParticipantLinkEndpoints.cs`, `ParticipantService.cs`
- Modify: `src/PositionTracker.Api/Auth/TokenService.cs` (database-backed)
- Test: `tests/PositionTracker.Api.Tests/ParticipantLinkTests.cs`

**Interfaces:**
- Produces (authenticated only by the invitation link token):
```csharp
// GET  /api/p/{linkToken}                       -> 200 {event:{name,location,startTime,sport}, me:{name,bib,category,gender,nickname,status,locked:{gender:bool}}, language}
// POST /api/p/{linkToken}/confirm   {nickname?, gender?}      -> 200 {status:"Confirmed"}   // idempotent
// POST /api/p/{linkToken}/decline                              -> 200 {status:"Declined"}
// PUT  /api/p/{linkToken}/profile   {nickname?, gender?}       -> 200   // gender 409 once the event started
// POST /api/p/{linkToken}/consent   {locationConsent:true, showNameToSpectators:bool} -> 200 {deviceToken}  // token returned once
```
`TokenService` persists hashed device tokens (`DeviceTokenEntity`), `Issue/Resolve/Revoke` keep their Plan 1 semantics and now survive restarts; resolving still hashes and compares in constant time.

- [ ] **Step 1: Write failing tests:** `GET` shows the current state for confirmed and declined participants instead of an error (**Review Focus 2**); confirm is idempotent; decline after confirm allowed until the event starts; nickname 1–30 chars, no control characters, profanity left to the organizer (organizer can edit via Task 6 PUT: add `nickname` there and a `hideNickname` flag); gender change after `StartTime` → 409 `gender_locked`; nickname change on a live event updates the runtime (`UpdateParticipant`) and the spectator view changes at the next tick; the device token is issued only after `consent` with `locationConsent:true`, is returned exactly once, a second `consent` call re-issues (old token revoked) only if the link is still valid; consent is stored with timestamp and text version; a revoked/old device token is rejected at ingest with 401 even after a restart (**Review Focus 3**); expired link 400 `link_expired`; the link token never appears in logs (assert with a log capture); minors: when the organizer flagged `IsMinor`, `showNameToSpectators` cannot be set true.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~ParticipantLinkTests`; expected: FAIL.
- [ ] **Step 3:** Implement; `UpdateParticipant` and `AddParticipant` calls keep `EventRuntime` consistent when the event is `Live`; rate limit per link token (60/min).
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: participant confirmation, profile, consent and device tokens`.

---

### Task 8: CSV import (optional)

**Files:**
- Create: `src/PositionTracker.Api/Registration/CsvImport.cs`
- Test: `tests/PositionTracker.Api.Tests/CsvImportTests.cs`

**Interfaces:**
- Produces: `POST /api/events/{id}/participants/import` (multipart, ≤ 1 MB) with `?dryRun=true|false`; columns `name,email,gender,language[,category,wave,bib]`, delimiter `,` or `;` auto-detected, UTF-8 with or without BOM; dry run returns `{rows:n, valid:n, errors:[{row,code,message{sk,en}}], duplicates:[…]}` and writes nothing; the real run is all-or-nothing.

- [ ] **Step 1: Write failing tests:** BOM, `;` delimiter, quoted cells with commas and newlines, header-injection characters, more than 500 rows → 413, invalid emails and duplicates (in file, and against existing participants, case-insensitive) reported with row numbers, dry run writes nothing, a real run with one error writes nothing, success enqueues one invitation per row.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~CsvImportTests`; expected: FAIL.
- [ ] **Step 3:** Implement a small RFC 4180 parser (no package) and reuse `RegistrationService` rules.
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: optional CSV import with dry run`.

---

### Task 9: Organizer operations, alarm acknowledgement and retention

**Files:**
- Create: `src/PositionTracker.Api/Operations/OperationsEndpoints.cs`, `RetentionWorker.cs`
- Modify: `src/PositionTracker.Api/Endpoints/LiveEndpoints.cs` (organizer session as an alternative to `X-Api-Key`), `src/PositionTracker.Api/Live/LiveHub.cs` (`JoinOrganizer` with session)
- Test: `tests/PositionTracker.Api.Tests/OperationsTests.cs`

**Interfaces:**
- Produces:
```csharp
// POST /api/events/{id}/alarms/ack   {participantId, kind}     -> 204   (stored; keyed on (participantId, kind); an ack stays until the alarm clears and later reappears)
// POST /api/events/{id}/participants/{pid}/dnf   -> 204        (Dnf via runtime.Act, persisted in the ActionLog)
// DELETE /api/events/{id}/participants/{pid}/data  -> 204      (privacy deletion on request: fixes, email, name anonymised; event results keep bib only)
public sealed class RetentionWorker : BackgroundService { /* daily: DeleteOlderThanAsync(now - Retention:FixDays) */ }
```
`LiveView.Participants[i].Alarms` gains `Acknowledged` (bool) filled from the store.

- [ ] **Step 1: Write failing tests:** ack hides an alarm from the "open" list but keeps it visible as acknowledged; the same ack key survives `StoppedSince` sliding; ack cleared when the alarm clears; organizer session authorises `/live` and `JoinOrganizer` for its own event only (another organizer's event: 404/`HubException`); `X-Api-Key` still works for operators; manual DNF; privacy deletion removes fixes and personal fields, keeps the bib, and the spectator view then shows `#bib`; retention worker deletes fixes of events that ended more than 30 days ago and leaves newer ones (FakeTimeProvider).
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~OperationsTests`; expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Rerun the filter; expected: PASS.
- [ ] **Step 5:** Commit `feat: alarm acknowledgement, manual DNF, privacy deletion and retention`.

---

### Task 10: End-to-end and operations documentation

**Files:**
- Create: `tests/PositionTracker.Api.Tests/EndToEndRegistrationTests.cs`; Modify: `README.md`, `docs/ops/email-deliverability.md`

- [ ] **Step 1: Write the failing end-to-end test:** organizer signs up → confirms (token from the fake outbox) → signs in → creates an event → uploads a GPX loop with 3 laps → registers two participants (one SK, one EN) → both confirm, one sets a nickname and grants consent → the event goes `live` → both send fixes with their device tokens → `/live` shows both, `/public` shows the nickname for the one who consented and `#bib` for the other → a restart (new host, same database) shows identical views → the organizer re-issues one device token and the old token gets 401.
- [ ] **Step 2:** Run it; expected: FAIL until all earlier tasks are in place; fix integration gaps only.
- [ ] **Step 3:** Update `README.md` (configuration keys `ConnectionStrings:Default`, `Email:*`, `Retention:FixDays`, how to run PostgreSQL with `docker compose up -d`, new endpoints, sign-in flow, rulings) and finish `docs/ops/email-deliverability.md`.
- [ ] **Step 4:** Run `dotnet test`; expected: all PASS, 0 warnings.
- [ ] **Step 5:** Commit `test: end-to-end registration flow; docs: operations guide`.

---

## Self-Review

**Spec coverage.** 6.0 organizer account, confirmation, event creation, course after confirmation, manual registration with optional CSV, statuses, invitation email SK/EN, confirmation link, nickname/gender with lock, separate location consent: Tasks 3 to 8. Retention, deletion on request, minors, email only for the organizer: Tasks 7 and 9. Own SMTP and the deliverability prerequisite: Task 3 (with a manual gate). Plan 1 deferrals: LapsInferred, SOS warm-up, fix budget, no-op pauses, `UpdateParticipant`/`ParticipantsVersion`, spectator delay validation, alarm ack key, `HasParticipant` rule: Tasks 1, 5, 7, 9. Persistence and restart: Task 2. Not in this plan by design: web UI and i18n of screens (Plans 3 to 5), public self-registration (later version), HTTPS/reverse proxy, forwarded headers, hub limits, payload reduction (Plan 5).

**Types.** `LinkTokenService` and `LinkPurpose` are defined in Task 4 and used by Tasks 6 and 7; `EmailMessage`/`IEmailSender`/templates in Task 3 are consumed by Tasks 4, 6, 7; `IFixStore` and `RuntimeRehydrator` (Task 2) are used by Task 5's `live` transition; `UpdateParticipant` and `EventSettings.Create` (Task 1) are used by Tasks 5 and 7; `TokenService` keeps its Plan 1 method names.

**Open choices for the executor (not decided here):** EF Core entity configuration details and index choices, the exact bounded-channel sizes of the fix writer, the cookie authentication scheme names, the CSV parser structure, template wording within the stated placeholders.
