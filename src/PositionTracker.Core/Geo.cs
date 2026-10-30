namespace PositionTracker.Core;

public readonly record struct GeoPoint(double Lat, double Lon, double? Ele = null);

public static class GeoMath
{
    private const double EarthRadiusM = 6_371_008.8;

    public static double DistanceM(GeoPoint a, GeoPoint b) => DistanceM(a.Lat, a.Lon, b.Lat, b.Lon);

    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRad(lat1);
        var phi2 = ToRad(lat2);
        var dPhi = ToRad(lat2 - lat1);
        var dLambda = ToRad(lon2 - lon1);
        var h = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
              + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
