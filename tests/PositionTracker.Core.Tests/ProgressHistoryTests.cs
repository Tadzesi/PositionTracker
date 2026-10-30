namespace PositionTracker.Core.Tests;

public class ProgressHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TimeAt_interpolates_between_points()
    {
        var h = new ProgressHistory();
        h.Add(T0, 0);
        h.Add(T0.AddSeconds(10), 100);

        Assert.Equal(T0.AddSeconds(5), h.TimeAt(50));
    }

    [Fact]
    public void TimeAt_returns_null_outside_range()
    {
        var h = new ProgressHistory();
        Assert.Null(h.TimeAt(0));

        h.Add(T0, 10);
        h.Add(T0.AddSeconds(10), 100);

        Assert.Null(h.TimeAt(5));
        Assert.Null(h.TimeAt(100.1));
        Assert.Equal(T0, h.TimeAt(10));
        Assert.Equal(T0.AddSeconds(10), h.TimeAt(100));
    }

    [Fact]
    public void TimeAt_on_a_plateau_returns_the_first_time_the_distance_was_reached()
    {
        var h = new ProgressHistory();
        h.Add(T0, 0);
        h.Add(T0.AddSeconds(10), 100);
        h.Add(T0.AddSeconds(20), 100);
        h.Add(T0.AddSeconds(30), 200);

        Assert.Equal(T0.AddSeconds(10), h.TimeAt(100));
    }

    [Fact]
    public void Distance_never_decreases_when_a_lower_value_is_added()
    {
        var h = new ProgressHistory();
        h.Add(T0, 100);
        h.Add(T0.AddSeconds(5), 80);

        Assert.Equal((T0.AddSeconds(5), 100.0), h.Last);
    }

    [Fact]
    public void SpeedMps_over_10s_window_is_10_for_10mps_motion()
    {
        var h = new ProgressHistory();
        for (var s = 0; s <= 60; s++) h.Add(T0.AddSeconds(s), 10.0 * s);

        Assert.Equal(10, h.SpeedMps(T0.AddSeconds(60), TimeSpan.FromSeconds(10)), 9);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(10.4)]
    [InlineData(15.0)]
    public void SpeedMps_with_points_about_one_window_apart_uses_the_point_before_the_window(double intervalS)
    {
        var h = new ProgressHistory();
        for (var k = 0; k <= 6; k++) h.Add(T0.AddSeconds(k * intervalS), 8 * k * intervalS);

        Assert.Equal(8, h.SpeedMps(T0.AddSeconds(6 * intervalS), TimeSpan.FromSeconds(10)), 9);
    }

    [Fact]
    public void SpeedMps_across_a_gap_longer_than_twice_the_window_is_zero()
    {
        var h = new ProgressHistory();
        h.Add(T0, 0);
        h.Add(T0.AddSeconds(21), 168);

        Assert.Equal(0, h.SpeedMps(T0.AddSeconds(21), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void SpeedMps_with_single_point_is_zero()
    {
        var h = new ProgressHistory();
        Assert.Equal(0, h.SpeedMps(T0, TimeSpan.FromSeconds(10)));

        h.Add(T0, 50);
        Assert.Equal(0, h.SpeedMps(T0, TimeSpan.FromSeconds(10)));
    }
}
