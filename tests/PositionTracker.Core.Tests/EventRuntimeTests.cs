using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class EventRuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The 12 500 m loop, 3 laps, with checkpoints at 3 000 m and 8 000 m.</summary>
    private static readonly Course Loop = Course.Create(
        TestCourses.Loop(), 3, new[] { new Checkpoint("CP1", 3_000), new Checkpoint("CP2", 8_000) });

    private static readonly EventSettings Settings = new(
        "evt-1", "Test Ride", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false, TrackerOptions.Defaults(Sport.Cycling));

    private static (EventRuntime Runtime, FakeTimeProvider Clock) Create(params string[] ids)
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new EventRuntime(Settings, Loop, clock);
        foreach (var id in ids)
            runtime.AddParticipant(new ParticipantInfo(id, "B-" + id, "Name " + id, null, "Open", true, false));
        return (runtime, clock);
    }

    private static PositionFix FixAt(double totalM, DateTimeOffset time, double accuracyM = 5)
    {
        var p = Loop.PointAt(totalM % Loop.LapLengthM);
        return new PositionFix(time, p.Lat, p.Lon, accuracyM);
    }

    /// <summary>Fixes every 5 s at constant speed from T0 and 0 m, up to <paramref name="untilS"/> seconds.</summary>
    private static List<PositionFix> Ride(double speedMps, int untilS)
    {
        var fixes = new List<PositionFix>();
        for (var s = 0; s <= untilS; s += 5) fixes.Add(FixAt(s * speedMps, T0.AddSeconds(s)));
        return fixes;
    }

    private static PositionBatch Batch(IEnumerable<PositionFix> fixes, DeviceStatus? status = null) => new(fixes.ToList(), status);

    private static ParticipantView View(EventRuntime runtime, string id) =>
        runtime.BuildLiveView().Participants.Single(p => p.ParticipantId == id);

    private static string Json(LiveView view) => JsonSerializer.Serialize(view);

    [Fact]
    public void Ingest_batch_of_three_good_fixes_returns_Accepted_3()
    {
        var (runtime, _) = Create("a", "b");

        var good = runtime.Ingest("a", Batch(Ride(8, 10)));
        Assert.Equal(new IngestResult(3, 0), good);

        var fixes = Ride(8, 10);
        fixes[1] = fixes[1] with { AccuracyM = 80 };
        Assert.Equal(new IngestResult(2, 1), runtime.Ingest("b", Batch(fixes)));
    }

    [Fact]
    public void Unknown_participant_throws_KeyNotFoundException()
    {
        var (runtime, _) = Create("a");

        Assert.Throws<KeyNotFoundException>(() => runtime.Ingest("nobody", Batch(Ride(8, 10))));
        Assert.Throws<KeyNotFoundException>(() => runtime.Act("nobody", ParticipantAction.Sos));
    }

    [Fact]
    public void Two_participants_get_ranks_1_and_2_and_the_gap_is_set()
    {
        var (runtime, clock) = Create("fast", "slow");
        runtime.Ingest("fast", Batch(Ride(8, 100)));   // 800 m at T0 + 100 s
        runtime.Ingest("slow", Batch(Ride(6.4, 100))); // 640 m at T0 + 100 s; fast was there at T0 + 80 s
        clock.SetUtcNow(T0.AddSeconds(100));

        var view = runtime.BuildLiveView();

        Assert.Equal(new[] { "fast", "slow" }, view.Participants.Select(p => p.ParticipantId));
        var fast = view.Participants[0];
        var slow = view.Participants[1];
        Assert.Equal(1, fast.Rank);
        Assert.Equal(2, slow.Rank);
        Assert.Equal(TimeSpan.Zero, fast.GapToLeader);
        Assert.NotNull(slow.GapToLeader);
        Assert.InRange(slow.GapToLeader!.Value.TotalSeconds, 19, 21);
        Assert.Equal(slow.GapToLeader, slow.GapToAhead);
        Assert.Equal("Name fast", fast.DisplayName);
        Assert.NotNull(fast.Lat);
        Assert.NotNull(fast.Eta);
        Assert.Equal(TimeSpan.Zero, fast.LastSignalAgo);
        Assert.Equal(2, view.CountLive);
        Assert.Equal(T0.AddSeconds(100), view.At);
        Assert.Equal("evt-1", view.EventId);
    }

    [Fact]
    public void LowBattery_alarm_when_battery_19_percent_and_none_at_20()
    {
        var (runtime, clock) = Create("low", "ok");
        runtime.Ingest("low", Batch(Ride(8, 20), new DeviceStatus(19, false, 4, true)));
        runtime.Ingest("ok", Batch(Ride(8, 20), new DeviceStatus(20, false, 0, true)));
        clock.SetUtcNow(T0.AddSeconds(20));

        var low = View(runtime, "low");
        var ok = View(runtime, "ok");

        Assert.Equal(new[] { new Alarm(AlarmKind.LowBattery, T0) }, low.Alarms);
        Assert.Equal(19, low.BatteryPct);
        Assert.Equal(4, low.QueueLength);
        Assert.Empty(ok.Alarms);
        Assert.Equal(20, ok.BatteryPct);
    }

    [Fact]
    public void Sos_action_adds_Sos_alarm_and_state_and_SosCleared_removes_both()
    {
        var (runtime, clock) = Create("a");
        runtime.Ingest("a", Batch(Ride(8, 20)));
        clock.SetUtcNow(T0.AddSeconds(20));

        runtime.Act("a", ParticipantAction.Sos);
        clock.SetUtcNow(T0.AddSeconds(25));
        var sos = runtime.BuildLiveView();
        Assert.Equal(ParticipantState.Sos, sos.Participants[0].State);
        Assert.Equal(new[] { new Alarm(AlarmKind.Sos, T0.AddSeconds(20)) }, sos.Participants[0].Alarms);
        Assert.Equal(1, sos.CountLive);

        runtime.Act("a", ParticipantAction.SosCleared);
        var cleared = View(runtime, "a");
        Assert.Equal(ParticipantState.Live, cleared.State);
        Assert.Empty(cleared.Alarms);
    }

    [Fact]
    public void Offline_alarm_after_121_seconds_and_counts_updated()
    {
        var (runtime, clock) = Create("a", "b");
        runtime.Ingest("a", Batch(Ride(8, 60)));
        runtime.Ingest("b", Batch(Ride(8, 60)));
        clock.SetUtcNow(T0.AddSeconds(60));
        var before = runtime.BuildLiveView();
        Assert.Equal((2, 0, 0, 0), (before.CountLive, before.CountDelayed, before.CountOffline, before.CountDnf));

        runtime.Ingest("b", Batch(new[] { FixAt(8 * 170, T0.AddSeconds(170)) }));
        clock.SetUtcNow(T0.AddSeconds(181)); // a silent for 121 s, b for 11 s
        var after = runtime.BuildLiveView();

        var a = after.Participants.Single(p => p.ParticipantId == "a");
        Assert.Equal(ParticipantState.Offline, a.State);
        Assert.Equal(new[] { new Alarm(AlarmKind.Offline, T0.AddSeconds(180)) }, a.Alarms);
        Assert.Equal(TimeSpan.FromSeconds(121), a.LastSignalAgo);
        Assert.Equal((1, 0, 1, 0), (after.CountLive, after.CountDelayed, after.CountOffline, after.CountDnf));

        runtime.Act("b", ParticipantAction.Dnf);
        var dnf = runtime.BuildLiveView();
        Assert.Equal((0, 0, 1, 1), (dnf.CountLive, dnf.CountDelayed, dnf.CountOffline, dnf.CountDnf));
    }

    [Fact]
    public void Segment_results_appear_with_colours_after_checkpoint_crossing()
    {
        var (runtime, clock) = Create("fast", "slow");
        runtime.Ingest("fast", Batch(Ride(8, 400)));  // CP1 at 375 s
        runtime.Ingest("slow", Batch(Ride(7.5, 420))); // CP1 at 400 s
        clock.SetUtcNow(T0.AddSeconds(420));

        var fast = View(runtime, "fast");
        var slow = View(runtime, "slow");

        var f = Assert.Single(fast.Segments);
        Assert.Equal((1, 0, SegmentColour.Purple), (f.Lap, f.SegmentIndex, f.Colour));
        Assert.InRange(f.Time.TotalSeconds, 374, 376);
        var s = Assert.Single(slow.Segments);
        Assert.Equal((1, 0, SegmentColour.Green), (s.Lap, s.SegmentIndex, s.Colour));
        Assert.InRange(s.Time.TotalSeconds, 399, 401);
    }

    [Fact]
    public void Participant_without_fixes_has_null_rank_and_NotStarted_or_state_from_start_time()
    {
        var (runtime, clock) = Create("rider", "idle");
        runtime.Ingest("rider", Batch(Ride(8, 30)));
        clock.SetUtcNow(T0.AddSeconds(30));

        var view = runtime.BuildLiveView();
        var idle = view.Participants.Single(p => p.ParticipantId == "idle");

        Assert.Null(idle.Rank);
        Assert.Equal(ParticipantState.NotStarted, idle.State);
        Assert.Null(idle.GapToLeader);
        Assert.Null(idle.Lat);
        Assert.Null(idle.Eta);
        Assert.Null(idle.LastSignalAgo);
        Assert.Null(idle.BatteryPct);
        Assert.Empty(idle.Alarms);
        Assert.Empty(idle.Segments);
        Assert.Equal(1, view.Participants.Single(p => p.ParticipantId == "rider").Rank);
        Assert.Equal("idle", view.Participants[^1].ParticipantId);
    }

    [Fact]
    public void Late_batch_gives_the_same_live_view_as_ordered_input()
    {
        var fixes = Ride(8, 800); // crosses CP1 (375 s) and CP2 (1 000 m short of it)
        var status = new DeviceStatus(55, true, 0, true);

        var (ordered, orderedClock) = Create("a");
        ordered.Ingest("a", Batch(fixes.Take(60), status));
        ordered.Ingest("a", Batch(fixes.Skip(60), status));
        orderedClock.SetUtcNow(T0.AddSeconds(800));

        var (late, lateClock) = Create("a");
        late.Ingest("a", Batch(fixes.Skip(60).Reverse(), status));
        late.Ingest("a", Batch(fixes.Take(60).Reverse(), status));
        lateClock.SetUtcNow(T0.AddSeconds(800));

        var expected = ordered.BuildLiveView();
        Assert.NotEmpty(expected.Participants[0].Segments);
        Assert.Equal(Json(expected), Json(late.BuildLiveView()));
    }

    /// <summary>Fixes every 5 s standing at <paramref name="totalM"/> from <paramref name="fromS"/> to <paramref name="toS"/> seconds.</summary>
    private static IEnumerable<PositionFix> Stand(double totalM, int fromS, int toS)
    {
        for (var s = fromS; s <= toS; s += 5) yield return FixAt(totalM, T0.AddSeconds(s));
    }

    /// <summary>Fixes every 5 s riding at 8 m/s from <paramref name="fromM"/>, starting at <paramref name="fromS"/>.</summary>
    private static IEnumerable<PositionFix> RideOn(double fromM, int fromS, int toS)
    {
        for (var s = fromS; s <= toS; s += 5) yield return FixAt(fromM + 8 * (s - fromS), T0.AddSeconds(s));
    }

    private static PositionFix North(double totalM, double northM, int atS)
    {
        var f = FixAt(totalM, T0.AddSeconds(atS));
        return f with { Lat = f.Lat + northM / 111_195 };
    }

    /// <summary>
    /// 8 m/s to 800 m at T0 + 100 s, then north off the first (east-running) edge at 8 m/s to 200 m (off course
    /// over 100 m from T0 + 115 s), then standing there every 5 s up to <paramref name="toS"/>.
    /// </summary>
    private static List<PositionFix> RideOffCourse(int toS)
    {
        var fixes = Ride(8, 100);
        for (var k = 1; k <= 5; k++) fixes.Add(North(800, 40 * k, 100 + 5 * k));
        for (var s = 130; s <= toS; s += 5) fixes.Add(North(800, 200, s));
        return fixes;
    }

    private static Alarm SingleAlarm(EventRuntime runtime, string id, AlarmKind kind) =>
        Assert.Single(View(runtime, id).Alarms, a => a.Kind == kind);

    [Fact]
    public void Stopped_alarm_since_is_stable_across_views_and_clears_when_moving_again()
    {
        var (runtime, clock) = Create("a");
        runtime.Ingest("a", Batch(Ride(8, 100).Concat(Stand(800, 105, 400)))); // stops at T0 + 100 s
        clock.SetUtcNow(T0.AddSeconds(400));
        var first = SingleAlarm(runtime, "a", AlarmKind.Stopped);

        runtime.Ingest("a", Batch(Stand(800, 405, 760)));
        clock.SetUtcNow(T0.AddSeconds(760)); // 6 minutes later
        var view = View(runtime, "a");
        var second = Assert.Single(view.Alarms);

        Assert.Equal(ParticipantState.Stopped, view.State);
        Assert.Equal(new Alarm(AlarmKind.Stopped, T0.AddSeconds(100)), first);
        Assert.Equal(first, second);
        Assert.Equal(TimeSpan.FromSeconds(660), clock.GetUtcNow() - second.Since);

        runtime.Ingest("a", Batch(RideOn(800, 765, 820)));
        clock.SetUtcNow(T0.AddSeconds(820));
        var moving = View(runtime, "a");
        Assert.Equal(ParticipantState.Live, moving.State);
        Assert.Empty(moving.Alarms);
    }

    [Fact]
    public void OffCourse_alarm_since_is_the_start_of_the_off_course_run_and_clears_on_return()
    {
        var (runtime, clock) = Create("a");
        runtime.Ingest("a", Batch(RideOffCourse(160)));
        clock.SetUtcNow(T0.AddSeconds(160));
        var first = SingleAlarm(runtime, "a", AlarmKind.OffCourse);

        runtime.Ingest("a", Batch(Enumerable.Range(0, 72).Select(i => North(800, 200, 165 + 5 * i)))); // to T0 + 520 s
        clock.SetUtcNow(T0.AddSeconds(520));
        var view = View(runtime, "a");
        var second = Assert.Single(view.Alarms);

        // The first fix over 100 m off (T0 + 115 s), not the moment the 15 s debounce elapsed.
        Assert.Equal(ParticipantState.OffCourse, view.State);
        Assert.Equal(new Alarm(AlarmKind.OffCourse, T0.AddSeconds(115)), first);
        Assert.Equal(first, second);

        var back = Enumerable.Range(1, 5).Select(k => North(800, 200 - 40 * k, 520 + 5 * k)) // on course at T0 + 545 s
            .Concat(RideOn(800, 550, 610));
        runtime.Ingest("a", Batch(back));
        clock.SetUtcNow(T0.AddSeconds(610));
        var returned = View(runtime, "a");
        Assert.Equal(ParticipantState.Live, returned.State);
        Assert.Empty(returned.Alarms);
    }

    [Fact]
    public void Late_batch_gives_the_same_alarm_since_as_ordered_input()
    {
        var stopper = Ride(8, 100).Concat(Stand(800, 105, 400)).ToList();
        var wanderer = RideOffCourse(400);

        var (ordered, orderedClock) = Create("stop", "off");
        ordered.Ingest("stop", Batch(stopper.Take(40)));
        ordered.Ingest("stop", Batch(stopper.Skip(40)));
        ordered.Ingest("off", Batch(wanderer.Take(25)));
        ordered.Ingest("off", Batch(wanderer.Skip(25)));
        orderedClock.SetUtcNow(T0.AddSeconds(400));

        var (late, lateClock) = Create("stop", "off");
        late.Ingest("stop", Batch(stopper.Skip(40)));
        late.Ingest("stop", Batch(stopper.Take(40)));
        late.Ingest("off", Batch(wanderer.Skip(25)));
        late.Ingest("off", Batch(wanderer.Take(25)));
        lateClock.SetUtcNow(T0.AddSeconds(400));

        Assert.Equal(new Alarm(AlarmKind.Stopped, T0.AddSeconds(100)), SingleAlarm(late, "stop", AlarmKind.Stopped));
        Assert.Equal(new Alarm(AlarmKind.OffCourse, T0.AddSeconds(115)), SingleAlarm(late, "off", AlarmKind.OffCourse));
        Assert.Equal(Json(ordered.BuildLiveView()), Json(late.BuildLiveView()));
    }

    [Fact]
    public void Late_backlog_of_120_fixes_gives_the_same_live_view_as_ordered_input()
    {
        var fixes = Ride(8, 4_000); // 801 fixes, crosses laps and checkpoints
        var backlog = fixes.Skip(400).Take(120).ToList();

        var (ordered, orderedClock) = Create("a");
        ordered.Ingest("a", Batch(fixes));
        orderedClock.SetUtcNow(T0.AddSeconds(4_000));

        var (late, lateClock) = Create("a");
        Assert.Equal(new IngestResult(681, 0), late.Ingest("a", Batch(fixes.Except(backlog))));
        Assert.Equal(new IngestResult(120, 0), late.Ingest("a", Batch(backlog)));
        lateClock.SetUtcNow(T0.AddSeconds(4_000));

        Assert.Equal(Json(ordered.BuildLiveView()), Json(late.BuildLiveView()));
    }

    [Fact]
    public void Sos_after_finish_raises_Sos_alarm_while_state_stays_Finished()
    {
        var (runtime, clock) = Create("a");
        runtime.Ingest("a", Batch(Ride(8, 4_700))); // 37 600 m, past the 37 500 m finish
        clock.SetUtcNow(T0.AddSeconds(4_700));
        Assert.Equal(ParticipantState.Finished, View(runtime, "a").State);

        runtime.Act("a", ParticipantAction.Sos);
        var view = View(runtime, "a");

        Assert.Equal(ParticipantState.Finished, view.State);
        Assert.Equal(new[] { new Alarm(AlarmKind.Sos, T0.AddSeconds(4_700)) }, view.Alarms);
    }

    [Fact]
    public void Pause_suppresses_Stopped_and_Resume_lets_it_return_counting_unpaused_time_only()
    {
        var (runtime, clock) = Create("a");
        runtime.Ingest("a", Batch(Ride(8, 100)));
        clock.SetUtcNow(T0.AddSeconds(100));
        runtime.Act("a", ParticipantAction.Pause);

        runtime.Ingest("a", Batch(Stand(800, 105, 400)));
        clock.SetUtcNow(T0.AddSeconds(400));
        var paused = View(runtime, "a");
        Assert.Equal(ParticipantState.Live, paused.State);
        Assert.Empty(paused.Alarms);

        runtime.Act("a", ParticipantAction.Resume);
        runtime.Ingest("a", Batch(Stand(800, 405, 500)));
        clock.SetUtcNow(T0.AddSeconds(500));
        Assert.Equal(ParticipantState.Live, View(runtime, "a").State); // 100 s unpaused standing so far

        runtime.Ingest("a", Batch(Stand(800, 505, 590)));
        clock.SetUtcNow(T0.AddSeconds(590));
        var stopped = View(runtime, "a");
        Assert.Equal(ParticipantState.Stopped, stopped.State);
        Assert.Equal(new[] { new Alarm(AlarmKind.Stopped, T0.AddSeconds(400)) }, stopped.Alarms);
    }

    [Fact]
    public void Parallel_ingest_on_different_participants_does_not_corrupt_state()
    {
        var ids = Enumerable.Range(0, 40).Select(i => $"p{i:D2}").ToArray();
        var fixes = ids.ToDictionary(id => id, id => Ride(6 + int.Parse(id[1..]) * 0.1, 600));

        var (parallel, parallelClock) = Create(ids);
        var (sequential, sequentialClock) = Create(ids);
        Parallel.For(0, ids.Length, i =>
        {
            foreach (var chunk in fixes[ids[i]].Chunk(10)) parallel.Ingest(ids[i], Batch(chunk));
        });
        foreach (var id in ids)
            foreach (var chunk in fixes[id].Chunk(10)) sequential.Ingest(id, Batch(chunk));
        parallelClock.SetUtcNow(T0.AddSeconds(600));
        sequentialClock.SetUtcNow(T0.AddSeconds(600));

        var expected = sequential.BuildLiveView();
        Assert.Equal(ids.Length, expected.CountLive);
        Assert.Equal(Json(expected), Json(parallel.BuildLiveView()));
    }
}
