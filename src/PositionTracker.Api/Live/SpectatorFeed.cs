using System.Collections.Concurrent;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Live;

/// <summary>
/// The delayed spectator state of every running event: one <see cref="DelayedViewBuffer"/> per event runtime,
/// created lazily with the event's <see cref="EventSettings.SpectatorDelay"/>. A buffer belongs to the runtime
/// instance, so an event removed and registered again under the same id starts empty. The projected
/// <see cref="SpectatorView"/> is cached per (buffered view instance, <see cref="EventRuntime.ParticipantsVersion"/>),
/// so repeated <c>/public</c> requests and the broadcaster reuse one instance and do not take the runtime's lock.
/// Thread-safe.
/// </summary>
public sealed class SpectatorFeed(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Entry> _buffers = new(StringComparer.Ordinal);
    private int _projections;

    /// <summary>How many projections were computed (cache misses); for tests.</summary>
    internal int Projections => Volatile.Read(ref _projections);

    public void Push(EventRuntime runtime, LiveView view)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(view);
        var entry = _buffers.AddOrUpdate(runtime.Settings.EventId,
            static (_, r) => new Entry(r),
            static (_, existing, r) => ReferenceEquals(existing.Runtime, r) ? existing : new Entry(r),
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
        if (entry.Buffer.GetCurrent(clock.GetUtcNow()) is not { } view) return null;

        var version = runtime.ParticipantsVersion;
        if (Volatile.Read(ref entry.Cached) is { } cached
            && ReferenceEquals(cached.Source, view) && cached.ParticipantsVersion == version)
            return cached.Projected;

        Interlocked.Increment(ref _projections);
        var projected = SpectatorProjector.Project(view, runtime.Participants);
        Volatile.Write(ref entry.Cached, new Projection(view, version, projected));
        return projected;
    }

    /// <summary>Drops the buffers of runtimes not in <paramref name="running"/>.</summary>
    public void RetainOnly(IReadOnlyCollection<EventRuntime> running)
    {
        foreach (var (eventId, entry) in _buffers)
            if (!running.Contains(entry.Runtime))
                _buffers.TryRemove(new KeyValuePair<string, Entry>(eventId, entry));
    }

    private sealed class Entry(EventRuntime runtime)
    {
        public EventRuntime Runtime { get; } = runtime;
        public DelayedViewBuffer Buffer { get; } = new(runtime.Settings.SpectatorDelay);
        /// <summary>The last projection; replaced as a whole, so concurrent readers see a consistent one.</summary>
        public Projection? Cached;
    }

    private sealed record Projection(LiveView Source, long ParticipantsVersion, SpectatorView Projected);
}
