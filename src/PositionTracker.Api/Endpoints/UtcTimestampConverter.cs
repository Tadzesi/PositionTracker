using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Reads timestamps strictly as UTC ISO 8601 (<c>yyyy-MM-ddTHH:mm:ss[.fffffff]Z</c>); anything else, including
/// an explicit offset or a missing <c>Z</c>, is a <see cref="JsonException"/>.
/// </summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <summary>Parses <paramref name="text"/> with the same strict rule as <see cref="Read"/>.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var value))
            return value;
        throw new JsonException("Expected a UTC ISO 8601 timestamp such as 2026-11-07T10:00:05Z.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
