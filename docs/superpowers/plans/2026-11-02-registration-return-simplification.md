# Registration and Return Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every role can come back to an event days later with nothing but the link it already has: the participant's invitation link stays valid until a week after the start and can renew itself, a returning participant sees their state instead of the setup steps, and spectators get a link (and a meaningful page) before the start.

**Architecture:** Backend changes stay inside the existing services: invitation expiry is computed from the event start (`LinkTokenService`, `RegistrationService`, `EventService`), the participant link API gains a `renew` route and three view fields (`ParticipantService`, `ParticipantLinkEndpoints`), and the public API gains an anonymous `GET /api/events/{id}/info` read from the database (`LiveEndpoints`). The web app changes the participant page, the event page and the spectator page.

**Tech Stack:** .NET minimal API, EF Core + PostgreSQL, xUnit + Testcontainers, React + Vite + TS (vitest + Testing Library).

**Spec:** the registration analysis of 2026-11-02 (session), points 1–5, approved by the user ("rozpracuj body 1–5 do plánu"). Point 5 (organizer lands on "create event" after confirming the email) is **already implemented**: `ConfirmEmail` navigates to `/org` (`web/src/pages/Account.tsx:160`) and an empty `/org` shows the `CreateEvent` form (`web/src/pages/OrgEvents.tsx:55`), covered by the test "an empty event list asks for the first event". No task.

## Global Constraints

- Code, comments, docs and commit messages in English; UI and email copy in Slovak and English (`t('sk', 'en')`).
- Every new or changed page works in light and dark mode (existing theme tokens and classes only, no hard-coded colours).
- No link token, device token or email address is logged or returned where it was not returned before.
- `/api/p/...` answers keep `Cache-Control: no-store`, `Referrer-Policy: no-referrer` and the per-link limit (60/min).
- Invitation validity: `max(issuedAt + 14 days, event.StartTime + 7 days)`; constant names `LinkTokenService.InviteLifetime` (14 d, exists) and `LinkTokenService.InviteAfterStart` (7 d, new).
- A start change only ever **extends** invitation links, never shortens them.
- Organizer and spectator flows stay without passwords and without participant/spectator accounts.
- One bundled commit per task on `main` (project rule: all work in `main`).

## Review Focus

1. **Start moved earlier** — existing links must keep their later expiry (no surprise expiry). Test in Task 1.
2. **Renew from an old, superseded link** — must mail a fresh link to the stored address only, never answer with a token; over the invitation limit 429 and no mail. Test in Task 2.
3. **Renew on a finished event** — 409 `event_finished`, no mail. Test in Task 2.
4. **Organizer reissued the device token** (`POST /{pid}/reissue-token`) — the participant page must show the setup steps again (`phoneSetUp` false), not "phone set up". Test in Task 3.
5. **`/info` for a Draft, unknown or non-GUID event** — 404 with no body data (a Draft event is not public). Test in Task 5.

---

### Task 1: Invitation links valid until a week after the start

**Files:**
- Modify: `src/PositionTracker.Api/Accounts/LinkTokenService.cs:35-37`
- Modify: `src/PositionTracker.Api/Registration/RegistrationService.cs:388-391` (`QueueInvitationAsync`)
- Modify: `src/PositionTracker.Api/Events/EventService.cs:114-142` (`UpdateAsync`)
- Test: `tests/PositionTracker.Api.Tests/AccountUnitTests.cs`, `tests/PositionTracker.Api.Tests/RegistrationTests.cs`

**Interfaces:**
- Produces: `LinkTokenService.InviteAfterStart : TimeSpan` (7 days); `static DateTimeOffset LinkTokenService.InviteExpiry(DateTimeOffset now, DateTimeOffset eventStart)`. Every invitation issued by `QueueInvitationAsync` (create, resend, reissue, remind, address change, and Task 2's renew) uses it.

- [x] **Step 1: Write the failing tests**

```csharp
// AccountUnitTests
[Fact]
public void Invite_expiry_is_the_later_of_fourteen_days_and_a_week_after_the_start()
{
    var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    Assert.Equal(now.AddDays(14), LinkTokenService.InviteExpiry(now, now.AddDays(2)));
    Assert.Equal(now.AddDays(37), LinkTokenService.InviteExpiry(now, now.AddDays(30)));
}

// RegistrationTests (organizer session + event via the API, as the existing tests do)
[Fact] public async Task An_invitation_for_a_distant_event_expires_a_week_after_the_start()
// event start = now + 30 d; invite one participant;
// Assert: the participant's LinkTokens row has ExpiresAt == start + 7 d;
// after Clock.Advance(15 d) GET /api/p/{link} is 200.

[Fact] public async Task Moving_the_start_later_extends_the_invitation_links_and_moving_it_earlier_keeps_them()
// event start = now + 20 d; invite; PUT the event with start = now + 40 d
// Assert ExpiresAt == now + 47 d; PUT start = now + 25 d → ExpiresAt still now + 47 d;
// a revoked (superseded) token of the participant keeps its old ExpiresAt.
```

- [x] **Step 2: Run them to see them fail**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~Invite_expiry|FullyQualifiedName~distant_event|FullyQualifiedName~Moving_the_start"`
Expected: FAIL (`InviteExpiry` not defined).

- [x] **Step 3: Implement**

- `LinkTokenService`: add `InviteAfterStart` and `InviteExpiry` (`max` of the two instants).
- `QueueInvitationAsync`: `var now = clock.GetUtcNow(); links.IssueAsync(LinkPurpose.ParticipantInvite, p.Id, LinkTokenService.InviteExpiry(now, ev.StartTime) - now, ct)`.
- `EventService.UpdateAsync`: after a successful save with `startChanged`, one `ExecuteUpdateAsync` on `db.LinkTokens` where `Purpose == nameof(LinkPurpose.ParticipantInvite)`, `RevokedAt == null`, `SubjectId` in the event's participant ids and `ExpiresAt < start + InviteAfterStart` → set `ExpiresAt = start + InviteAfterStart`.

- [x] **Step 4: Run the tests, then the whole API suite**

Run: `dotnet test tests/PositionTracker.Api.Tests`
Expected: PASS (existing `InviteLifetime + 1 min` expiry tests still pass: their events start at T0 + 1 d, so 14 days still wins).

- [x] **Step 5: Commit**

```bash
git add src/PositionTracker.Api tests/PositionTracker.Api.Tests
git commit -m "feat(registration): invitation links stay valid until a week after the start"
```

---

### Task 2: The participant renews an expired link by themselves

**Files:**
- Modify: `src/PositionTracker.Api/Registration/RegistrationService.cs` (new public method)
- Modify: `src/PositionTracker.Api/Participants/ParticipantLinkEndpoints.cs:466-485` (new route, doc comment list)
- Modify: `web/src/pages/Participant.tsx:33-44` (`LinkProblem`)
- Test: `tests/PositionTracker.Api.Tests/ParticipantLinkTests.cs`, `web/src/app.test.tsx`

**Interfaces:**
- Consumes: `LinkTokenService.InviteExpiry` (Task 1, through `QueueInvitationAsync`).
- Produces: `POST /api/p/{linkToken}/renew` (no body) → 202 `{"status":"accepted"}`; 404 `link_invalid` (unknown token), 409 `event_finished`, 429 `rate_limited`. `RegistrationService.RenewLinkAsync(string linkToken, CancellationToken ct) : Task<EventResult<RenewAnswer>>`, `record RenewAnswer(string Status)`.

Decision: the renewal is keyed on the **old link**, not on a typed email — the participant clicks one button, the new link goes only to the stored address, so holding an old link gives nothing new. A participant who lost every email still asks the organizer (YAGNI).

- [x] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task An_expired_link_renews_itself_by_mailing_a_new_link_to_the_participant()
// Ready event, Invited participant, link; Clock.Advance(InviteLifetime + 1 min)
// POST /renew → 202, body {"status":"accepted"}, body contains no token;
// Outbox has one more Invitation row to the participant's address; its link (parse TextBody) → GET 200;
// the old link → 410 link_superseded.

[Fact] public async Task Renewal_is_refused_after_the_finish_and_over_the_invitation_limit()
// Finished event → 409 event_finished, no new Outbox row.
// Settings "Registration:InvitationsPerAddressPerHour" = "1": first renew 202, second 429 rate_limited, one new row.

[Fact] public async Task Renewal_of_an_unknown_link_is_link_invalid()
// POST /api/p/AAAA…/renew → 404 link_invalid.
```

```tsx
it('an expired participant link offers to mail a new one', async () => {
  const calls = mockFetch({
    'GET /api/p/old': { status: 400, body: { code: 'link_expired', message: { sk: 'Platnosť vypršala', en: 'This link has expired' } } },
    'POST /api/p/old/renew': { status: 202, body: { status: 'accepted' } } });
  renderAt('/p?token=old');
  await userEvent.click(await screen.findByRole('button', { name: 'Email me a new link' }));
  expect(await screen.findByText('We have sent a new link to your email address.')).toBeInTheDocument();
  expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/p/old/renew'))).toBe(true);
});
```

- [x] **Step 2: Run them to see them fail**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~ParticipantLinkTests"` and `cd web && npx vitest run src/app.test.tsx -t "mail a new one"`
Expected: FAIL (404 route / button missing).

- [x] **Step 3: Implement**

- `RenewLinkAsync`: `links.RedeemAsync(LinkPurpose.ParticipantInvite, linkToken, singleUse: false, ct)`; `Unknown` → `ParticipantErrors.LinkInvalid`; `Ok`, `Expired`, `Superseded` → use `SubjectId`. In a transaction: lock the participant's event row (same `FOR UPDATE` pattern as `LockEventAsync`), `Finished` → `ParticipantErrors.EventFinished`; then the existing `WithBudgetAsync(ev.OrganizerId, p.EmailNormalized, …)` around `QueueInvitationAsync` + save + commit (over the budget → `ParticipantErrors.RateLimited`). `EmailFailed` becomes `Invited`, as in `ResendAsync`.
- Route in `ParticipantLinkEndpoints` like `/decline` (no body), answered with status 202.
- Web `LinkProblem` (needs `token`): for `link_expired` and `link_superseded` show the button "Pošlite mi nový odkaz e-mailom" / "Email me a new link" (`btn primary`), after 202 the ok banner "Nový odkaz sme poslali na váš e-mail." / "We have sent a new link to your email address.", errors through `ErrorBanner`. Keep the existing hint texts below the button.

- [x] **Step 4: Run the tests**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~ParticipantLinkTests"` and `cd web && npm test`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/PositionTracker.Api tests/PositionTracker.Api.Tests web/src
git commit -m "feat(participants): an expired or superseded link can mail a new one to the participant"
```

---

### Task 3: A returning participant sees their state, not the setup steps

**Files:**
- Modify: `src/PositionTracker.Api/Participants/ParticipantService.cs:35-46` (`GetAsync`), `:309` (`LinkMe`)
- Modify: `web/src/lib/types.ts:75-79` (`LinkView`)
- Modify: `web/src/pages/Participant.tsx:46-68` (`Flow`)
- Test: `tests/PositionTracker.Api.Tests/ParticipantLinkTests.cs`, `web/src/app.test.tsx`

**Interfaces:**
- Produces: `LinkMe.PhoneSetUp : bool` (JSON `me.phoneSetUp`) — true when the participant has a non-revoked row in `DeviceTokens`. Task 4 adds further fields to the same records; keep the property order `…, Locked, PhoneSetUp`.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task The_view_says_whether_the_phone_is_set_up_and_a_reissued_token_clears_it()
// Confirmed participant: GET → me.phoneSetUp false; ConsentAsync → GET → true;
// organizer POST /api/events/{ev}/participants/{pid}/reissue-token → the new link's GET → false.
```

```tsx
it('a returning participant with a set-up phone sees the state and sets the phone up again only on request', async () => {
  // GET /api/p/back: status Confirmed, me.phoneSetUp: true, link: green health; GET /api/p/back/link
  renderAt('/p?token=back');
  expect(await screen.findByText('You are registered and your phone is set up.')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Agree and continue' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Set up the phone again' }));
  expect(screen.getByText(/replaces the previous setup/)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Agree and continue' })).toBeInTheDocument();
});
```

- [x] **Step 2: Run them to see them fail**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~phone_is_set_up"` and `cd web && npx vitest run src/app.test.tsx -t "returning participant"`
Expected: FAIL.

- [x] **Step 3: Implement**

- `GetAsync`: `await db.DeviceTokens.AnyAsync(t => t.ParticipantId == p.Id && t.RevokedAt == null, ct)` into `LinkMe`.
- `Flow`: when `me.status === 'Confirmed' && me.phoneSetUp && !deviceToken && !redo` render a card "Ste prihlásený a telefón je nastavený." / "You are registered and your phone is set up." with the button "Nastaviť telefón znova" / "Set up the phone again" (`btn`), which sets `redo` and shows `Step2` with the note "Nové nastavenie nahradí to predchádzajúce – telefón potom treba nastaviť znova." / "A new setup replaces the previous setup – the phone then has to be set up again." `Step1` (profile, decline) and `ConnectionCard` stay visible.

- [x] **Step 4: Run the tests**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~ParticipantLinkTests"` and `cd web && npm test`
Expected: PASS (update the fixtures of the existing participant-page tests with `phoneSetUp: false` where the setup steps are expected).

- [x] **Step 5: Commit**

```bash
git add src/PositionTracker.Api tests/PositionTracker.Api.Tests web/src
git commit -m "feat(participants): a returning participant sees the phone is set up instead of the setup steps"
```

---

### Task 4: Spectator link for the participant and for the organizer before the start

**Files:**
- Modify: `src/PositionTracker.Api/Participants/ParticipantService.cs:305-309` (`LinkEvent`, `LinkMe`), `GetAsync`
- Modify: `web/src/lib/types.ts` (`LinkView`)
- Modify: `web/src/pages/Participant.tsx` (`Flow`, new `SpectatorLink` component in the same file)
- Modify: `web/src/pages/EventLayout.tsx:150`
- Test: `tests/PositionTracker.Api.Tests/ParticipantLinkTests.cs`, `web/src/app.test.tsx`

**Interfaces:**
- Consumes: `LinkMe` with `PhoneSetUp` (Task 3).
- Produces: `LinkEvent.Id : string` (runtime id, `RuntimeFactory.RuntimeId(ev.Id)`, JSON `event.id`), `LinkMe.Id : string` (`Id(p.Id)`, the participant's runtime id, JSON `me.id`). Spectator URL: `${location.origin}${BASE}/e/${event.id}?follow=${me.id}` (the existing `follow` parameter of `Spectator.tsx`).

- [x] **Step 1: Write the failing tests**

```csharp
// extend Get_shows_the_event_and_the_participant_without_email_or_tokens:
Assert.Equal(ev.ToString("D"), e.GetProperty("id").GetString());
Assert.Equal(pid.ToString("D"), me.GetProperty("id").GetString());
```

```tsx
it('a confirmed participant gets a spectator link that follows them', async () => {
  // GET /api/p/ok: event.id 'e1', me.id 'p7', status Confirmed
  renderAt('/p?token=ok');
  expect(await screen.findByRole('link', { name: /\/e\/e1\?follow=p7$/ })).toBeInTheDocument();
});
it('the organizer sees the spectator link once the event is ready', async () => {
  // event page fixture with state 'ready' (reuse the event page test's routes)
  expect(await screen.findByText('Public link for spectators:')).toBeInTheDocument();
  // and with state 'draft' the text is absent
});
```

- [x] **Step 2: Run them to see them fail**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~Get_shows_the_event"` and `cd web && npx vitest run src/app.test.tsx -t "spectator link"`
Expected: FAIL.

- [x] **Step 3: Implement**

- Records and `GetAsync` as in Interfaces.
- `SpectatorLink` card (only for `Confirmed`, not after the finish): heading "Odkaz pre divákov" / "Link for spectators", text "Pošlite ho rodine – uvidia vás na mape od štartu." / "Send it to your family – they will see you on the map from the start.", the URL as a link and the button "Zdieľať" / "Share" (`navigator.share` with a clipboard fallback, the pattern of `Spectator.tsx:161`).
- `EventLayout.tsx:150`: condition `event.state !== 'draft'` instead of `=== 'live'`.

- [x] **Step 4: Run the tests**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~ParticipantLinkTests"` and `cd web && npm test`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/PositionTracker.Api tests/PositionTracker.Api.Tests web/src
git commit -m "feat(web): spectator link for the participant and for the organizer before the start"
```

---

### Task 5: The spectator page before the start

**Files:**
- Modify: `src/PositionTracker.Api/Endpoints/LiveEndpoints.cs` (route + handler + doc comment)
- Modify: `web/src/lib/types.ts` (new `PublicEventInfo`)
- Modify: `web/src/pages/Spectator.tsx:84-90` (the not-running branch)
- Create: `tests/PositionTracker.Api.Tests/PublicEventInfoTests.cs`
- Test: `web/src/app.test.tsx`

**Interfaces:**
- Produces: `GET /api/events/{eventId}/info` (anonymous) → 200 `{name, location, startTime, sport, state}` (`state` lower case: `ready|live|finished`) for an event in the database that is not Draft; 404 for Draft, unknown, non-canonical id or without a database. `Cache-Control: no-cache`. Web: `interface PublicEventInfo { name: string; location: string; startTime: string; sport: Sport; state: 'ready' | 'live' | 'finished' }`.

- [x] **Step 1: Write the failing tests**

```csharp
// PublicEventInfoTests: Postgres host configured like ParticipantLinkTests.Host; seed with ParticipantLinkTests.SeedEventAsync
[Fact] public async Task A_ready_event_shows_its_name_place_and_start_to_anyone()
// 200; name "Jarná časovka", location "Žilina", startTime "2026-11-08T10:00:00Z", sport "cycling", state "ready";
// body contains no "@" and no organizer data.

[Theory, InlineData("draft"), InlineData("unknown"), InlineData("not-a-guid")]
public async Task Draft_unknown_and_malformed_events_are_not_found(string which)
// 404.
```

```tsx
it('before the start the spectator page shows the event, the start time and a countdown, then switches to the map', async () => {
  // GET /api/events/e1/course → 404; GET /api/events/e1/info → { state: 'ready', name: 'Time trial', startTime: <now + 2 d 3 h>, ... }
  renderAt('/e/e1');
  expect(await screen.findByRole('heading', { name: 'Time trial' })).toBeInTheDocument();
  expect(screen.getByText(/Starts in 2 d 3 h/)).toBeInTheDocument();
});
```

- [x] **Step 2: Run them to see them fail**

Run: `dotnet test tests/PositionTracker.Api.Tests --filter "FullyQualifiedName~PublicEventInfoTests"` and `cd web && npx vitest run src/app.test.tsx -t "before the start the spectator page"`
Expected: FAIL.

- [x] **Step 3: Implement**

- Handler with `IDbContextFactory<AppDbContext>` and `IOptions<PersistenceOptions>` (pattern of `OperationsEndpoints.OwnsEventAsync`), `EventService.TryParseId`, `AsNoTracking`, only the five fields.
- `Spectator.tsx`: when `course` fails, load `/info` once (`useLoad`); `ready` → card with the name (h1), location, `formatDateTime(startTime)` and "Štart za X d Y h" / "Starts in X d Y h" (minutes under one hour, recomputed every minute), plus "Mapa sa zobrazí automaticky po štarte." / "The map appears automatically after the start."; the course poll keeps running, so the page switches to the map when the event goes live. `/info` 404 or a finished event without a runtime → the existing "not running" card.

- [x] **Step 4: Run the tests and the builds**

Run: `dotnet test` and `cd web && npm test && npm run build`
Expected: PASS, build without errors. Check the new card in light and dark mode in the browser (`/e/{id}` of a Ready event).

- [x] **Step 5: Commit**

```bash
git add src/PositionTracker.Api tests/PositionTracker.Api.Tests web/src
git commit -m "feat(spectators): the public page shows the event and a countdown before the start"
```
