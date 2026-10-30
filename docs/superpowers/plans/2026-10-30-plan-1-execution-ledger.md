# SDD ledger — plan: docs/superpowers/plans/2026-10-30-plan-1-core-tracking-engine.md

Spec: docs/superpowers/specs/2026-10-30-possition-tracker-design.md (read; binding). Branch: feat/core-engine (main = docs commit a12e92e). Controller bootstrapped `git init` + `.gitignore` + docs commit before Task 1.

## Pre-flight scan (tasks sharing a file or interface)

| Pair / task | Produces vs consumes | Finding |
|---|---|---|
| T1 vs bootstrap | T1 step 1 `git init`, `.gitignore` | Already done by controller. |
| T3 vs T14 | T3 `TestCourses` (test project) vs T14 default demo course (Api project) | Api cannot use a test-project helper. |
| T3 vs T4 | `TestCourses.FigureEight/OutAndBack` consumed by matcher tests | OK once generators live in Core (see ruling 1). |
| T6 vs T7 | `GetSnapshot` state stub vs full state logic | OK; T7 completes it. |
| T6 vs T9 | `ApplyResult.Completed` empty, then populated | Replay semantics of `Completed` unclear. |
| T7 vs T10 | No fixes ever: `NotStarted` vs "state from start time" | Ambiguous. |
| T10 vs T13 | `ParticipantView` vs `SpectatorParticipantView` | OK (projector drops fields). |
| T12 vs T13/T14 | `ApiFactory` reused: clock, registry, tokens, key, `Live:IntervalMs` | Factory members not specified. |
| T12 Program vs T14 | `TimeProvider` registration; API key config | Must be DI-replaceable; key config name unspecified. |
| T14 vs T11 | `SimulationService` uses `VirtualRider`; test compares dropout vs no-dropout same profile | Default profiles differ in speed, so the test needs custom profiles. |
| T14 clock | `ScaledTimeProvider` vs test `Step(eventId, simulatedTime)` | Manual stepping vs scaled real time unspecified. |
| T2 | tests vs code | consistent. T3 | tests vs code | consistent. T4 | consistent. T5 | consistent. T8 | consistent. T9 | consistent. T11 | consistent. T13 | consistent. |

## Rulings (pre-flight)

- Ruling 1: Course generators (`Loop`, `FigureEight`, `OutAndBack`) live in Core as `PositionTracker.Core.Simulation.DemoCourses` (public static, same signatures as planned `TestCourses`); `tests/.../TestData/TestCourses.cs` is a thin wrapper or tests call `DemoCourses` directly. — T14 needs a loop in the Api project; spec section 10 wants a demo course. — Cost if wrong: a file move.
- Ruling 2: `ApplyResult.Completed` = segment completions that are new or whose time changed in this `Apply` (on a replay: all that differ from what was last emitted); no duplicate `(Lap, SegmentIndex)` keys inside one result; consumers record idempotently into `SegmentBoard`. — keeps board consistent after late fixes. — Cost: adjust test assertion.
- Ruling 3: A participant with no accepted fix is `NotStarted` regardless of the clock. "Expected but not broadcasting" is out of Plan 1. — spec 5.4 chain starts at Not started; the extra state is not in the plan's enum. — Cost: add a state later.
- Ruling 4: `ApiFactory` exposes public `FakeTimeProvider Clock`, `EventRegistry Registry`, `TokenService Tokens`, `string OrganizerKey` (= "test-key"), a seeded event id, and lets tests set configuration (e.g. `Live:IntervalMs`). `Program` registers `TimeProvider` as a replaceable singleton and reads `Organizer:ApiKey` from configuration (`appsettings.Development.json` sets "dev-key"). — T13/T14 need them. — Cost: refactor factory.
- Ruling 5: `SimulationService.Step(eventId, simulatedTime)` sets the event's simulated elapsed time directly (manual mode); the background loop drives the same state from a `ScaledTimeProvider(baseClock, simStart, scale)`. Add `StartWithProfiles(DemoRequest, IReadOnlyList<VirtualRiderProfile>)` as a public method so tests can supply identical profiles. — plan's tests need deterministic stepping and a controlled comparison. — Cost: small API addition.

## Progress
Task 1: minor (deferred): test csprojs repeat TargetFramework/Nullable/ImplicitUsings; template MapGet Hello World and launchBrowser in Api (replaced by T12)
Task 1: complete (commits a12e92e..f70babb, review clean)
Task 2: minor (deferred): GpxParser accepts NaN/Infinity and out-of-range lat/lon; empty <ele/> rejects whole file; no tests for missing lat attr / malformed number / empty GPX
Task 2: complete (commits f70babb..843ae2c, review clean)
Task 3: minor (deferred): hysteresis undercounts up to ~3 m ascent/descent per direction reversal (Course.cs:79-82, use running extremum); PointAt/ElevationAt/GradePercentAt/Locate edges untested (T4 matcher consumes PointAt); flat-noise test weak (smoothing hides hysteresis); (double[]) cast on CumulativeM; partial elevation silently drops profile; duplicate checkpoint distances accepted
Task 3: complete (commits 843ae2c..4975844, review clean)
Task 4: fix round 1 started (open: I1 backward jitter near shared road jumps to the other pass [CourseMatcher.cs:82,92]; I2 stale-progress lockout after long dropout undocumented)
Task 4: minor (deferred): off-course distance wrong just beyond window edge; List/LINQ allocation per call; planar vs haversine mismatch
Ruling 6 (T4/T6): re-acquisition after dropout is the TRACKER job (Task 6): window widens with elapsed time x max speed since the last matched fix, and after 3 consecutive unmatched fixes the tracker retries with ForwardWindowM = LapLengthM (full-lap search). Matcher stays stateless; I2 is satisfied by documenting the contract in Match XML doc. Cost if wrong: add logic in T6.
Task 4: fix round 1/5 (3 addressed, 1 new open — Important: BackwardPenalty=3 locks onto outbound leg after hairpin when step not aligned with turn/participant slows; doc inaccurate; commits 9b9a21f..95887c1)
Task 4: minor (deferred then included in round 2): zero-length segment sets only AtStart
Task 4: fix round 2/5 (3 addressed, 0 open; commits 95887c1..0c99420)
Task 4: minor (deferred): XML doc re-acquisition recipe incomplete (T6 must widen window AND use BackwardWindowM=0 or shift `last` to expected position; full-lap with default backward window still prefers the outbound image); XML doc references private PlausibleForwardM; stall-at-most-one-fix wording; residual: backward jitter within ~75 m before a turn can jump <=150 m to return leg; lock-on-outbound if fixes straddling turn >150 m apart then <50 m/fix (detectable: Matched=true with falling progress) -> T6 must detect via speed/progress checks
Task 4: complete (commits 4975844..0c99420, review clean after 2 fix rounds)
Task 5: minor (deferred): first fix with non-finite coordinates is accepted (previousAccepted null) -> T6 must validate coordinates (finite, lat +-90, lon +-180) BEFORE the filter; missing tests for exact-threshold speed, accuracy-vs-duplicate ordering, NaN accuracy, undefined Sport
Task 5: complete (commits 0c99420..598ad27, review clean)
Task 6: fix round 1 started (open: I1 false lap on short loops/after long off-course stretches; I2 speed/max spike after unmatched fixes; I3 10 s speed window vs 5-10 s send interval gives speed 0)
Ruling 7 (T6): fixes with Time < startTime update last-fix fields (position, accuracy, time) but do NOT change total distance, laps, history or moving/stopped time; participant stays NotStarted until startTime. Why: warm-up on course before the start must not add distance/laps. Cost if wrong: small tracker change.
Task 6: minor (deferred): Lap/LapProgress can disagree with TotalDistance near seam (T9 must derive segment/lap from TotalDistanceM, not Lap); forward GPS spike counts as stopped time; ProgressHistory.Add throws on earlier time (document); ParticipantTracker.cs holds 5 public types (cosmetic split)
Task 6: fix round 1/5 (4 addressed, 0 open; commits cb88f3a..079fb8c)
Task 6: minor (deferred): final-lap rider on a short loop (<=~1 km) can stay unmatched and never get FinishTime after a ~60-90 s dropout (near-full-lap moves never credited) -> final review/Plan 2 decide; AvgSpeedMps inflated after off-course stretches (matched-interval progress speed not credited as moving); no stopped time during rejected on-course drift; OffCourseM ~ 0 does not imply the fix was accepted (T7 must use tracker state, not OffCourseM alone); full-lap retry removed (ruling 2b superseded)
Task 6: complete (commits 598ad27..079fb8c, review clean after 1 fix round)
Task 7: fix round 1 started (open: I1 backward GPS drift on course reads as OffCourse [ParticipantTracker.cs:293,196,351]); included minor: pause+unpause with equal timestamps leaves participant paused
Task 7: minor (deferred): AvgSpeed keeps distance ridden while paused; Stopped can turn on ~1 fix early (averaged window); consider ParticipantStateEvaluator split later
Task 7: fix round 1/5 (2 addressed, 0 open; commits 79d630c..02e5586)
Task 7: minor (deferred): SetPaused duplicate call adds list entries and triggers full replay; runtime must read state via GetSnapshot(clock now) (DNF/Finished semantics)
Task 7: complete (commits 079fb8c..02e5586, review clean after 1 fix round)
Task 8: minor (deferred): uphill/extreme-grade tests assert direction only, not magnitude/clamp bound; reference flat speed divides rolling-average speed by instantaneous grade factor (noisy grade biases it) -> consider using average grade of the last window in final review/runtime
Task 8: complete (commits 02e5586..1e0eabd, review clean)
Task 9: minor (deferred): finisher gap mixes FinishTime (fix time) with leader TimeAt interpolation (error up to one fix interval) -> use History.TimeAt(TotalDistanceM) in final fix wave; leader gets TimeSpan.Zero when nobody has fixes (should be null); gap null silently when leader history starts after participant distance (document/test); withdrawn completions not signalled after replay lowers total (document in Apply XML doc); missing tests: two-finisher gap, multi-boundary emission in one Apply, purple tie
Task 9: complete (commits 1e0eabd..498f649, review clean)
Task 10: fix round 1 started (open: I1 Alarm.Since for Stopped/OffCourse = view time -> expose StateSince/OffCourseSince/StoppedSince from tracker snapshot); included minors: ApplyMany to avoid O(batch x stored) replay under lock, Sos alarm raised regardless of displayed Finished/Dnf state, Pause/Resume Act test
Task 10: minor (deferred): DNF participants stay in distance ranking and act as gap reference (decide: rank after non-DNF, exclude from gap reference) -> final review/Plan 2; DeviceStatus by arrival order (document "as of send"); ParticipantInfo lookup needed by T13 projector
Task 10: fix round 1/5 (4 addressed, 1 new open — Important I-1: StoppedSince uses per-interval SlowSince rule, jumps forward with GPS jitter (>=1.4 m new high-water mark in 5 s) so Stopped alarm Since resets/duration wrong [ParticipantTracker.cs:291-299, ProgressHistory.SlowSince]; recommended fix: since = History.TimeAt(_totalM - toleranceM 10-20 m) + pause adjustment, test with +-2-4 m along-track jitter; commits 919f127..c530c70)
RESUME HERE (usage limit hit): resume implementer agent a4178bb9cd6d1b4b3 (or fresh opus implementer with task-10-brief.md + report) for fix round 2 of Task 10, then scoped re-review (review-package 919f127..HEAD), then Tasks 11-14 per plan; Tasks 1-9 complete.
Task 10: fix round 2/5 started->done (commit 5660dd0): StoppedSince = first fix within 15 m tolerance of current total, not later than stopped window start; SlowSince removed
Ruling 8 (T10): a pause during a stop gives a NEW StoppedSince equal to the Resume time (a declared pause is a planned rest and suppresses the alarm; after Resume it is a new stop episode). Cost if wrong: alarm duration restarts after a pause.
Task 10: fix round 2/5 (1 addressed, 0 open; commits c530c70..5660dd0)
Task 10: minor (deferred): StoppedSince slides (<= ~25 s for walkers) at onset of Stopped and during very slow creep/15 m+ multipath jump, so (Kind, Since) is not a stable ack key there -> Ruling 9: alarm acknowledgement (Plan 2/UI) must be keyed on (participantId, Kind) not Since; after Resume Since = Resume time while state already counts earlier stop
Task 10: complete (commits 498f649..5660dd0, review clean after 2 fix rounds)
Task 11: minor (deferred): no multi-lap/graded-course tests; off-course test measures distance to vertices not segments; null Dropouts NRE; overlapping dropouts first-match; first off-course fix rejected by speed filter for 150 m jump -> T14 must use OffCourseFor >= 30 s for running (demo uses 120 s)
Task 11: complete (commits 5660dd0..952c87e, review clean)
Task 12: fix round 1 started (open: I1 no request body size limit, 413 decided after parsing 30 MB body [IngestEndpoints.cs:62-68]); included minors: Retry-After on 429, WWW-Authenticate on 401, test isolation of Token2, explicit participant check instead of catching KeyNotFoundException
Task 12: minor (deferred): shared per-IP bucket behind reverse proxy until ForwardedHeaders/KnownProxies configured (Plan 5); client contract: drop a batch on 400 (one bad t rejects whole batch) -> Plan 4; JSON leniency (string numbers, case-insensitive, unknown/duplicate props) decide on purpose; later tasks must read config via options or UseSetting (eager builder.Configuration reads miss factory overrides)
Task 12: fix round 1/5 (4 addressed, 0 open; commits 235b531..b9c2b56)
Task 12: minor (deferred): Kestrel MaxRequestBodySize path reasoned not tested (TestServer lacks feature); slow-trickle bodies rely on Kestrel MinRequestBodyDataRate/reverse proxy timeouts (Plan 5); HasParticipant check-then-act gap valid only while participants cannot be removed
Task 12: complete (commits 952c87e..b9c2b56, review clean after 1 fix round)
Task 13: review approved with minors; Ruling 10 (T13): spec 5.5 says alarms and SOS are NOT visible to spectators, so the spectator projection maps states Sos, Stopped and OffCourse to Live (Offline/Delayed/Finished/Dnf/NotStarted stay visible: spectator mockup shows "bez signalu"). Cost if wrong: spectators lose the stopped/off-course hint. Fix round 1 started to apply it plus: cache SpectatorView per (buffered view, participants version) in SpectatorFeed; fixed HubException messages (no echo of eventId)
Task 13: minor (deferred): SpectatorDelay = 0 is allowed (explicit organizer choice; validate in Plan 2 event creation); no limits/logging on hub JoinOrganizer attempts or anonymous connection count (Plan 5 hardening); sequential awaited group sends can be held back by one slow client (add per-send timeout/fire-and-forget in Plan 5); AdvanceUntil test timing margin ~10x; lat/lon full precision for minors (policy, later)
Task 13: fix round 1/5 (3 addressed, 0 open; commits 545f46b..240231f)
Task 13: minor (deferred): cache invalidation depends on AddParticipant being the only participant-info mutation (any future consent/nickname update MUST bump ParticipantsVersion — privacy); spectator-visible correlations (speed 0, frozen position/ETA/gap) can hint at a stop; verify EtaEstimate carries no reason field
Task 13: complete (commits b9c2b56..240231f, review clean after 1 fix round)
Task 14: minor (deferred): GpxParser XDocument.Load uses default DtdProcessing (use DtdProcessing.Prohibit; parse happens before the 409 limit check); overlapping unsorted dropouts via StartWithProfiles reorder delivery; loop at 100 x scale 200 does ~12000 Ingest calls/s (batch due fixes per rider per tick); base64 size precheck counts whitespace; failing event logs twice a second forever; SimulationService.cs 394 lines (split DTOs)
Task 14: complete (commits 240231f..92aa2a0, review clean)
ALL 14 TASKS COMPLETE. NEXT: final whole-branch review (most capable model) over a12e92e..HEAD, then ONE fix wave, scoped re-review, then Finish (rulings list).
FINAL REVIEW (fable, a12e92e..92aa2a0): Ready to merge = With fixes. Critical C1 (short-loop signal gap loses laps), Important I1 future-timestamp fix, I2 token overload (fix spacing/caps), I3 DNF in ranking, I4 spectator sees position outside race window, I5 payload size, I6 ETA window/drift, I7 simulator bypasses HTTP. Full list in final-fix-wave.md.
Ruling 11 (final): simulator feeds EventRuntime.Ingest in-process (per-token 60 req/min makes HTTP impossible at TimeScale >= 20); document in README. Cost if wrong: spec wording amended.
Ruling 12 (final): I5 only rounds spectator values now; spectator cadence and on-demand segments are Plan 5.
Final fix wave dispatched as ONE implementer (opus) with final-fix-wave.md; then ONE scoped re-review; then finishing.
Final fix wave (commits 92aa2a0..3f0fba7): C1, I1-I7, Minor 1-10, README, mockups done per report; Core 274 + Api 88 tests green.
Ruling 13 (final, I5): rounding gave only ~15-18% payload reduction (segments are ~60% of bytes); the 30% target and on-demand segments / lower broadcast cadence move to Plan 5. Cost if wrong: 500 spectators x 1.5 s needs ~200 Mbit/s until Plan 5 -> do not run a 500-spectator event before Plan 5.
Ruling 14 (final, I2a): at most ONE stored fix per second (order-independent) instead of a spacing rule. Cost: none known.
Final re-review: all findings addressed; one new Important (design-level): after a signal gap with no fixes at all the lap count is chosen by expected advance, so a rider whose speed/stops differ from the expectation by |dv|*gap > L/2 gets a permanent wrong lap count (false extra lap -> false Finished one lap early, which outranks Offline/Stopped and suppresses safety alarms; or a missing lap -> never finishes). Repro: 2 km x5 cyclist, phone off 300-600 s, stopped 400-580 s: total +2000 m.
Ruling 15 (final): accept this C1 trade-off for Plan 1 (no second fix wave per process). Mitigation to implement in Plan 2/4: organizer flag/alarm "laps inferred after a gap" and no Finished from an inferred lap without a confirming fix; until then do not rely on Finished from a rider after a long signal gap on a loop shorter than ~2 km. Cost if wrong: false early Finished / missing lap for riders with long gaps on short loops.
Final re-review minors (to later plans): SOS before first race fix exposes warm-up position to spectators (StateAt checks Sos before NotStarted; hide position when LastRaceFixTime is null); per-token 600 fixes/min budget silently drops the NEWEST fixes after ~50 min outages (use 429+Retry-After or keep newest); repeated no-op SetPaused still appends to _pauseSwitches.
FINAL: branch feat/core-engine head 3f0fba7, Core 274 + Api 88 tests green, 0 warnings.
