using PositionTracker.Core.Runtime;

namespace PositionTracker.Data.Entities;

// The position history is replayed into a runtime at startup and must rebuild the same state, so its times are
// stored tick-exact (bigint UTC ticks, see AppDbContext); PostgreSQL timestamps keep only microseconds. Insertion
// order (the identity Id) breaks ties.

/// <summary>A position fix the tracker stored for a participant (see <c>ApplyManyResult.Stored</c>).</summary>
public sealed class FixEntity
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    /// <summary>Device time of the fix (UTC).</summary>
    public DateTimeOffset Time { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? AccuracyM { get; set; }
}

/// <summary>The device status sent with an ingested batch, at the event clock time the batch was ingested.</summary>
public sealed class DeviceStatusEntity
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public double? BatteryPct { get; set; }
    public bool? Charging { get; set; }
    public int QueueLength { get; set; }
    public bool InForeground { get; set; }
}

/// <summary>A participant action (SOS, pause, DNF, ...) at the event clock time it was applied.</summary>
public sealed class ActionLogEntity
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    public ParticipantAction Action { get; set; }
    public DateTimeOffset Time { get; set; }
}
