# Stop Tracking After the Finish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A phone that keeps sending OwnTracks positions after the event is finished is switched off by the server (layer 1), and if it still sends, the participant is told by email and on the participant page how to switch it off (layer 2).

**Architecture:** Layer 1: the OwnTracks configuration enables remote commands (`cmd`, `remoteConfiguration`); for a finished event the ingest answers every authenticated request with one `setConfiguration` command that sets `monitoring: -1` (quiet). Layer 2: the first request after the finish is recorded on the participant; a request that still arrives at least 10 minutes later queues one "tracking still on" email (the command evidently did not work). The email links to a public page `/stop-tracking` with a stop deep link (QR + button + manual steps); the participant page shows the same panel once the event is finished.

**Tech Stack:** .NET 9 minimal API, EF Core + PostgreSQL (migrations in `src/PositionTracker.Data`), xUnit + Testcontainers, React + Vite + TS (vitest), `qrcode`.

**Spec:** this plan (decision of 2026-11-02 in the session: layers 1 and 2 of the analysis "tracking after the finish"); background `docs/ops/owntracks-setup.md`, `docs/superpowers/specs/2026-10-30-possition-tracker-design.md`.

## Global Constraints

- Code, comments, docs and commit messages in English; UI and email copy in Slovak and English (`t('sk', 'en')` on the web, `sk ? … : …` in emails).
- Every new or changed page works in light and dark mode (existing theme tokens only, no hard-coded colours).
- Positions after the finish are never applied or stored (existing behaviour stays: `registry.IsFinished` / not live → nothing reaches the runtime or `Fixes`).
- An event that is **not live yet** (Draft/Ready) must never receive the stop command: only `EventState.Finished` does.
- No credential or token is logged; the stop deep link contains no secret.
- Stop configuration JSON, exactly: `{"_type":"configuration","monitoring":-1}`; deep link, exactly:
  `owntracks:///config?inline=eyJfdHlwZSI6ImNvbmZpZ3VyYXRpb24iLCJtb25pdG9yaW5nIjotMX0%3D`
- Stop command, exactly (the whole HTTP 200 body): `[{"_type":"cmd","action":"setConfiguration","configuration":{"_type":"configuration","monitoring":-1}}]`
- Notice grace period: 10 minutes after the first post-finish request. At most one notice email per participant and event.
- The bearer endpoint `/api/events/{id}/positions` keeps answering `409 event_finished` (no change).
- One bundled commit per task on `main` (project rule: all work in `main`).

## Review Focus

1. **Event Ready/Draft, runtime absent** — the phone was set up before the start and sends a test position: must get `[]`, never the stop command (else tracking is off at the start). Test in Task 2.
2. **Finished event whose runtime was evicted** (`registry.Get` is null days after the end) — must still get the stop command, decided from the database event state. Test in Task 2.
3. **OwnTracks offline backlog after the finish** — a burst of queued messages arrives within seconds: must not send the email (grace period not over), only one `seenAt` write. Test in Task 3.
4. **Database failure in the notice path** — the stop command must still be returned (the notice is best effort); the request must not become 503. Test in Task 3.
5. **Participant page after the finish** — the OwnTracks setup step (which now returns `event_finished`) must not render an error; the stop panel shows instead. Test in Task 4.

---

### Task 1: Configuration enables remote commands; stop command and stop link

**Files:**
- Modify: `src/PositionTracker.Api/OwnTracks/OwnTracksConfigBuilder.cs`
- Test: `tests/PositionTracker.Api.Tests/OwnTracksTests.cs`

**Interfaces:**
- Produces (in `OwnTracksConfigBuilder`):
  - `public const string StopDeepLink` = the exact deep link from Global Constraints.
  - `public static JsonObject StopConfiguration()` → `{"_type":"configuration","monitoring":-1}` (new instance per call).
  - `public static JsonArray StopCommand()` → the exact array from Global Constraints.
  - `Build(...)` now writes `"cmd": true` and, right after it, `"remoteConfiguration": true`.

- [ ] **Step 1: Update the failing config test.** In `The_config_is_valid_http_mode_json_that_round_trips_and_holds_no_secret_but_the_token` assert `json.GetProperty("cmd").GetBoolean()` is true, `json.GetProperty("remoteConfiguration").GetBoolean()` is true, and the key order ends with `..., "ignoreInaccurateLocations", "cmd", "remoteConfiguration"`.
- [ ] **Step 2: Add `The_stop_link_and_command_set_quiet_monitoring_only`:**

```csharp
const string prefix = "owntracks:///config?inline=";
Assert.Equal("owntracks:///config?inline=eyJfdHlwZSI6ImNvbmZpZ3VyYXRpb24iLCJtb25pdG9yaW5nIjotMX0%3D", OwnTracksConfigBuilder.StopDeepLink);
var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(OwnTracksConfigBuilder.StopDeepLink[prefix.Length..])));
Assert.Equal(OwnTracksConfigBuilder.StopConfiguration().ToJsonString(), decoded);
Assert.Equal("""[{"_type":"cmd","action":"setConfiguration","configuration":{"_type":"configuration","monitoring":-1}}]""",
    OwnTracksConfigBuilder.StopCommand().ToJsonString());
```

- [ ] **Step 3: Run** `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~OwnTracksTests"` → the two tests FAIL (missing members / `cmd` false).
- [ ] **Step 4: Implement** the members above. `StopDeepLink` stays a literal constant; the test proves it matches `StopConfiguration()`. Update the class XML doc: the configuration allows the server to send `setConfiguration` (used only to switch the phone to quiet after the finish).
- [ ] **Step 5: Run** the same filter → PASS.
- [ ] **Step 6: Commit** `feat(owntracks): enable remote commands in the config; stop command and stop link`.

### Task 2: Ingest answers a finished event with the stop command

**Files:**
- Modify: `src/PositionTracker.Api/OwnTracks/OwnTracksEndpoints.cs`
- Modify: `docs/ops/owntracks-setup.md`
- Test: `tests/PositionTracker.Api.Tests/OwnTracksTests.cs`

**Interfaces:**
- Consumes: `OwnTracksConfigBuilder.StopCommand()` (Task 1).
- Produces: `private static async Task<bool> IsFinishedAsync(HttpContext context, EventRegistry registry, string eventId)` in `OwnTracksEndpoints` — true when `registry.IsFinished(eventId)`; otherwise, only when `registry.Get(eventId)` is null and persistence is enabled, one `AsNoTracking` read of `Events.State` for `RuntimeFactory.RuntimeId`'s Guid (`Guid.Parse(eventId)`) == `EventState.Finished`. Task 3 hooks its notice into the branch where this is true.

- [ ] **Step 1: Write failing tests** (helpers `LiveRiderAsync`, `Location`, `PostAsync` exist in the file):
  - `A_finished_event_answers_every_message_with_the_stop_command`: `LiveRiderAsync`; set the event row `State = Finished` in the DB and `host.Registry.SetFinished(ev.ToString("D"), true)`; post `Location(host)` and then `{"_type":"lwt","tst":1790000000}` → both `200` with body exactly the stop command string; `db.Fixes` empty.
  - `A_finished_event_without_a_runtime_still_answers_the_stop_command`: `SeedEventAsync(host, EventState.Finished)`, confirm + consent as in `A_finished_or_not_live_event_answers_200_and_ignores_the_message`; post → body is the stop command.
  - Rename the existing `A_finished_or_not_live_event_answers_200_and_ignores_the_message` to `A_not_live_event_answers_200_with_an_empty_array` (it seeds `Ready`; keeps asserting `[]`).
  - `A_live_event_still_answers_an_empty_array`: `LiveRiderAsync`, post → `[]`.
- [ ] **Step 2: Run** the `OwnTracksTests` filter → the two finished tests FAIL (body `[]`).
- [ ] **Step 3: Implement.** After authentication succeeds and before the body is parsed (the size check `ContentLength > MaxBodyBytes` stays first), `if (await IsFinishedAsync(...)) return Results.Json(OwnTracksConfigBuilder.StopCommand());`. A database failure in `IsFinishedAsync` falls into the existing `IsDatabaseFailure` → 503 handling (wrap the call the same way as `ResolveAsync`). Update the class summary: finished event → `200` with the stop command; not live → `200 []`.
- [ ] **Step 4: Run** the filter → PASS; then `dotnet test` (whole solution) → PASS.
- [ ] **Step 5: Docs** `docs/ops/owntracks-setup.md`: add `remoteConfiguration` to the "Config keys" row; change the "Response" row to "we send `[]`, except for a finished event: one `cmd`/`setConfiguration` with `monitoring: -1`"; in *Server contract* replace "(or finished) → `200 []`" accordingly; add to **UNVERIFIED**: "Whether both apps apply a `cmd` from the HTTP response (needs `cmd: true`, on Android also `remoteConfiguration: true`), and whether `monitoring: -1` stops sending at once (also with a locked screen). Field test: finish a test event while the phone sends; expect no further request after the first answer." Add to the SK/EN participant steps: "Po skončení preteku server prepne OwnTracks do tichého režimu (Quiet)." / "After the event the server switches OwnTracks to quiet mode."
- [ ] **Step 6: Commit** `feat(owntracks): switch the phone to quiet mode when the event is finished`.

### Task 3: "Tracking still on" email after the grace period

**Files:**
- Modify: `src/PositionTracker.Data/Entities/ParticipantEntities.cs` (two columns)
- Create: migration `TrackingAfterFinish` via `dotnet ef migrations add TrackingAfterFinish -p src/PositionTracker.Data -s src/PositionTracker.Api`
- Modify: `src/PositionTracker.Email/EmailMessage.cs`, `src/PositionTracker.Email/EmailTemplates.cs`
- Create: `src/PositionTracker.Api/OwnTracks/TrackingStopNotifier.cs`
- Modify: `src/PositionTracker.Api/OwnTracks/OwnTracksEndpoints.cs`, DI registration next to the OwnTracks endpoints in `Program.cs`
- Test: `tests/PositionTracker.Api.Tests/OwnTracksTests.cs`, `tests/PositionTracker.Api.Tests/EmailTests.cs`, `tests/PositionTracker.Data.Tests/MigrationTests.cs` (if it lists columns)

**Interfaces:**
- Consumes: the finished branch of Task 2.
- Produces:
  - `ParticipantEntity.TrackingAfterFinishAt : DateTimeOffset?` (first request after the finish), `ParticipantEntity.StopNoticeQueuedAt : DateTimeOffset?`.
  - `EmailPurpose.TrackingStop = "trackingStop"`.
  - `EmailTemplates.TrackingStillOn(ParticipantEntity p, EventEntity e, OrganizerEntity organizer, string stopUrl, string language) : EmailMessage` (reply-to organizer like `Invitation`).
  - `public const string StopTrackingPath = "/stop-tracking";` and `public static readonly TimeSpan NoticeGrace = TimeSpan.FromMinutes(10);` in `TrackingStopNotifier`.
  - `TrackingStopNotifier` (scoped): `Task NoteAsync(string eventId, string participantId, CancellationToken ct)`; plus singleton `TrackingStopMemo` (in the same file) holding per `(eventId, participantId)` the known `seenAt` and a "done" flag, so repeated requests skip the database.

Email copy (exact):
- Subject SK `Vypnite sledovanie polohy – {eventName}`, EN `Please switch off location tracking – {eventName}`.
- Intro SK `Podujatie {eventName} sa skončilo, ale váš telefón stále posiela polohu. Polohu už neukladáme ani nezobrazujeme.` EN `The event {eventName} has finished, but your phone is still sending its location. We no longer store or show it.`
- BeforeButton SK `Sledovanie vypnete jedným klepnutím na telefóne, kde máte OwnTracks:` EN `Switch tracking off with one tap on the phone with OwnTracks:`
- Button SK `Vypnúť sledovanie` EN `Switch off tracking`; Url = `stopUrl`.
- After SK `Alebo v aplikácii OwnTracks prepnite režim na Quiet (Tichý), prípadne aplikáciu odinštalujte.` EN `Or switch OwnTracks to Quiet mode, or uninstall the app.`
- Footer: same as `Invitation`.

- [ ] **Step 1: Write failing tests** in `OwnTracksTests` (finished live rider as in Task 2; clock via `host.Clock`):
  - `A_phone_that_stops_within_the_grace_period_gets_no_email`: post at T; post at T+9 min → `TrackingAfterFinishAt == T`, `StopNoticeQueuedAt` null, no `Outbox` row with `Purpose == "trackingStop"`.
  - `A_phone_still_sending_after_the_grace_period_gets_one_email`: posts at T, T+10 min, T+11 min, T+60 min → exactly one outbox row, `Purpose == "trackingStop"`, to the participant address, `TextBody` contains `{PublicBaseUrl}/stop-tracking?lang=sk`; `StopNoticeQueuedAt == T+10 min`.
  - `A_backlog_burst_writes_seen_once`: 20 posts at T → one `TrackingAfterFinishAt`, no email.
  - `The_stop_command_is_returned_even_when_the_notice_fails`: register a notifier replacement (or a hook) that throws a database exception → response still `200` + stop command.
  - In `EmailTests`: `The_tracking_still_on_email_is_bilingual_and_links_the_stop_page` (SK and EN subject strings from above; HTML and text contain the url).
- [ ] **Step 2: Run** `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~OwnTracksTests|FullyQualifiedName~EmailTests"` → FAIL (members missing).
- [ ] **Step 3: Implement** entity columns + migration; `EmailPurpose.TrackingStop`; `EmailTemplates.TrackingStillOn` with the copy above. `TrackingStopNotifier.NoteAsync`:
  1. Persistence disabled → return.
  2. Memo says done, or memo `seenAt` is known and `now - seenAt < NoticeGrace` → return.
  3. `ExecuteUpdateAsync` set `TrackingAfterFinishAt = now` where id matches and it is null; read the stored value into the memo.
  4. If `now - seenAt >= NoticeGrace`: in one transaction `ExecuteUpdateAsync` set `StopNoticeQueuedAt = now` where it is null; when 1 row changed, load event + organizer, build the email (`stopUrl = PublicBaseUrl.TrimEnd('/') + StopTrackingPath + "?lang=" + (sk ? "sk" : "en")`, delivery address via `EmailAddress.ToDeliveryAddress`), add `EmailOutbox.Create(message, participantId, now)` (so deleting the participant's data deletes it), commit; mark memo done.
  In `OwnTracksEndpoints`, the finished branch calls `NoteAsync` inside `try/catch` (database failures and `OperationCanceledException` not from `RequestAborted` are logged as a warning without ids of the token, then ignored) and then returns the stop command.
- [ ] **Step 4: Run** the filter → PASS; `dotnet test` → PASS (incl. `MigrationTests`).
- [ ] **Step 5: Commit** `feat: email a participant whose phone still tracks 10 minutes after the finish`.

### Task 4: Stop panel on the participant page and the public `/stop-tracking` page

**Files:**
- Modify: `src/PositionTracker.Api/Participants/ParticipantService.cs` (`LinkEvent` + `GetAsync`)
- Create: `web/src/owntracksStop.ts`, `web/src/components/StopTrackingPanel.tsx`, `web/src/pages/StopTracking.tsx`
- Modify: `web/src/App.tsx` (route `stop-tracking`), `web/src/pages/Participant.tsx`
- Test: `tests/PositionTracker.Api.Tests/ParticipantLinkTests.cs`, `web/src/app.test.tsx`

**Interfaces:**
- Consumes: `OwnTracksConfigBuilder.StopDeepLink` value (Task 1), `TrackingStopNotifier.StopTrackingPath` (Task 3).
- Produces: `LinkEvent(string Name, string Location, string StartTime, string Sport, bool Finished)` (JSON `finished`); `export const STOP_DEEP_LINK` in `web/src/owntracksStop.ts` (same literal as Global Constraints); `StopTrackingPanel()` React component (no props); page `StopTracking` (reads `?lang=sk|en` for the initial language like the rest of the app does).

Panel copy (exact): heading SK `Vypnite sledovanie polohy` / EN `Switch off location tracking`; text SK `Podujatie sa skončilo. Polohu už neukladáme. Vypnite OwnTracks, aby telefón zbytočne nemíňal batériu a dáta.` / EN `The event has finished. We no longer store your location. Switch off OwnTracks so your phone does not waste battery and data.`; button SK `Vypnúť sledovanie v OwnTracks` / EN `Switch off tracking in OwnTracks` (`href={STOP_DEEP_LINK}`); QR of `STOP_DEEP_LINK` (same `QRCode.toDataURL` options as the setup QR) with caption SK `Na počítači? Naskenujte kód telefónom.` / EN `On a computer? Scan the code with your phone.`; manual steps SK `Alebo v OwnTracks prepnite režim na Quiet (Tichý), prípadne aplikáciu odinštalujte.` / EN `Or switch OwnTracks to Quiet mode, or uninstall the app.`

- [ ] **Step 1: Write failing tests.**
  - `ParticipantLinkTests`: `The_view_says_whether_the_event_is_finished` — `GET /api/p/{link}` for a Live event → `event.finished == false`; for a Finished event → `true`.
  - `app.test.tsx`: `STOP_DEEP_LINK` equals the literal; `/stop-tracking` renders the button with that `href`; the participant page with a mocked view `event.finished: true` renders the panel heading and does **not** request `/owntracks-config`.
- [ ] **Step 2: Run** `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~ParticipantLinkTests"` and `npm --prefix web test -- --run` → FAIL.
- [ ] **Step 3: Implement** `Finished = ev.State == EventState.Finished`; the panel and page; in `Participant.tsx`, when `event.finished`, render `StopTrackingPanel` in place of the consent and OwnTracks setup steps.
- [ ] **Step 4: Run** both test commands → PASS; `npm --prefix web run build` → no errors.
- [ ] **Step 5: Visual check** in the running app (skill `run`): `/stop-tracking` and the participant page of a finished event, light and dark mode, mobile width 390 px; button and QR readable in both modes.
- [ ] **Step 6: Commit** `feat(web): stop-tracking panel on the participant page and the public stop page`.

### Task 5: Field-test gate (manual, not code)

- [ ] Add to the pilot checklist in `docs/ops/deployment.md` an item "Tracking stop after the finish (Android + iOS)": set up a phone from the participant page, finish the test event, confirm the next request gets the stop command and the phone sends nothing more; open the stop link from the email on the phone and confirm OwnTracks shows Quiet. Record the result in `docs/ops/owntracks-setup.md` (move the item out of UNVERIFIED).
- [ ] Commit `docs: field-test item for the tracking stop` together with Task 4 if it is done in the same session.
