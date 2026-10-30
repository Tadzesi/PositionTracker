using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PositionTracker.Api.Persistence;

/// <summary>
/// Writes the queued history in batches: once it holds <see cref="PersistenceOptions.BatchFixes"/> fixes, or
/// <see cref="PersistenceOptions.BatchIntervalMs"/> after the oldest held write was queued (on the injected clock),
/// whichever comes first, in one transaction. Ingest never waits for it (see <see cref="PersistenceQueue"/>).
/// <para>Failures: a transient one (connection, timeout) is retried with growing delay, holding the queue slots, so a
/// database outage turns into 503 back-pressure instead of lost rows. A permanent one (integrity violation, a
/// malformed id) is narrowed down by writing the batch's rows one by one; a row that still fails is logged with its
/// ids and dropped, so one bad row cannot block the queue.</para>
/// <para>Shutdown: <see cref="StopAsync"/> closes the queue and writes everything still queued; only if the host's
/// shutdown timeout runs out first are the remaining rows given up (logged with their count).</para>
/// </summary>
public sealed class PersistenceWriter(
    PersistenceQueue queue, IFixStore store, IOptions<PersistenceOptions> options, TimeProvider clock,
    ILogger<PersistenceWriter> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // Cancelled only when the shutdown timeout runs out; the writer deliberately ignores the stopping token, which
    // is cancelled as soon as shutdown begins, so it can drain the queue.
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.Enabled) return;
        var maxFixes = options.Value.BatchFixes;
        var window = TimeSpan.FromMilliseconds(options.Value.BatchIntervalMs);
        var reader = queue.Reader;
        var batch = new List<PendingWrite>();
        try
        {
            while (await reader.WaitToReadAsync(_abort.Token))
            {
                var fixes = 0;
                while (true)
                {
                    while (fixes < maxFixes && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        fixes += item.FixCount;
                    }
                    if (fixes >= maxFixes) break;
                    if (!await MoreBeforeAsync(reader, batch[0].QueuedAt + window)) break;
                }

                await WriteAsync(batch);
                queue.Written(batch.Count);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            var lost = batch.Count;
            while (reader.TryRead(out _)) lost++;
            if (lost > 0)
                logger.LogError("Shutdown timeout: {Count} queued position history writes were not persisted.", lost);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        using var registration = cancellationToken.Register(() => _abort.Cancel());
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    /// <summary>True when more writes arrive before <paramref name="deadline"/>; false when it passes or the queue closed.</summary>
    private async Task<bool> MoreBeforeAsync(ChannelReader<PendingWrite> reader, DateTimeOffset deadline)
    {
        var remaining = deadline - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        var more = reader.WaitToReadAsync(wait.Token).AsTask();
        var elapsed = Task.Delay(remaining, clock, wait.Token);
        // The timer counts from its creation: if the clock passed the deadline while it was being set up, it is
        // already over (matters for a test clock that jumps; with the system clock it is a few ticks at most).
        var first = clock.GetUtcNow() >= deadline ? elapsed : await Task.WhenAny(more, elapsed);
        await wait.CancelAsync();
        _abort.Token.ThrowIfCancellationRequested();
        return first == more && await more;
    }

    private async Task WriteAsync(List<PendingWrite> batch)
    {
        var writes = batch.Select(p => p.Write).ToList();
        try
        {
            await WriteWithRetryAsync(writes);
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            if (writes.Count == 1)
            {
                Drop(writes[0], ex);
                return;
            }
            foreach (var write in writes)
            {
                try
                {
                    await WriteWithRetryAsync([write]);
                }
                catch (Exception single) when (IsPermanent(single))
                {
                    Drop(write, single);
                }
            }
        }
    }

    /// <summary>Retries transient failures with a growing delay until the write succeeds or the shutdown timeout ends.</summary>
    private async Task WriteWithRetryAsync(IReadOnlyList<HistoryWrite> writes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.WriteAsync(writes, _abort.Token);
                return;
            }
            catch (Exception ex) when (!IsPermanent(ex) && !_abort.IsCancellationRequested)
            {
                if (attempt == 1 || attempt % 10 == 0)
                    logger.LogError(ex, "Writing {Count} position history rows failed (attempt {Attempt}); retrying.",
                        writes.Count, attempt);
                var delay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, Math.Pow(2, attempt - 1)));
                await Task.Delay(delay, clock, _abort.Token);
            }
        }
    }

    private void Drop(HistoryWrite write, Exception ex) =>
        logger.LogError(ex, "Dropped a position history write ({Kind}) for event {EventId}, participant {ParticipantId}: it cannot be stored.",
            write.GetType().Name, write.EventId, write.ParticipantId);

    /// <summary>Retrying cannot help: the rows violate a constraint or carry an id that is not a Guid.</summary>
    private static bool IsPermanent(Exception ex) =>
        ex is FormatException
        || ex is DbUpdateException { InnerException: PostgresException { SqlState: var state } } && state.StartsWith("23", StringComparison.Ordinal);
}
