using System.Text;
using PositionTracker.Core;

namespace PositionTracker.Core.Tests;

public class GpxParserTests
{
    private const string Ns = "http://www.topografix.com/GPX/1/1";

    private static MemoryStream Stream(string s) => new(Encoding.UTF8.GetBytes(s));

    private static string Gpx(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><gpx version=\"1.1\" creator=\"t\" xmlns=\"{Ns}\">{body}</gpx>";

    [Fact]
    public void Parses_track_points_with_elevation()
    {
        var gpx = Gpx("<trk><trkseg>" +
            "<trkpt lat=\"49.0\" lon=\"18.0\"><ele>400</ele></trkpt>" +
            "<trkpt lat=\"49.1\" lon=\"18.1\"><ele>410.5</ele></trkpt>" +
            "<trkpt lat=\"49.2\" lon=\"18.2\"><ele>420</ele></trkpt>" +
            "</trkseg></trk>");

        var pts = GpxParser.Parse(Stream(gpx));

        Assert.Equal(3, pts.Count);
        Assert.Equal(new GeoPoint(49.0, 18.0, 400), pts[0]);
        Assert.Equal(410.5, pts[1].Ele);
        Assert.Equal(18.2, pts[2].Lon);
        Assert.Equal(420, pts[2].Ele);
    }

    [Fact]
    public void Missing_elevation_gives_null_ele()
    {
        var gpx = Gpx("<trk><trkseg>" +
            "<trkpt lat=\"49.0\" lon=\"18.0\"/>" +
            "<trkpt lat=\"49.1\" lon=\"18.1\"/>" +
            "</trkseg></trk>");

        var pts = GpxParser.Parse(Stream(gpx));

        Assert.All(pts, p => Assert.Null(p.Ele));
    }

    [Fact]
    public void Joins_multiple_track_segments_in_order()
    {
        var gpx = Gpx("<trk>" +
            "<trkseg><trkpt lat=\"1\" lon=\"1\"/><trkpt lat=\"2\" lon=\"2\"/></trkseg>" +
            "<trkseg><trkpt lat=\"3\" lon=\"3\"/><trkpt lat=\"4\" lon=\"4\"/></trkseg>" +
            "</trk>");

        var pts = GpxParser.Parse(Stream(gpx));

        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, pts.Select(p => p.Lat).ToArray());
    }

    [Fact]
    public void Accepts_route_points_rtept()
    {
        var gpx = Gpx("<rte><rtept lat=\"49.0\" lon=\"18.0\"/><rtept lat=\"49.1\" lon=\"18.1\"/></rte>");

        var pts = GpxParser.Parse(Stream(gpx));

        Assert.Equal(2, pts.Count);
    }

    [Fact]
    public void Single_point_throws_GpxFormatException()
    {
        var gpx = Gpx("<trk><trkseg><trkpt lat=\"49.0\" lon=\"18.0\"/></trkseg></trk>");

        var ex = Assert.Throws<GpxFormatException>(() => GpxParser.Parse(Stream(gpx)));

        Assert.Contains("at least 2", ex.Message);
    }

    [Fact]
    public void Non_xml_throws_GpxFormatException()
    {
        Assert.Throws<GpxFormatException>(() => GpxParser.Parse(Stream("this is not xml")));
    }
}
