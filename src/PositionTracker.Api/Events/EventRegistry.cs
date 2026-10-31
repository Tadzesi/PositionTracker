using System.Collections.Concurrent;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Events;

/// <summary>
/// The running events of this process, keyed by <see cref="EventSettings.EventId"/>. Thread-safe. A runtime of a
/// finished event stays registered (read-only, for viewing the results) and is flagged <see cref="IsFinished"/>:
/// the device endpoints refuse it with 409 <c>event_finished</c>.
/// </summary>
public sealed class EventRegistry
{
    private readonly ConcurrentDictionary<string, EventRuntime> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _finished = new(StringComparer.Ordinal);

    /// <summary>Registers the runtime and returns it; throws <see cref="ArgumentException"/> if the id is taken.</summary>
    public EventRuntime Add(EventRuntime runtime) => Add(runtime, finished: false);

    /// <summary>
    /// Registers the runtime, already flagged finished when <paramref name="finished"/> (so it never accepts ingest);
    /// throws <see cref="ArgumentException"/> if the id is taken.
    /// </summary>
    public EventRuntime Add(EventRuntime runtime, bool finished)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var id = runtime.Settings.EventId;
        var wasFinished = _finished.ContainsKey(id);
        if (finished) _finished[id] = 0;
        if (!_events.TryAdd(id, runtime))
        {
            if (finished && !wasFinished) _finished.TryRemove(id, out _);
            throw new ArgumentException($"Event '{id}' already exists.", nameof(runtime));
        }
        return runtime;
    }

    public EventRuntime? Get(string eventId) => _events.GetValueOrDefault(eventId);

    public bool Remove(string eventId)
    {
        _finished.TryRemove(eventId, out _);
        return _events.TryRemove(eventId, out _);
    }

    /// <summary>Whether the event is finished: its runtime (if registered) is read-only.</summary>
    public bool IsFinished(string eventId) => _finished.ContainsKey(eventId);

    /// <summary>Flags the event finished (or, with false, takes the flag back after a failed state change).</summary>
    public void SetFinished(string eventId, bool finished)
    {
        if (finished) _finished[eventId] = 0;
        else _finished.TryRemove(eventId, out _);
    }

    public IReadOnlyCollection<EventRuntime> All => _events.Values.ToList();
}
