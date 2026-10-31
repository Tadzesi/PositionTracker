using PositionTracker.Core;

namespace PositionTracker.Data.Entities;

public enum EventState { Draft, Ready, Live, Finished }

/// <summary>
/// An event. The course input is kept as uploaded: <see cref="GpxBytes"/>, <see cref="Laps"/> and
/// <see cref="CheckpointsJson"/> (a JSON array of <c>{"name": string, "lapDistanceM": number}</c>); the parameters
/// derived from it are in <see cref="CourseEntity"/>. The runtime id of an event is <see cref="Id"/> formatted
/// <c>"D"</c> (lower case).
/// </summary>
public sealed class EventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizerId { get; set; }
    public required string Name { get; set; }
    public required string Location { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public Sport Sport { get; set; }
    public EventState State { get; set; }
    public int Laps { get; set; }
    public int SpectatorDelaySeconds { get; set; }
    public byte[]? GpxBytes { get; set; }
    public string? CheckpointsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the event went <see cref="EventState.Finished"/>; position retention counts from here.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>
    /// Optimistic concurrency token: every write of the event (or of its course, categories or waves) sets a new
    /// value, and a write based on an older read fails with <c>DbUpdateConcurrencyException</c>.
    /// </summary>
    public Guid Version { get; set; }
}

/// <summary>Course parameters derived from an event's GPX (one row per event with a course).</summary>
public sealed class CourseEntity
{
    public Guid EventId { get; set; }
    public double LapLengthM { get; set; }
    public double AscentM { get; set; }
    public double DescentM { get; set; }
    public int PointCount { get; set; }
    /// <summary>Whether the end lies within 100 m of the start (required for more than one lap).</summary>
    public bool IsClosed { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class CategoryEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Name { get; set; }
    public string? Gender { get; set; }
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
}

public sealed class WaveEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset StartTime { get; set; }
}
