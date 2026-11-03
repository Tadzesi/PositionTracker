using System.Collections.Concurrent;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Remembers which database device token belongs to which event and participant, so a phone sending every few seconds is
/// not looked up in the database on every request (under load that lookup queued on the connection pool). Keyed by the
/// token's SHA-256 (never the raw token); only tokens that resolved are kept, so unknown tokens always reach the database.
/// </summary>
/// <remarks>
/// A revocation or re-issue (<see cref="Forget"/>) and an event delete (<see cref="ForgetEvent"/>) take effect at once: a
/// remembered entry older than the revocation is dropped. The revocation runs in the caller's transaction, so for
/// <see cref="Grace"/> after it a concurrent lookup may still have read the old row; entries taken within that window are
/// not trusted either, and the database decides. An entry is also dropped after <see cref="Lifetime"/>.
/// </remarks>
public sealed class DeviceTokenCache(TimeProvider clock)
{
    /// <summary>How long a resolved token is trusted without asking the database again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long after a revocation lookups of that participant (or event) are not remembered.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>More entries than this and the cache starts over (a bound on memory, far above any real event).</summary>
    private const int MaxEntries = 100_000;

    private readonly ConcurrentDictionary<string, Entry> _byHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedParticipants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedEvents = new(StringComparer.Ordinal);

    /// <summary>The event and participant of the token with this hash, if it was resolved recently and not revoked since.</summary>
    public (string EventId, string ParticipantId)? Get(string tokenHash)
    {
        if (!_byHash.TryGetValue(tokenHash, out var entry)) return null;
        if (Trusted(entry, clock.GetUtcNow())) return (entry.EventId, entry.ParticipantId);
        _byHash.TryRemove(new KeyValuePair<string, Entry>(tokenHash, entry));
        return null;
    }

    /// <summary>Remembers a token the database resolved; <paramref name="readAt"/> is the time taken before the lookup.</summary>
    public void Remember(string tokenHash, string eventId, string participantId, DateTimeOffset readAt)
    {
        var entry = new Entry(eventId, participantId, readAt);
        if (!Trusted(entry, clock.GetUtcNow())) return;
        if (_byHash.Count >= MaxEntries) _byHash.Clear();
        _byHash[tokenHash] = entry;
    }

    /// <summary>The participant's tokens were revoked or re-issued: forget them and distrust lookups for <see cref="Grace"/>.</summary>
    public void Forget(string eventId, string participantId)
    {
        var now = clock.GetUtcNow();
        Prune(_revokedParticipants, now);
        _revokedParticipants[Key(eventId, participantId)] = now;
        foreach (var (hash, entry) in _byHash)
            if (entry.EventId == eventId && entry.ParticipantId == participantId)
                _byHash.TryRemove(new KeyValuePair<string, Entry>(hash, entry));
    }

    /// <summary>The event was deleted (its tokens went with it): forget every token of it.</summary>
    public void ForgetEvent(string eventId)
    {
        var now = clock.GetUtcNow();
        Prune(_revokedEvents, now);
        _revokedEvents[eventId] = now;
        foreach (var (hash, entry) in _byHash)
            if (entry.EventId == eventId)
                _byHash.TryRemove(new KeyValuePair<string, Entry>(hash, entry));
    }

    private bool Trusted(Entry entry, DateTimeOffset now)
    {
        if (now - entry.ReadAt > Lifetime) return false;
        if (_revokedParticipants.TryGetValue(Key(entry.EventId, entry.ParticipantId), out var revoked) && entry.ReadAt <= revoked + Grace)
            return false;
        return !(_revokedEvents.TryGetValue(entry.EventId, out var deleted) && entry.ReadAt <= deleted + Grace);
    }

    /// <summary>A revocation older than an entry can live plus the grace no longer matters.</summary>
    private static void Prune(ConcurrentDictionary<string, DateTimeOffset> revoked, DateTimeOffset now)
    {
        foreach (var (key, at) in revoked)
            if (now - at > Lifetime + Grace)
                revoked.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, at));
    }

    private static string Key(string eventId, string participantId) => eventId + "/" + participantId;

    private sealed record Entry(string EventId, string ParticipantId, DateTimeOffset ReadAt);
}
