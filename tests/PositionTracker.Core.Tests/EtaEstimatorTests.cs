using PositionTracker.Core;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class EtaEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static Course Flat() => Course.Create(TestCourses.Loop(12_500), 3);

    private static Course Climbing(double slope) =>
        Course.Create(TestCourses.Loop(12_500, 25, d => slope * d), 3);

    private static ParticipantSnapshot Snap(double totalM, double speedMps, double gradePercent = 0, TimeSpan? moving = null) =>
        new(Now, null, null, null, totalM, 0, 0, 0, speedMps, speedMps, speedMps, speedMps, null,
            moving ?? TimeSpan.FromMinutes(30), TimeSpan.Zero, 0, 0, gradePercent, ParticipantState.Live, null, null, null,
            EtaSpeedMps: speedMps, EtaWindowFromM: totalM);

    [Fact]
    public void Flat_course_expected_is_remaining_distance_over_speed_with_ordered_spread()
    {
        var course = Flat();
        var eta = EtaEstimator.Estimate(course, Sport.Cycling, Snap(10_000, 8), Now);

        Assert.NotNull(eta);
        Assert.Equal((course.TotalLengthM - 10_000) / 8.0, (eta!.Expected - Now).TotalSeconds, 0.5);
        Assert.True(eta.Earliest < eta.Expected);
        Assert.True(eta.Expected < eta.Latest);
        Assert.False(eta.Approximate);
    }

    [Fact]
    public void Spread_is_8_percent_of_remaining_time_but_at_least_60_seconds()
    {
        var course = Flat();
        var wide = EtaEstimator.Estimate(course, Sport.Cycling, Snap(10_000, 8), Now)!;
        var expectedSpread = 0.08 * (wide.Expected - Now).TotalSeconds;
        Assert.Equal(expectedSpread, (wide.Latest - wide.Expected).TotalSeconds, 0.01);
        Assert.Equal(expectedSpread, (wide.Expected - wide.Earliest).TotalSeconds, 0.01);

        var narrow = EtaEstimator.Estimate(course, Sport.Cycling, Snap(course.TotalLengthM - 400, 8), Now)!;
        Assert.Equal(60, (narrow.Latest - narrow.Expected).TotalSeconds, 1);
        // Earliest never lies before now (remaining time here is only 50 s).
        Assert.Equal(Now, narrow.Earliest);
    }

    [Fact]
    public void Climbing_remainder_gives_later_expected_time_than_flat()
    {
        var flat = EtaEstimator.Estimate(Flat(), Sport.Cycling, Snap(10_000, 8), Now)!;
        var hilly = EtaEstimator.Estimate(Climbing(0.05), Sport.Cycling, Snap(10_000, 8), Now)!;

        Assert.True(hilly.Expected > flat.Expected);
    }

    [Fact]
    public void At_or_after_target_distance_returns_now()
    {
        var course = Flat();
        var atFinish = EtaEstimator.Estimate(course, Sport.Cycling, Snap(course.TotalLengthM, 8), Now)!;
        Assert.Equal(Now, atFinish.Expected);
        Assert.Equal(Now, atFinish.Earliest);
        Assert.Equal(Now, atFinish.Latest);

        var past = EtaEstimator.Estimate(course, Sport.Cycling, Snap(course.TotalLengthM + 100, 8), Now)!;
        Assert.Equal(Now, past.Expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.29)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unusable_moving_speed_returns_null(double speed)
    {
        Assert.Null(EtaEstimator.Estimate(Flat(), Sport.Cycling, Snap(0, speed), Now));
    }

    [Fact]
    public void Approximate_true_with_4_minutes_of_moving_time_false_with_6()
    {
        var four = EtaEstimator.Estimate(Flat(), Sport.Running, Snap(1_000, 3, moving: TimeSpan.FromMinutes(4)), Now)!;
        var six = EtaEstimator.Estimate(Flat(), Sport.Running, Snap(1_000, 3, moving: TimeSpan.FromMinutes(6)), Now)!;

        Assert.True(four.Approximate);
        Assert.False(six.Approximate);
    }

    [Fact]
    public void Target_at_checkpoint_distance_returns_time_to_that_checkpoint()
    {
        var course = Course.Create(TestCourses.Loop(12_500), 3, new[] { new Checkpoint("CP1", 4_000) });

        var eta = EtaEstimator.Estimate(course, Sport.Cycling, Snap(1_000, 8), Now, targetTotalDistanceM: 4_000)!;

        Assert.Equal(3_000 / 8.0, (eta.Expected - Now).TotalSeconds, 0.5);
    }

    [Fact]
    public void Target_in_next_lap_crosses_the_lap_boundary()
    {
        var eta = EtaEstimator.Estimate(Flat(), Sport.Cycling, Snap(12_000, 8), Now, targetTotalDistanceM: 13_050)!;

        Assert.Equal(1_050 / 8.0, (eta.Expected - Now).TotalSeconds, 0.5);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(-0.30)]
    public void Extreme_grades_are_clamped_so_result_stays_finite_and_positive(double slope)
    {
        foreach (var sport in new[] { Sport.Cycling, Sport.Running })
        {
            var eta = EtaEstimator.Estimate(Climbing(slope), sport, Snap(10_000, 8, gradePercent: slope * 100), Now)!;

            var seconds = (eta.Expected - Now).TotalSeconds;
            Assert.True(double.IsFinite(seconds));
            Assert.True(seconds > 0);
            Assert.True(eta.Earliest < eta.Expected && eta.Expected < eta.Latest);
        }
    }

    [Fact]
    public void Grade_model_clamps_speed_factor()
    {
        Assert.Equal(0.3, GradeModel.SpeedFactor(Sport.Cycling, 30));
        Assert.Equal(2.0, GradeModel.SpeedFactor(Sport.Cycling, -30));
        Assert.Equal(0.4, GradeModel.SpeedFactor(Sport.Running, 40));
        Assert.Equal(1.5, GradeModel.SpeedFactor(Sport.Running, -40));
        Assert.Equal(1.0, GradeModel.SpeedFactor(Sport.Running, 0));
        Assert.Equal(0.7, GradeModel.SpeedFactor(Sport.Cycling, 5), 10);
    }

    /// <summary>Flat to 6 000 m of the lap, then 10 % up to 6 500 m, then flat again at 50 m.</summary>
    private static Course FootOfClimb() => Course.Create(
        TestCourses.Loop(12_500, 25, d => d < 6_000 ? 0 : d < 6_500 ? (d - 6_000) * 0.10 : 50), 1);

    [Fact]
    public void Flat_speed_comes_from_the_grade_of_the_window_not_from_the_grade_at_the_rider()
    {
        // 8 m/s over the last 7 minutes on the flat; the rider now stands at the foot of a 10 % climb, where the
        // smoothed grade is already steep. The flat speed must stay about 8 m/s, so the time up to 6 500 m is close
        // to the course's own grades at 8 m/s flat speed (the raw profile alone would give 500 m / (8 x 0.4) =
        // 156 s; smoothing starts the ramp about 125 m earlier and moves part of it past 6 500 m). Using the grade
        // at the rider instead inflates the flat speed by 1 / 0.7 and gives about 95 s.
        var course = FootOfClimb();
        var snap = Snap(6_000, 8, gradePercent: course.GradePercentAt(6_000)) with { EtaWindowFromM = 6_000 - 8 * 420 };
        var reference = Enumerable.Range(0, 5)
            .Sum(i => 100 / (8 * GradeModel.SpeedFactor(Sport.Cycling, course.GradePercentAt(6_050 + 100 * i))));

        var eta = EtaEstimator.Estimate(course, Sport.Cycling, snap, Now, targetTotalDistanceM: 6_500)!;

        Assert.True(course.GradePercentAt(6_000) > 3, $"grade {course.GradePercentAt(6_000)}");
        Assert.InRange(reference, 130, 160);
        Assert.InRange((eta.Expected - Now).TotalSeconds, reference * 0.95, reference * 1.01);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(-0.30)]
    public void Clamped_grade_factors_give_the_measured_speed_on_a_constant_grade(double slope)
    {
        // On a constant grade the factor of the window equals the factor ahead, clamped or not, so the remaining
        // 2 000 m take 2 000 / 8 = 250 s.
        var course = Climbing(slope);
        var snap = Snap(4_000, 8, gradePercent: slope * 100) with { EtaWindowFromM = 4_000 - 8 * 420 };

        var eta = EtaEstimator.Estimate(course, Sport.Cycling, snap, Now, targetTotalDistanceM: 6_000)!;

        Assert.InRange((eta.Expected - Now).TotalSeconds, 245, 255);
    }

    [Fact]
    public void Expected_time_is_counted_from_the_last_fix_not_from_now()
    {
        var eta = EtaEstimator.Estimate(Flat(), Sport.Cycling, Snap(10_000, 8), Now.AddMinutes(4))!;

        Assert.Equal((Flat().TotalLengthM - 10_000) / 8.0, (eta.Expected - Now).TotalSeconds, 0.5);
    }

    [Theory]
    [InlineData(ParticipantState.Delayed, true)]
    [InlineData(ParticipantState.Offline, true)]
    [InlineData(ParticipantState.Live, false)]
    public void Approximate_while_delayed_or_offline(ParticipantState state, bool approximate)
    {
        var eta = EtaEstimator.Estimate(Flat(), Sport.Cycling, Snap(10_000, 8) with { State = state }, Now)!;

        Assert.Equal(approximate, eta.Approximate);
    }
}
