using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class ParticipantTrackerStateTests
{
    private const double MetresPerDegLat = 111_195;
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Flat loop of 12 500 m, 3 laps; the first edge runs east from the start.</summary>
    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3);

    private static PositionFix FixAt(double totalM, DateTimeOffset time) => At(Loop.PointAt(totalM % Loop.LapLengthM), time);

    private static PositionFix At(GeoPoint p, DateTimeOffset time) => new(time, p.Lat, p.Lon, 5);

    private static GeoPoint Offset(GeoPoint p, double northM, double eastM = 0) =>
        new(p.Lat + northM / MetresPerDegLat, p.Lon + eastM / (MetresPerDegLat * Math.Cos(p.Lat * Math.PI / 180)));

    /// <summary>Fixes every 5 s at 8 m/s along the course from T0 and 0 m.</summary>
    private static List<PositionFix> Ride(double untilM)
    {
        var fixes = new List<PositionFix>();
        for (var k = 0; k * 40 <= untilM; k++) fixes.Add(FixAt(k * 40, T0.AddSeconds(5 * k)));
        return fixes;
    }

    /// <summary>8 m/s for 100 s (800 m), then 0.1 m/s for <paramref name="slowS"/> seconds; the slow part starts at T0 + 100 s.</summary>
    private static List<PositionFix> RideThenCrawl(int slowS)
    {
        var fixes = Ride(800);
        for (var s = 5; s <= slowS; s += 5) fixes.Add(FixAt(800 + 0.1 * s, SlowStart.AddSeconds(s)));
        return fixes;
    }

    private static readonly DateTimeOffset SlowStart = T0.AddSeconds(100);

    private static ParticipantTracker Track(IEnumerable<PositionFix> fixes)
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        foreach (var f in fixes) tracker.Apply(f);
        return tracker;
    }

    private static ParticipantState StateAfter(ParticipantTracker tracker, double seconds) =>
        tracker.GetSnapshot(tracker.GetSnapshot(T0).LastFixTime!.Value.AddSeconds(seconds)).State;

    [Fact]
    public void Before_start_time_and_without_fixes_is_NotStarted()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        Assert.Equal(ParticipantState.NotStarted, tracker.GetSnapshot(T0.AddMinutes(-5)).State);
        Assert.Equal(ParticipantState.NotStarted, tracker.GetSnapshot(T0.AddMinutes(30)).State);

        // A warm-up fix before the start is not a race fix: still NotStarted, never Offline.
        tracker.Apply(FixAt(100, T0.AddMinutes(-1)));
        Assert.Equal(ParticipantState.NotStarted, tracker.GetSnapshot(T0.AddMinutes(30)).State);
    }

    [Fact]
    public void Fix_14s_ago_is_Live() => Assert.Equal(ParticipantState.Live, StateAfter(Track(Ride(800)), 14));

    [Fact]
    public void Fix_20s_ago_is_Delayed() => Assert.Equal(ParticipantState.Delayed, StateAfter(Track(Ride(800)), 20));

    [Fact]
    public void Fix_121s_ago_is_Offline() => Assert.Equal(ParticipantState.Offline, StateAfter(Track(Ride(800)), 121));

    [Theory]
    [InlineData(14.9, ParticipantState.Live)]
    [InlineData(15.0, ParticipantState.Live)]
    [InlineData(15.1, ParticipantState.Delayed)]
    [InlineData(119.9, ParticipantState.Delayed)]
    [InlineData(120.0, ParticipantState.Delayed)]
    [InlineData(120.1, ParticipantState.Offline)]
    public void Silence_thresholds_are_exclusive(double seconds, ParticipantState expected) =>
        Assert.Equal(expected, StateAfter(Track(Ride(800)), seconds));

    [Fact]
    public void Speed_0_1mps_for_3_minutes_is_Stopped()
    {
        var fixes = RideThenCrawl(180);
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        foreach (var f in fixes.Take(fixes.Count - 2)) tracker.Apply(f);
        Assert.Equal(ParticipantState.Live, StateAfter(tracker, 0)); // 170 s slow

        foreach (var f in fixes.Skip(fixes.Count - 2)) tracker.Apply(f);
        var s = tracker.GetSnapshot(SlowStart.AddSeconds(180));
        Assert.Equal(ParticipantState.Stopped, s.State);
        Assert.Equal(TimeSpan.FromSeconds(180), s.StoppedTime);
        Assert.Equal(TimeSpan.FromSeconds(100), s.MovingTime);
    }

    [Fact]
    public void Same_with_pause_is_not_Stopped_and_excludes_paused_time_from_StoppedTime_and_MovingTime()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.SetPaused(SlowStart, true);
        foreach (var f in RideThenCrawl(240)) tracker.Apply(f);

        var s = tracker.GetSnapshot(SlowStart.AddSeconds(240));
        Assert.Equal(ParticipantState.Live, s.State);
        Assert.Equal(TimeSpan.Zero, s.StoppedTime);
        Assert.Equal(TimeSpan.FromSeconds(100), s.MovingTime);
    }

    [Fact]
    public void Pause_spanning_part_of_a_stop_counts_only_the_unpaused_part()
    {
        var tracker = Track(RideThenCrawl(180));
        tracker.SetPaused(SlowStart.AddSeconds(62.5), true);
        tracker.SetPaused(SlowStart.AddSeconds(122.5), false);

        var s = tracker.GetSnapshot(SlowStart.AddSeconds(180));
        Assert.Equal(TimeSpan.FromSeconds(120), s.StoppedTime);
        Assert.Equal(TimeSpan.FromSeconds(100), s.MovingTime);
        // Only 120 s of unpaused stop so far.
        Assert.Equal(ParticipantState.Live, s.State);

        // Three minutes of unpaused stop.
        for (var t = 185; t <= 240; t += 5) tracker.Apply(FixAt(800 + 0.1 * t, SlowStart.AddSeconds(t)));
        s = tracker.GetSnapshot(SlowStart.AddSeconds(240));
        Assert.Equal(ParticipantState.Stopped, s.State);
        Assert.Equal(TimeSpan.FromSeconds(180), s.StoppedTime);
    }

    [Fact]
    public void Standing_with_5m_gps_jitter_is_Stopped()
    {
        var tracker = Track(StandWithJitter(rejectSome: false));
        Assert.Equal(ParticipantState.Stopped, StateAfter(tracker, 0));
        // Per-interval accounting still sees the odd jitter step to a new high-water mark as moving.
        Assert.InRange(tracker.GetSnapshot(T0).StoppedTime, TimeSpan.FromSeconds(220), TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void Standing_with_5m_gps_jitter_and_partly_rejected_fixes_is_Stopped()
    {
        // Every 4th fix is a 300 m spike (rejected by the speed filter) and every 7th lies 120 m behind on
        // the course (accepted by the filter but left unmatched by the tracker).
        var fixes = StandWithJitter(rejectSome: true);
        var tracker = Track(fixes);
        Assert.Equal(ParticipantState.Stopped, StateAfter(tracker, 0));

        // The spike never became the last fix; the unmatched fixes broke the stopped-time chain.
        var s = tracker.GetSnapshot(T0);
        Assert.Equal(fixes[^2].Time, s.LastFixTime);
        Assert.True(s.StoppedTime < TimeSpan.FromSeconds(240), $"StoppedTime {s.StoppedTime}");
    }

    /// <summary>Ride to 2 000 m, then stand there for 4 minutes with ±5 m jitter in both axes.</summary>
    private static List<PositionFix> StandWithJitter(bool rejectSome)
    {
        var random = new Random(7);
        var fixes = Ride(2_000);
        var t = fixes[^1].Time;
        var spot = Loop.PointAt(2_000);
        for (var k = 1; k <= 48; k++)
        {
            t = t.AddSeconds(5);
            if (rejectSome && k % 4 == 0) fixes.Add(At(Offset(spot, 300), t));
            else if (rejectSome && k % 7 == 0) fixes.Add(FixAt(1_880, t));
            else fixes.Add(At(Offset(spot, random.NextDouble() * 10 - 5, random.NextDouble() * 10 - 5), t));
        }
        return fixes;
    }

    [Fact]
    public void Sos_overrides_Offline()
    {
        var tracker = Track(Ride(800));
        tracker.SetSos(true);
        Assert.Equal(ParticipantState.Sos, StateAfter(tracker, 300));
    }

    [Fact]
    public void Sos_cleared_returns_to_Offline()
    {
        var tracker = Track(Ride(800));
        tracker.SetSos(true);
        tracker.SetSos(false);
        Assert.Equal(ParticipantState.Offline, StateAfter(tracker, 300));
    }

    [Fact]
    public void Sos_set_before_any_fix_is_Sos()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.SetSos(true);
        Assert.Equal(ParticipantState.Sos, tracker.GetSnapshot(T0).State);

        tracker.Apply(FixAt(0, T0));
        Assert.Equal(ParticipantState.Sos, tracker.GetSnapshot(T0).State);
    }

    /// <summary>Ride to 2 000 m, step 75 m then 150 m south and stay 150 m off course for <paramref name="offS"/> seconds.</summary>
    private static List<PositionFix> RideOffCourse(int offS)
    {
        var fixes = Ride(2_000);
        var t = fixes[^1].Time;
        fixes.Add(At(Offset(Loop.PointAt(2_040), -75), t = t.AddSeconds(5)));
        for (var s = 0; s <= offS; s += 5) fixes.Add(At(Offset(Loop.PointAt(2_080 + 8 * s), -150), t.AddSeconds(5 + s)));
        return fixes;
    }

    [Fact]
    public void OffCourse_150m_for_20s_is_OffCourse()
    {
        var tracker = Track(RideOffCourse(20));
        Assert.Equal(ParticipantState.OffCourse, StateAfter(tracker, 0));

        // A matched fix back on the course ends the run.
        var last = tracker.GetSnapshot(T0).LastFixTime!.Value;
        tracker.Apply(FixAt(2_320, last.AddSeconds(10)));
        Assert.Equal(ParticipantState.Live, StateAfter(tracker, 0));
    }

    [Fact]
    public void OffCourse_150m_for_10s_is_Live()
    {
        var tracker = Track(RideOffCourse(10));
        Assert.Equal(ParticipantState.Live, StateAfter(tracker, 0));
        Assert.True(tracker.GetSnapshot(T0).OffCourseM > 100);
    }

    [Fact]
    public void OffCourse_150m_for_exactly_15s_is_Live() =>
        Assert.Equal(ParticipantState.Live, StateAfter(Track(RideOffCourse(15)), 0));

    private static readonly double[] BackwardDrift = { 60, 70, 80, 75, 65 };

    /// <summary>Ride to 2 000 m, then stand there while GPS drifts 60-80 m behind on the course for <paramref name="count"/> fixes.</summary>
    private static List<PositionFix> StandWithBackwardDrift(int count)
    {
        var fixes = Ride(2_000);
        var t = fixes[^1].Time;
        for (var k = 1; k <= count; k++) fixes.Add(FixAt(2_000 - BackwardDrift[k % BackwardDrift.Length], t = t.AddSeconds(5)));
        return fixes;
    }

    [Fact]
    public void Standing_rider_with_backward_drift_on_the_course_is_not_OffCourse_and_becomes_Stopped()
    {
        var fixes = StandWithBackwardDrift(37);
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        foreach (var f in fixes.Take(fixes.Count - 30)) tracker.Apply(f); // 35 s of drift
        Assert.Equal(ParticipantState.Live, StateAfter(tracker, 0));
        Assert.True(tracker.GetSnapshot(T0).OffCourseM < 100, $"OffCourseM {tracker.GetSnapshot(T0).OffCourseM}");

        foreach (var f in fixes.Skip(fixes.Count - 30)) tracker.Apply(f); // 185 s of drift
        Assert.Equal(ParticipantState.Stopped, StateAfter(tracker, 0));
    }

    [Fact]
    public void Backward_drift_state_is_identical_for_a_late_batch()
    {
        var fixes = StandWithBackwardDrift(37);
        var a = Track(fixes);
        var b = Track(fixes.Skip(45).Concat(fixes.Take(45).Reverse()));

        foreach (var now in new[] { fixes[50].Time, fixes[^1].Time, fixes[^1].Time.AddSeconds(30) })
            Assert.Equal(a.GetSnapshot(now), b.GetSnapshot(now));
        Assert.Equal(ParticipantState.Stopped, b.GetSnapshot(fixes[^1].Time).State);
    }

    [Theory]
    [InlineData(10, ParticipantState.Live)]
    [InlineData(20, ParticipantState.OffCourse)]
    public void Off_course_debounce_starts_at_the_first_fix_beyond_the_threshold_not_at_earlier_drift(int offS, ParticipantState expected)
    {
        // 15 s of drift 70 m behind (refused by the plausibility check, on course for the matcher), then
        // 120 m south of the course: the debounce runs from the first fix 120 m off.
        var fixes = Ride(2_000);
        var t = fixes[^1].Time;
        for (var k = 0; k < 4; k++) fixes.Add(FixAt(1_930, t = t.AddSeconds(5)));
        for (var s = 0; s <= offS; s += 5) fixes.Add(At(Offset(Loop.PointAt(1_930), -120), t.AddSeconds(5 + s)));

        var tracker = Track(fixes);
        Assert.True(tracker.GetSnapshot(T0).OffCourseM > 100);
        Assert.Equal(expected, StateAfter(tracker, 0));
    }

    [Fact]
    public void Pause_and_unpause_at_the_same_time_cancel_out()
    {
        var tracker = Track(RideThenCrawl(240));
        tracker.SetPaused(SlowStart.AddSeconds(50), true);
        tracker.SetPaused(SlowStart.AddSeconds(50), false);

        var s = tracker.GetSnapshot(SlowStart.AddSeconds(240));
        Assert.Equal(ParticipantState.Stopped, s.State);
        Assert.Equal(TimeSpan.FromSeconds(240), s.StoppedTime);
    }

    [Fact]
    public void Unpause_then_pause_at_the_same_time_leaves_the_participant_paused()
    {
        // The later call wins.
        var tracker = Track(RideThenCrawl(240));
        tracker.SetPaused(SlowStart.AddSeconds(50), false);
        tracker.SetPaused(SlowStart.AddSeconds(50), true);

        var s = tracker.GetSnapshot(SlowStart.AddSeconds(240));
        Assert.Equal(ParticipantState.Live, s.State);
        Assert.Equal(TimeSpan.FromSeconds(50), s.StoppedTime);
    }

    [Fact]
    public void Reaching_total_length_is_Finished_and_stays_Finished_with_no_further_fixes_even_after_10_minutes()
    {
        var tracker = Track(Ride(Loop.TotalLengthM + 40));
        var s = tracker.GetSnapshot(T0);
        Assert.NotNull(s.FinishTime);

        Assert.Equal(ParticipantState.Finished, StateAfter(tracker, 0));
        Assert.Equal(ParticipantState.Finished, StateAfter(tracker, 600));
    }

    [Fact]
    public void MarkDnf_is_Dnf_and_overrides_everything()
    {
        var tracker = Track(Ride(Loop.TotalLengthM + 40));
        var last = tracker.GetSnapshot(T0).LastFixTime!.Value;
        tracker.SetSos(true);
        tracker.MarkDnf(last.AddSeconds(30));

        Assert.Equal(ParticipantState.Finished, tracker.GetSnapshot(last.AddSeconds(29)).State);
        Assert.Equal(ParticipantState.Dnf, tracker.GetSnapshot(last.AddSeconds(30)).State);
        Assert.Equal(ParticipantState.Dnf, tracker.GetSnapshot(last.AddMinutes(10)).State);

        var notStarted = new ParticipantTracker(Loop, Sport.Cycling, T0);
        notStarted.MarkDnf(T0);
        Assert.Equal(ParticipantState.Dnf, notStarted.GetSnapshot(T0.AddMinutes(1)).State);
    }

    [Fact]
    public void State_and_setters_give_identical_snapshots_for_late_batches_and_any_call_order()
    {
        // Crawl with a pause inside it, ride on, then 20 s at 150 m off course.
        var fixes = RideThenCrawl(240);
        var t = fixes[^1].Time;
        var d = 824.0;
        for (var k = 0; k < 12; k++) fixes.Add(FixAt(d += 40, t = t.AddSeconds(5)));
        fixes.Add(At(Offset(Loop.PointAt(d += 40), -75), t = t.AddSeconds(5)));
        for (var k = 0; k < 5; k++) fixes.Add(At(Offset(Loop.PointAt(d += 40), -150), t = t.AddSeconds(5)));
        var pauseFrom = SlowStart.AddSeconds(42.5);
        var pauseTo = SlowStart.AddSeconds(97.5);

        var a = new ParticipantTracker(Loop, Sport.Cycling, T0);
        foreach (var f in fixes) a.Apply(f);
        a.SetPaused(pauseFrom, true);
        a.SetPaused(pauseTo, false);
        a.SetSos(true);

        var b = new ParticipantTracker(Loop, Sport.Cycling, T0);
        b.SetSos(true);
        b.SetPaused(pauseTo, false);
        foreach (var f in fixes.Skip(30)) b.Apply(f);
        b.SetPaused(pauseFrom, true);
        foreach (var f in fixes.Take(30).Reverse()) b.Apply(f);

        foreach (var now in new[] { T0, SlowStart.AddSeconds(60), SlowStart.AddSeconds(240), fixes[^1].Time, fixes[^1].Time.AddMinutes(5) })
            Assert.Equal(a.GetSnapshot(now), b.GetSnapshot(now));

        a.SetSos(false);
        b.SetSos(false);
        foreach (var now in new[] { SlowStart.AddSeconds(240), fixes[^1].Time })
            Assert.Equal(a.GetSnapshot(now), b.GetSnapshot(now));
        Assert.Equal(ParticipantState.OffCourse, a.GetSnapshot(fixes[^1].Time).State);
    }
}
