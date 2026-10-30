using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

/// <summary>What <see cref="EventRuntime"/> reports for persisting its input: stored fixes and the times it used.</summary>
public class EventRuntimeDetailsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3);

    private static PositionFix FixAt(double totalM, DateTimeOffset time, double acc = 5)
    {
        var p = Loop.PointAt(totalM);
        return new PositionFix(time, p.Lat, p.Lon, acc);
    }

    private static (EventRuntime Runtime, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(T0.AddSeconds(20));
        var settings = new EventSettings("evt-d", "Details", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false,
            TrackerOptions.Defaults(Sport.Cycling));
        var runtime = new EventRuntime(settings, Loop, clock);
        runtime.AddParticipant(new ParticipantInfo("a", "1", "A", null, "Open", true, false));
        return (runtime, clock);
    }

    [Fact]
    public void IngestDetailed_reports_every_newly_stored_fix_including_one_the_speed_filter_skips_until_a_late_fix_arrives()
    {
        var (runtime, clock) = Create();
        var spike = FixAt(2_000, T0.AddSeconds(10)); // the first fix is accepted whatever it is
        var real = FixAt(120, T0.AddSeconds(15));    // 2 km away from the spike in 5 s: skipped by the speed filter for now
        var inaccurate = FixAt(130, T0.AddSeconds(16), acc: 500);

        var first = runtime.IngestDetailed("a", new PositionBatch([spike], null));
        var second = runtime.IngestDetailed("a", new PositionBatch([real, inaccurate, real], null));

        Assert.Equal(new IngestResult(1, 0), first.Result);
        Assert.Equal([spike], first.Stored);
        Assert.Equal(new IngestResult(0, 3), second.Result);
        Assert.Equal([real], second.Stored); // kept by the tracker: a later replay may accept it
        Assert.Equal(clock.GetUtcNow(), second.At);

        // A late fix before the spike: on replay the spike is rejected and the skipped fix is accepted.
        clock.Advance(TimeSpan.FromSeconds(3));
        var late = runtime.IngestDetailed("a", new PositionBatch([FixAt(0, T0)], new DeviceStatus(50, false, 0, true)));

        Assert.Equal([FixAt(0, T0)], late.Stored);
        Assert.Equal(T0.AddSeconds(23), late.At);
        var view = runtime.BuildLiveView().Participants.Single();
        Assert.Equal(real.Time, view.LastRaceFixTime);
        Assert.Equal(new IngestResult(0, 1), runtime.Ingest("a", new PositionBatch([FixAt(0, T0)], null))); // a resend is not stored again
    }

    [Fact]
    public void Act_returns_the_clock_time_it_applied_the_action_at()
    {
        var (runtime, clock) = Create();

        var at = runtime.Act("a", ParticipantAction.Sos);

        Assert.Equal(clock.GetUtcNow(), at);
        Assert.Equal(at, runtime.BuildLiveView().Participants.Single().Alarms.Single(a => a.Kind == AlarmKind.Sos).Since);
    }
}
