using PositionTracker.Core.Simulation;

namespace PositionTracker.Core.Tests.TestData;

internal static class TestCourses
{
    public static IReadOnlyList<GeoPoint> Loop(double lapLengthM = 12_500, double stepM = 25, Func<double, double>? elevationAtM = null)
        => DemoCourses.Loop(lapLengthM, stepM, elevationAtM);

    public static IReadOnlyList<GeoPoint> FigureEight(double lapLengthM = 4_000, double stepM = 10)
        => DemoCourses.FigureEight(lapLengthM, stepM);

    public static IReadOnlyList<GeoPoint> OutAndBack(double oneWayM = 5_000, double stepM = 25)
        => DemoCourses.OutAndBack(oneWayM, stepM);
}
