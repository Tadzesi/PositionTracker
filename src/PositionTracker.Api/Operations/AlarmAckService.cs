using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Persistence;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Operations;

/// <summary>
/// Organizer acknowledgements of alarms, keyed on (event, participant, kind) (Plan 1 ruling 9), so a sliding
/// <c>Since</c> (a rider who stops again) keeps the same acknowledgement. An acknowledgement stays while the alarm is
/// raised; when the alarm is gone it is deleted, so the alarm shows as new when it reappears. Held in memory per event
/// and, for database events, written to <c>AlarmAcks</c> and loaded on first use, so it survives a restart.
/// <see cref="ApplyAsync"/> marks <see cref="Alarm.Acknowledged"/> in the organizer's view; the spectator view has no alarms.
/// </summary>
public sealed class AlarmAckService(IDbContextFactory<AppDbContext> contexts, IOptions<PersistenceOptions> persistence,
    TimeProvider clock, ILogger<AlarmAckService> logger)
{
    private readonly ConcurrentDictionary<string, EventAcks> _events = new(StringComparer.Ordinal);

    /// <summary>The view with <see cref="Alarm.Acknowledged"/> set; acknowledgements of cleared alarms are dropped.</summary>
    public async ValueTask<LiveView> ApplyAsync(LiveView view, CancellationToken ct = default)
    {
        var acks = await GetAsync(view.EventId, ct);
        var active = new HashSet<(string, AlarmKind)>();
        foreach (var p in view.Participants)
            foreach (var a in p.Alarms) active.Add((p.ParticipantId, a.Kind));

        List<(string, AlarmKind)> stale;
        lock (acks.Gate)
        {
            stale = acks.Set.Where(k => !active.Contains(k)).ToList();
            foreach (var k in stale) acks.Set.Remove(k);
        }
        if (stale.Count > 0) await DeleteAsync(view.EventId, stale, ct);

        HashSet<(string, AlarmKind)> snapshot;
        lock (acks.Gate) snapshot = [.. acks.Set];
        if (snapshot.Count == 0) return view;
        return view with
        {
            Participants = view.Participants.Select(p => p.Alarms.Count == 0
                ? p
                : p with { Alarms = p.Alarms.Select(a => a with { Acknowledged = snapshot.Contains((p.ParticipantId, a.Kind)) }).ToList() }).ToList(),
        };
    }

    /// <summary>Acknowledges the alarm; false when the participant does not have that alarm now.</summary>
    public async Task<bool> AckAsync(EventRuntime runtime, string participantId, AlarmKind kind, CancellationToken ct)
    {
        var eventId = runtime.Settings.EventId;
        var raised = runtime.BuildLiveView().Participants.FirstOrDefault(p => p.ParticipantId == participantId)
            ?.Alarms.Any(a => a.Kind == kind) == true;
        if (!raised) return false;
        var acks = await GetAsync(eventId, ct);
        bool added;
        lock (acks.Gate) added = acks.Set.Add((participantId, kind));
        if (added && Persisted(eventId) && DbTokenService.IsDatabaseId(participantId))
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var ev = Guid.ParseExact(eventId, "D");
            var pid = Guid.ParseExact(participantId, "D");
            if (!await db.AlarmAcks.AnyAsync(a => a.EventId == ev && a.ParticipantId == pid && a.Kind == kind, ct))
            {
                db.AlarmAcks.Add(new AlarmAckEntity { EventId = ev, ParticipantId = pid, Kind = kind, AcknowledgedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);
            }
        }
        return true;
    }

    private async Task<EventAcks> GetAsync(string eventId, CancellationToken ct)
    {
        var acks = _events.GetOrAdd(eventId, _ => new EventAcks());
        if (acks.Loaded || !Persisted(eventId)) return acks;
        await acks.Load.WaitAsync(ct);
        try
        {
            if (acks.Loaded) return acks;
            await using var db = await contexts.CreateDbContextAsync(ct);
            var ev = Guid.ParseExact(eventId, "D");
            var rows = await db.AlarmAcks.AsNoTracking().Where(a => a.EventId == ev).ToListAsync(ct);
            lock (acks.Gate)
                foreach (var r in rows) acks.Set.Add((r.ParticipantId.ToString("D"), r.Kind));
            acks.Loaded = true;
        }
        finally
        {
            acks.Load.Release();
        }
        return acks;
    }

    private async Task DeleteAsync(string eventId, List<(string, AlarmKind)> stale, CancellationToken ct)
    {
        if (!Persisted(eventId)) return;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var ev = Guid.ParseExact(eventId, "D");
            foreach (var (participantId, kind) in stale)
            {
                if (!DbTokenService.IsDatabaseId(participantId)) continue;
                var pid = Guid.ParseExact(participantId, "D");
                await db.AlarmAcks.Where(a => a.EventId == ev && a.ParticipantId == pid && a.Kind == kind).ExecuteDeleteAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Cleared alarm acknowledgements of event {EventId} could not be deleted ({ExceptionType}).", eventId, ex.GetType().Name);
        }
    }

    private bool Persisted(string eventId) => persistence.Value.Enabled && DbTokenService.IsDatabaseId(eventId);

    private sealed class EventAcks
    {
        public object Gate { get; } = new();
        public HashSet<(string, AlarmKind)> Set { get; } = [];
        public SemaphoreSlim Load { get; } = new(1, 1);
        public volatile bool Loaded;
    }
}
