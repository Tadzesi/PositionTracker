namespace PositionTracker.Core.Simulation;

/// <summary>Deterministic synthetic courses built in local planar metres around (49.0, 18.7).</summary>
public static class DemoCourses
{
    private const double OriginLat = 49.0;
    private const double OriginLon = 18.7;
    private const double MetresPerDegLat = 111_195;

    /// <summary>Rectangle traversed once; the last point equals the first.</summary>
    public static IReadOnlyList<GeoPoint> Loop(double lapLengthM = 12_500, double stepM = 25, Func<double, double>? elevationAtM = null)
    {
        double w = lapLengthM * 0.3, h = lapLengthM * 0.2;
        var corners = new (double X, double Y)[] { (0, 0), (w, 0), (w, h), (0, h), (0, 0) };
        var pts = new List<GeoPoint>();
        double dist = 0;
        for (var c = 0; c < 4; c++)
        {
            var (x0, y0) = corners[c];
            var (x1, y1) = corners[c + 1];
            var len = Math.Abs(x1 - x0) + Math.Abs(y1 - y0);
            var n = Math.Max(1, (int)Math.Round(len / stepM));
            for (var i = 0; i < n; i++)
            {
                var t = (double)i / n;
                pts.Add(ToGeo(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, elevationAtM?.Invoke(dist + len * t)));
            }
            dist += len;
        }
        pts.Add(pts[0]);
        return pts;
    }

    /// <summary>Lemniscate (figure eight) scaled to the requested lap length; closed.</summary>
    public static IReadOnlyList<GeoPoint> FigureEight(double lapLengthM = 4_000, double stepM = 10)
    {
        const int fine = 20_000;
        double X(double t) => Math.Sin(t);
        double Y(double t) => Math.Sin(t) * Math.Cos(t);
        double len = 0;
        for (var i = 1; i <= fine; i++)
        {
            var a = 2 * Math.PI * (i - 1) / fine;
            var b = 2 * Math.PI * i / fine;
            len += Math.Sqrt(Math.Pow(X(b) - X(a), 2) + Math.Pow(Y(b) - Y(a), 2));
        }
        var scale = lapLengthM / len;
        var n = Math.Max(4, (int)Math.Round(lapLengthM / stepM));
        var pts = new List<GeoPoint>();
        for (var i = 0; i < n; i++)
        {
            var t = 2 * Math.PI * i / n;
            pts.Add(ToGeo(X(t) * scale, Y(t) * scale, null));
        }
        pts.Add(pts[0]);
        return pts;
    }

    /// <summary>Straight line out and back along the same path; the last point equals the first.</summary>
    public static IReadOnlyList<GeoPoint> OutAndBack(double oneWayM = 5_000, double stepM = 25)
    {
        var n = Math.Max(1, (int)Math.Round(oneWayM / stepM));
        var pts = new List<GeoPoint>();
        for (var i = 0; i <= n; i++) pts.Add(ToGeo(oneWayM * i / n, 0, null));
        for (var i = n - 1; i >= 0; i--) pts.Add(pts[i]);
        return pts;
    }

    private static GeoPoint ToGeo(double xM, double yM, double? ele)
    {
        var lat = OriginLat + yM / MetresPerDegLat;
        var lon = OriginLon + xM / (MetresPerDegLat * Math.Cos(OriginLat * Math.PI / 180));
        return new GeoPoint(lat, lon, ele);
    }
}
