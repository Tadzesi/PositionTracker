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
        var course = FlatLoop();
        var fixes = VirtualRider.Generate(course, Sport.Running, Profile(), Start, Step).ToList();
        var last = fixes[^1].Fix;
        var end = course.PointAt(course.LapLengthM);
        Assert.True(GeoMath.DistanceM(last.Lat, last.Lon, end.Lat, end.Lon) < 1);
        var elapsed = (last.Time - Start).TotalSeconds;
        Assert.InRange(elapsed, course.TotalLengthM / 5.0 - 5, course.TotalLengthM / 5.0 + 5);
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
}
