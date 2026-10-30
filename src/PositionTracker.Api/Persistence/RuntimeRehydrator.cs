using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Events;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Persistence;

/// <summary>
/// Rebuilds the runtimes of all <see cref="EventState.Live"/> events at startup (it is a hosted service; with
/// <see cref="PersistenceOptions.MigrateOnStartup"/> it first applies pending migrations). Per event: the course from
/// the stored GPX, laps and checkpoints; <see cref="EventSettings.Create"/>; the <see cref="ParticipantStatus.Confirmed"/>
/// participants in registration order; then the history on a <see cref="ReplayTimeProvider"/>:
/// <list type="number">
/// <item>actions in the order they were applied, each at its stored time (pauses and DNF are known before the fixes
/// are folded, so the fixes are folded once; SOS keeps its original start);</item>
/// <item>every stored fix, per participant in device-time order, with the clock at the newest stored fix (all of
/// them passed the fix-ahead check originally, so none is refused now);</item>
/// <item>device statuses in arrival order, each at its receive time (the last one wins and the low-battery start
/// is the original one).</item>
/// </list>
/// The clock then goes live and the runtime is registered. Nothing in the replay reads the wall clock, so the
/// rebuilt state is the state the old process had. An event that cannot be rebuilt is logged and skipped.
/// </summary>
public sealed class RuntimeRehydrator(
    IDbContextFactory<AppDbContext> contexts, IFixStore store, EventRegistry registry, TimeProvider clock,
    IOptions<PersistenceOptions> options, ILogger<RuntimeRehydrator> logger) : IHostedService
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);

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

    /// <summary>Rebuilds and registers every live event not already registered; returns how many were rebuilt.</summary>
    public async Task<int> RebuildAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return 0;
        List<EventEntity> live;
        await using (var db = await contexts.CreateDbContextAsync(ct))
            live = await db.Events.AsNoTracking().Where(e => e.State == EventState.Live)
                .OrderBy(e => e.StartTime).ThenBy(e => e.Id).ToListAsync(ct);

        var rebuilt = 0;
        foreach (var ev in live)
        {
            var eventId = ev.Id.ToString("D");
            if (registry.Get(eventId) is not null) continue;
            try
            {
                registry.Add(await BuildAsync(ev, ct));
                rebuilt++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Live event {EventId} could not be rebuilt and is not running.", eventId);
            }
        }
        logger.LogInformation("Rebuilt {Count} of {Live} live events from the database.", rebuilt, live.Count);
        return rebuilt;
    }

    /// <summary>The runtime view of a participant row (the email never leaves the database).</summary>
    public static ParticipantInfo ParticipantInfoOf(ParticipantEntity p) =>
        new(p.Id.ToString("D"), p.Bib, p.Name, p.Nickname, p.Category?.Name ?? "", p.ShowNameToSpectators, p.IsMinor);

    private async Task<EventRuntime> BuildAsync(EventEntity ev, CancellationToken ct)
    {
        var eventId = ev.Id.ToString("D");
        var settings = EventSettings.Create(eventId, ev.Name, ev.Sport, ev.StartTime,
            TimeSpan.FromSeconds(ev.SpectatorDelaySeconds), isSimulated: false);
        var replay = new ReplayTimeProvider(clock, ev.StartTime);
        var runtime = new EventRuntime(settings, CourseOf(ev), replay);

        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var participants = await db.Participants.AsNoTracking().Include(p => p.Category)
                .Where(p => p.EventId == ev.Id && p.Status == ParticipantStatus.Confirmed)
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .ToListAsync(ct);
            foreach (var p in participants) runtime.AddParticipant(ParticipantInfoOf(p));
        }

        await foreach (var action in store.ReadActionsAsync(eventId, ct))
        {
            if (!runtime.HasParticipant(action.ParticipantId)) continue;
            replay.SetReplayTime(action.Time);
            runtime.Act(action.ParticipantId, action.Action);
        }

        var fixes = new Dictionary<string, List<PositionFix>>(StringComparer.Ordinal);
        var newest = ev.StartTime;
        await foreach (var stored in store.ReadAsync(eventId, ct))
        {
            if (!runtime.HasParticipant(stored.ParticipantId)) continue;
            if (!fixes.TryGetValue(stored.ParticipantId, out var list)) fixes[stored.ParticipantId] = list = [];
            list.Add(stored.Fix);
            if (stored.Fix.Time > newest) newest = stored.Fix.Time;
        }
        replay.SetReplayTime(newest);
        foreach (var (participantId, list) in fixes) runtime.Ingest(participantId, new PositionBatch(list, null));

        await foreach (var status in store.ReadStatusesAsync(eventId, ct))
        {
            if (!runtime.HasParticipant(status.ParticipantId)) continue;
            replay.SetReplayTime(status.ReceivedAt);
            runtime.Ingest(status.ParticipantId, new PositionBatch([], status.Status));
        }

        replay.GoLive();
        return runtime;
    }

    private static Course CourseOf(EventEntity ev)
    {
        if (ev.GpxBytes is not { Length: > 0 } gpx)
            throw new InvalidOperationException("The event has no course.");
        using var stream = new MemoryStream(gpx, writable: false);
        var points = GpxParser.Parse(stream);
        var checkpoints = string.IsNullOrWhiteSpace(ev.CheckpointsJson)
            ? []
            : JsonSerializer.Deserialize<List<StoredCheckpoint>>(ev.CheckpointsJson, CheckpointJson) ?? [];
        return Course.Create(points, ev.Laps, checkpoints.Select(c => new Checkpoint(c.Name, c.LapDistanceM)).ToList());
    }

    /// <summary>An element of <see cref="EventEntity.CheckpointsJson"/>.</summary>
    private sealed record StoredCheckpoint(string Name, double LapDistanceM);
}
