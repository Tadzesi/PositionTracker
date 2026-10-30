using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class CourseMatcherTests
{
    private const double MetresPerDegLat = 111_195;

    private static readonly Course Loop = Course.Create(TestCourses.Loop(), 1);

    /// <summary>Moves a point by the given metres north and east (local planar approximation).</summary>
    private static GeoPoint Offset(GeoPoint p, double northM, double eastM) =>
        new(p.Lat + northM / MetresPerDegLat,
            p.Lon + eastM / (MetresPerDegLat * Math.Cos(p.Lat * Math.PI / 180)));

    /// <summary>Signed forward difference in lap space, wrapped to (-L/2, L/2] for closed courses.</summary>
    private static double Advance(Course course, double from, double to)
    {
        var d = to - from;
        if (course.IsClosed)
        {
            if (d > course.LapLengthM / 2) d -= course.LapLengthM;
            else if (d <= -course.LapLengthM / 2) d += course.LapLengthM;
        }
        return d;
    }

    [Fact]
    public void Point_on_course_matches_its_own_distance_within_5m()
    {
        var p = Loop.PointAt(3_000);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 2_900);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 2_995, 3_005);
        Assert.InRange(r.OffCourseM, 0, 1);
    }

    [Fact]
    public void Point_40m_off_course_matches_with_OffCourseM_about_40()
    {
        // 3 000 m lies on the first (eastbound) edge; move 40 m south, away from the loop.
        var p = Offset(Loop.PointAt(3_000), -40, 0);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 2_900);
        Assert.True(r.Matched);
        Assert.InRange(r.OffCourseM, 38, 42);
        Assert.InRange(r.LapProgressM, 2_995, 3_005);
    }

    [Fact]
    public void Point_90m_off_course_keeps_its_perpendicular_projection()
    {
        // Neighbouring vertices 25 m along are only ~3 m farther than the perpendicular foot;
        // they belong to the same pass of the course and must not win the "closest ahead" tie-break.
        var p = Offset(Loop.PointAt(3_010), -90, 0);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 2_900);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 3_005, 3_015);
    }

    [Fact]
    public void Point_300m_off_course_is_not_matched_and_progress_is_unchanged()
    {
        var p = Offset(Loop.PointAt(3_000), -300, 0);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 2_900);
        Assert.False(r.Matched);
        Assert.Equal(2_900, r.LapProgressM);
        Assert.InRange(r.OffCourseM, 290, double.MaxValue);
    }

    [Fact]
    public void Search_is_limited_to_the_window()
    {
        var p = Loop.PointAt(6_000);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 1_000);
        Assert.False(r.Matched);
        Assert.Equal(1_000, r.LapProgressM);
    }

    [Fact]
    public void Small_backward_jitter_within_50m_is_accepted()
    {
        var p = Loop.PointAt(2_970);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 3_000);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 2_965, 2_975);
    }

    [Fact]
    public void Backward_jump_of_500m_is_not_accepted()
    {
        var p = Loop.PointAt(2_500);
        var r = CourseMatcher.Match(Loop, p.Lat, p.Lon, 3_000);
        Assert.False(r.Matched);
        Assert.Equal(3_000, r.LapProgressM);
    }

    [Fact]
    public void Lap_wrap_on_closed_loop()
    {
        var start = Loop.Points[0];
        var last = Loop.LapLengthM - 50; // about 12 450 m
        var r = CourseMatcher.Match(Loop, start.Lat, start.Lon, last);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 0, 10);
    }

    [Fact]
    public void Open_course_does_not_wrap_at_the_end()
    {
        var oneWay = Course.Create(TestCourses.OutAndBack().Take(201).ToList(), 1); // straight 5 000 m
        Assert.False(oneWay.IsClosed);
        var end = oneWay.Points[^1];
        var r = CourseMatcher.Match(oneWay, end.Lat, end.Lon, oneWay.LapLengthM - 20);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, oneWay.LapLengthM - 5, oneWay.LapLengthM);
    }

    [Theory]
    [InlineData(4_000)] // default demo course: the other branch stays outside the window
    [InlineData(2_000)] // compact: both branches of the crossing are inside the forward window
    public void Figure_eight_crossing_does_not_switch_branch(double lapLengthM)
    {
        var course = Course.Create(TestCourses.FigureEight(lapLengthM), 1);
        double last = 0;
        for (var d = 25.0; d < 2 * course.LapLengthM; d += 25)
        {
            var p = course.PointAt(d % course.LapLengthM);
            var r = CourseMatcher.Match(course, p.Lat, p.Lon, last);
            Assert.True(r.Matched, $"unmatched at {d} m");
            var step = Advance(course, last, r.LapProgressM);
            Assert.True(step > 0 && step < 100, $"step {step:F1} m at {d} m (last {last:F1}, now {r.LapProgressM:F1})");
            last = r.LapProgressM;
        }
    }

    [Fact]
    public void Backward_jitter_on_out_and_back_stays_on_outbound_leg()
    {
        // The return-leg image of the fix (5 710 m) is inside the forward window and equally close.
        var course = Course.Create(TestCourses.OutAndBack(), 1);
        var p = course.PointAt(4_290);
        var r = CourseMatcher.Match(course, p.Lat, p.Lon, 4_300);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 4_285, 4_295);
    }

    [Fact]
    public void Backward_jitter_at_figure_eight_crossing_stays_on_current_branch()
    {
        // Compact figure eight: the crossing is at 0 and ~1 000 m, both inside the window from 1 010 m.
        var course = Course.Create(TestCourses.FigureEight(2_000), 1);
        var crossing = course.LapLengthM / 2;
        var p = course.PointAt(crossing);
        var r = CourseMatcher.Match(course, p.Lat, p.Lon, crossing + 10);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, crossing - 5, crossing + 5);
    }

    [Fact]
    public void Out_and_back_returning_over_same_road_matches_return_leg()
    {
        var course = Course.Create(TestCourses.OutAndBack(), 1); // 5 000 m out, 5 000 m back
        var turn = course.LapLengthM / 2;
        double last = 0;
        for (var d = 25.0; d < course.LapLengthM - 25; d += 25)
        {
            var p = course.PointAt(d);
            var r = CourseMatcher.Match(course, p.Lat, p.Lon, last);
            Assert.True(r.Matched, $"unmatched at {d} m");
            // Right at the turn one fix is genuinely ambiguous (same spot on both legs);
            // once past the backward window the outbound leg is unreachable.
            if (d < turn - 50)
                Assert.True(Math.Abs(r.LapProgressM - d) < 5, $"matched {r.LapProgressM:F1} m for {d} m");
            if (d >= turn + 2 * 50)
                Assert.True(Math.Abs(r.LapProgressM - d) < 5, $"matched {r.LapProgressM:F1} m for {d} m");
            if (d > turn)
                Assert.True(r.LapProgressM >= turn - 50, $"fell back to outbound leg: {r.LapProgressM:F1} m at {d} m");
            last = r.LapProgressM;
        }
    }

    [Fact]
    public void Out_and_back_hairpin_with_slow_3m_steps_recovers_onto_return_leg()
    {
        var course = Course.Create(TestCourses.OutAndBack(), 1);
        var turn = course.LapLengthM / 2;
        // 4 990 m, then 5 004, 5 007, 5 010, ... : off-grid step through the turn, then a slowdown.
        var path = new[] { 4_990.0 }.Concat(Enumerable.Range(0, int.MaxValue).Select(i => turn + 4 + 3 * i)
            .TakeWhile(d => d < course.LapLengthM - 3));
        AssertTracksThroughHairpin(course, turn, path, maxFixesToRecover: 2);
    }

    [Fact]
    public void Out_and_back_hairpin_with_7m_steps_recovers_onto_return_leg()
    {
        var course = Course.Create(TestCourses.OutAndBack(), 1);
        var turn = course.LapLengthM / 2;
        AssertTracksThroughHairpin(course, turn, Walk(course, turn - 15, 7), maxFixesToRecover: 2);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    public void Out_and_back_hairpin_recovers_for_every_start_offset(double stepM)
    {
        var course = Course.Create(TestCourses.OutAndBack(), 1);
        var turn = course.LapLengthM / 2;
        for (var offset = 1; offset <= 49; offset++)
            AssertTracksThroughHairpin(course, turn, Walk(course, turn - 200 - offset, stepM), maxFixesToRecover: 3);
    }

    [Fact]
    public void Duplicated_vertex_is_not_a_separate_candidate()
    {
        // Same 90 m-off setup as above, with the vertex at 3 000 m duplicated (zero-length segment).
        var points = TestCourses.Loop().ToList();
        var dupIndex = points.FindIndex(p => Math.Abs(GeoMath.DistanceM(p, Loop.PointAt(3_000))) < 0.5);
        Assert.True(dupIndex > 0);
        points.Insert(dupIndex, points[dupIndex]);
        var course = Course.Create(points, 1);

        var p = Offset(course.PointAt(3_010), -90, 0);
        var r = CourseMatcher.Match(course, p.Lat, p.Lon, 2_900);
        Assert.True(r.Matched);
        Assert.InRange(r.LapProgressM, 3_005, 3_015);
    }

    private static IEnumerable<double> Walk(Course course, double from, double stepM)
    {
        for (var d = from; d < course.LapLengthM - stepM; d += stepM) yield return d;
    }

    /// <summary>
    /// Feeds fixes at the given true lap distances, carrying the matched progress forward
    /// (the first one seeds last progress). Before the turn every fix must be exact; after it,
    /// at most <paramref name="maxFixesToRecover"/> fixes may be ambiguous, all later ones exact.
    /// </summary>
    private static void AssertTracksThroughHairpin(Course course, double turn, IEnumerable<double> truePath, int maxFixesToRecover)
    {
        double? last = null;
        var fixesPastTurn = 0;
        foreach (var d in truePath)
        {
            var p = course.PointAt(d);
            var r = CourseMatcher.Match(course, p.Lat, p.Lon, last ?? d);
            Assert.True(r.Matched, $"unmatched at {d:F1} m");
            var err = Math.Abs(Advance(course, d, r.LapProgressM));
            if (d > turn) fixesPastTurn++;
            var mustBeExact = d <= turn ? d < turn - 50 : fixesPastTurn > maxFixesToRecover;
            if (mustBeExact)
                Assert.True(err < 5, $"matched {r.LapProgressM:F1} m for true {d:F1} m (fix {fixesPastTurn} past turn)");
            if (d > turn + 100)
                Assert.True(err <= 60, $"matched {r.LapProgressM:F1} m for true {d:F1} m");
            last = r.LapProgressM;
        }
        Assert.True(fixesPastTurn > maxFixesToRecover);
    }
}
