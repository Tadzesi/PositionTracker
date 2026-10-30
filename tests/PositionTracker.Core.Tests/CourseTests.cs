using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class CourseTests
{
    private static double Hill(double d) => d <= 6_250 ? d / 6_250 * 100 : (12_500 - d) / 6_250 * 100;

    [Fact]
    public void Lap_length_of_rectangular_loop_is_12500_within_25m()
    {
        var course = Course.Create(TestCourses.Loop(), 1);
        Assert.InRange(course.LapLengthM, 12_475, 12_525);
        Assert.True(course.IsClosed);
    }

    [Fact]
    public void Three_laps_total_length_is_three_times_lap_length()
    {
        var course = Course.Create(TestCourses.Loop(), 3);
        Assert.Equal(3 * course.LapLengthM, course.TotalLengthM, 6);
        Assert.Equal(3, course.Laps);
    }

    [Fact]
    public void Non_closed_course_with_two_laps_throws_ArgumentException()
    {
        var oneWay = TestCourses.OutAndBack().Take(201).ToList();
        Assert.Throws<ArgumentException>(() => Course.Create(oneWay, 2));
    }

    [Fact]
    public void Checkpoint_outside_lap_range_throws_ArgumentException()
    {
        var pts = TestCourses.Loop();
        Assert.Throws<ArgumentException>(() => Course.Create(pts, 1, new[] { new Checkpoint("far", 20_000) }));
        Assert.Throws<ArgumentException>(() => Course.Create(pts, 1, new[] { new Checkpoint("zero", 0) }));
    }

    [Fact]
    public void Ascent_of_single_hill_is_100m_within_5m()
    {
        var course = Course.Create(TestCourses.Loop(elevationAtM: Hill), 1);
        Assert.True(course.HasElevation);
        Assert.InRange(course.AscentPerLapM, 95, 105);
        Assert.InRange(course.DescentPerLapM, 95, 105);
    }

    [Fact]
    public void Noise_of_plus_minus_1_5m_on_flat_course_gives_ascent_below_5m()
    {
        var i = 0;
        var course = Course.Create(TestCourses.Loop(elevationAtM: _ => 200 + (i++ % 2 == 0 ? 1.5 : -1.5)), 1);
        Assert.True(course.AscentPerLapM < 5);
        Assert.True(course.DescentPerLapM < 5);
    }

    [Fact]
    public void Course_without_elevation_has_HasElevation_false_ascent_zero_and_ElevationAt_zero()
    {
        var course = Course.Create(TestCourses.Loop(), 1);
        Assert.False(course.HasElevation);
        Assert.Equal(0, course.AscentPerLapM);
        Assert.Equal(0, course.DescentPerLapM);
        Assert.Equal(0, course.ElevationAt(3_000));
    }

    [Fact]
    public void AscentUpTo_one_and_a_half_laps_equals_one_lap_plus_half_lap_ascent()
    {
        var course = Course.Create(TestCourses.Loop(elevationAtM: Hill), 2);
        var lap = course.LapLengthM;
        var expected = course.AscentPerLapM + course.AscentUpTo(lap / 2);
        Assert.Equal(expected, course.AscentUpTo(1.5 * lap), 6);
        Assert.Equal(2 * course.AscentPerLapM, course.AscentUpTo(2 * lap), 6);
    }

    [Fact]
    public void SegmentBoundaries_for_two_checkpoints_are_zero_cp1_cp2_lapLength()
    {
        var course = Course.Create(TestCourses.Loop(), 1, new[] { new Checkpoint("b", 8_000), new Checkpoint("a", 4_000) });
        Assert.Equal(new[] { 0, 4_000, 8_000, course.LapLengthM }, course.SegmentBoundariesM);
        Assert.Equal(3, course.SegmentCount);
        Assert.Equal("a", course.Checkpoints[0].Name);
    }
}
