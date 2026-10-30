using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PositionTracker.Core;

public sealed class GpxFormatException(string message) : Exception(message);

public static class GpxParser
{
    public static IReadOnlyList<GeoPoint> Parse(Stream gpx)
    {
        try
        {
            var doc = XDocument.Load(gpx);
            var points = ReadPoints(doc, "trkpt");
            if (points.Count == 0)
                points = ReadPoints(doc, "rtept");
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
                ParseDouble(Required(e, "lat")),
                ParseDouble(Required(e, "lon")),
                e.Elements().FirstOrDefault(c => c.Name.LocalName == "ele") is { } ele
                    ? ParseDouble(ele.Value)
                    : null))
            .ToList();

    private static string Required(XElement e, string attr) =>
        (string?)e.Attribute(attr) ?? throw new GpxFormatException($"Point is missing '{attr}' attribute.");

    private static double ParseDouble(string s) =>
        double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
