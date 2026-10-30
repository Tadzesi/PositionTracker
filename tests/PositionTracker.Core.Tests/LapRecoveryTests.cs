using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

/// <summary>
/// Whole laps ridden during a signal gap (no fixes at all) on loops shorter than the reach of the gap.
/// </summary>
public class LapRecoveryTests
{
    private const double MetresPerDegLat = 111_195;
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static Course ShortLoop(double lapM, int laps) => Course.Create(TestCourses.Loop(lapM, Math.Min(25, lapM / 80)), laps);

    private static PositionFix FixAt(Course course, double totalM, DateTimeOffset time)
    {
        var p = course.PointAt(totalM % course.LapLengthM);
        return new PositionFix(time, p.Lat, p.Lon, 5);
    }

    /// <summary>
    /// Fixes every 5 s at constant speed from T0 until the course is done (plus a few fixes past the finish),
    /// with no fixes at all strictly inside (<paramref name="gapFromS"/>, <paramref name="gapToS"/>).
    /// </summary>
    private static List<PositionFix> RideWithGap(Course course, double speedMps, int gapFromS, int gapToS)
    {
        var fixes = new List<PositionFix>();
        var endS = (int)Math.Ceiling(course.TotalLengthM / speedMps) + 20;
        for (var s = 0; s <= endS; s += 5)
        {
            if (s > gapFromS && s < gapToS) continue;
            fixes.Add(FixAt(course, Math.Min(s * speedMps, course.TotalLengthM + 15), T0.AddSeconds(s)));
        }
        if (fixes.All(f => f.Time != T0.AddSeconds(gapToS)))
            throw new InvalidOperationException("The gap must end on a 5 s step.");
        return fixes;
    }

    private static ParticipantTracker Track(Course course, Sport sport, IEnumerable<PositionFix> fixes)
    {
        var tracker = new ParticipantTracker(course, sport, T0);
        foreach (var f in fixes) tracker.Apply(f);
        return tracker;
    }

    /// <summary>Total distance right after the first fix that ends the gap.</summary>
    private static double TotalAfterGap(Course course, Sport sport, List<PositionFix> fixes, int gapToS)
    {
        var tracker = new ParticipantTracker(course, sport, T0);
        foreach (var f in fixes.Where(f => f.Time <= T0.AddSeconds(gapToS))) tracker.Apply(f);
        return tracker.GetSnapshot(T0.AddSeconds(gapToS)).TotalDistanceM;
    }

    private static void AssertRecovers(Course course, Sport sport, double speedMps, int gapFromS, int gapToS)
    {
        var fixes = RideWithGap(course, speedMps, gapFromS, gapToS);

        var afterGap = TotalAfterGap(course, sport, fixes, gapToS);
        Assert.InRange(afterGap, speedMps * gapToS - 50, speedMps * gapToS + 50);

        var tracker = Track(course, sport, fixes);
        var end = fixes[^1].Time;
        var s = tracker.GetSnapshot(end);
        Assert.Equal(ParticipantState.Finished, s.State);
        Assert.Equal(course.TotalLengthM, s.TotalDistanceM);
        Assert.Equal(course.Laps, s.Lap);
        var expectedFinish = T0.AddSeconds(course.TotalLengthM / speedMps);
        Assert.InRange(s.FinishTime!.Value, expectedFinish.AddSeconds(-5), expectedFinish.AddSeconds(5));
    }

    [Fact]
    public void Cyclist_on_2km_x5_with_a_4_minute_gap_finishes_at_the_expected_time() =>
        AssertRecovers(ShortLoop(2_000, 5), Sport.Cycling, 8, 300, 540);

    [Fact]
    public void Runner_on_a_400m_track_with_a_60s_gap_finishes() =>
        AssertRecovers(ShortLoop(400, 10), Sport.Running, 4, 300, 360);

    [Fact]
    public void Runner_on_1km_x10_with_a_3_minute_gap_finishes() =>
        AssertRecovers(ShortLoop(1_000, 10), Sport.Running, 3.5, 600, 780);

    [Fact]
    public void Cyclist_on_5km_x4_with_a_10_minute_gap_finishes() =>
        AssertRecovers(ShortLoop(5_000, 4), Sport.Cycling, 8, 800, 1_400);

    [Fact]
    public void Late_delivery_of_the_fixes_around_a_gap_gives_the_identical_snapshot()
    {
        var course = ShortLoop(2_000, 5);
        var fixes = RideWithGap(course, 8, 300, 540);
        var ordered = Track(course, Sport.Cycling, fixes);

        var late = new ParticipantTracker(course, Sport.Cycling, T0);
        late.ApplyMany(fixes.Skip(120).ToList());
        late.ApplyMany(fixes.Take(60).Reverse().ToList());
        late.ApplyMany(fixes.Skip(60).Take(60).ToList());

        var end = fixes[^1].Time;
        Assert.Equal(ParticipantState.Finished, ordered.GetSnapshot(end).State);
        Assert.Equal(ordered.GetSnapshot(end), late.GetSnapshot(end));
    }

    [Fact]
    public void Laps_inferred_flag_is_set_after_a_gap_that_chose_whole_laps_and_cleared_when_not_needed()
    {
        var course = ShortLoop(2_000, 5);
        var full = RideWithGap(course, 8, 0, 5); // no gap at all
        var withGap = RideWithGap(course, 8, 300, 540);
        var end = full[^1].Time;

        Assert.False(Track(course, Sport.Cycling, full).GetSnapshot(end).LapsInferred);

        var tracker = Track(course, Sport.Cycling, withGap);
        var afterGap = tracker.GetSnapshot(end);
        Assert.True(afterGap.LapsInferred);
        Assert.Equal(ParticipantState.Finished, afterGap.State);

        // The fixes of the gap arrive late: the laps are observed, so the flag is cleared on the replay and the
        // snapshot equals the one of the complete ride.
        tracker.ApplyMany(full.Except(withGap).ToList());
        var filled = tracker.GetSnapshot(end);
        Assert.False(filled.LapsInferred);
        Assert.Equal(Track(course, Sport.Cycling, full).GetSnapshot(end), filled);
    }

    /// <summary>
    /// 2 km x 5, cyclist at 8 m/s, fixes every 5 s to 7 600 m (950 s), then no fixes for 4 minutes while the rider
    /// rides 30 s, stands 180 s and rides 30 s: the gap ends at 8 080 m, but the expected advance (1 920 m) picks
    /// the extra lap, which puts the rider past the finish.
    /// </summary>
    private static List<PositionFix> FalseLapAtTheFinish(Course course)
    {
        var fixes = new List<PositionFix>();
        for (var s = 0; s <= 950; s += 5) fixes.Add(FixAt(course, 8.0 * s, T0.AddSeconds(s)));
        fixes.Add(FixAt(course, 8_080, T0.AddSeconds(1_190)));
        return fixes;
    }

    [Fact]
    public void Finished_from_an_inferred_lap_waits_for_a_confirming_fix_and_stays_not_finished_until_one_arrives()
    {
        var course = ShortLoop(2_000, 5);
        var settings = new EventSettings("evt-inferred", "Inferred", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false,
            TrackerOptions.Defaults(Sport.Cycling));
        var clock = new FakeTimeProvider(T0);
        var runtime = new EventRuntime(settings, course, clock);
        runtime.AddParticipant(new ParticipantInfo("r", "1", "Rider", null, "Open", true, false));
        var fixes = FalseLapAtTheFinish(course);
        foreach (var chunk in fixes.Chunk(6))
        {
            clock.SetUtcNow(chunk[^1].Time);
            runtime.Ingest("r", new PositionBatch(chunk, null));
        }

        var pending = runtime.BuildLiveView().Participants.Single();
        Assert.True(pending.LapsInferred);
        Assert.Equal(course.TotalLengthM, pending.TotalDistanceM); // the organizer sees the inferred distance
        Assert.Equal(ParticipantState.Live, pending.State);
        clock.Advance(TimeSpan.FromMinutes(10)); // silence: the alarm chain applies, not Finished
        Assert.Equal(ParticipantState.Offline, runtime.BuildLiveView().Participants.Single().State);

        // The flag is organizer-only.
        var spectator = SpectatorProjector.Project(runtime.BuildLiveView(), runtime.Participants);
        Assert.DoesNotContain("lapsInferred", JsonSerializer.Serialize(spectator, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Contains("lapsInferred", JsonSerializer.Serialize(runtime.BuildLiveView(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        // The same fixes delivered late and out of order give the identical snapshot, flag and pending finish included.
        var ordered = Track(course, Sport.Cycling, fixes);
        var late = new ParticipantTracker(course, Sport.Cycling, T0);
        late.ApplyMany(fixes.Skip(100).ToList());
        late.ApplyMany(fixes.Take(100).Reverse().ToList());
        var at = T0.AddSeconds(1_190);
        Assert.Null(ordered.GetSnapshot(at).FinishTime);
        Assert.Equal(ordered.GetSnapshot(at), late.GetSnapshot(at));

        // A race fix after the one that reached the finish confirms it; Finished then outranks the silence.
        runtime.Ingest("r", new PositionBatch([FixAt(course, 8_120, T0.AddSeconds(1_195))], null));
        var finished = runtime.BuildLiveView().Participants.Single();
        Assert.Equal(ParticipantState.Finished, finished.State);
        Assert.True(finished.LapsInferred);
        Assert.DoesNotContain(finished.Alarms, a => a.Kind == AlarmKind.Offline);
        Assert.Null(late.GetSnapshot(at).FinishTime);
        late.Apply(FixAt(course, 8_120, T0.AddSeconds(1_195)));
        Assert.NotNull(late.GetSnapshot(at).FinishTime);
    }

    [Fact]
    public void A_paused_rider_standing_through_a_gap_gains_no_lap()
    {
        // Pause at 500 m, no fixes for 4 minutes, resume on the spot: the expected advance excludes the pause.
        var course = ShortLoop(1_000, 5);
        var tracker = new ParticipantTracker(course, Sport.Cycling, T0);
        for (var s = 0; s <= 60; s += 5) tracker.Apply(FixAt(course, 500.0 * s / 60, T0.AddSeconds(s)));
        tracker.SetPaused(T0.AddSeconds(62), true);
        tracker.SetPaused(T0.AddSeconds(300), false);

        tracker.Apply(FixAt(course, 505, T0.AddSeconds(305)));
        var s1 = tracker.GetSnapshot(T0.AddSeconds(305));

        Assert.Equal(1, s1.Lap);
        Assert.InRange(s1.TotalDistanceM, 500, 510);
    }

    [Fact]
    public void Drift_behind_a_standing_rider_during_a_long_stop_on_a_400m_track_gains_no_lap()
    {
        // The drifted fixes (70 m behind) are refused, so the time since the last accepted match grows far beyond
        // a lap at the sport's max speed; the steady stream of fixes still shows the rider did not ride a lap.
        var course = ShortLoop(400, 10);
        var fixes = Enumerable.Range(0, 25).Select(k => FixAt(course, 4.0 * 5 * k, T0.AddSeconds(5 * k))).ToList();
        var t = fixes[^1].Time;
        for (var k = 1; k <= 60; k++) fixes.Add(FixAt(course, 480 - 60 - 10 * (k % 3), t = t.AddSeconds(5)));

        var s = Track(course, Sport.Running, fixes).GetSnapshot(t);

        Assert.Equal(2, s.Lap);
        Assert.InRange(s.TotalDistanceM, 475, 485);
    }

    [Fact]
    public void Three_minutes_off_course_on_a_1km_loop_then_returning_80m_behind_gains_no_lap()
    {
        // The first edge runs east from the start; south of it is away from the loop.
        var course = ShortLoop(1_000, 5);
        var fixes = Enumerable.Range(0, 13).Select(k => FixAt(course, 17.5 * k, T0.AddSeconds(5 * k))).ToList(); // 210 m
        var t = fixes[^1].Time;
        var anchor = course.PointAt(210);
        for (var k = 1; k <= 36; k++)
        {
            var south = Math.Min(150, 25 * k);
            fixes.Add(new PositionFix(t = t.AddSeconds(5), anchor.Lat - south / MetresPerDegLat, anchor.Lon, 5));
        }
        for (var d = 130.0; d <= 400; d += 17.5) fixes.Add(FixAt(course, d, t = t.AddSeconds(5)));

        var s = Track(course, Sport.Running, fixes).GetSnapshot(t);

        Assert.Equal(1, s.Lap);
        Assert.InRange(s.TotalDistanceM, 385, 405);
    }

    [Fact]
    public void Gap_long_enough_to_pass_the_finish_stops_at_the_finish()
    {
        // The phone is off for 20 minutes around the finish and comes back on with the rider standing at the line.
        var course = ShortLoop(2_000, 3);
        var fixes = Enumerable.Range(0, 140).Select(k => FixAt(course, 8.0 * 5 * k, T0.AddSeconds(5 * k))).ToList();
        var back = T0.AddSeconds(5 * 139 + 1_200);
        fixes.Add(FixAt(course, course.TotalLengthM + 5, back));

        var s = Track(course, Sport.Cycling, fixes).GetSnapshot(back);

        Assert.Equal(ParticipantState.Finished, s.State);
        Assert.Equal(course.TotalLengthM, s.TotalDistanceM);
        Assert.Equal(3, s.Lap);
    }

    /// <summary>Deterministic ride with ±5 % speed jitter and ±3 m GPS noise; returns the fixes and the true distance per fix.</summary>
    private static List<(PositionFix Fix, double TrueM)> NoisyRide(Course course, double speedMps, int seed, double untilS)
    {
        var rng = new Random(seed);
        var result = new List<(PositionFix, double)>();
        double dist = 0;
        for (var s = 0; s <= untilS && dist < course.TotalLengthM; s += 5)
        {
            var p = course.PointAt(dist % course.LapLengthM);
            var north = (rng.NextDouble() * 2 - 1) * 3;
            var east = (rng.NextDouble() * 2 - 1) * 3;
            var fix = new PositionFix(T0.AddSeconds(s),
                p.Lat + north / MetresPerDegLat,
                p.Lon + east / (MetresPerDegLat * Math.Cos(p.Lat * Math.PI / 180)), 5);
            result.Add((fix, dist));
            dist += speedMps * (1 + (rng.NextDouble() * 2 - 1) * 0.05) * 5;
        }
        return result;
    }

    public static TheoryData<double, Sport, double> LoopsAndSpeeds => new()
    {
        { 400, Sport.Running, 3.0 },
        { 400, Sport.Running, 5.0 },
        { 1_000, Sport.Running, 3.5 },
        { 1_000, Sport.Cycling, 7.0 },
        { 2_000, Sport.Cycling, 6.0 },
        { 2_000, Sport.Cycling, 10.0 },
        { 5_000, Sport.Cycling, 9.0 },
        { 12_500, Sport.Cycling, 8.0 },
    };

    [Theory]
    [MemberData(nameof(LoopsAndSpeeds))]
    public void Total_after_a_gap_stays_within_50m_of_the_true_distance(double lapM, Sport sport, double speedMps)
    {
        var course = ShortLoop(lapM, 30);
        var failures = new List<string>();
        foreach (var gapS in new[] { 30, 60, 120, 240, 420, 600 })
        {
            for (var seed = 1; seed <= 3; seed++)
            {
                var ride = NoisyRide(course, speedMps, seed * 31 + gapS, 900 + gapS + 60);
                const int gapFromS = 900;
                var gapToS = gapFromS + gapS;
                var kept = ride.Where(r => r.Fix.Time <= T0.AddSeconds(gapFromS) || r.Fix.Time >= T0.AddSeconds(gapToS)).ToList();
                var tracker = new ParticipantTracker(course, sport, T0);
                foreach (var (fix, _) in kept.Where(r => r.Fix.Time <= T0.AddSeconds(gapToS))) tracker.Apply(fix);

                var trueM = kept.Single(r => r.Fix.Time == T0.AddSeconds(gapToS)).TrueM;
                var total = tracker.GetSnapshot(T0.AddSeconds(gapToS)).TotalDistanceM;
                if (Math.Abs(total - trueM) > 50) failures.Add($"gap {gapS} s seed {seed}: total {total:F0} true {trueM:F0}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void Virtual_rider_losing_fixes_for_5_minutes_on_a_short_loop_finishes_through_EventRuntime()
    {
        // The phone loses its fixes (not queued) from 10 to 15 minutes; 2 km loop, 4 laps.
        var course = Course.Create(TestCourses.Loop(2_000, 25), 4);
        var settings = new EventSettings("evt-gap", "Gap", Sport.Cycling, T0, TimeSpan.FromSeconds(30), true,
            TrackerOptions.Defaults(Sport.Cycling));
        var clock = new FakeTimeProvider(T0);
        var runtime = new EventRuntime(settings, course, clock);
        runtime.AddParticipant(new ParticipantInfo("r", "1", "Rider", null, "Open", true, false));
        var profile = new VirtualRiderProfile("r", 8, 0.05, 4, 77, [], null, null, null);
        var fixes = VirtualRider.Generate(course, Sport.Cycling, profile, T0, TimeSpan.FromSeconds(5))
            .Select(g => g.Fix)
            .Where(f => f.Time < T0.AddMinutes(10) || f.Time >= T0.AddMinutes(15))
            .ToList();

        foreach (var chunk in fixes.Chunk(6))
        {
            clock.SetUtcNow(chunk[^1].Time);
            runtime.Ingest("r", new PositionBatch(chunk, null));
        }

        var view = runtime.BuildLiveView().Participants.Single();
        Assert.Equal(ParticipantState.Finished, view.State);
        Assert.Equal(course.TotalLengthM, view.TotalDistanceM);
        Assert.Equal(4, view.Lap);
    }
}
