using PositionTracker.Core.Simulation;

namespace PositionTracker.Core.Tests;

public class VirtualRiderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private static Course FlatLoop() => Course.Create(DemoCourses.Loop(), 1);

    private static VirtualRiderProfile Profile(
        int seed = 1, double noise = 0, double jitter = 0,
        IReadOnlyList<(TimeSpan From, TimeSpan To)>? dropouts = null,
        TimeSpan? offAt = null, TimeSpan? offFor = null) =>
        new("r1", 5.0, jitter, noise, seed, dropouts ?? [], null, offAt, offFor);

    [Fact]
    public void Same_seed_gives_identical_sequences()
    {
        var course = FlatLoop();
        var a = VirtualRider.Generate(course, Sport.Running, Profile(7, 5, 0.1), Start, Step).ToList();
        var b = VirtualRider.Generate(course, Sport.Running, Profile(7, 5, 0.1), Start, Step).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_seeds_differ()
    {
        var course = FlatLoop();
        var a = VirtualRider.Generate(course, Sport.Running, Profile(1, 5, 0.1), Start, Step).ToList();
        var b = VirtualRider.Generate(course, Sport.Running, Profile(2, 5, 0.1), Start, Step).ToList();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Noise_free_flat_ride_finishes_at_total_length_in_expected_time()
    {
        // The last fix lies RollOutM past the finish line (on this closed loop: that far into the next lap).
        var course = FlatLoop();
        var fixes = VirtualRider.Generate(course, Sport.Running, Profile(), Start, Step).ToList();
        var last = fixes[^1].Fix;
        var end = course.PointAt(VirtualRider.RollOutM);
        Assert.True(GeoMath.DistanceM(last.Lat, last.Lon, end.Lat, end.Lon) < 1);
        var elapsed = (last.Time - Start).TotalSeconds;
        var expected = (course.TotalLengthM + VirtualRider.RollOutM) / 5.0;
        Assert.InRange(elapsed, expected - 5, expected + 5);
    }

    [Fact]
    public void Fixes_are_exactly_step_apart()
    {
        var fixes = VirtualRider.Generate(FlatLoop(), Sport.Running, Profile(3, 4, 0.2), Start, Step).ToList();
        Assert.Equal(Start, fixes[0].Fix.Time);
        for (var i = 1; i < fixes.Count; i++)
            Assert.Equal(Step, fixes[i].Fix.Time - fixes[i - 1].Fix.Time);
    }

    [Fact]
    public void Dropout_fixes_are_delivered_at_dropout_end()
    {
        var drop = new[] { (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)) };
        var fixes = VirtualRider.Generate(FlatLoop(), Sport.Running, Profile(dropouts: drop), Start, Step).ToList();
        var inside = fixes.Where(f => f.Fix.Time - Start >= drop[0].Item1 && f.Fix.Time - Start < drop[0].Item2).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, f =>
        {
            Assert.Equal(Start + drop[0].Item2, f.DeliverAt);
            Assert.True(f.Fix.Time < f.DeliverAt);
        });
        Assert.All(fixes.Except(inside), f => Assert.Equal(f.Fix.Time, f.DeliverAt));
    }

    [Fact]
    public void Off_course_window_offsets_fixes_by_150m()
    {
        var course = FlatLoop();
        var profile = Profile(offAt: TimeSpan.FromSeconds(100), offFor: TimeSpan.FromSeconds(60));
        var fixes = VirtualRider.Generate(course, Sport.Running, profile, Start, Step).ToList();
        double DistToCourse(PositionFix f) => course.Points.Min(p => GeoMath.DistanceM(f.Lat, f.Lon, p.Lat, p.Lon));
        var off = fixes.Where(f => f.Fix.Time - Start >= TimeSpan.FromSeconds(100) && f.Fix.Time - Start < TimeSpan.FromSeconds(160)).ToList();
        Assert.NotEmpty(off);
        Assert.All(off, f => Assert.InRange(DistToCourse(f.Fix), 148, 153));
        var on = fixes.Where(f => f.Fix.Time - Start < TimeSpan.FromSeconds(100)).ToList();
        Assert.All(on, f => Assert.True(DistToCourse(f.Fix) < 15));
    }

    [Fact]
    public void Non_positive_step_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VirtualRider.Generate(FlatLoop(), Sport.Running, Profile(), Start, TimeSpan.Zero).ToList());
    }

    [Fact]
    public void Values_are_finite_even_with_extreme_jitter()
    {
        var fixes = VirtualRider.Generate(FlatLoop(), Sport.Running, Profile(5, 10, 3.0), Start, Step).ToList();
        Assert.All(fixes, f =>
        {
            Assert.True(double.IsFinite(f.Fix.Lat) && double.IsFinite(f.Fix.Lon));
            Assert.True(f.Fix.AccuracyM >= 3);
        });
    }

    [Fact]
    public void Null_dropouts_throw_when_generate_is_called_not_when_enumerated()
    {
        var profile = Profile() with { Dropouts = null! };

        Assert.Throws<ArgumentNullException>(() => VirtualRider.Generate(FlatLoop(), Sport.Running, profile, Start, Step));
    }

    [Fact]
    public void Multi_lap_ride_runs_all_laps_and_the_tracker_follows_it_to_the_finish()
    {
        var course = Course.Create(DemoCourses.Loop(3_000, 25), 4);
        var fixes = VirtualRider.Generate(course, Sport.Running, Profile(9, 3, 0.05), Start, Step).ToList();

        var tracker = new ParticipantTracker(course, Sport.Running, Start);
        tracker.ApplyMany(fixes.Select(f => f.Fix).ToList());
        var s = tracker.GetSnapshot(fixes[^1].Fix.Time);

        Assert.InRange((fixes[^1].Fix.Time - Start).TotalSeconds, 12_000 / 5.0 * 0.95, 12_000 / 5.0 * 1.05);
        Assert.Equal(ParticipantState.Finished, s.State);
        Assert.Equal(4, s.Lap);
        Assert.Equal(course.TotalLengthM, s.TotalDistanceM);
    }

    [Fact]
    public void Graded_course_slows_the_rider_on_the_climb_and_speeds_it_up_on_the_descent()
    {
        // 4 km loop: 2 km at +4 %, 2 km at -4 %. Running factors: 0.88 up, 1.12 down.
        var course = Course.Create(DemoCourses.Loop(4_000, 25, d => d < 2_000 ? 0.04 * d : 80 - 0.04 * (d - 2_000)), 1);
        var fixes = VirtualRider.Generate(course, Sport.Running, Profile(), Start, Step).ToList();
        var tracker = new ParticipantTracker(course, Sport.Running, Start);
        var at = new Dictionary<int, double>();
        foreach (var f in fixes) at[(int)(f.Fix.Time - Start).TotalSeconds] = tracker.Apply(f.Fix).Snapshot.TotalDistanceM;

        var upSpeed = (at[300] - at[100]) / 200;
        var downSpeed = (at[700] - at[500]) / 200;

        Assert.InRange(upSpeed, 5 * 0.88 * 0.95, 5 * 0.88 * 1.05);
        Assert.InRange(downSpeed, 5 * 1.12 * 0.95, 5 * 1.12 * 1.05);
        var total = (fixes[^1].Fix.Time - Start).TotalSeconds;
        Assert.InRange(total, 2_000 / (5 * 0.88) + 2_000 / (5 * 1.12) - 15, 2_000 / (5 * 0.88) + 2_000 / (5 * 1.12) + 15);
    }
}
