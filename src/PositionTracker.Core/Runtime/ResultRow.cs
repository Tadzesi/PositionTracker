namespace PositionTracker.Core.Runtime;

/// <summary>
/// One line of the results of an event, as the runtime knows it now (the API stores it when the event finishes).
/// <see cref="NetTime"/> is counted from the participant's own start (wave), <see cref="GapToLeader"/> is in net time.
/// </summary>
/// <param name="Rank">The overall rank (null for a participant without a position in the race).</param>
/// <param name="FinishTime">The clock time of the finish; null when the participant did not finish.</param>
/// <param name="StartObservedM">
/// Where on the course the first fix of the participant was matched (m). Far from 0 means the start was not observed: the
/// participant appeared mid-course, so the time and the distance before that point are not under observation.
/// </param>
/// <param name="Segments">The times at the checkpoints and the sector colours, in course order.</param>
public sealed record ResultRow(
    string ParticipantId, string Bib, string Name, string? Nickname, string Category, bool IsMinor, int? Rank, ParticipantState State,
    DateTimeOffset Start, DateTimeOffset? FinishTime, TimeSpan? NetTime, TimeSpan? GapToLeader,
    double TotalDistanceM, int Laps, double AvgSpeedMps, double MaxSpeedMps, TimeSpan MovingTime, TimeSpan StoppedTime,
    double AscentM, double DescentM, IReadOnlyList<SegmentResult> Segments, double StartObservedM = 0);
