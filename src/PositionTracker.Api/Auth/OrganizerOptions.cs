using System.Security.Cryptography;
using System.Text;

namespace PositionTracker.Api.Auth;

/// <summary>Configuration section <c>Organizer</c>; <see cref="ApiKey"/> is required (validated at startup).</summary>
public sealed class OrganizerOptions
{
    public const string Section = "Organizer";

    /// <summary>The request header that carries the organizer key.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Whether <paramref name="candidate"/> is the configured key, compared in constant time (both sides are
    /// hashed first so the comparison does not reveal the key's length either). An empty key never matches.
    /// </summary>
    public bool IsValidKey(string? candidate) =>
        !string.IsNullOrEmpty(candidate) && !string.IsNullOrEmpty(ApiKey)
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey)));
}
