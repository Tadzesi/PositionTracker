# Plan 1: Core Tracking Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tested .NET back end that takes position batches from phones, matches them to a multi-lap GPX course, computes metrics, ETA, ranking, sector colours and alarm states, serves them to organizer and spectator clients (REST and SignalR), and can run a simulated demo event through the same ingest path.

**Architecture:** A dependency-free `PositionTracker.Core` library holds all geometry and race logic as pure, deterministic code driven by `TimeProvider`. `PositionTracker.Api` (ASP.NET Core minimal API + SignalR) wraps it with token auth, rate limiting, in-memory event runtimes, a batching broadcaster, and a simulation service. Persistence, accounts and email are Plan 2; storage here is in memory behind `EventRuntime`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, xunit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-10-30-possition-tracker-design.md` (series: `docs/superpowers/plans/2026-10-30-roadmap.md`)

## Global Constraints

- Platform: .NET 10 (SDK 10.0.x installed), `Nullable` enabled, `TreatWarningsAsErrors` true. Project root is `C:\Projects\development\lab463.com\PossitionTracker`; it is not yet a git repository (Task 1 runs `git init`).
- All code, comments, commit messages in English. Commit messages are conventional (`feat:`, `test:`, `chore:`).
- Progress is **total distance** = laps completed × lap length + progress in the lap, and is monotonic. Rank, gaps, ETA, finish use total distance. One lap count per event. A course with `laps > 1` must be closed (end within 100 m of start).
- **Elevation comes from the course profile at the matched position, never from GPS altitude.** Ascent/descent are counted with a 3 m hysteresis on a smoothed profile.
- Time gaps use interpolation: the time the participant ahead was at the same total distance.
- Ordering of fixes is by device timestamp; late batches are accepted and results recomputed deterministically.
- Defaults (all configurable per event): Delayed after 15 s without a fix; Offline after 2 min; Stopped = speed under 1 km/h for 3 min; off course = more than 100 m from the course for more than 15 s; spectator delay 30 s; accuracy filter 50 m; impossible-speed filter 10 m/s running, 30 m/s cycling; live fan-out every 1.5 s; send interval from phones 5 to 10 s.
- A manual Pause suppresses the Stopped state; paused time counts as neither moving nor stopped time and is excluded from the ETA basis.
- Participant states: `NotStarted, Live, Delayed, Offline, Stopped, OffCourse, Sos, Finished, Dnf`. Display priority: Dnf > Finished > Sos > Offline > Delayed > OffCourse > Stopped > Live.
- Sector colours: Purple = fastest recorded time on that segment among all participants and laps, needing results from at least 2 distinct participants; Green = the participant's own best; Yellow = otherwise.
- Spectators never receive battery, GPS accuracy, queue length, alarms or email. A participant without `ShowNameToSpectators`, or a minor, appears as bib number only.
- Scale target: 100 participants, 500 concurrent spectators, single instance, no backplane.
- Participant tokens are random 32-byte values stored only as SHA-256 hashes. Organizer access in this plan is one configured API key (`X-Api-Key`); real accounts arrive in Plan 2.

## Review Focus

Inputs and conditions the spec implies but does not spell out. Each has a test in the task named in brackets.

1. A course that crosses itself (figure eight) or runs out and back must not make the matcher jump to the other branch. [Task 4]
2. A participant jittering ±20 m across the start/finish line, or a batch of old fixes arriving after newer ones, must not add laps or change the outcome versus ordered input. [Task 6]
3. Identical timestamps, duplicate fixes and zero time delta must not produce NaN, infinity or division by zero in speed or pace. [Tasks 5, 6]
4. A participant standing still at the start (speed zero) must get no ETA instead of an infinite or absurd one. [Task 8]
5. A GPX without elevation, with several track segments, or with a single point must be handled: no elevation gives ascent 0, several segments are joined, a single point is rejected with a clear error. [Tasks 2, 3]

---

### Task 1: Solution scaffold and repository

**Files:**
- Create: `PositionTracker.sln`, `Directory.Build.props`, `.gitignore`
- Create: `src/PositionTracker.Core/PositionTracker.Core.csproj`, `src/PositionTracker.Api/PositionTracker.Api.csproj`
- Create: `tests/PositionTracker.Core.Tests/PositionTracker.Core.Tests.csproj`, `tests/PositionTracker.Api.Tests/PositionTracker.Api.Tests.csproj`

**Interfaces:**
- Produces: namespaces `PositionTracker.Core` and `PositionTracker.Api`; the test projects reference Core and Api respectively; `Api.Tests` references `Microsoft.AspNetCore.Mvc.Testing` and `Microsoft.Extensions.TimeProvider.Testing`; Api exposes `public partial class Program`.

- [ ] **Step 1:** In the project root run `git init`. Create `.gitignore` ignoring `bin/`, `obj/`, `.vs/`, `.superpowers/`, `.playwright-mcp/`, `*.user`.
- [ ] **Step 2:** Create `Directory.Build.props` with `TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `LangVersion latest`.
- [ ] **Step 3:** Create the solution and four projects with `dotnet new sln/classlib/web/xunit`, add the references listed above, add the Api project to the solution (Api uses `Microsoft.AspNetCore.App` framework reference; Core has no package references).
- [ ] **Step 4:** Verify. Run: `dotnet build` and `dotnet test`. Expected: build succeeds with 0 warnings; tests run (0 or template tests) without failure.
- [ ] **Step 5:** Commit `chore: scaffold solution` (include `docs/`).

---

### Task 2: Geometry and GPX parsing

**Files:**
- Create: `src/PositionTracker.Core/Geo.cs`, `src/PositionTracker.Core/GpxParser.cs`
- Test: `tests/PositionTracker.Core.Tests/GeoMathTests.cs`, `tests/PositionTracker.Core.Tests/GpxParserTests.cs`

**Interfaces:**
- Produces:
```csharp
public readonly record struct GeoPoint(double Lat, double Lon, double? Ele = null);
public static class GeoMath {
  public static double DistanceM(GeoPoint a, GeoPoint b);
  public static double DistanceM(double lat1, double lon1, double lat2, double lon2);
}
public sealed class GpxFormatException(string message) : Exception(message);
public static class GpxParser { public static IReadOnlyList<GeoPoint> Parse(Stream gpx); }
```

- [ ] **Step 1: Write the failing tests**
  - `GeoMathTests.Distance_one_degree_of_longitude_at_49N`: `DistanceM(49,18,49,19)` is within 100 m of 72 950 m.
  - `GeoMathTests.Distance_is_symmetric_and_zero_for_same_point`.
  - `GpxParserTests`: `Parses_track_points_with_elevation` (3 `trkpt`, elevations 400/410/420); `Missing_elevation_gives_null_ele`; `Joins_multiple_track_segments_in_order`; `Accepts_route_points_rtept`; `Single_point_throws_GpxFormatException` (message contains "at least 2"); `Non_xml_throws_GpxFormatException`. Build the GPX strings inline in the tests.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~GeoMathTests|FullyQualifiedName~GpxParserTests"`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the signatures above. Haversine with earth radius 6 371 008.8 m. Parser uses `XDocument` with namespace-agnostic element lookup (`LocalName`), reads `trkpt` first, falls back to `rtept`, parses with `CultureInfo.InvariantCulture`, wraps any `XmlException`/`FormatException` in `GpxFormatException`.
- [ ] **Step 4:** Rerun the filter. Expected: PASS.
- [ ] **Step 5:** Commit `feat: geometry and GPX parsing`.

---

### Task 3: Course model

**Files:**
- Create: `src/PositionTracker.Core/Models.cs` (shared types), `src/PositionTracker.Core/Course.cs`
- Create: `tests/PositionTracker.Core.Tests/TestData/TestCourses.cs`
- Test: `tests/PositionTracker.Core.Tests/CourseTests.cs`

**Interfaces:**
- Produces (in `Models.cs`):
```csharp
public enum Sport { Running, Cycling }
public sealed record PositionFix(DateTimeOffset Time, double Lat, double Lon, double? AccuracyM);
public sealed record DeviceStatus(double? BatteryPct, bool? Charging, int QueueLength, bool InForeground);
public sealed record PositionBatch(IReadOnlyList<PositionFix> Fixes, DeviceStatus? Status);
```
- Produces (in `Course.cs`):
```csharp
public sealed record Checkpoint(string Name, double LapDistanceM);
public sealed class Course {
  public IReadOnlyList<GeoPoint> Points { get; }  public IReadOnlyList<double> CumulativeM { get; }
  public double LapLengthM { get; }  public int Laps { get; }  public double TotalLengthM { get; }
  public bool IsClosed { get; }  public bool HasElevation { get; }
  public double AscentPerLapM { get; }  public double DescentPerLapM { get; }
  public IReadOnlyList<Checkpoint> Checkpoints { get; }
  public IReadOnlyList<double> SegmentBoundariesM { get; }   // [0, cp1.., LapLengthM]
  public int SegmentCount { get; }                            // SegmentBoundariesM.Count - 1
  public double ElevationAt(double lapDistanceM);  public GeoPoint PointAt(double lapDistanceM);
  public double GradePercentAt(double lapDistanceM);          // over a 50 m window
  public double AscentUpTo(double totalDistanceM);  public double DescentUpTo(double totalDistanceM);
  public static Course Create(IReadOnlyList<GeoPoint> points, int laps, IReadOnlyList<Checkpoint>? checkpoints = null);
}
```
- Produces (test helper): `TestCourses.Loop(double lapLengthM = 12_500, double stepM = 25, Func<double,double>? elevationAtM = null)`, `TestCourses.FigureEight(double lapLengthM = 4_000, double stepM = 10)`, `TestCourses.OutAndBack(double oneWayM = 5_000, double stepM = 25)`. Each builds points in local planar metres around (49.0, 18.7) converted to degrees (111 195 m per degree of latitude, scaled by cos(lat) for longitude); `Loop` is a rectangle traversed once and closed (last point equals first).

- [ ] **Step 1: Write the failing tests** in `CourseTests`:
  - `Lap_length_of_rectangular_loop_is_12500_within_25m`.
  - `Three_laps_total_length_is_three_times_lap_length`.
  - `Non_closed_course_with_two_laps_throws_ArgumentException` (use `OutAndBack` trimmed to one way).
  - `Checkpoint_outside_lap_range_throws_ArgumentException`.
  - `Ascent_of_single_hill_is_100m_within_5m`: elevation rises 0→100 m over the first 6 250 m and falls back; assert `AscentPerLapM` and `DescentPerLapM`.
  - `Noise_of_plus_minus_1_5m_on_flat_course_gives_ascent_below_5m` (Review Focus 5).
  - `Course_without_elevation_has_HasElevation_false_ascent_zero_and_ElevationAt_zero` (Review Focus 5).
  - `AscentUpTo_one_and_a_half_laps_equals_one_lap_plus_half_lap_ascent`.
  - `SegmentBoundaries_for_two_checkpoints_are_zero_cp1_cp2_lapLength`.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~CourseTests`. Expected: FAIL.
- [ ] **Step 3:** Implement `Course.Create`: cumulative distances; `IsClosed` when first and last point are within 100 m; smooth elevation with a moving average of ±5 points; count ascent/descent with a 3 m hysteresis (accumulate a swing only after it exceeds 3 m); `laps > 1 && !IsClosed` throws `ArgumentException`; checkpoints validated to lie in (0, LapLengthM) and sorted. Interpolation methods use binary search on `CumulativeM`; `AscentUpTo` splits total distance into whole laps plus a remainder.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: course model with elevation and checkpoints`.

---

### Task 4: Course matcher

**Files:**
- Create: `src/PositionTracker.Core/CourseMatcher.cs`
- Test: `tests/PositionTracker.Core.Tests/CourseMatcherTests.cs`

**Interfaces:**
- Consumes: `Course`, `GeoPoint`, `TestCourses`.
- Produces:
```csharp
public sealed record MatcherOptions(double ForwardWindowM = 1500, double BackwardWindowM = 50, double OffCourseThresholdM = 100);
public sealed record MatchResult(double LapProgressM, double OffCourseM, bool Matched);
public static class CourseMatcher {
  public static MatchResult Match(Course course, double lat, double lon, double lastLapProgressM, MatcherOptions? options = null);
}
```
`Matched` is false when the nearest course point inside the search window is farther than `OffCourseThresholdM`; in that case `LapProgressM` equals `lastLapProgressM`.

- [ ] **Step 1: Write the failing tests** in `CourseMatcherTests`:
  - `Point_on_course_matches_its_own_distance_within_5m` (course point at 3 000 m, last progress 2 900).
  - `Point_40m_off_course_matches_with_OffCourseM_about_40`.
  - `Point_300m_off_course_is_not_matched_and_progress_is_unchanged`.
  - `Search_is_limited_to_the_window`: a point on the course 5 000 m ahead of last progress is not matched forward (Matched false).
  - `Small_backward_jitter_within_50m_is_accepted`; `Backward_jump_of_500m_is_not_accepted`.
  - `Lap_wrap_on_closed_loop`: last progress 12 450 m on a 12 500 m loop, fix at the start line yields progress about 0–10 m (wraps, not 12 500).
  - **Review Focus 1:** `Figure_eight_crossing_does_not_switch_branch`: ride the figure eight in order with last progress carried forward; at every step the matched progress increases by less than 100 m per 25 m step, including at the crossing. `Out_and_back_returning_over_same_road_matches_return_leg`: after the turn point, fixes on the same road match progress beyond the turn, not the outbound leg.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~CourseMatcherTests`. Expected: FAIL.
- [ ] **Step 3:** Implement: for each course segment whose lap distance lies in `[last - Backward, last + Forward]` (modulo `LapLengthM` for closed courses), project the point onto the segment in local planar metres, keep the candidate with the smallest distance and, on ties within 5 m, the one closest ahead of `last`. Return unmatched when the best distance exceeds the threshold.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: windowed course matching for loops and crossings`.

---

### Task 5: Position filter

**Files:**
- Create: `src/PositionTracker.Core/PositionFilter.cs`
- Test: `tests/PositionTracker.Core.Tests/PositionFilterTests.cs`

**Interfaces:**
- Consumes: `PositionFix`, `Sport`, `GeoMath`.
- Produces:
```csharp
public sealed record FilterOptions(double MaxAccuracyM, double MaxSpeedMps) {
  public static FilterOptions For(Sport sport);   // 50 m; 10 m/s running, 30 m/s cycling
}
public enum FilterDecision { Accept, RejectAccuracy, RejectImpossibleSpeed, RejectDuplicate }
public static class PositionFilter {
  public static FilterDecision Evaluate(PositionFix fix, PositionFix? previousAccepted, FilterOptions options);
}
```

- [ ] **Step 1: Write the failing tests:** accuracy 51 m is `RejectAccuracy`, 50 m is `Accept`; null accuracy is `Accept`; a 1 000 m jump in 10 s is `RejectImpossibleSpeed` for a runner (100 m/s) and for a cyclist, while 200 m in 10 s (20 m/s) is `RejectImpossibleSpeed` for a runner and `Accept` for a cyclist; identical timestamp and position is `RejectDuplicate`; **Review Focus 3:** same timestamp with a different position does not divide by zero and returns `RejectDuplicate`; a fix older than the previous accepted one is evaluated against it by absolute time delta and is not rejected for speed when plausible.
- [ ] **Step 2:** Run the filter tests. Expected: FAIL.
- [ ] **Step 3:** Implement `Evaluate` with the order accuracy, duplicate (equal `Time`), speed (`distance / |dt|`).
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: position quality filter`.

---

### Task 6: Progress history and participant tracker (motion, laps, late fixes)

**Files:**
- Create: `src/PositionTracker.Core/ProgressHistory.cs`, `src/PositionTracker.Core/ParticipantTracker.cs`
- Test: `tests/PositionTracker.Core.Tests/ProgressHistoryTests.cs`, `tests/PositionTracker.Core.Tests/ParticipantTrackerMotionTests.cs`

**Interfaces:**
- Consumes: `Course`, `CourseMatcher`, `PositionFilter`, `FilterOptions`, `PositionFix`, `Sport`.
- Produces:
```csharp
public sealed class ProgressHistory {
  public void Add(DateTimeOffset time, double totalDistanceM);   // keeps distance non-decreasing
  public DateTimeOffset? TimeAt(double totalDistanceM);          // linear interpolation, null outside range
  public (DateTimeOffset Time, double DistanceM)? Last { get; }
  public double SpeedMps(DateTimeOffset until, TimeSpan window); // 0 if fewer than 2 points in window
}
public sealed record TrackerOptions(
  TimeSpan DelayedAfter, TimeSpan OfflineAfter, double StoppedSpeedMps, TimeSpan StoppedAfter,
  double OffCourseM, TimeSpan OffCourseDebounce, TimeSpan SpeedWindow, TimeSpan MovingSpeedWindow, FilterOptions Filter) {
  public static TrackerOptions Defaults(Sport sport);  // 15 s, 2 min, 1/3.6, 3 min, 100, 15 s, 10 s, 60 s
}
public enum ParticipantState { NotStarted, Live, Delayed, Offline, Stopped, OffCourse, Sos, Finished, Dnf }
public sealed record SegmentCompletion(int Lap, int SegmentIndex, TimeSpan Time);
public sealed record ParticipantSnapshot(
  DateTimeOffset? LastFixTime, double? LastLat, double? LastLon, double? LastAccuracyM,
  double TotalDistanceM, int Lap, double LapProgressM, double OffCourseM,
  double SpeedMps, double MovingSpeedMps, double AvgSpeedMps, double MaxSpeedMps, double? PaceSecPerKm,
  TimeSpan MovingTime, TimeSpan StoppedTime, double AscentM, double DescentM, double GradePercent,
  ParticipantState State, DateTimeOffset? FinishTime);
public sealed record ApplyResult(ParticipantSnapshot Snapshot, IReadOnlyList<SegmentCompletion> Completed);
public sealed class ParticipantTracker {
  public ParticipantTracker(Course course, Sport sport, DateTimeOffset startTime, TrackerOptions? options = null);
  public ProgressHistory History { get; }
  public ApplyResult Apply(PositionFix fix);
  public ParticipantSnapshot GetSnapshot(DateTimeOffset now);   // state is evaluated here (Task 7)
}
```
In this task `GetSnapshot` returns `Live` once a fix exists and `NotStarted` before; Task 7 completes the state logic. `Completed` is empty until Task 9.

- [ ] **Step 1: Write the failing tests**
  - `ProgressHistoryTests`: `TimeAt_interpolates_between_points` (0 m at t0, 100 m at t0+10 s, TimeAt(50) = t0+5 s); `TimeAt_returns_null_outside_range`; `Distance_never_decreases_when_a_lower_value_is_added`; `SpeedMps_over_10s_window_is_10_for_10mps_motion`; `SpeedMps_with_single_point_is_zero`.
  - `ParticipantTrackerMotionTests` (loop course 12 500 m, 3 laps, cycling, fixes every 5 s at 8 m/s along the course using `Course.PointAt`): `Total_distance_after_1_000_s_is_8_000_m_within_50`; `Lap_is_2_after_13_000_m` and `LapProgress_wraps`; `MovingSpeed_is_8mps_within_0_3`; `Pace_for_runner_at_3mps_is_333_sec_per_km_within_2`; `Ascent_comes_from_course_profile_not_gps`: fixes carry no altitude and the snapshot ascent equals `course.AscentUpTo(total)`; `FinishTime_is_set_when_total_reaches_TotalLength` (FinishTime equals the time of the first fix at or beyond 37 500 m).
  - **Review Focus 2:** `Jitter_across_start_finish_line_does_not_add_laps`: fixes alternating ±20 m around the start line at the start for 60 s leave `Lap == 1` and `TotalDistanceM < 100`. `Late_batch_gives_same_result_as_ordered_input`: apply 100 fixes in order to tracker A; apply the same fixes with the first 40 delivered after the last 60 to tracker B; snapshots (excluding nothing) are equal for `TotalDistanceM`, `MaxSpeedMps`, `MovingTime`, `AscentM`.
  - **Review Focus 3:** `Two_fixes_with_same_timestamp_do_not_produce_NaN_or_infinity`: assert all double fields are finite.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~ProgressHistoryTests|FullyQualifiedName~ParticipantTrackerMotionTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement `ProgressHistory` (list + binary search). Implement the tracker pipeline: filter, match with the last lap progress, lap counter increments when matched progress drops by more than half a lap, total = (lap-1)·lapLength + progress, never decreasing by more than 50 m (otherwise keep the previous total). Speeds come from `History.SpeedMps` (progress-based, not raw GPS). Moving time accumulates between consecutive fixes when the interval speed is at least `StoppedSpeedMps`, stopped time otherwise; average = total distance / moving time (0 if no moving time); max = largest 10 s windowed speed. A fix with `Time` before the last stored fix triggers a full replay of all stored accepted fixes sorted by time into fresh state, so the result is independent of arrival order. Keep the stored fixes in a sorted list.
- [ ] **Step 4:** Rerun the filter. Expected: PASS.
- [ ] **Step 5:** Commit `feat: participant tracker with laps, speeds and deterministic late-fix replay`.

---

### Task 7: Participant states, pause, SOS, off-course debounce

**Files:**
- Modify: `src/PositionTracker.Core/ParticipantTracker.cs`
- Test: `tests/PositionTracker.Core.Tests/ParticipantTrackerStateTests.cs`

**Interfaces:**
- Consumes: Task 6 types.
- Produces (added to `ParticipantTracker`):
```csharp
public void SetSos(bool on);
public void SetPaused(DateTimeOffset at, bool paused);
public void MarkDnf(DateTimeOffset at);
```
`GetSnapshot(now)` now evaluates `State` by the priority in Global Constraints.

- [ ] **Step 1: Write the failing tests** (fixtures: tracker started at T0, fixes at 8 m/s):
  - `Before_start_time_and_without_fixes_is_NotStarted`.
  - `Fix_14s_ago_is_Live`; `Fix_20s_ago_is_Delayed`; `Fix_121s_ago_is_Offline`.
  - `Speed_0_1mps_for_3_minutes_is_Stopped`; `Same_with_pause_is_not_Stopped_and_excludes_paused_time_from_StoppedTime_and_MovingTime`.
  - `Sos_overrides_Offline`; `Sos_cleared_returns_to_Offline`.
  - `OffCourse_150m_for_20s_is_OffCourse`; `OffCourse_150m_for_10s_is_Live`.
  - `Reaching_total_length_is_Finished_and_stays_Finished_with_no_further_fixes_even_after_10_minutes` (not Offline).
  - `MarkDnf_is_Dnf_and_overrides_everything`.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~ParticipantTrackerStateTests`. Expected: FAIL.
- [ ] **Step 3:** Implement the state evaluation and the three setters. Pause intervals are stored and subtracted from moving/stopped accounting; the off-course debounce measures the time since the first consecutive unmatched or over-threshold fix.
- [ ] **Step 4:** Rerun the Task 6 and Task 7 tests together: `dotnet test --filter "FullyQualifiedName~ParticipantTracker"`. Expected: PASS.
- [ ] **Step 5:** Commit `feat: participant states, pause, SOS and DNF`.

---

### Task 8: ETA estimator and grade model

**Files:**
- Create: `src/PositionTracker.Core/EtaEstimator.cs`
- Test: `tests/PositionTracker.Core.Tests/EtaEstimatorTests.cs`

**Interfaces:**
- Consumes: `Course`, `ParticipantSnapshot`, `Sport`.
- Produces:
```csharp
public static class GradeModel { public static double SpeedFactor(Sport sport, double gradePercent); }
// cycling: clamp(1 - 0.06 * grade, 0.3, 2.0); running: clamp(1 - 0.03 * grade, 0.4, 1.5)
public sealed record EtaEstimate(DateTimeOffset Expected, DateTimeOffset Earliest, DateTimeOffset Latest, bool Approximate);
public static class EtaEstimator {
  public static EtaEstimate? Estimate(Course course, Sport sport, ParticipantSnapshot snapshot, DateTimeOffset now, double? targetTotalDistanceM = null);
}
```
The target defaults to `course.TotalLengthM`. Reference flat speed = `snapshot.MovingSpeedMps / SpeedFactor(sport, snapshot.GradePercent)`. Remaining time = sum over 100 m steps of step length / (flat speed × `SpeedFactor` at that step's grade). Spread: ±8 % of remaining time, at least ±60 s. `Approximate` is true when the participant has less than 5 minutes of moving time.

- [ ] **Step 1: Write the failing tests:** on a flat 12 500 m × 3 loop, a cyclist at 8 m/s with 10 000 m covered: expected arrival is `now + 27 500/8 s` within 1 s, `Earliest < Expected < Latest`; same position on a course whose remaining part climbs 5 % gives a later expected time than the flat course; at the finish distance the result is `now`; **Review Focus 4:** `MovingSpeedMps = 0` (standing still at the start) returns `null`, as does a speed below 0.3 m/s; `Approximate_true_with_4_minutes_of_moving_time_false_with_6`; target at checkpoint distance returns the time to that checkpoint.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~EtaEstimatorTests`. Expected: FAIL.
- [ ] **Step 3:** Implement as described.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: grade-aware ETA estimate with spread`.

---

### Task 9: Ranking, time gaps and sector colours

**Files:**
- Create: `src/PositionTracker.Core/Ranking.cs`, `src/PositionTracker.Core/SegmentBoard.cs`
- Modify: `src/PositionTracker.Core/ParticipantTracker.cs` (populate `ApplyResult.Completed`)
- Test: `tests/PositionTracker.Core.Tests/RankingTests.cs`, `tests/PositionTracker.Core.Tests/SegmentBoardTests.cs`, and additions to `ParticipantTrackerMotionTests.cs`

**Interfaces:**
- Consumes: `ProgressHistory`, `SegmentCompletion`.
- Produces:
```csharp
public sealed record RankInput(string ParticipantId, double TotalDistanceM, DateTimeOffset? LastFixTime, DateTimeOffset? FinishTime, ProgressHistory History);
public sealed record RankedParticipant(string ParticipantId, int Rank, TimeSpan? GapToLeader, TimeSpan? GapToAhead, double GapToLeaderM);
public static class Ranking { public static IReadOnlyList<RankedParticipant> Rank(IReadOnlyList<RankInput> inputs); }
public enum SegmentColour { Yellow, Green, Purple }
public sealed class SegmentBoard {
  public SegmentColour Record(string participantId, int lap, int segmentIndex, TimeSpan time); // idempotent per (participant, lap, segment): later call replaces the time
  public SegmentColour ColourOf(string participantId, int lap, int segmentIndex);
}
```
Finishers rank first, ordered by `FinishTime`; the rest by `TotalDistanceM` descending, ties broken by earlier time at that distance. Gap to leader = participant's `LastFixTime` minus the leader's `History.TimeAt(participant.TotalDistanceM)`; `null` when either is missing. `GapToAhead` is the same against the participant directly ahead. The leader has gaps of `TimeSpan.Zero`.

- [ ] **Step 1: Write the failing tests**
  - `RankingTests`: three participants at 3 000, 2 500 and 2 500 m with histories at constant speeds give ranks 1, 2, 3 (tie broken by earlier arrival at 2 500 m); the time gap of the second to the first equals (second's last time − leader's time at 2 500 m) within 1 ms; a finisher ranks above every unfinished participant, and two finishers are ordered by `FinishTime`; a participant with no fixes ranks last with `null` gaps.
  - `SegmentBoardTests`: a single participant's first result is `Green` (no purple with fewer than 2 participants); with two participants the faster is `Purple` and the slower, if it is its own best, `Green`; the slower participant's later worse result is `Yellow`; re-recording the same key replaces the time and does not create a second entry; a purple colour stays purple for the participant until a faster time appears.
  - Tracker additions: with two checkpoints at 3 000 m and 8 000 m on the loop, riding at 8 m/s emits `SegmentCompletion(Lap: 1, SegmentIndex: 0, Time: 375 s ±5)` exactly once, then index 1, then index 2 at the lap end, then `Lap: 2, SegmentIndex: 0`; a replayed late batch does not emit duplicates in the final snapshot sequence (assert uniqueness by `(Lap, SegmentIndex)` over all `Completed` lists of tracker B when fed the shuffled input, taking the final list from the full replay).
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~RankingTests|FullyQualifiedName~SegmentBoardTests|FullyQualifiedName~ParticipantTrackerMotionTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Segment boundary crossing is detected when total distance passes `(lap-1)·lapLength + SegmentBoundariesM[i+1]`; the segment time is the interpolated crossing time (`History.TimeAt`) minus the previous boundary's crossing time (the start time for lap 1 segment 0).
- [ ] **Step 4:** Rerun the filter. Expected: PASS.
- [ ] **Step 5:** Commit `feat: ranking with interpolated gaps and sector colours`.

---

### Task 10: Event runtime and ingest

**Files:**
- Create: `src/PositionTracker.Core/Runtime/EventSettings.cs`, `src/PositionTracker.Core/Runtime/LiveView.cs`, `src/PositionTracker.Core/Runtime/EventRuntime.cs`
- Test: `tests/PositionTracker.Core.Tests/EventRuntimeTests.cs`

**Interfaces:**
- Consumes: Tasks 2 to 9.
- Produces:
```csharp
public sealed record EventSettings(
  string EventId, string Name, Sport Sport, DateTimeOffset StartTime, TimeSpan SpectatorDelay,
  bool IsSimulated, TrackerOptions Tracker);
public sealed record ParticipantInfo(string Id, string Bib, string Name, string? Nickname, string Category,
  bool ShowNameToSpectators, bool IsMinor);
public enum ParticipantAction { Sos, SosCleared, Pause, Resume, Dnf }
public sealed record IngestResult(int Accepted, int Rejected);
public enum AlarmKind { Sos, Stopped, OffCourse, Offline, LowBattery }          // LowBattery under 20 %
public sealed record Alarm(AlarmKind Kind, DateTimeOffset Since);
public sealed record ParticipantView(
  string ParticipantId, string Bib, string DisplayName, string Category, int? Rank, int Lap,
  double TotalDistanceM, double LapProgressM, double? Lat, double? Lon,
  double SpeedMps, double AvgSpeedMps, double? PaceSecPerKm, double AscentM, double DescentM, double GradePercent,
  ParticipantState State, TimeSpan? GapToLeader, TimeSpan? GapToAhead, EtaEstimate? Eta,
  IReadOnlyList<SegmentResult> Segments,
  double? BatteryPct, double? GpsAccuracyM, int QueueLength, TimeSpan? LastSignalAgo, IReadOnlyList<Alarm> Alarms);
public sealed record SegmentResult(int Lap, int SegmentIndex, TimeSpan Time, SegmentColour Colour);
public sealed record LiveView(DateTimeOffset At, string EventId, IReadOnlyList<ParticipantView> Participants,
  int CountLive, int CountDelayed, int CountOffline, int CountDnf);
public sealed class EventRuntime {
  public EventRuntime(EventSettings settings, Course course, TimeProvider clock);
  public EventSettings Settings { get; }  public Course Course { get; }
  public void AddParticipant(ParticipantInfo info);
  public IngestResult Ingest(string participantId, PositionBatch batch);   // throws KeyNotFoundException for unknown id
  public void Act(string participantId, ParticipantAction action);
  public LiveView BuildLiveView();                                         // organizer view: all fields
}
```
`DisplayName` in `BuildLiveView` is the full name; the spectator projection (Task 13) replaces it. `Eta` is for the finish. Counts: Live includes `Live`, `Stopped`, `OffCourse`, `Sos`. Use a single lock for all mutation and view building.

- [ ] **Step 1: Write the failing tests** (cycling event, loop course, `FakeTimeProvider`):
  - `Ingest_batch_of_three_good_fixes_returns_Accepted_3`; one fix with 80 m accuracy gives `Accepted 2, Rejected 1`.
  - `Unknown_participant_throws_KeyNotFoundException`.
  - `Two_participants_get_ranks_1_and_2_and_the_gap_is_set`.
  - `LowBattery_alarm_when_battery_19_percent_and_none_at_20`.
  - `Sos_action_adds_Sos_alarm_and_state_and_SosCleared_removes_both`.
  - `Offline_alarm_after_121_seconds_and_counts_updated`.
  - `Segment_results_appear_with_colours_after_checkpoint_crossing`.
  - `Participant_without_fixes_has_null_rank_and_NotStarted_or_state_from_start_time`.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~EventRuntimeTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. `Ingest` sorts the batch by time, applies each fix to the participant's tracker, records completions into a shared `SegmentBoard`, and stores the latest `DeviceStatus`. `BuildLiveView` takes snapshots at `clock.GetUtcNow()`, ranks via `Ranking.Rank`, computes ETA, and derives alarms from state and battery.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: event runtime with ingest, alarms and live view`.

---

### Task 11: Virtual rider generator

**Files:**
- Create: `src/PositionTracker.Core/Simulation/VirtualRider.cs`
- Test: `tests/PositionTracker.Core.Tests/VirtualRiderTests.cs`

**Interfaces:**
- Consumes: `Course`, `GradeModel`, `PositionFix`.
- Produces:
```csharp
public sealed record VirtualRiderProfile(
  string Id, double BaseSpeedMps, double SpeedJitter, double GpsNoiseM, int Seed,
  IReadOnlyList<(TimeSpan From, TimeSpan To)> Dropouts,
  TimeSpan? SosAt, TimeSpan? OffCourseAt, TimeSpan? OffCourseFor);
public sealed record GeneratedFix(PositionFix Fix, DateTimeOffset DeliverAt);
public static class VirtualRider {
  public static IEnumerable<GeneratedFix> Generate(Course course, Sport sport, VirtualRiderProfile profile,
    DateTimeOffset start, TimeSpan step);   // runs until total distance reaches course.TotalLengthM
}
```
Speed at each step = `BaseSpeedMps × SpeedFactor(sport, grade) × (1 + jitter)` from a seeded `Random`. Fixes inside a dropout have `DeliverAt` equal to the dropout end (queued on the phone); others deliver at their own time. The off-course excursion offsets the position 150 m sideways for `OffCourseFor`. GPS noise is Gaussian with the given sigma.

- [ ] **Step 1: Write the failing tests:** same seed gives identical sequences, different seeds differ; with `GpsNoiseM = 0` and `SpeedJitter = 0` on a flat loop the last fix is at total length and elapsed time is `TotalLengthM / BaseSpeedMps` within one step; a dropout of 60 s to 120 s yields fixes whose `DeliverAt` is the dropout end and whose `Fix.Time` is earlier; the off-course window produces fixes 150 m ± noise from the course; steps are exactly `step` apart.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~VirtualRiderTests`. Expected: FAIL.
- [ ] **Step 3:** Implement as described.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: deterministic virtual rider generator`.

---

### Task 12: Api: tokens, ingest, actions, rate limit

**Files:**
- Create: `src/PositionTracker.Api/Events/EventRegistry.cs`, `src/PositionTracker.Api/Auth/TokenService.cs`, `src/PositionTracker.Api/Endpoints/IngestEndpoints.cs`
- Modify: `src/PositionTracker.Api/Program.cs` (add `public partial class Program`, services, endpoints)
- Test: `tests/PositionTracker.Api.Tests/IngestEndpointTests.cs`, `tests/PositionTracker.Api.Tests/ApiFactory.cs`

**Interfaces:**
- Consumes: `EventRuntime`.
- Produces:
```csharp
public sealed class TokenService {
  public string Issue(string eventId, string participantId);                  // returns the raw token once
  public (string EventId, string ParticipantId)? Resolve(string rawToken);    // compares SHA-256 hashes
  public void Revoke(string eventId, string participantId);
}
public sealed class EventRegistry {
  public EventRuntime Add(EventRuntime runtime);  public EventRuntime? Get(string eventId);
  public bool Remove(string eventId);  public IReadOnlyCollection<EventRuntime> All { get; }
}
// HTTP
// POST /api/events/{eventId}/positions   Authorization: Bearer <token>
//   body {"fixes":[{"t":"2026-11-07T10:00:05Z","lat":49.0,"lon":18.7,"acc":6.0}],"status":{"battery":71,"charging":false,"queue":0,"foreground":true}}
//   202 {"accepted":n,"rejected":m}
// POST /api/events/{eventId}/actions     Bearer; body {"action":"sos|sosCleared|pause|resume|dnf"}  204
```
`ApiFactory : WebApplicationFactory<Program>` registers a `FakeTimeProvider`, a seeded test event with two participants and their tokens, and the organizer key `test-key`.

- [ ] **Step 1: Write the failing tests:** no `Authorization` header gives 401; unknown token 401; token of another event 403; valid batch gives 202 with `accepted` 3; one fix with `acc` 80 gives `rejected` 1; malformed JSON 400; empty `fixes` 400; more than 500 fixes in one batch 413; the 61st request within a minute from the same token gives 429 and a different token is unaffected; `sos` action returns 204 and the runtime shows state `Sos`; unknown action 400; revoked token gives 401.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~IngestEndpointTests`. Expected: FAIL.
- [ ] **Step 3:** Implement with minimal APIs. Token format 32 random bytes, base64url. Rate limiting with `AddRateLimiter` partitioned by token (fixed window 60 per minute, 429). Request DTOs parse timestamps strictly as UTC ISO 8601.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5:** Commit `feat: authenticated ingest and participant actions`.

---

### Task 13: Live views, spectator projection, SignalR

**Files:**
- Create: `src/PositionTracker.Core/Runtime/SpectatorProjector.cs`, `src/PositionTracker.Core/Runtime/DelayedViewBuffer.cs`
- Create: `src/PositionTracker.Api/Live/LiveHub.cs`, `src/PositionTracker.Api/Live/LiveBroadcaster.cs`, `src/PositionTracker.Api/Endpoints/LiveEndpoints.cs`
- Modify: `src/PositionTracker.Api/Program.cs`
- Test: `tests/PositionTracker.Core.Tests/SpectatorProjectorTests.cs`, `tests/PositionTracker.Core.Tests/DelayedViewBufferTests.cs`, `tests/PositionTracker.Api.Tests/LiveEndpointTests.cs`

**Interfaces:**
- Consumes: `LiveView`, `ParticipantInfo`.
- Produces:
```csharp
public sealed record SpectatorParticipantView(
  string ParticipantId, string Bib, string DisplayName, string Category, int? Rank, int Lap, double TotalDistanceM,
  double? Lat, double? Lon, double SpeedMps, double? PaceSecPerKm, ParticipantState State,
  TimeSpan? GapToLeader, TimeSpan? GapToAhead, EtaEstimate? Eta, IReadOnlyList<SegmentResult> Segments);
public sealed record SpectatorView(DateTimeOffset At, string EventId, IReadOnlyList<SpectatorParticipantView> Participants);
public static class SpectatorProjector {
  public static SpectatorView Project(LiveView view, IReadOnlyDictionary<string, ParticipantInfo> participants);
}
public sealed class DelayedViewBuffer(TimeSpan delay) {
  public void Push(LiveView view);
  public LiveView? GetCurrent(DateTimeOffset now);   // newest view with At <= now - delay, null if none
}
// HTTP: GET /api/events/{eventId}/live   header X-Api-Key  -> LiveView JSON
//       GET /api/events/{eventId}/public                    -> SpectatorView JSON (delayed)
// SignalR /hubs/live:  JoinOrganizer(eventId, apiKey), JoinSpectator(eventId); server pushes "LiveUpdate"(LiveView) to
//   group org:{eventId} and "PublicUpdate"(SpectatorView) to group spec:{eventId}
```
`DisplayName` for spectators is the nickname when `ShowNameToSpectators` is true and the participant is not a minor and a nickname exists, otherwise the full name when allowed, otherwise `"#" + Bib`. The projection drops battery, accuracy, queue length, alarms and the ascent/descent fields. `LiveBroadcaster : BackgroundService` runs every `Live:IntervalMs` (default 1500) for each non-removed event: build the view, push it to `DelayedViewBuffer`, send to the hub groups.

- [ ] **Step 1: Write the failing tests:** projector hides a participant's name when `ShowNameToSpectators` is false (display `#21`), always hides it for minors, prefers nickname; the projected JSON contains none of the strings `battery`, `gpsAccuracy`, `queue`, `alarms`; buffer returns null until a view is 30 s old, returns the newest view not newer than `now − delay`, and discards views older than twice the delay when pushing; `GET /live` without key 401, with key 200; `GET /public` returns the view from 30 s earlier on a fake clock (advance 31 s after the first push); SignalR test: connect `HubConnection` through the test server, `JoinSpectator`, set `Live:IntervalMs` to 100 in the factory, and receive `PublicUpdate` within 3 s; `JoinOrganizer` with a wrong key throws `HubException`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~SpectatorProjectorTests|FullyQualifiedName~DelayedViewBufferTests|FullyQualifiedName~LiveEndpointTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Add `Microsoft.AspNetCore.SignalR.Client` to `Api.Tests`. Configure JSON enums as strings.
- [ ] **Step 4:** Rerun the filter. Expected: PASS.
- [ ] **Step 5:** Commit `feat: spectator projection, delayed buffer, SignalR live updates`.

---

### Task 14: Demo simulation and end-to-end test

**Files:**
- Create: `src/PositionTracker.Api/Simulation/SimulationService.cs`, `src/PositionTracker.Api/Endpoints/DemoEndpoints.cs`, `src/PositionTracker.Api/Simulation/ScaledTimeProvider.cs`
- Modify: `src/PositionTracker.Api/Program.cs`
- Test: `tests/PositionTracker.Api.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `VirtualRider`, `EventRegistry`, `TokenService`, `EventRuntime`.
- Produces:
```csharp
public sealed record DemoRequest(int Participants = 30, double TimeScale = 20, int Laps = 3, string? GpxBase64 = null, Sport Sport = Sport.Cycling);
public sealed record DemoResponse(string EventId, string OrganizerUrl, string PublicUrl);
public sealed class SimulationService {
  public DemoResponse Start(DemoRequest request);        // creates a simulated event (IsSimulated = true) and starts feeding it
  public void Step(string eventId, TimeSpan simulatedTime);   // test hook: advance without a background loop
  public bool Stop(string eventId);                       // removes the event and its tokens
}
// POST /api/demo/events  (X-Api-Key)  body DemoRequest -> DemoResponse
// DELETE /api/demo/events/{eventId}   (X-Api-Key)      -> 204
```
`ScaledTimeProvider` returns `start + (real elapsed × TimeScale)` so the runtime and the dashboards see accelerated time. Profiles for N participants: base speeds spread evenly between 6 and 10 m/s cycling (3 and 4.5 m/s running), 5 % jitter, GPS noise 5 m; exactly one participant gets `SosAt` at 40 minutes, one gets an off-course excursion of 120 s starting at 25 minutes, two get a 4-minute dropout starting at 30 minutes. Default course when no GPX is given: `Course` built from a generated 12 500 m loop with checkpoints at 3 000 m and 8 000 m. Simulated events are created through `EventRegistry` like real ones and are listed only for requests carrying the organizer key. `GET /api/demo/events` (key) lists them.

- [ ] **Step 1: Write the failing tests** (use a manual step API with `FakeTimeProvider`, not wall-clock sleeps):
  - `Start_creates_simulated_event_with_30_participants_and_tokens_that_ingest` (HTTP ingest of one generated fix returns 202).
  - `After_stepping_60_minutes_ranks_are_1_to_N_without_gaps_or_duplicates`.
  - `Exactly_one_participant_is_in_Sos_and_has_a_Sos_alarm`.
  - `The_off_course_participant_was_OffCourse_at_some_step_and_is_not_OffCourse_at_minute_40`.
  - `Dropout_participants_were_Offline_during_the_dropout_and_Live_after_delivery_of_queued_fixes_and_their_final_distance_matches_a_participant_without_dropout_with_the_same_profile_within_50_m` (late-batch consistency end to end).
  - `Stop_removes_event_and_revokes_tokens` (later ingest gives 404/401).
  - `Demo_endpoints_require_the_organizer_key`.
  - `Public_view_never_contains_battery_or_alarms_for_simulated_events` (serialize and assert).
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~SimulationTests`. Expected: FAIL.
- [ ] **Step 3:** Implement `SimulationService` (generate all fixes up front via `VirtualRider.Generate`, group by `DeliverAt`, deliver due batches on `Step` and from a `BackgroundService` loop that calls `Step` every 500 ms using the scaled clock) and the demo endpoints.
- [ ] **Step 4:** Run the whole suite: `dotnet test`. Expected: all tests PASS, build with 0 warnings.
- [ ] **Step 5:** Manual check: `dotnet run --project src/PositionTracker.Api`, then `POST /api/demo/events` with the configured key (for example with `Invoke-RestMethod`) and open `GET /api/events/{id}/live`; confirm participants move and ranks change between two requests a few seconds apart.
- [ ] **Step 6:** Commit `feat: demo simulation through the real ingest path`.

---

## Self-Review

**Spec coverage.** Section 5.1 and 5.2 data and derived metrics: Tasks 6, 8, 9, 10. 5.3 sector colours: Task 9. 5.4 states: Task 7. 5.5 visibility: Task 13. Section 7 course handling (GPX, matching, laps, checkpoints, off-course): Tasks 2, 3, 4, 6, 7. Section 8 ingest (batches, late points, token auth, batched fan-out): Tasks 6, 12, 13. Section 10 simulator: Tasks 11, 14. Section 11a decisions (laps, gaps by interpolation, stopped 3 min and pause, simulator flagged event): Tasks 6, 7, 9, 14. Not in this plan by design: accounts, registration, email, persistence (Plan 2), all UI, i18n, themes (Plans 3 to 5), deployment and HTTPS (Plan 5).

**Types.** `ParticipantSnapshot`, `ApplyResult`, `SegmentCompletion`, `SegmentColour`, `EtaEstimate`, `ParticipantView`, `LiveView`, `SpectatorView` are each defined once and used under the same names in later tasks. `GradeModel.SpeedFactor` is defined in Task 8 and consumed in Task 11. `FilterOptions` is part of `TrackerOptions` (Task 6).

**Open choices for the executor (not decided here):** exact JSON property naming beyond the examples (camelCase assumed), project folder placement for helper classes inside the named files, the Gaussian generator implementation in Task 11.
