using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class DeviceLinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3, []);
    private static readonly EventSettings Settings = new(
        "evt-1", "Test Ride", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false, TrackerOptions.Defaults(Sport.Cycling));

    private static (EventRuntime Runtime, TestClock Clock) Create()
    {
        var clock = new TestClock(T0.AddMinutes(-10));
        var runtime = new EventRuntime(Settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("a", "1", "A", null, "Open", true, false));
        return (runtime, clock);
    }

    private static PositionBatch Batch(DateTimeOffset time, double accuracy = 5, double battery = 80)
    {
        var p = Loop.PointAt(0);
        return new PositionBatch([new PositionFix(time, p.Lat, p.Lon, accuracy)], new DeviceStatus(battery, false, 0, true));
    }

    [Fact]
    public void Nothing_received_is_waiting_before_the_start_and_silent_after_it()
    {
        var (runtime, clock) = Create();
        Assert.Equal(LinkLevel.Waiting, runtime.Link("a").Level);
        clock.Now = T0.AddMinutes(1);
        Assert.Equal(LinkLevel.Silent, runtime.Link("a").Level);
    }

    [Fact]
    public void Stored_positions_are_good_and_go_silent_after_90_seconds()
    {
        var (runtime, clock) = Create();
        runtime.Ingest("a", Batch(clock.Now));
        var link = runtime.Link("a");
        Assert.Equal(LinkLevel.Good, link.Level);
        Assert.Empty(link.Problems);
        clock.Now = clock.Now.AddSeconds(91);
        Assert.Equal(LinkLevel.Silent, runtime.Link("a").Level);
    }

    [Fact]
    public void A_device_clock_ahead_is_a_warning_even_though_the_positions_are_stored()
    {
        var (runtime, clock) = Create();
        runtime.Ingest("a", Batch(clock.Now.AddSeconds(71)));
        var link = runtime.Link("a");
        Assert.Equal(LinkLevel.Warning, link.Level);
        Assert.Contains(LinkProblem.Clock, link.Problems);
        Assert.InRange(link.ClockAheadSeconds!.Value, 70, 72);
    }

    [Fact]
    public void Poor_accuracy_is_named_and_clears_when_a_good_position_arrives()
    {
        var (runtime, clock) = Create();
        runtime.Ingest("a", Batch(clock.Now, accuracy: 300));
        var bad = runtime.Link("a");
        Assert.Equal(LinkLevel.Warning, bad.Level);
        Assert.Contains(LinkProblem.Accuracy, bad.Problems);
        clock.Now = clock.Now.AddSeconds(5);
        runtime.Ingest("a", Batch(clock.Now, accuracy: 5));
        Assert.Equal(LinkLevel.Good, runtime.Link("a").Level);
    }

    [Fact]
    public void A_clock_far_ahead_is_rejected_and_reported_as_a_clock_problem()
    {
        var (runtime, clock) = Create();
        runtime.Ingest("a", Batch(clock.Now.AddMinutes(30)));
        var link = runtime.Link("a");
        Assert.Equal(LinkLevel.Warning, link.Level);
        Assert.Contains(LinkProblem.Clock, link.Problems);
    }

    [Fact]
    public void A_low_battery_is_a_warning()
    {
        var (runtime, clock) = Create();
        runtime.Ingest("a", Batch(clock.Now, battery: 13));
        var link = runtime.Link("a");
        Assert.Equal(LinkLevel.Warning, link.Level);
        Assert.Contains(LinkProblem.LowBattery, link.Problems);
    }

    [Fact]
    public void A_position_more_than_an_hour_before_the_start_means_connected_but_not_stored_yet()
    {
        var (runtime, clock) = Create();
        clock.Now = T0.AddHours(-3);
        runtime.Ingest("a", Batch(clock.Now));
        var link = runtime.Link("a");
        Assert.Equal(LinkLevel.Good, link.Level);
        Assert.True(link.EarlyOnly);
        Assert.Null(link.LastAcceptedAt);
    }

    [Fact]
    public void An_unknown_participant_throws()
    {
        var (runtime, _) = Create();
        Assert.Throws<KeyNotFoundException>(() => runtime.Link("nobody"));
    }
}
