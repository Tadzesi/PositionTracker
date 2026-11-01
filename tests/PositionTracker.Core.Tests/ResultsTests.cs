using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class ResultsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3, new[] { new Checkpoint("CP1", 3_000), new Checkpoint("CP2", 8_000) });
    private static readonly EventSettings Settings = new(
        "evt-1", "Test Ride", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false, TrackerOptions.Defaults(Sport.Cycling));

    private static List<PositionFix> Ride(DateTimeOffset start, double speedMps, int untilS)
    {
        var fixes = new List<PositionFix>();
        for (var s = 0; s <= untilS; s += 5)
        {
            var p = Loop.PointAt(s * speedMps % Loop.LapLengthM);
            fixes.Add(new PositionFix(start.AddSeconds(s), p.Lat, p.Lon, 5));
        }
        return fixes;
    }

    [Fact]
    public void Results_rank_finishers_by_net_time_from_their_own_start_and_keep_the_others_last()
    {
        var clock = new TestClock(T0.AddHours(3));
        var runtime = new EventRuntime(Settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("fast", "1", "Fast Rider", "Rocket", "Open", true, false));
        runtime.AddParticipant(new ParticipantInfo("slow", "2", "Slow Rider", null, "Open", true, false, T0.AddMinutes(10)));
        runtime.AddParticipant(new ParticipantInfo("none", "3", "Never Started", null, "Open", true, false));
        runtime.Ingest("fast", new PositionBatch(Ride(T0, 8, 4_800), null));
        runtime.Ingest("slow", new PositionBatch(Ride(T0.AddMinutes(10), 7, 5_500), null));

        var rows = runtime.BuildResults();

        Assert.Equal(["fast", "slow", "none"], rows.Select(r => r.ParticipantId));
        var fast = rows[0];
        var slow = rows[1];
        Assert.Equal(ParticipantState.Finished, fast.State);
        Assert.Equal(1, fast.Rank);
        Assert.InRange(fast.NetTime!.Value.TotalSeconds, 4_680, 4_700); // 37 500 m at 8 m/s
        Assert.Equal(T0 + fast.NetTime!.Value, fast.FinishTime);
        Assert.Equal(T0.AddMinutes(10), slow.Start);
        Assert.InRange(slow.NetTime!.Value.TotalSeconds, 5_340, 5_380); // 37 500 m at 7 m/s from his own start, not from T0
        Assert.True(slow.GapToLeader > TimeSpan.Zero);
        Assert.Equal(3, fast.Laps);
        Assert.True(fast.Segments.Count >= 6);
        var none = rows[2];
        Assert.Null(none.Rank);
        Assert.Null(none.NetTime);
        Assert.Null(none.FinishTime);
    }

    [Fact]
    public void A_rider_first_seen_mid_course_has_his_average_over_the_distance_he_was_observed_not_the_whole_course()
    {
        var clock = new TestClock(T0.AddMinutes(60));
        var runtime = new EventRuntime(Settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("late", "1", "Late", null, "Open", true, false));
        // the phone was switched on 20 minutes after the start, 6 km into the course; from there 8 m/s for 10 minutes
        var fixes = new List<PositionFix>();
        for (var s = 0; s <= 600; s += 5)
        {
            var p = Loop.PointAt((6_000 + s * 8.0) % Loop.LapLengthM);
            fixes.Add(new PositionFix(T0.AddMinutes(20).AddSeconds(s), p.Lat, p.Lon, 5));
        }
        runtime.Ingest("late", new PositionBatch(fixes, null));

        var row = Assert.Single(runtime.BuildResults());
        Assert.InRange(row.StartObservedM, 5_800, 6_200);
        Assert.InRange(row.AvgSpeedMps, 7, 9); // 4.8 km over 10 minutes; 10.8 km over the same time would be 18 m/s
        Assert.InRange(row.TotalDistanceM, 10_500, 11_000);
    }

    [Fact]
    public void A_rider_seen_at_the_start_has_no_unobserved_part()
    {
        var clock = new TestClock(T0.AddMinutes(30));
        var runtime = new EventRuntime(Settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("a", "1", "A", null, "Open", true, false));
        runtime.Ingest("a", new PositionBatch(Ride(T0, 8, 1_500), null));

        var row = Assert.Single(runtime.BuildResults());
        Assert.InRange(row.StartObservedM, 0, 100);
        Assert.InRange(row.AvgSpeedMps, 7.5, 8.5);
    }

    [Fact]
    public void A_rider_still_on_the_course_has_no_net_time()
    {
        var clock = new TestClock(T0.AddMinutes(30));
        var runtime = new EventRuntime(Settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("a", "1", "A", null, "Open", true, false));
        runtime.Ingest("a", new PositionBatch(Ride(T0, 8, 1_500), null));

        var row = Assert.Single(runtime.BuildResults());
        Assert.Null(row.NetTime);
        Assert.NotEqual(ParticipantState.Finished, row.State);
        Assert.InRange(row.TotalDistanceM, 11_000, 12_100);
        Assert.Equal(0, row.Laps);
    }
}
