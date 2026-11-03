using System.Globalization;
using System.Text.RegularExpressions;

namespace LoadLab;

/// <summary>A closed GPX loop with cumulative distances; positions are interpolated along it.</summary>
public sealed partial class Course
{
    private readonly (double Lat, double Lon)[] _points;
    private readonly double[] _cumulative;

    public double LengthM { get; }

    private Course((double Lat, double Lon)[] points)
    {
        _points = points;
        _cumulative = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            _cumulative[i] = _cumulative[i - 1] + Distance(points[i - 1], points[i]);
        LengthM = _cumulative[^1];
    }

    public static Course Load(string gpxPath)
    {
        var text = File.ReadAllText(gpxPath);
        var points = TrackPoint().Matches(text)
            .Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToArray();
        if (points.Length < 2) throw new InvalidOperationException("The GPX has no track points.");
        return new Course(points);
    }

    /// <summary>The position at <paramref name="distanceM"/> metres along the loop (wraps around).</summary>
    public (double Lat, double Lon) At(double distanceM)
    {
        var d = distanceM % LengthM;
        if (d < 0) d += LengthM;
        var i = Array.BinarySearch(_cumulative, d);
        if (i >= 0) return _points[i];
        i = ~i;
        var a = _points[i - 1];
        var b = _points[i];
        var span = _cumulative[i] - _cumulative[i - 1];
        var f = span <= 0 ? 0 : (d - _cumulative[i - 1]) / span;
        return (a.Lat + (b.Lat - a.Lat) * f, a.Lon + (b.Lon - a.Lon) * f);
    }

    private static double Distance((double Lat, double Lon) p, (double Lat, double Lon) q)
    {
        const double r = 6371000;
        var la1 = p.Lat * Math.PI / 180;
        var la2 = q.Lat * Math.PI / 180;
        var x = (q.Lon - p.Lon) * Math.PI / 180 * Math.Cos((la1 + la2) / 2);
        var y = la2 - la1;
        return r * Math.Sqrt(x * x + y * y);
    }

    [GeneratedRegex("<trkpt[^>]*lat=\"([-\\d.]+)\"[^>]*lon=\"([-\\d.]+)\"")]
    private static partial Regex TrackPoint();
}
