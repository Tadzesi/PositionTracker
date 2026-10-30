using System.Collections.Concurrent;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Live;

/// <summary>
/// The delayed spectator state of every running event: one <see cref="DelayedViewBuffer"/> per event runtime,
/// created lazily with the event's <see cref="EventSettings.SpectatorDelay"/>. A buffer belongs to the runtime
/// instance, so an event removed and registered again under the same id starts empty. Thread-safe.
/// </summary>
public sealed class SpectatorFeed(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (EventRuntime Runtime, DelayedViewBuffer Buffer)> _buffers =
        new(StringComparer.Ordinal);

    public void Push(EventRuntime runtime, LiveView view)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(view);
        var entry = _buffers.AddOrUpdate(runtime.Settings.EventId,
            static (_, r) => (r, new DelayedViewBuffer(r.Settings.SpectatorDelay)),
            static (_, existing, r) => ReferenceEquals(existing.Runtime, r)
                ? existing
                : (r, new DelayedViewBuffer(r.Settings.SpectatorDelay)),
            runtime);
        entry.Buffer.Push(view);
    }

    /// <summary>
    /// The spectator projection of the newest view at least the spectator delay old, or <c>null</c> if there is
    /// none yet. Display names follow the participants' current consent.
    /// </summary>
    public SpectatorView? GetCurrent(EventRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_buffers.TryGetValue(runtime.Settings.EventId, out var entry) || !ReferenceEquals(entry.Runtime, runtime))
            return null;
        return entry.Buffer.GetCurrent(clock.GetUtcNow()) is { } view
            ? SpectatorProjector.Project(view, runtime.Participants)
            : null;
    }

    /// <summary>Drops the buffers of runtimes not in <paramref name="running"/>.</summary>
    public void RetainOnly(IReadOnlyCollection<EventRuntime> running)
    {
        foreach (var (eventId, entry) in _buffers)
            if (!running.Contains(entry.Runtime))
                _buffers.TryRemove(new KeyValuePair<string, (EventRuntime, DelayedViewBuffer)>(eventId, entry));
    }
}
