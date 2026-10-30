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
    public void Backtracking_beyond_the_window_does_not_add_a_lap_via_full_lap_search()
    {
        // Ride to 3 000 m, turn and ride back to 2 400 m: the fixes fall behind the backward window, become
        // unmatched and trigger the full-lap search, whose only image lies almost a whole lap ahead.
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
}
