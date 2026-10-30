using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Persistence;

/// <summary>A queued write and the time it was queued (on the injected clock; starts the writer's batch window).</summary>
public sealed record PendingWrite(HistoryWrite Write, DateTimeOffset QueuedAt)
{
    public int FixCount => Write is FixBatchWrite batch ? batch.Fixes.Count : 0;
}

/// <summary>
/// The hand-over from the request path to <see cref="PersistenceWriter"/>: a bounded channel guarded by
/// <see cref="PersistenceOptions.QueueCapacity"/> slots. A request takes a slot <em>before</em> it applies anything to
/// the runtime (<see cref="TryReserveAsync"/>), then queues its write (<see cref="Enqueue"/>) or gives the slot back
/// (<see cref="Release"/>); the writer frees the slots once the rows are in the database. So the channel can never be
/// full when a holder of a slot writes to it, and a request that cannot get a slot within
/// <see cref="PersistenceOptions.EnqueueTimeoutMs"/> is refused with 503 having changed nothing: the client resends
/// the batch, nothing is dropped and the in-memory state never runs ahead of what will be persisted. Thread-safe.
/// </summary>
public sealed class PersistenceQueue
{
    private readonly Channel<PendingWrite> _channel;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _enqueueTimeout;
    private readonly TimeProvider _clock;

    public PersistenceQueue(IOptions<PersistenceOptions> options, TimeProvider clock)
    {
        var o = options.Value;
        Enabled = o.Enabled;
        _clock = clock;
        _enqueueTimeout = TimeSpan.FromMilliseconds(o.EnqueueTimeoutMs);
        _slots = new SemaphoreSlim(o.QueueCapacity, o.QueueCapacity);
        _channel = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(o.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Whether a database is configured.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Whether the event's history is written: only with a database, never for a simulated event, and only for ids
    /// of database events (a Guid formatted <c>"D"</c>).
    /// </summary>
    public bool ShouldPersist(EventSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enabled && !settings.IsSimulated && Guid.TryParseExact(settings.EventId, "D", out _);
    }

    /// <summary>Takes a slot, waiting up to the enqueue timeout; false when none became free (answer 503).</summary>
    public async Task<bool> TryReserveAsync(CancellationToken ct) =>
        _slots.Wait(0) || await _slots.WaitAsync(_enqueueTimeout, ct);

    /// <summary>Gives back a reserved slot that will not be used.</summary>
    public void Release() => _slots.Release();

    /// <summary>
    /// Queues a write on a reserved slot. False (and the slot is given back) once shutdown has begun: the write will
    /// not be persisted, so the caller answers 503 and the client resends after the restart.
    /// </summary>
    public bool Enqueue(HistoryWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_channel.Writer.TryWrite(new PendingWrite(write, _clock.GetUtcNow()))) return true;
        _slots.Release();
        return false;
    }

    internal ChannelReader<PendingWrite> Reader => _channel.Reader;

    /// <summary>Frees the slots of writes that are now in the database (or given up).</summary>
    internal void Written(int count)
    {
        if (count > 0) _slots.Release(count);
    }

    /// <summary>No more writes are accepted; the writer drains what is queued.</summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
