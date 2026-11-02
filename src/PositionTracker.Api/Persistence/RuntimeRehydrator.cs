using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Events;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Persistence;

/// <summary>
/// Rebuilds at startup the runtimes of all <see cref="EventState.Live"/> events and of the events that went
/// <see cref="EventState.Finished"/> less than <see cref="FinishedRuntimeDays"/> ago (registered read-only, for viewing
/// the results). It is a hosted service; with <see cref="PersistenceOptions.MigrateOnStartup"/> it first applies
/// pending migrations. Each runtime is built by <see cref="RuntimeFactory"/> (the same code path as the organizer's
/// <c>live</c> transition). An event that cannot be rebuilt is logged and skipped; the organizer can retry with
/// <c>live</c>. A rebuilt finished event without stored results (it finished before the results were stored at the finish)
/// gets them now, from the replayed positions, so they stay once the runtime leaves the memory.
/// </summary>
public sealed class RuntimeRehydrator(
    IDbContextFactory<AppDbContext> contexts, RuntimeFactory factory, EventRegistry registry, TimeProvider clock,
    IOptions<PersistenceOptions> options, ILogger<RuntimeRehydrator> logger) : IHostedService
{
    /// <summary>How long after its end a finished event's runtime is rebuilt for viewing the results.</summary>
    public const int FinishedRuntimeDays = 7;

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogWarning("ConnectionStrings:Default is not set: running without persistence (events live in memory only).");
            return;
        }
        if (options.Value.MigrateOnStartup)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
        }
        await RebuildAsync(cancellationToken);
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Rebuilds and registers every such event not already registered; returns how many were rebuilt.</summary>
    public async Task<int> RebuildAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return 0;
        var finishedSince = clock.GetUtcNow().AddDays(-FinishedRuntimeDays);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var events = await db.Events.AsNoTracking()
            .Where(e => e.State == EventState.Live || (e.State == EventState.Finished && e.EndedAt >= finishedSince))
            .OrderBy(e => e.StartTime).ThenBy(e => e.Id).ToListAsync(ct);

        var rebuilt = 0;
        foreach (var ev in events)
        {
            var eventId = RuntimeFactory.RuntimeId(ev.Id);
            if (registry.Get(eventId) is not null) continue;
            try
            {
                var runtime = registry.Add(await factory.BuildAsync(db, ev, ct), finished: ev.State == EventState.Finished, ev.EndedAt);
                rebuilt++;
                if (ev.State == EventState.Finished) await StoreMissingResultsAsync(ev, runtime, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Event {EventId} could not be rebuilt and is not running.", eventId);
            }
        }
        logger.LogInformation("Rebuilt {Count} of {Total} live or recently finished events from the database.", rebuilt, events.Count);
        return rebuilt;
    }

    private async Task StoreMissingResultsAsync(EventEntity ev, PositionTracker.Core.Runtime.EventRuntime runtime, CancellationToken ct)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            if (await db.ResultRows.AnyAsync(r => r.EventId == ev.Id, ct)) return;
            var rows = await Operations.ResultsService.SnapshotAsync(db, ev, runtime, ct);
            db.ResultRows.AddRange(rows);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Stored the missing results of finished event {EventId} ({Count} lines).", runtime.Settings.EventId, rows.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // most likely stored by someone else meanwhile (unique per participant); the organizer still sees the runtime's standing
            logger.LogWarning(ex, "The missing results of finished event {EventId} could not be stored.", runtime.Settings.EventId);
        }
    }
}
