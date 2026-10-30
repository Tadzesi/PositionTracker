using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class ParticipantTrackerMotionTests
{
    private const double MetresPerDegLat = 111_195;
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Loop of 12 500 m, 3 laps, with a rolling profile so ascent is non-zero.</summary>
    private static readonly Course Loop = Course.Create(
        TestCourses.Loop(elevationAtM: d => 300 + 40 * Math.Sin(2 * Math.PI * d / 2_500)), 3);

    private static PositionFix FixAt(Course course, double totalM, DateTimeOffset time)
    {
        var p = course.PointAt(course.IsClosed ? totalM % course.LapLengthM : totalM);
        return new PositionFix(time, p.Lat, p.Lon, 5);
    }

    /// <summary>Fixes every <paramref name="intervalS"/> seconds at constant speed along the course, from T0 and 0 m.</summary>
    private static List<PositionFix> Ride(Course course, double speedMps, double untilM, double intervalS = 5)
    {
        var fixes = new List<PositionFix>();
        for (var k = 0; k * intervalS * speedMps <= untilM; k++)
            fixes.Add(FixAt(course, k * intervalS * speedMps, T0.AddSeconds(k * intervalS)));
        return fixes;
    }

    private static ParticipantTracker Track(Course course, Sport sport, IEnumerable<PositionFix> fixes)
    {
        var tracker = new ParticipantTracker(course, sport, T0);
        foreach (var f in fixes) tracker.Apply(f);
        return tracker;
    }

    private static ParticipantSnapshot Snapshot(ParticipantTracker t) => t.GetSnapshot(t.History.Last!.Value.Time);

    private static GeoPoint Offset(GeoPoint p, double northM) => new(p.Lat + northM / MetresPerDegLat, p.Lon);

    [Fact]
    public void Total_distance_after_1_000_s_is_8_000_m_within_50()
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 8_000)));

        Assert.InRange(s.TotalDistanceM, 7_950, 8_050);
        Assert.Equal(T0.AddSeconds(1_000), s.LastFixTime);
        Assert.Equal(ParticipantState.Live, s.State);
    }

    [Fact]
    public void Lap_is_2_after_13_000_m()
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 13_000)));

        Assert.Equal(2, s.Lap);
        Assert.InRange(s.TotalDistanceM, 12_950, 13_050);
    }

    [Fact]
    public void LapProgress_wraps()
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 13_000)));

        Assert.InRange(s.LapProgressM, 450, 550);
    }

    [Fact]
    public void MovingSpeed_is_8mps_within_0_3()
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 4_000)));

        Assert.InRange(s.MovingSpeedMps, 7.7, 8.3);
        Assert.InRange(s.SpeedMps, 7.7, 8.3);
        Assert.InRange(s.AvgSpeedMps, 7.7, 8.3);
        Assert.InRange(s.MaxSpeedMps, 7.7, 8.3);
        Assert.Equal(TimeSpan.FromSeconds(500), s.MovingTime);
        Assert.Equal(TimeSpan.Zero, s.StoppedTime);
    }

    [Fact]
    public void Pace_for_runner_at_3mps_is_333_sec_per_km_within_2()
    {
        var s = Snapshot(Track(Loop, Sport.Running, Ride(Loop, 3, 1_500)));

        Assert.NotNull(s.PaceSecPerKm);
        Assert.InRange(s.PaceSecPerKm!.Value, 331.3, 335.3);
    }

    [Fact]
    public void Ascent_comes_from_course_profile_not_gps()
    {
        // PositionFix carries no altitude at all; ascent must follow the course profile at the matched distance.
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 6_000)));

        Assert.True(s.AscentM > 0);
        Assert.Equal(Loop.AscentUpTo(s.TotalDistanceM), s.AscentM);
        Assert.Equal(Loop.DescentUpTo(s.TotalDistanceM), s.DescentM);
    }

    [Fact]
    public void FinishTime_is_set_when_total_reaches_TotalLength()
    {
        var fixes = Ride(Loop, 8, 38_500);
        var expected = T0.AddSeconds(fixes.Select((_, k) => k * 5.0).First(d => d * 8 >= Loop.TotalLengthM));

        var s = Snapshot(Track(Loop, Sport.Cycling, fixes));

        Assert.Equal(expected, s.FinishTime);
        Assert.Equal(Loop.TotalLengthM, s.TotalDistanceM);
        Assert.Equal(3, s.Lap);
    }

    [Fact]
    public void Jitter_across_start_finish_line_does_not_add_laps()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        for (var s = 0; s <= 60; s += 2)
            tracker.Apply(FixAt(Loop, s % 4 == 0 ? Loop.LapLengthM - 20 : 20, T0.AddSeconds(s)));

        var snap = Snapshot(tracker);
        Assert.Equal(1, snap.Lap);
        Assert.True(snap.TotalDistanceM < 100, $"Total {snap.TotalDistanceM}");
    }

    [Fact]
    public void Late_batch_gives_same_result_as_ordered_input()
    {
        var fixes = Ride(Loop, 8, 99 * 5 * 8);
        Assert.Equal(100, fixes.Count);

        var a = Snapshot(Track(Loop, Sport.Cycling, fixes));
        var b = Snapshot(Track(Loop, Sport.Cycling, fixes.Skip(40).Concat(fixes.Take(40))));

        Assert.Equal(a.TotalDistanceM, b.TotalDistanceM);
        Assert.Equal(a.MaxSpeedMps, b.MaxSpeedMps);
        Assert.Equal(a.MovingTime, b.MovingTime);
        Assert.Equal(a.AscentM, b.AscentM);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fixes_sharing_a_timestamp_resolve_the_same_regardless_of_arrival_order()
    {
        // Only one of two fixes with the same timestamp can be accepted; which one must not depend on arrival.
        var start = FixAt(Loop, 0, T0);
        var first = FixAt(Loop, 40, T0.AddSeconds(5));
        var second = FixAt(Loop, 45, T0.AddSeconds(5));
        var end = FixAt(Loop, 80, T0.AddSeconds(10));

        var a = Snapshot(Track(Loop, Sport.Cycling, new[] { start, first, second, end }));
        var b = Snapshot(Track(Loop, Sport.Cycling, new[] { start, second, first, end }));
        var c = Snapshot(Track(Loop, Sport.Cycling, new[] { end, second, start, first }));

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Two_fixes_with_same_timestamp_do_not_produce_NaN_or_infinity()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.Apply(FixAt(Loop, 100, T0));
        tracker.Apply(FixAt(Loop, 110, T0));
        var s = tracker.Apply(FixAt(Loop, 105, T0)).Snapshot;

        double?[] values =
        {
            s.LastLat, s.LastLon, s.LastAccuracyM, s.TotalDistanceM, s.LapProgressM, s.OffCourseM, s.SpeedMps,
            s.MovingSpeedMps, s.AvgSpeedMps, s.MaxSpeedMps, s.PaceSecPerKm, s.AscentM, s.DescentM, s.GradePercent,
        };
        Assert.All(values, v => Assert.True(v is null || double.IsFinite(v.Value), $"{v}"));
    }

    [Fact]
    public void Apply_returns_no_segment_completions_yet()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        var results = Ride(Loop, 8, 4_000).Select(tracker.Apply).ToList();

        Assert.All(results, r => Assert.Empty(r.Completed));
    }

    [Fact]
    public void Before_any_fix_the_snapshot_is_NotStarted_and_empty()
    {
        var s = new ParticipantTracker(Loop, Sport.Cycling, T0).GetSnapshot(T0.AddMinutes(5));

        Assert.Equal(ParticipantState.NotStarted, s.State);
        Assert.Null(s.LastFixTime);
        Assert.Equal(0, s.TotalDistanceM);
        Assert.Equal(1, s.Lap);
        Assert.Null(s.PaceSecPerKm);
    }

    [Theory]
    [InlineData(double.NaN, 18.7, 5.0)]
    [InlineData(49.0, double.PositiveInfinity, 5.0)]
    [InlineData(91.0, 18.7, 5.0)]
    [InlineData(49.0, -180.5, 5.0)]
    [InlineData(49.0, 18.7, double.NaN)]
    public void Invalid_fix_is_rejected_and_not_stored(double lat, double lon, double accuracy)
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        var s = tracker.Apply(new PositionFix(T0, lat, lon, accuracy)).Snapshot;
        Assert.Equal(ParticipantState.NotStarted, s.State);
        Assert.Null(tracker.History.Last);

        // A later valid fix is evaluated as the first one, unaffected by the rejected input.
        s = tracker.Apply(FixAt(Loop, 40, T0.AddSeconds(5))).Snapshot;
        Assert.Equal(ParticipantState.Live, s.State);
        Assert.InRange(s.TotalDistanceM, 35, 45);
    }

    [Fact]
    public void Unmatched_fix_keeps_progress_and_reports_OffCourseM()
    {
        var tracker = Track(Loop, Sport.Cycling, Ride(Loop, 8, 2_000));
        // 2 000 m lies on the first (eastbound) edge; 300 m south is away from the loop.
        var p = Offset(Loop.PointAt(2_040), -300);

        // 60 s after the last fix keeps the ~300 m move below the 30 m/s speed filter.
        var s = tracker.Apply(new PositionFix(T0.AddSeconds(310), p.Lat, p.Lon, 5)).Snapshot;

        Assert.InRange(s.TotalDistanceM, 1_995, 2_005);
        Assert.InRange(s.OffCourseM, 290, 310);
        Assert.Equal(T0.AddSeconds(310), s.LastFixTime);
    }

    [Fact]
    public void Dropout_beyond_the_default_window_is_reacquired_by_widening_with_elapsed_time()
    {
        // 240 s without fixes at 8 m/s: the rider is 1 920 m ahead, beyond the default 1 500 m window
        // and around the first corner, more than 100 m from its end.
        var fixes = Ride(Loop, 8, 2_000);
        fixes.Add(FixAt(Loop, 3_920, T0.AddSeconds(250 + 240)));

        var s = Snapshot(Track(Loop, Sport.Cycling, fixes));

        Assert.InRange(s.TotalDistanceM, 3_915, 3_925);
        Assert.InRange(s.OffCourseM, 0, 1);
    }

    [Fact]
    public void Backtracking_beyond_the_window_does_not_add_a_lap()
    {
        // Ride to 3 000 m, turn and ride back to 2 400 m: the fixes fall behind the backward window while the
        // forward window keeps growing; the only image of them lies almost a whole lap ahead.
        var fixes = Ride(Loop, 6, 3_000);
        var t = fixes[^1].Time;
        for (var d = 2_970.0; d >= 2_400; d -= 30) fixes.Add(FixAt(Loop, d, t = t.AddSeconds(5)));

        var s = Snapshot(Track(Loop, Sport.Cycling, fixes));

        Assert.Equal(1, s.Lap);
        Assert.InRange(s.TotalDistanceM, 2_995, 3_005);
        Assert.True(s.OffCourseM > 100, $"OffCourseM {s.OffCourseM}");
    }

    [Fact]
    public void Matched_progress_falling_at_out_and_back_hairpin_does_not_reduce_total()
    {
        // Out-and-back 2 × 5 000 m. A dropout straddles the turn: the fix after it lies on the return leg at
        // the same spot as the last outbound fix, so the matcher prefers the outbound image and then follows
        // it backwards (Matched=true with falling progress) unless the tracker rejects the regression.
        var course = Course.Create(TestCourses.OutAndBack(), 1);
        var fixes = Ride(course, 6, 4_890);
        var t = fixes[^1].Time.AddSeconds(37);
        for (var d = 5_110.0; d <= 6_000; d += 30, t = t.AddSeconds(5)) fixes.Add(FixAt(course, d, t));

        var tracker = new ParticipantTracker(course, Sport.Cycling, T0);
        var totals = fixes.Select(f => tracker.Apply(f).Snapshot.TotalDistanceM).ToList();

        Assert.All(totals.Zip(totals.Skip(1)), p => Assert.True(p.Second >= p.First, $"{p.First} -> {p.Second}"));
        var s = Snapshot(tracker);
        Assert.InRange(s.TotalDistanceM, 5_950, 6_000);
        Assert.InRange(s.LapProgressM, 5_950, 6_000);
    }

    private static readonly double[] BackwardDrift = { 60, 70, 80, 75, 65 };

    [Fact]
    public void Drift_60_to_80m_behind_the_start_line_on_a_short_loop_does_not_add_a_lap()
    {
        // On a 1 000 m loop the forward window spans the whole lap from the first fix, so drift behind the
        // anchor (outside the 50 m backward window) is matched almost a whole lap ahead.
        var shortLoop = Course.Create(TestCourses.Loop(1_000, 10), 3);
        var fixes = new List<PositionFix> { FixAt(shortLoop, 0, T0) };
        for (var k = 1; k <= 60; k++)
            fixes.Add(FixAt(shortLoop, shortLoop.LapLengthM - BackwardDrift[k % BackwardDrift.Length], T0.AddSeconds(5 * k)));

        var s = Snapshot(Track(shortLoop, Sport.Cycling, fixes));

        Assert.Equal(1, s.Lap);
        Assert.Equal(0, s.TotalDistanceM);
    }

    [Fact]
    public void Drift_60_to_80m_behind_a_standing_rider_on_a_short_loop_does_not_add_a_lap()
    {
        var shortLoop = Course.Create(TestCourses.Loop(1_000, 10), 3);
        var fixes = Ride(shortLoop, 5, 500);
        var t = fixes[^1].Time;
        for (var k = 1; k <= 60; k++)
            fixes.Add(FixAt(shortLoop, 500 - BackwardDrift[k % BackwardDrift.Length], t = t.AddSeconds(5)));

        var s = Snapshot(Track(shortLoop, Sport.Cycling, fixes));

        Assert.Equal(1, s.Lap);
        Assert.InRange(s.TotalDistanceM, 495, 505);
    }

    [Fact]
    public void Return_slightly_behind_after_8_minutes_off_course_does_not_add_a_lap()
    {
        // After 8 min off course (cycling) elapsed × max speed exceeds the 12 500 m lap, so the window spans the
        // whole lap and a return 80 m behind the anchor is matched almost a whole lap ahead.
        var fixes = Ride(Loop, 8, 3_000);
        var t = fixes[^1].Time;
        var anchor = Loop.PointAt(3_000);
        for (var south = 20; south <= 300; south += 20) fixes.Add(At(Offset(anchor, -south), t = t.AddSeconds(5)));
        for (var k = 0; k < 96; k++) fixes.Add(At(Offset(anchor, -300), t = t.AddSeconds(5)));
        var back = Loop.PointAt(2_920);
        for (var south = 280; south >= 0; south -= 20) fixes.Add(At(Offset(back, -south), t = t.AddSeconds(5)));
        for (var k = 0; k < 6; k++) fixes.Add(At(back, t = t.AddSeconds(5)));

        var tracker = Track(Loop, Sport.Cycling, fixes);
        var s = Snapshot(tracker);
        Assert.Equal(1, s.Lap);
        Assert.InRange(s.TotalDistanceM, 2_995, 3_005);

        // Riding on from there is picked up again once the rider passes the anchor.
        for (var d = 2_960.0; d <= 3_400; d += 40) tracker.Apply(FixAt(Loop, d, t = t.AddSeconds(5)));
        s = Snapshot(tracker);
        Assert.Equal(1, s.Lap);
        Assert.InRange(s.TotalDistanceM, 3_395, 3_405);
    }

    [Fact]
    public void Off_course_stretch_causes_no_speed_spike_and_no_stopped_time()
    {
        // 8 m/s along the first edge; between 2 040 and 2 480 m the fixes are 120 m south of the course.
        var fixes = new List<PositionFix>();
        for (var k = 0; k <= 75; k++)
        {
            var d = 40.0 * k;
            var t = T0.AddSeconds(5 * k);
            fixes.Add(d is > 2_000 and < 2_520 ? At(Offset(Loop.PointAt(d), -120), t) : FixAt(Loop, d, t));
        }

        var s = Snapshot(Track(Loop, Sport.Cycling, fixes));

        Assert.InRange(s.TotalDistanceM, 2_995, 3_005);
        Assert.InRange(s.MaxSpeedMps, 7.5, 8.5);
        Assert.True(s.MaxSpeedMps <= FilterOptions.For(Sport.Cycling).MaxSpeedMps);
        Assert.Equal(TimeSpan.Zero, s.StoppedTime);
        // 0-2 000 m and 2 520-3 000 m; the off-course interval and the step back onto the course are excluded.
        Assert.Equal(TimeSpan.FromSeconds(250 + 60), s.MovingTime);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(10.4)]
    public void Fixes_about_10s_apart_give_a_speed(double intervalS)
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 3_000, intervalS)));

        Assert.InRange(s.SpeedMps, 7.5, 8.5);
        Assert.InRange(s.MaxSpeedMps, 7.5, 8.5);
    }

    [Fact]
    public void Fixes_60s_apart_give_no_10s_speed_and_no_spike()
    {
        var s = Snapshot(Track(Loop, Sport.Cycling, Ride(Loop, 8, 4_800, 60)));

        Assert.InRange(s.TotalDistanceM, 4_795, 4_805);
        Assert.Equal(0, s.SpeedMps);
        Assert.Equal(0, s.MaxSpeedMps);
        Assert.InRange(s.MovingSpeedMps, 7.5, 8.5);
    }

    [Fact]
    public void Warm_up_fixes_before_the_start_only_update_the_last_fix()
    {
        // Warm-up 3 km into the course during the 5 minutes before the start.
        var warmUp = new List<PositionFix>();
        for (var k = 0; k < 60; k++) warmUp.Add(FixAt(Loop, 3_000 + 5 * k, T0.AddSeconds(-300 + 5 * k)));

        var tracker = Track(Loop, Sport.Cycling, warmUp);
        var s = tracker.GetSnapshot(T0);
        Assert.Equal(0, s.TotalDistanceM);
        Assert.Equal(1, s.Lap);
        Assert.Equal(TimeSpan.Zero, s.MovingTime);
        Assert.Equal(TimeSpan.Zero, s.StoppedTime);
        Assert.Null(tracker.History.Last);
        Assert.Equal(ParticipantState.NotStarted, s.State);
        Assert.Equal(warmUp[^1].Time, s.LastFixTime);
        Assert.Equal(warmUp[^1].Lat, s.LastLat);
        Assert.Equal(warmUp[^1].Lon, s.LastLon);

        // From the start the race counts from 0, unaffected by the warm-up position.
        var race = Ride(Loop, 8, 800);
        foreach (var f in race) tracker.Apply(f);
        s = Snapshot(tracker);
        Assert.InRange(s.TotalDistanceM, 795, 805);
        Assert.Equal(TimeSpan.FromSeconds(100), s.MovingTime);
        Assert.Equal(race[^1].Time, s.LastFixTime);

        // Warm-up delivered late gives the same result.
        Assert.Equal(s, Snapshot(Track(Loop, Sport.Cycling, race.Concat(warmUp))));
    }

    private static PositionFix At(GeoPoint p, DateTimeOffset time) => new(time, p.Lat, p.Lon, 5);
}
