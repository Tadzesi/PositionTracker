using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PositionTracker.Core;

public sealed class GpxFormatException(string message) : Exception(message);

public static class GpxParser
{
    /// <summary>
    /// Points closer than this to the previous kept point are dropped (<see cref="Thin"/>). GPS noise below that adds nothing to
    /// the course, but every point is a segment the course matcher scans for each fix: a 1 MB GPX of densely packed points on a
    /// short lap would make every fix of the event cost thousands of segments.
    /// </summary>
    public const double MinPointSpacingM = 2.0;

    /// <summary>
    /// Keeps the first point, every point at least <paramref name="minSpacingM"/> from the last kept one, and the last point
    /// (a closed lap stays closed). Deterministic, so a stored GPX always yields the same course.
    /// </summary>
    public static List<GeoPoint> Thin(IReadOnlyList<GeoPoint> points, double minSpacingM)
    {
        var kept = new List<GeoPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (kept.Count == 0 || i == points.Count - 1 || GeoMath.DistanceM(kept[^1], p) >= minSpacingM) kept.Add(p);
        }
        // the last point may sit right after the previous kept one: drop that previous one instead, unless it is the first
        if (kept.Count > 2 && GeoMath.DistanceM(kept[^2], kept[^1]) < minSpacingM) kept.RemoveAt(kept.Count - 2);
        return kept;
    }

    /// <summary>
    /// Reads the track points (or, without any, the route points) of a GPX document. A DTD is refused and no
    /// external resource is resolved. Latitude and longitude must be finite and in range; an empty <c>ele</c>
    /// element means no elevation for that point. Anything else invalid is a <see cref="GpxFormatException"/>.
    /// </summary>
    public static IReadOnlyList<GeoPoint> Parse(Stream gpx)
    {
        ArgumentNullException.ThrowIfNull(gpx);
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(gpx, settings);
            var doc = XDocument.Load(reader);
            var points = ReadPoints(doc, "trkpt");
            if (points.Count == 0)
                points = ReadPoints(doc, "rtept");
            points = Thin(points, MinPointSpacingM);
            if (points.Count < 2)
                throw new GpxFormatException("GPX must contain at least 2 track or route points.");
            return points;
        }
        catch (XmlException ex)
        {
            throw new GpxFormatException($"Invalid GPX XML: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new GpxFormatException($"Invalid number in GPX: {ex.Message}");
        }
    }

    private static List<GeoPoint> ReadPoints(XDocument doc, string localName) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == localName)
            .Select(e => new GeoPoint(
                Coordinate(Required(e, "lat"), "lat", 90),
                Coordinate(Required(e, "lon"), "lon", 180),
                e.Elements().FirstOrDefault(c => c.Name.LocalName == "ele") is { } ele && !string.IsNullOrWhiteSpace(ele.Value)
                    ? Finite(ParseDouble(ele.Value), "ele")
                    : null))
            .ToList();

    private static string Required(XElement e, string attr) =>
        (string?)e.Attribute(attr) ?? throw new GpxFormatException($"Point is missing '{attr}' attribute.");

    private static double Coordinate(string s, string name, double limit)
    {
        var value = Finite(ParseDouble(s), name);
        return Math.Abs(value) <= limit ? value : throw new GpxFormatException($"'{name}' {s} is out of range.");
    }

    private static double Finite(double value, string name) =>
        double.IsFinite(value) ? value : throw new GpxFormatException($"'{name}' must be a finite number.");

    private static double ParseDouble(string s) =>
        double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
