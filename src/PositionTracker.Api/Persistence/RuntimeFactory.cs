using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Persistence;

/// <summary>
/// The one code path that builds the <see cref="EventRuntime"/> of a database event, used both by the startup
/// rebuild (<see cref="RuntimeRehydrator"/>) and by the organizer's <c>live</c> transition. Per event: the course from
/// the stored GPX, laps and checkpoints; <see cref="EventSettings.Create"/> (validates the spectator delay); the
/// <see cref="ParticipantStatus.Confirmed"/> participants in registration order; then the stored history on a
/// <see cref="ReplayTimeProvider"/>:
/// <list type="number">
/// <item>actions in the order they were applied, each at its stored time (pauses and DNF are known before the fixes
/// are folded, so the fixes are folded once; SOS keeps its original start);</item>
/// <item>every stored fix, per participant in device-time order, with the clock at the newest stored fix (all of
/// them passed the fix-ahead check originally, so none is refused now);</item>
/// <item>device statuses in arrival order, each at its receive time (the last one wins and the low-battery start
/// is the original one).</item>
/// </list>
/// The clock then goes live. Nothing in the replay reads the wall clock, so the rebuilt state is the state the old
/// process had. For an event that has just gone live the history is empty. The runtime is returned, not registered.
/// </summary>
public sealed class RuntimeFactory(IFixStore store, TimeProvider clock)
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);

    /// <summary>The runtime id of a database event: its Guid formatted <c>"D"</c> (controller ruling P5).</summary>
    public static string RuntimeId(Guid eventId) => eventId.ToString("D");

    /// <summary>
    /// The runtime view of a participant row (the email never leaves the database; a nickname the organizer hid is not
    /// passed on).
    /// </summary>
    public static ParticipantInfo ParticipantInfoOf(ParticipantEntity p) =>
        new(p.Id.ToString("D"), p.Bib, p.Name, p.NicknameHidden ? null : p.Nickname, p.Category?.Name ?? "", p.ShowNameToSpectators, p.IsMinor);

    /// <summary>
    /// Builds the runtime of <paramref name="ev"/>, reading its participants through <paramref name="db"/> (so a caller
    /// inside a transaction reads them in it). Throws when the stored event cannot be run (no or invalid course,
    /// invalid settings).
    /// </summary>
    public async Task<EventRuntime> BuildAsync(AppDbContext db, EventEntity ev, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ev);
        var eventId = RuntimeId(ev.Id);
        var settings = EventSettings.Create(eventId, ev.Name, ev.Sport, ev.StartTime,
            TimeSpan.FromSeconds(ev.SpectatorDelaySeconds), isSimulated: false);
        var replay = new ReplayTimeProvider(clock, ev.StartTime);
        var runtime = new EventRuntime(settings, CourseOf(ev), replay);

        var participants = await db.Participants.AsNoTracking().Include(p => p.Category)
            .Where(p => p.EventId == ev.Id && p.Status == ParticipantStatus.Confirmed)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .ToListAsync(ct);
        foreach (var p in participants) runtime.AddParticipant(ParticipantInfoOf(p));

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

    /// <summary>The course of a stored event; throws <see cref="InvalidOperationException"/> without one.</summary>
    public static Course CourseOf(EventEntity ev)
    {
        if (ev.GpxBytes is not { Length: > 0 } gpx)
            throw new InvalidOperationException("The event has no course.");
        using var stream = new MemoryStream(gpx, writable: false);
        var points = GpxParser.Parse(stream);
        return Course.Create(points, ev.Laps, CheckpointsOf(ev.CheckpointsJson));
    }

    /// <summary>The checkpoints stored in <see cref="EventEntity.CheckpointsJson"/>.</summary>
    public static IReadOnlyList<Checkpoint> CheckpointsOf(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : (JsonSerializer.Deserialize<List<StoredCheckpoint>>(json, CheckpointJson) ?? [])
                .Select(c => new Checkpoint(c.Name, c.LapDistanceM)).ToList();

    /// <summary>The <see cref="EventEntity.CheckpointsJson"/> of <paramref name="checkpoints"/>.</summary>
    public static string CheckpointsJsonOf(IEnumerable<Checkpoint> checkpoints) =>
        JsonSerializer.Serialize(checkpoints.Select(c => new StoredCheckpoint(c.Name, c.LapDistanceM)), CheckpointJson);

    /// <summary>An element of <see cref="EventEntity.CheckpointsJson"/>.</summary>
    private sealed record StoredCheckpoint(string Name, double LapDistanceM);
}
