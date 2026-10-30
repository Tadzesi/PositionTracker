using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Issues and resolves participant bearer tokens. A token is 32 random bytes, base64url encoded, and is returned
/// only once by <see cref="Issue"/>; only its SHA-256 hash is kept. Each participant has at most one active token:
/// issuing again replaces the previous one. Thread-safe.
/// </summary>
public sealed class TokenService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<(string EventId, string ParticipantId), string> _hashByParticipant = new();

    /// <summary>Creates a new token for the participant, invalidating any earlier one, and returns it.</summary>
    public string Issue(string eventId, string participantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentException.ThrowIfNullOrEmpty(participantId);
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var hash = HashBytes(raw);
        var key = Convert.ToHexString(hash);
        lock (_lock)
        {
            RevokeLocked(eventId, participantId);
            _byHash[key] = new Entry(eventId, participantId, hash);
            _hashByParticipant[(eventId, participantId)] = key;
        }
        return raw;
    }

    /// <summary>
    /// The event and participant the token was issued for, or <c>null</c> for an unknown or revoked token.
    /// </summary>
    /// <remarks>
    /// The lookup is keyed by the token's SHA-256 hash, which a caller cannot steer towards a stored hash, and
    /// the stored hash is then compared in constant time.
    /// </remarks>
    public (string EventId, string ParticipantId)? Resolve(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        var hash = HashBytes(rawToken);
        lock (_lock)
        {
            return _byHash.TryGetValue(Convert.ToHexString(hash), out var entry)
                   && CryptographicOperations.FixedTimeEquals(entry.Hash, hash)
                ? (entry.EventId, entry.ParticipantId)
                : null;
        }
    }

    /// <summary>Invalidates the participant's active token, if any.</summary>
    public void Revoke(string eventId, string participantId)
    {
        lock (_lock) RevokeLocked(eventId, participantId);
    }

    /// <summary>Hex SHA-256 of the raw token; safe to use as a key (for example a rate-limit partition).</summary>
    internal static string Hash(string rawToken) => Convert.ToHexString(HashBytes(rawToken));

    private static byte[] HashBytes(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

    private void RevokeLocked(string eventId, string participantId)
    {
        if (_hashByParticipant.Remove((eventId, participantId), out var key)) _byHash.Remove(key);
    }

    private sealed record Entry(string EventId, string ParticipantId, byte[] Hash);
}
