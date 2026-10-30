using System.Text.Json.Serialization;

namespace PositionTracker.Core.Runtime;

/// <summary>Alarm kinds; <see cref="LowBattery"/> is raised below 20 % (not at 20 %).</summary>
public enum AlarmKind { Sos, Stopped, OffCourse, Offline, LowBattery }

public sealed record Alarm(AlarmKind Kind, DateTimeOffset Since);

public sealed record SegmentResult(int Lap, int SegmentIndex, TimeSpan Time, SegmentColour Colour);

/// <param name="FinishLat">
/// The position frozen at the finish (last fix at or before the finish time) for the spectator projection; not
/// serialized, the organizer sees the live <paramref name="Lat"/>/<paramref name="Lon"/>.
/// </param>
/// <param name="FinishLon">See <paramref name="FinishLat"/>.</param>
public sealed record ParticipantView(
    string ParticipantId, string Bib, string DisplayName, string Category, int? Rank, int Lap,
    double TotalDistanceM, double LapProgressM, double? Lat, double? Lon,
    double SpeedMps, double AvgSpeedMps, double? PaceSecPerKm, double AscentM, double DescentM, double GradePercent,
    ParticipantState State, TimeSpan? GapToLeader, TimeSpan? GapToAhead, EtaEstimate? Eta,
    IReadOnlyList<SegmentResult> Segments,
    double? BatteryPct, double? GpsAccuracyM, int QueueLength, TimeSpan? LastSignalAgo, IReadOnlyList<Alarm> Alarms,
    [property: JsonIgnore] double? FinishLat = null, [property: JsonIgnore] double? FinishLon = null);

/// <param name="CountLive">Participants in state Live, Stopped, OffCourse or Sos.</param>
public sealed record LiveView(
    DateTimeOffset At, string EventId, IReadOnlyList<ParticipantView> Participants,
    int CountLive, int CountDelayed, int CountOffline, int CountDnf);
