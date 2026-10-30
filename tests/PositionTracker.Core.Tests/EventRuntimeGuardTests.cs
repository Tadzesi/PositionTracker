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
}
