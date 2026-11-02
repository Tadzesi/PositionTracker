using System.Collections.Concurrent;

namespace PositionTracker.Api.OwnTracks;

/// <summary>
/// The stored "is this event finished" answer for events without a runtime in memory (not live yet, or finished and
/// evicted), so the OwnTracks ingest does not read the event row on every request of every phone. "Finished" is final
/// and kept; "not finished" is kept for <see cref="NotFinishedTtl"/>, so a runtime-less event that is finished meanwhile
/// sends the stop command at most that much later.
/// </summary>
public sealed class EventFinishedCache(TimeProvider clock)
{
    public static readonly TimeSpan NotFinishedTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool Finished, DateTimeOffset At)> _answers = new(StringComparer.Ordinal);

    /// <summary>The cached answer; when there is none (or it expired) <paramref name="load"/> reads it and the result is cached.</summary>
    public async Task<bool> GetAsync(string eventId, Func<Task<bool>> load)
    {
        var now = clock.GetUtcNow();
        if (_answers.TryGetValue(eventId, out var known) && (known.Finished || now - known.At < NotFinishedTtl)) return known.Finished;
        var finished = await load();
        _answers[eventId] = (finished, now);
        return finished;
    }
}
