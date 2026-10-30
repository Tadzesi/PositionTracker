using PositionTracker.Core;

namespace PositionTracker.Core.Tests;

public class GeoMathTests
{
    [Fact]
    public void Distance_one_degree_of_longitude_at_49N()
    {
        var d = GeoMath.DistanceM(49, 18, 49, 19);
        Assert.InRange(d, 72_950 - 100, 72_950 + 100);
    }

    [Fact]
    public void Distance_is_symmetric_and_zero_for_same_point()
    {
        var a = new GeoPoint(49.1, 18.2);
        var b = new GeoPoint(49.5, 18.9, 300);
        Assert.Equal(GeoMath.DistanceM(a, b), GeoMath.DistanceM(b, a), 6);
        Assert.Equal(0, GeoMath.DistanceM(a, a), 6);
    }
}
