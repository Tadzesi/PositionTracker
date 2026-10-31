using System.Text;
using System.Text.Json.Nodes;
using PositionTracker.Core;

namespace PositionTracker.Api.OwnTracks;

/// <summary>Location reporting settings of the phone, derived from the sport (documented in docs/ops/owntracks-setup.md).</summary>
public sealed record EventTuning(int LocatorIntervalSeconds, int LocatorDisplacementM, int IgnoreInaccurateM)
{
    /// <summary>Move mode: a fix every 5 s cycling, 10 s running; at least 10 m apart; fixes worse than 50 m are not sent.</summary>
    public static EventTuning For(Sport sport) => new(sport == Sport.Cycling ? 5 : 10, 10, 50);
}

/// <summary>The OwnTracks configuration of one participant: the <c>.otrc</c> JSON, the deep link and the QR payload.</summary>
public sealed record OwnTracksConfig(JsonObject OtrcJson, string DeepLink, string QrPayload);

/// <summary>
/// Builds the <c>.otrc</c> configuration (HTTP mode = 3, Basic auth) and the <c>owntracks:///config?inline=</c> deep link.
/// The only secret in it is the device token (the Basic password). The booklet encodes the file with
/// <c>openssl enc -a</c> (standard base64); that text is percent-encoded here so it survives as a URL query value.
/// </summary>
public static class OwnTracksConfigBuilder
{
    public const string IngestPath = "/api/ingest/owntracks";

    public static OwnTracksConfig Build(string serverBaseUrl, string username, string deviceToken, string trackerId, EventTuning tuning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        var otrc = new JsonObject
        {
            ["_type"] = "configuration",
            ["mode"] = 3,
            ["url"] = serverBaseUrl.TrimEnd('/') + IngestPath,
            ["auth"] = true,
            ["username"] = username,
            ["password"] = deviceToken,
            ["tid"] = trackerId,
            ["monitoring"] = 2,
            ["locatorInterval"] = tuning.LocatorIntervalSeconds,
            ["locatorDisplacement"] = tuning.LocatorDisplacementM,
            ["ignoreInaccurateLocations"] = tuning.IgnoreInaccurateM,
            ["cmd"] = false,
        };
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(otrc.ToJsonString()));
        var link = "owntracks:///config?inline=" + Uri.EscapeDataString(base64);
        return new OwnTracksConfig(otrc, link, link);
    }

    /// <summary>The two-character tracker id shown by OwnTracks: the first two letters/digits of the bib (padded with 0).</summary>
    public static string TrackerId(string bib)
    {
        var chars = bib.Where(char.IsAsciiLetterOrDigit).Take(2).Select(char.ToUpperInvariant).ToArray();
        return new string(chars).PadLeft(2, '0');
    }
}
