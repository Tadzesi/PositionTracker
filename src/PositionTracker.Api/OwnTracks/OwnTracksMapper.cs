using System.Text.Json;
using PositionTracker.Core;

namespace PositionTracker.Api.OwnTracks;

/// <summary>
/// Maps an OwnTracks JSON message (HTTP mode) to a <see cref="PositionBatch"/> (booklet "JSON" page, checked
/// 2026-10-31, see docs/ops/owntracks-setup.md): <c>_type</c> must be <c>location</c> with numeric <c>lat</c>/<c>lon</c>
/// in range and <c>tst</c> as integer epoch <b>seconds</b> (milliseconds and values outside 2000..2100 are invalid).
/// <c>acc</c> → accuracy (m, optional); <c>batt</c> → battery % (outside 0..100 → unknown); <c>bs</c> 1 → not charging,
/// 2/3 → charging, else unknown. <c>vel</c> and <c>alt</c> are ignored (speed and elevation come from the course
/// matching). The queue length and the foreground flag are not reported by OwnTracks: 0 and true. One message = one fix.
/// </summary>
public static class OwnTracksMapper
{
    private static readonly long MinTst = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly long MaxTst = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>The batch of a valid location message; null for any other type or an invalid message.</summary>
    public static PositionBatch? ToBatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("_type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "location")
            return null;
        if (!TryNumber(message, "lat", out var lat) || lat is < -90 or > 90) return null;
        if (!TryNumber(message, "lon", out var lon) || lon is < -180 or > 180) return null;
        if (!TryNumber(message, "tst", out var tst) || tst != Math.Floor(tst) || tst < MinTst || tst >= MaxTst) return null;

        double? accuracy = TryNumber(message, "acc", out var acc) && acc >= 0 ? acc : null;
        double? battery = TryNumber(message, "batt", out var batt) && batt is >= 0 and <= 100 ? batt : null;
        bool? charging = TryNumber(message, "bs", out var bs) ? bs switch { 1 => false, 2 or 3 => true, _ => null } : null;

        var fix = new PositionFix(DateTimeOffset.FromUnixTimeSeconds((long)tst), lat, lon, accuracy);
        return new PositionBatch([fix], new DeviceStatus(battery, charging, 0, true));
    }

    private static bool TryNumber(JsonElement message, string name, out double value)
    {
        value = 0;
        return message.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetDouble(out value) && double.IsFinite(value);
    }
}
