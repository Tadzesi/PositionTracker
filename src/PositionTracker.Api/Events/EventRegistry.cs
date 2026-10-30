using System.Collections.Concurrent;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Events;

/// <summary>The running events of this process, keyed by <see cref="EventSettings.EventId"/>. Thread-safe.</summary>
public sealed class EventRegistry
{
    private readonly ConcurrentDictionary<string, EventRuntime> _events = new(StringComparer.Ordinal);

    /// <summary>Registers the runtime and returns it; throws <see cref="ArgumentException"/> if the id is taken.</summary>
    public EventRuntime Add(EventRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_events.TryAdd(runtime.Settings.EventId, runtime))
            throw new ArgumentException($"Event '{runtime.Settings.EventId}' already exists.", nameof(runtime));
        return runtime;
    }

    public EventRuntime? Get(string eventId) => _events.GetValueOrDefault(eventId);

    public bool Remove(string eventId) => _events.TryRemove(eventId, out _);

    public IReadOnlyCollection<EventRuntime> All => _events.Values.ToList();
}
