using PositionTracker.Core;

namespace PositionTracker.Core.Tests;

public class PositionFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private const double BaseLat = 49.0;
    private const double BaseLon = 18.0;
    private const double MetersPerDegLat = 111_195.0;

    private static PositionFix Fix(double secondsFromT0, double northMeters = 0, double? accuracy = 5) =>
        new(T0.AddSeconds(secondsFromT0), BaseLat + northMeters / MetersPerDegLat, BaseLon, accuracy);

    private static readonly FilterOptions Runner = FilterOptions.For(Sport.Running);
    private static readonly FilterOptions Cyclist = FilterOptions.For(Sport.Cycling);

    [Fact]
    public void Options_per_sport()
    {
        Assert.Equal(new FilterOptions(50, 10), Runner);
        Assert.Equal(new FilterOptions(50, 30), Cyclist);
    }

    [Fact]
    public void A_negative_accuracy_is_rejected()
    {
        Assert.Equal(FilterDecision.RejectAccuracy, PositionFilter.Evaluate(Fix(0, accuracy: -1), null, Runner));
    }

    [Fact]
    public void Accuracy_above_limit_is_rejected()
    {
        Assert.Equal(FilterDecision.RejectAccuracy, PositionFilter.Evaluate(Fix(0, accuracy: 51), null, Runner));
    }

    [Fact]
    public void Accuracy_at_limit_is_accepted()
    {
        Assert.Equal(FilterDecision.Accept, PositionFilter.Evaluate(Fix(0, accuracy: 50), null, Runner));
    }

    [Fact]
    public void Null_accuracy_is_accepted()
    {
        Assert.Equal(FilterDecision.Accept, PositionFilter.Evaluate(Fix(0, accuracy: null), null, Runner));
    }

    [Fact]
    public void Thousand_meter_jump_in_ten_seconds_is_impossible_for_both_sports()
    {
        var prev = Fix(0);
        var fix = Fix(10, northMeters: 1000);
        Assert.Equal(FilterDecision.RejectImpossibleSpeed, PositionFilter.Evaluate(fix, prev, Runner));
        Assert.Equal(FilterDecision.RejectImpossibleSpeed, PositionFilter.Evaluate(fix, prev, Cyclist));
    }

    [Fact]
    public void Twenty_mps_is_impossible_for_runner_but_fine_for_cyclist()
    {
        var prev = Fix(0);
        var fix = Fix(10, northMeters: 200);
        Assert.Equal(FilterDecision.RejectImpossibleSpeed, PositionFilter.Evaluate(fix, prev, Runner));
        Assert.Equal(FilterDecision.Accept, PositionFilter.Evaluate(fix, prev, Cyclist));
    }

    [Fact]
    public void Identical_time_and_position_is_duplicate()
    {
        var prev = Fix(0);
        Assert.Equal(FilterDecision.RejectDuplicate, PositionFilter.Evaluate(Fix(0), prev, Runner));
    }

    [Fact]
    public void Same_time_different_position_is_duplicate_without_division_by_zero()
    {
        var prev = Fix(0);
        var fix = Fix(0, northMeters: 500);
        Assert.Equal(FilterDecision.RejectDuplicate, PositionFilter.Evaluate(fix, prev, Runner));
        Assert.Equal(FilterDecision.RejectDuplicate, PositionFilter.Evaluate(fix, prev, Cyclist));
    }

    [Fact]
    public void Older_fix_is_evaluated_by_absolute_time_delta()
    {
        var prev = Fix(10);
        var plausible = Fix(0, northMeters: 50); // 50 m in |-10| s = 5 m/s
        var impossible = Fix(0, northMeters: 1000); // 100 m/s
        Assert.Equal(FilterDecision.Accept, PositionFilter.Evaluate(plausible, prev, Runner));
        Assert.Equal(FilterDecision.RejectImpossibleSpeed, PositionFilter.Evaluate(impossible, prev, Runner));
    }

    [Fact]
    public void Non_finite_coordinates_are_never_accepted()
    {
        var prev = Fix(0);
        var bad = new PositionFix(T0.AddSeconds(10), double.NaN, BaseLon, 5);
        Assert.Equal(FilterDecision.RejectImpossibleSpeed, PositionFilter.Evaluate(bad, prev, Runner));
    }

    [Fact]
    public void First_fix_without_previous_is_accepted()
    {
        Assert.Equal(FilterDecision.Accept, PositionFilter.Evaluate(Fix(0), null, Runner));
    }
}
