namespace PositionTracker.Data.Entities;

/// <summary>
/// One line of the results of a finished event, stored when the event finishes (the runtime and the positions go away
/// later, the results stay). Deleting a participant's personal data replaces the name here too.
/// </summary>
public sealed class ResultRowEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    public required string Bib { get; set; }
    public required string Name { get; set; }
    public string? Nickname { get; set; }
    public string? Gender { get; set; }
    public string? Category { get; set; }
    public string? Wave { get; set; }
    public bool IsMinor { get; set; }
    /// <summary>The participant agreed to show the name to spectators; an export for publication uses the name only with it.</summary>
    public bool NameConsent { get; set; }
    /// <summary><c>finished</c>, <c>dnf</c> (started, did not finish) or <c>dns</c> (never had a position in the race).</summary>
    public required string Status { get; set; }
    public int? Rank { get; set; }
    public int? CategoryRank { get; set; }
    public int? GenderRank { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    public TimeSpan? NetTime { get; set; }
    public TimeSpan? GapToLeader { get; set; }
    public double DistanceM { get; set; }
    public int Laps { get; set; }
    public double AvgSpeedMps { get; set; }
    public double MaxSpeedMps { get; set; }
    public TimeSpan MovingTime { get; set; }
    public TimeSpan StoppedTime { get; set; }
    public double AscentM { get; set; }
    public double DescentM { get; set; }
    /// <summary>Where on the course the first position was matched; far from 0 means the start was not observed.</summary>
    public double StartObservedM { get; set; }
    /// <summary>The checkpoint times and sector colours, <c>[{lap,segmentIndex,time,colour}]</c>.</summary>
    public string SplitsJson { get; set; } = "[]";
}
