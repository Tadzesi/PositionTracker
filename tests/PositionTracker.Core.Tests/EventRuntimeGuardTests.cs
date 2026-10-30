using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

/// <summary>Ingest validation, load limits and ranking rules of <see cref="EventRuntime"/>.</summary>
public class EventRuntimeGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3);

    private static readonly EventSettings Settings = new(
        "evt-g", "Guard", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false, TrackerOptions.Defaults(Sport.Cycling));

    private static (EventRuntime Runtime, FakeTimeProvider Clock) Create(params string[] ids) => CreateAt(T0, ids);

    private static (EventRuntime Runtime, FakeTimeProvider Clock) CreateAt(DateTimeOffset now, params string[] ids)
    {
        var clock = new FakeTimeProvider(now);
        var runtime = new EventRuntime(Settings, Loop, clock);
        foreach (var id in ids)
            runtime.AddParticipant(new ParticipantInfo(id, "B-" + id, "Name " + id, null, "Open", true, false));
        return (runtime, clock);
    }

    private static PositionFix FixAt(double totalM, DateTimeOffset time)
    {
        var p = Loop.PointAt(totalM % Loop.LapLengthM);
        return new PositionFix(time, p.Lat, p.Lon, 5);
    }

    private static List<PositionFix> Ride(double speedMps, int fromS, int toS, double fromM = 0)
    {
        var fixes = new List<PositionFix>();
        for (var s = fromS; s <= toS; s += 5) fixes.Add(FixAt(fromM + (s - fromS) * speedMps, T0.AddSeconds(s)));
        return fixes;
    }

    /// <summary>Ingests as a phone sending at the time of its newest fix.</summary>
    private static IngestResult Send(EventRuntime runtime, FakeTimeProvider clock, string id, IReadOnlyList<PositionFix> fixes)
    {
        var newest = fixes.Max(f => f.Time);
        if (newest > clock.GetUtcNow()) clock.SetUtcNow(newest);
        return runtime.Ingest(id, new PositionBatch(fixes, null));
    }

    private static ParticipantView View(EventRuntime runtime, string id) =>
        runtime.BuildLiveView().Participants.Single(p => p.ParticipantId == id);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    // ---- I1: fix time validation ----

    [Fact]
    public void Fix_more_than_60s_in_the_future_is_rejected_and_leaves_the_view_unchanged()
    {
        var (runtime, clock) = Create("a");
        Send(runtime, clock, "a", Ride(8, 0, 300));
        var before = Json(runtime.BuildLiveView());

        var result = runtime.Ingest("a", new PositionBatch(new[] { FixAt(14_900, T0.AddHours(1).AddSeconds(300)) }, null));

        Assert.Equal(new IngestResult(0, 1), result);
        Assert.Equal(before, Json(runtime.BuildLiveView()));
    }

    [Fact]
    public void Offline_alarm_is_still_raised_after_a_rejected_future_fix()
    {
        var (runtime, clock) = Create("a");
        Send(runtime, clock, "a", Ride(8, 0, 300));
        runtime.Ingest("a", new PositionBatch(new[] { FixAt(14_900, T0.AddHours(1).AddSeconds(300)) }, null));

        clock.SetUtcNow(T0.AddSeconds(900));
        var view = View(runtime, "a");

        Assert.Equal(ParticipantState.Offline, view.State);
        Assert.Contains(view.Alarms, a => a.Kind == AlarmKind.Offline);
        Assert.Equal(TimeSpan.FromSeconds(600), view.LastSignalAgo);
        Assert.InRange(view.TotalDistanceM, 2_395, 2_405);
    }

    [Fact]
    public void Fix_within_60s_ahead_of_the_clock_is_accepted_with_silence_clamped_to_zero()
    {
        var (runtime, clock) = Create("a");
        Send(runtime, clock, "a", Ride(8, 0, 300));

        var result = runtime.Ingest("a", new PositionBatch(new[] { FixAt(2_800, T0.AddSeconds(350)) }, null));
        var view = View(runtime, "a");

        Assert.Equal(new IngestResult(1, 0), result);
        Assert.Equal(TimeSpan.Zero, view.LastSignalAgo);
        Assert.Equal(ParticipantState.Live, view.State);
    }

    [Fact]
    public void Fix_more_than_an_hour_before_the_start_is_rejected_and_a_warm_up_fix_is_kept()
    {
        var (runtime, _) = CreateAt(T0.AddMinutes(-30), "a");

        var old = runtime.Ingest("a", new PositionBatch(new[] { FixAt(100, T0.AddHours(-1).AddSeconds(-1)) }, null));
        Assert.Equal(new IngestResult(0, 1), old);
        Assert.Null(View(runtime, "a").LastSignalAgo);

        var warmUp = runtime.Ingest("a", new PositionBatch(new[] { FixAt(100, T0.AddMinutes(-31)) }, null));
        Assert.Equal(new IngestResult(1, 0), warmUp);
        Assert.Equal(TimeSpan.FromMinutes(1), View(runtime, "a").LastSignalAgo);
    }

    // ---- I3: DNF in the ranking ----

    [Fact]
    public void Dnf_participant_far_ahead_is_last_and_others_are_not_gapped_to_him()
    {
        var (runtime, clock) = Create("dnf", "a", "b");
        Send(runtime, clock, "dnf", Ride(10, 0, 900));
        Send(runtime, clock, "a", Ride(8, 0, 900));
        Send(runtime, clock, "b", Ride(6, 0, 900));
        clock.SetUtcNow(T0.AddSeconds(900));
        runtime.Act("dnf", ParticipantAction.Dnf);

        var view = runtime.BuildLiveView();

        Assert.Equal(new[] { "a", "b", "dnf" }, view.Participants.Select(p => p.ParticipantId));
        Assert.Equal(new int?[] { 1, 2, 3 }, view.Participants.Select(p => p.Rank));
        Assert.Equal(TimeSpan.Zero, view.Participants[0].GapToLeader);
        Assert.InRange(view.Participants[1].GapToLeader!.Value.TotalSeconds, 220, 230); // 5 400 m: a at 675 s, b at 900 s
        Assert.Null(view.Participants[2].GapToLeader);
        Assert.Null(view.Participants[2].GapToAhead);
    }

    // ---- I4: spectator position outside the race window ----

    [Fact]
    public void Spectator_json_hides_warm_up_and_dnf_positions_and_freezes_the_finisher_at_the_finish()
    {
        var course = Course.Create(TestCourses.Loop(2_000, 25), 1);
        var clock = new FakeTimeProvider(T0.AddSeconds(-200));
        var runtime = new EventRuntime(Settings with { EventId = "evt-pos" }, course, clock);
        foreach (var id in new[] { "warm", "fin", "dnf" })
            runtime.AddParticipant(new ParticipantInfo(id, id, "Name " + id, null, "Open", true, false));
        PositionFix On(double totalM, int s)
        {
            var p = course.PointAt(totalM % course.LapLengthM);
            return new PositionFix(T0.AddSeconds(s), p.Lat, p.Lon, 5);
        }

        runtime.Ingest("warm", new PositionBatch(Enumerable.Range(0, 10).Select(k => On(500 + 5 * k, -300 + 10 * k)).ToList(), null));
        clock.SetUtcNow(T0.AddSeconds(100));
        runtime.Ingest("dnf", new PositionBatch(Enumerable.Range(0, 21).Select(k => On(40 * k, 5 * k)).ToList(), null));
        runtime.Act("dnf", ParticipantAction.Dnf);
        clock.SetUtcNow(T0.AddSeconds(275));
        var ride = Enumerable.Range(0, 56).Select(k => On(20 + 8 * 5 * k, 5 * k)).ToList(); // crosses the line at 247.5 s, rides on
        runtime.Ingest("fin", new PositionBatch(ride, null));
        runtime.Ingest("dnf", new PositionBatch(Enumerable.Range(21, 35).Select(k => On(40 * k, 5 * k)).ToList(), null));

        var view = runtime.BuildLiveView();
        var json = JsonSerializer.Serialize(SpectatorProjector.Project(view, runtime.Participants), JsonSerializerOptions.Web);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("participants").EnumerateArray()
            .ToDictionary(r => r.GetProperty("participantId").GetString()!, r => r.Clone());

        var finish = ride.Single(f => f.Time == T0.AddSeconds(245)); // the last fix before the crossing
        Assert.Equal(ParticipantState.Finished, view.Participants.Single(p => p.ParticipantId == "fin").State);
        Assert.Equal(finish.Lat, rows["fin"].GetProperty("lat").GetDouble(), 5);
        Assert.Equal(finish.Lon, rows["fin"].GetProperty("lon").GetDouble(), 5);
        Assert.Equal(JsonValueKind.Null, rows["warm"].GetProperty("lat").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows["warm"].GetProperty("lon").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows["dnf"].GetProperty("lat").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows["dnf"].GetProperty("lon").ValueKind);

        // The organizer view keeps the live positions.
        Assert.Equal(ride[^1].Lat, view.Participants.Single(p => p.ParticipantId == "fin").Lat);
        Assert.NotNull(view.Participants.Single(p => p.ParticipantId == "warm").Lat);
        Assert.NotNull(view.Participants.Single(p => p.ParticipantId == "dnf").Lat);
    }

    // ---- I6: ETA ----

    [Fact]
    public void A_one_minute_stop_does_not_null_the_eta()
    {
        var (runtime, clock) = Create("a");
        Send(runtime, clock, "a", Ride(8, 0, 600));
        Send(runtime, clock, "a", Enumerable.Range(1, 12).Select(k => FixAt(4_800, T0.AddSeconds(600 + 5 * k))).ToList());

        var eta = View(runtime, "a").Eta;

        Assert.NotNull(eta);
        var expected = T0.AddSeconds(660 + (Loop.TotalLengthM - 4_800) / 8);
        Assert.InRange(eta!.Expected, expected.AddSeconds(-30), expected.AddSeconds(30));
    }

    [Fact]
    public void Eta_does_not_move_during_an_outage_and_is_approximate_while_delayed_or_offline()
    {
        var (runtime, clock) = Create("a");
        Send(runtime, clock, "a", Ride(8, 0, 600));
        var live = View(runtime, "a").Eta!;

        clock.SetUtcNow(T0.AddSeconds(630));
        var delayed = View(runtime, "a");
        clock.SetUtcNow(T0.AddSeconds(840));
        var offline = View(runtime, "a");

        Assert.False(live.Approximate);
        Assert.Equal(ParticipantState.Delayed, delayed.State);
        Assert.Equal(ParticipantState.Offline, offline.State);
        Assert.Equal(live.Expected, delayed.Eta!.Expected);
        Assert.Equal(live.Expected, offline.Eta!.Expected);
        Assert.True(delayed.Eta.Approximate);
        Assert.True(offline.Eta.Approximate);
    }

    // ---- Minor 1: finish time interpolated between fixes ----

    [Fact]
    public void Two_finishers_crossing_1s_apart_with_fixes_5s_apart_rank_and_gap_correctly()
    {
        // Same fix times; z-first crosses 2 000 m at 247.5 s, a-second at 248.5 s. The first fix past the line is
        // at 250 s for both, and the ids sort against the true order.
        var course = Course.Create(TestCourses.Loop(2_000, 25), 1);
        var clock = new FakeTimeProvider(T0.AddSeconds(300));
        var runtime = new EventRuntime(Settings with { EventId = "evt-fin" }, course, clock);
        foreach (var id in new[] { "z-first", "a-second" })
            runtime.AddParticipant(new ParticipantInfo(id, id, id, null, "Open", true, false));
        List<PositionFix> Ride(double offsetM) => Enumerable.Range(0, 56).Select(k =>
        {
            var p = course.PointAt((offsetM + 40 * k) % course.LapLengthM);
            return new PositionFix(T0.AddSeconds(5 * k), p.Lat, p.Lon, 5);
        }).ToList();
        runtime.Ingest("z-first", new PositionBatch(Ride(20), null));
        runtime.Ingest("a-second", new PositionBatch(Ride(12), null));

        var view = runtime.BuildLiveView();

        Assert.Equal(new[] { "z-first", "a-second" }, view.Participants.Select(p => p.ParticipantId));
        Assert.All(view.Participants, p => Assert.Equal(ParticipantState.Finished, p.State));
        Assert.InRange(view.Participants[1].GapToLeader!.Value.TotalSeconds, 0.9, 1.1);
        Assert.Equal(view.Participants[1].GapToLeader, view.Participants[1].GapToAhead);
    }

    // ---- Minor 4: warm-up only participants and the lap at the seam ----

    [Fact]
    public void Participant_with_only_warm_up_fixes_has_no_rank_and_no_gaps()
    {
        var (runtime, clock) = CreateAt(T0.AddSeconds(-100), "rider", "warm");
        runtime.Ingest("warm", new PositionBatch(new[] { FixAt(300, T0.AddSeconds(-60)) }, null));
        clock.SetUtcNow(T0.AddSeconds(100));
        runtime.Ingest("rider", new PositionBatch(Ride(8, 0, 100), null));

        var warm = View(runtime, "warm");

        Assert.Equal(ParticipantState.NotStarted, warm.State);
        Assert.Null(warm.Rank);
        Assert.Null(warm.GapToLeader);
        Assert.Null(warm.GapToAhead);
        Assert.Equal(1, View(runtime, "rider").Rank);
    }

    [Fact]
    public void Lap_in_the_view_follows_the_total_distance_at_the_start_finish_seam()
    {
        // Past the line at 12 520 m, then a fix drifting 30 m back to 10 m behind it: the lap counter steps back to 1
        // while the total stays in lap 2.
        var (runtime, clock) = Create("a");
        var fixes = Ride(8, 0, 1_565);
        fixes.Add(FixAt(Loop.LapLengthM - 10, T0.AddSeconds(1_570)));
        Send(runtime, clock, "a", fixes);

        var view = View(runtime, "a");

        Assert.True(view.TotalDistanceM > Loop.LapLengthM, $"total {view.TotalDistanceM}");
        Assert.Equal(2, view.Lap);
        Assert.Equal(view.TotalDistanceM - Loop.LapLengthM, view.LapProgressM, 6);
    }
}
