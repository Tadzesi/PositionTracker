using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Persistence;

/// <summary>A row of the position history to write: a batch's stored fixes and status, or an action.</summary>
public abstract record HistoryWrite(string EventId, string ParticipantId);

/// <summary>
/// The fixes the tracker stored from one batch (<see cref="IngestDetails.Stored"/>) and the batch's device status,
/// received at <paramref name="ReceivedAt"/> on the event clock (<see cref="IngestDetails.At"/>).
/// </summary>
public sealed record FixBatchWrite(
    string EventId, string ParticipantId, IReadOnlyList<PositionFix> Fixes, DeviceStatus? Status, DateTimeOffset ReceivedAt)
    : HistoryWrite(EventId, ParticipantId);

/// <summary>An action applied at <paramref name="Time"/> on the event clock (the value <see cref="EventRuntime.Act"/> returned).</summary>
public sealed record ActionWrite(string EventId, string ParticipantId, ParticipantAction Action, DateTimeOffset Time)
    : HistoryWrite(EventId, ParticipantId);

public sealed record StoredFix(string ParticipantId, PositionFix Fix);

public sealed record StoredStatus(string ParticipantId, DeviceStatus Status, DateTimeOffset ReceivedAt);

public sealed record StoredAction(string ParticipantId, ParticipantAction Action, DateTimeOffset Time);

/// <summary>
/// The position history of real events: stored fixes, device statuses and participant actions. Event and participant
/// ids are the database Guids formatted <c>"D"</c>. Times round-trip tick-exact.
/// </summary>
public interface IFixStore
{
    /// <summary>Writes one batch's stored fixes and its status (if any) at once.</summary>
    Task AppendAsync(string eventId, string participantId, IReadOnlyList<PositionFix> accepted, DeviceStatus? status,
        DateTimeOffset receivedAt, CancellationToken ct);

    /// <summary>Writes all rows in one transaction, in list order.</summary>
    Task WriteAsync(IReadOnlyList<HistoryWrite> writes, CancellationToken ct);

    /// <summary>The event's fixes ordered by device time, then insertion.</summary>
    IAsyncEnumerable<StoredFix> ReadAsync(string eventId, CancellationToken ct);

    /// <summary>The event's device statuses in insertion (arrival) order.</summary>
    IAsyncEnumerable<StoredStatus> ReadStatusesAsync(string eventId, CancellationToken ct);

    /// <summary>The event's actions in insertion (application) order.</summary>
    IAsyncEnumerable<StoredAction> ReadActionsAsync(string eventId, CancellationToken ct);

    /// <summary>
    /// Deletes the fixes and device statuses of events that ended before <paramref name="eventEndedBefore"/> and
    /// returns the number of fixes deleted. Events that have not ended keep everything; the action log is kept.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset eventEndedBefore, CancellationToken ct);
}

/// <summary>EF Core implementation of <see cref="IFixStore"/>; thread-safe (a context per call).</summary>
public sealed class FixStore(IDbContextFactory<AppDbContext> contexts) : IFixStore
{
    public Task AppendAsync(string eventId, string participantId, IReadOnlyList<PositionFix> accepted,
        DeviceStatus? status, DateTimeOffset receivedAt, CancellationToken ct) =>
        WriteAsync([new FixBatchWrite(eventId, participantId, accepted, status, receivedAt)], ct);

    public async Task WriteAsync(IReadOnlyList<HistoryWrite> writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        await using var db = await contexts.CreateDbContextAsync(ct);
        foreach (var write in writes)
        {
            var eventId = Guid.ParseExact(write.EventId, "D");
            var participantId = Guid.ParseExact(write.ParticipantId, "D");
            switch (write)
            {
                case FixBatchWrite batch:
                    db.Fixes.AddRange(batch.Fixes.Select(f => new FixEntity
                    {
                        EventId = eventId, ParticipantId = participantId, Time = f.Time, Lat = f.Lat, Lon = f.Lon,
                        AccuracyM = f.AccuracyM,
                    }));
                    if (batch.Status is { } s)
                        db.DeviceStatuses.Add(new DeviceStatusEntity
                        {
                            EventId = eventId, ParticipantId = participantId, ReceivedAt = batch.ReceivedAt,
                            BatteryPct = s.BatteryPct, Charging = s.Charging, QueueLength = s.QueueLength,
                            InForeground = s.InForeground,
                        });
                    break;
                case ActionWrite action:
                    db.ActionLog.Add(new ActionLogEntity
                    {
                        EventId = eventId, ParticipantId = participantId, Action = action.Action, Time = action.Time,
                    });
                    break;
                default:
                    throw new ArgumentException($"Unknown history write {write.GetType().Name}.", nameof(writes));
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async IAsyncEnumerable<StoredFix> ReadAsync(string eventId, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.ParseExact(eventId, "D");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = db.Fixes.AsNoTracking().Where(f => f.EventId == id).OrderBy(f => f.Time).ThenBy(f => f.Id);
        await foreach (var f in rows.AsAsyncEnumerable().WithCancellation(ct))
            yield return new StoredFix(f.ParticipantId.ToString("D"), new PositionFix(f.Time, f.Lat, f.Lon, f.AccuracyM));
    }

    public async IAsyncEnumerable<StoredStatus> ReadStatusesAsync(string eventId, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.ParseExact(eventId, "D");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = db.DeviceStatuses.AsNoTracking().Where(s => s.EventId == id).OrderBy(s => s.Id);
        await foreach (var s in rows.AsAsyncEnumerable().WithCancellation(ct))
            yield return new StoredStatus(s.ParticipantId.ToString("D"),
                new DeviceStatus(s.BatteryPct, s.Charging, s.QueueLength, s.InForeground), s.ReceivedAt);
    }

    public async IAsyncEnumerable<StoredAction> ReadActionsAsync(string eventId, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.ParseExact(eventId, "D");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = db.ActionLog.AsNoTracking().Where(a => a.EventId == id).OrderBy(a => a.Id);
        await foreach (var a in rows.AsAsyncEnumerable().WithCancellation(ct))
            yield return new StoredAction(a.ParticipantId.ToString("D"), a.Action, a.Time);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset eventEndedBefore, CancellationToken ct)
    {
        var cutoff = eventEndedBefore.ToUniversalTime();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ended = db.Events.Where(e => e.EndedAt != null && e.EndedAt < cutoff).Select(e => e.Id);
        var deleted = await db.Fixes.Where(f => ended.Contains(f.EventId)).ExecuteDeleteAsync(ct);
        await db.DeviceStatuses.Where(s => ended.Contains(s.EventId)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}
