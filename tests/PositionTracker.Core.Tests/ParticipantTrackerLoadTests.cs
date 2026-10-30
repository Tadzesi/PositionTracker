using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

/// <summary>Bounds on what one participant's fixes and state inputs may cost (final review I2).</summary>
public class ParticipantTrackerLoadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 3);

    private static PositionFix FixAt(double totalM, DateTimeOffset time)
    {
        var p = Loop.PointAt(totalM % Loop.LapLengthM);
        return new PositionFix(time, p.Lat, p.Lon, 5);
    }

    [Fact]
    public void At_most_one_fix_per_second_is_stored()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        var fixes = Enumerable.Range(0, 40).Select(i => FixAt(i * 2.0, T0.AddMilliseconds(250 * i))).ToList(); // 4 per second

        var result = tracker.ApplyMany(fixes);

        Assert.Equal(10, tracker.StoredFixCount);
        Assert.Equal((10, 30), (result.Accepted, result.Rejected));
    }

    [Fact]
    public void Five_thousand_closely_spaced_fixes_leave_at_most_one_per_second_stored()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        for (var batch = 0; batch < 10; batch++)
        {
            var fixes = Enumerable.Range(0, 500)
                .Select(i => batch * 500 + i)
                .Select(n => FixAt(n * 0.08, T0.AddMilliseconds(10 * n)))
                .Append(FixAt(0, T0.AddMilliseconds(5 * batch + 1))) // one older fix per batch forces a replay
                .ToList();
            tracker.ApplyMany(fixes);
        }

        Assert.InRange(tracker.StoredFixCount, 1, 51);
        Assert.True(tracker.StoredFixCount <= ParticipantTracker.MaxStoredFixes);
    }

    [Fact]
    public void A_late_fix_within_the_second_of_a_stored_one_gives_the_same_set_in_any_arrival_order()
    {
        var a = FixAt(100, T0.AddSeconds(20));
        var b = FixAt(104, T0.AddSeconds(20.6));
        var c = FixAt(108, T0.AddSeconds(21.2));
        var start = FixAt(0, T0);
        var orders = new[] { new[] { start, a, b, c }, new[] { start, b, c, a }, new[] { c, b, start, a }, new[] { start, c, a, b } };

        var snapshots = orders.Select(order =>
        {
            var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
            foreach (var f in order) tracker.Apply(f);
            Assert.Equal(3, tracker.StoredFixCount);
            return tracker.GetSnapshot(T0.AddSeconds(30));
        }).ToList();

        Assert.All(snapshots, s => Assert.Equal(snapshots[0], s));
        Assert.Equal(T0.AddSeconds(21.2), snapshots[0].LastFixTime);
    }

    [Fact]
    public void Fixes_beyond_the_stored_cap_are_rejected()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        var cap = ParticipantTracker.MaxStoredFixes;
        var fixes = Enumerable.Range(0, cap + 10).Select(i => FixAt(500, T0.AddSeconds(i))).ToList();

        var result = tracker.ApplyMany(fixes);

        Assert.Equal(cap, tracker.StoredFixCount);
        Assert.Equal((cap, 10), (result.Accepted, result.Rejected));
    }

    [Fact]
    public void Repeating_a_pause_or_resume_with_the_same_state_does_not_replay()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.ApplyMany(Enumerable.Range(0, 60).Select(i => FixAt(40 * i, T0.AddSeconds(5 * i))).ToList());

        tracker.SetPaused(T0.AddSeconds(100), true);
        var replays = tracker.ReplayCount;
        tracker.SetPaused(T0.AddSeconds(150), true);  // already paused
        tracker.SetPaused(T0.AddSeconds(200), true);
        Assert.Equal(replays, tracker.ReplayCount);

        tracker.SetPaused(T0.AddSeconds(250), false); // a real change
        Assert.Equal(replays + 1, tracker.ReplayCount);
        tracker.SetPaused(T0.AddSeconds(260), false); // already running
        Assert.Equal(replays + 1, tracker.ReplayCount);
    }

    [Fact]
    public void Repeated_identical_SetPaused_calls_do_not_grow_the_pause_list()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.ApplyMany(Enumerable.Range(0, 60).Select(i => FixAt(40 * i, T0.AddSeconds(5 * i))).ToList());

        tracker.SetPaused(T0.AddSeconds(100), true);
        for (var i = 0; i < 50; i++)
        {
            tracker.SetPaused(T0.AddSeconds(100), true);     // the same call again
            tracker.SetPaused(T0.AddSeconds(101 + i), true); // a device repeating "pause" while paused
        }
        Assert.Equal(1, tracker.PauseSwitchCount);

        tracker.SetPaused(T0.AddSeconds(250), false);
        for (var i = 0; i < 50; i++) tracker.SetPaused(T0.AddSeconds(251 + i), false);
        Assert.Equal(2, tracker.PauseSwitchCount);

        var reference = new ParticipantTracker(Loop, Sport.Cycling, T0);
        reference.ApplyMany(Enumerable.Range(0, 60).Select(i => FixAt(40 * i, T0.AddSeconds(5 * i))).ToList());
        reference.SetPaused(T0.AddSeconds(100), true);
        reference.SetPaused(T0.AddSeconds(250), false);
        Assert.Equal(reference.GetSnapshot(T0.AddSeconds(400)), tracker.GetSnapshot(T0.AddSeconds(400)));
    }

    [Fact]
    public void Repeating_sos_or_dnf_does_not_replay()
    {
        var tracker = new ParticipantTracker(Loop, Sport.Cycling, T0);
        tracker.ApplyMany(Enumerable.Range(0, 60).Select(i => FixAt(40 * i, T0.AddSeconds(5 * i))).ToList());
        var replays = tracker.ReplayCount;

        for (var i = 0; i < 5; i++)
        {
            tracker.SetSos(true);
            tracker.MarkDnf(T0.AddSeconds(300 + i));
        }

        Assert.Equal(replays, tracker.ReplayCount);
    }
}
