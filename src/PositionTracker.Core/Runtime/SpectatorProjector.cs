namespace PositionTracker.Core.Runtime;

/// <summary>
/// A participant as spectators see it: race progress only. Device and safety data (battery, GPS accuracy, queue
/// length, last signal, alarms) and the ascent/descent/grade fields of <see cref="ParticipantView"/> are absent.
/// Values are rounded for size: coordinates to 5 decimals, distance (floored) and speed to 0.1, pace, gaps,
/// ETA and segment times to whole seconds.
/// </summary>
public sealed record SpectatorParticipantView(
    string ParticipantId, string Bib, string DisplayName, string Category, int? Rank, int Lap, double TotalDistanceM,
    double? Lat, double? Lon, double SpeedMps, double? PaceSecPerKm, ParticipantState State,
    TimeSpan? GapToLeader, TimeSpan? GapToAhead, EtaEstimate? Eta, IReadOnlyList<SegmentResult> Segments);

/// <param name="At">The time of the organizer view this was projected from (delayed for spectators).</param>
public sealed record SpectatorView(DateTimeOffset At, string EventId, IReadOnlyList<SpectatorParticipantView> Participants);

/// <summary>Reduces an organizer <see cref="LiveView"/> to what spectators may see.</summary>
public static class SpectatorProjector
{
    /// <summary>
    /// Projects every row of <paramref name="view"/>, keeping its order. The display name comes from
    /// <paramref name="participants"/>, never from the organizer row: see <see cref="DisplayName"/>. The state is
    /// reduced by <see cref="SpectatorState"/> so alarms (SOS, stopped, off course) are never visible, and the
    /// position by <see cref="SpectatorPosition"/>.
    /// </summary>
    public static SpectatorView Project(LiveView view, IReadOnlyDictionary<string, ParticipantInfo> participants)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(participants);
        return new SpectatorView(view.At, view.EventId, view.Participants
            .Select(p =>
            {
                var (lat, lon) = SpectatorPosition(p);
                return new SpectatorParticipantView(
                    p.ParticipantId, p.Bib, DisplayName(participants.GetValueOrDefault(p.ParticipantId), p.Bib),
                    p.Category, p.Rank, p.Lap, Distance(p),
                    Round(lat, 5), Round(lon, 5), Math.Round(p.SpeedMps, 1), Round(p.PaceSecPerKm, 0),
                    SpectatorState(p.State),
                    Seconds(p.GapToLeader), Seconds(p.GapToAhead),
                    p.Eta is { } eta ? eta with { Expected = Seconds(eta.Expected), Earliest = Seconds(eta.Earliest), Latest = Seconds(eta.Latest) } : null,
                    p.Segments.Select(s => s with { Time = Seconds(s.Time) }).ToList());
            })
            .ToList());
    }

    // Spectator payload rounding (final review I5): coordinates to 5 decimals (about 1 m), speeds and pace to what
    // a screen shows, times to whole seconds. The order, ranks and states are taken before rounding, so rounding
    // never reorders rows or changes a state. Distances of riders still riding are floored to 0.1 m, so the shown
    // order of distances is kept and they do not reach the course length; a finisher's is rounded, so the clamped
    // course length (e.g. 37 499.999 999 998) shows as 37 500.0.
    private static double Distance(ParticipantView p) => p.State == ParticipantState.Finished
        ? Math.Round(p.TotalDistanceM, 1, MidpointRounding.AwayFromZero)
        : Math.Floor(p.TotalDistanceM * 10) / 10;

    private static double? Round(double? value, int decimals) =>
        value is { } v ? Math.Round(v, decimals, MidpointRounding.AwayFromZero) : null;

    private static TimeSpan Seconds(TimeSpan value) =>
        TimeSpan.FromSeconds(Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero));

    private static TimeSpan? Seconds(TimeSpan? value) => value is { } v ? Seconds(v) : null;

    private static DateTimeOffset Seconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond + (value.Ticks % TimeSpan.TicksPerSecond >= TimeSpan.TicksPerSecond / 2 ? TimeSpan.TicksPerSecond : 0), value.Offset);

    /// <summary>
    /// The position spectators see, only inside the race window: none before the first race fix (warm-up, also while
    /// an SOS outranks NotStarted, see <see cref="ParticipantView.LastRaceFixTime"/>) and none for a DNF participant
    /// (a person who left the race is not tracked publicly); a finisher stays frozen at the position of the finish
    /// (<see cref="ParticipantView.FinishLat"/>), never the cool-down or the way home.
    /// </summary>
    private static (double? Lat, double? Lon) SpectatorPosition(ParticipantView p) => p.State switch
    {
        _ when p.LastRaceFixTime is null => (null, null),
        ParticipantState.NotStarted or ParticipantState.Dnf => (null, null),
        ParticipantState.Finished => (p.FinishLat, p.FinishLon),
        _ => (p.Lat, p.Lon),
    };

    /// <summary>
    /// The state spectators see: the alarm states <see cref="ParticipantState.Sos"/>,
    /// <see cref="ParticipantState.Stopped"/> and <see cref="ParticipantState.OffCourse"/> are shown as
    /// <see cref="ParticipantState.Live"/>; <see cref="ParticipantState.NotStarted"/>, <see cref="ParticipantState.Live"/>,
    /// <see cref="ParticipantState.Delayed"/>, <see cref="ParticipantState.Offline"/> ("no signal"),
    /// <see cref="ParticipantState.Finished"/> and <see cref="ParticipantState.Dnf"/> are kept.
    /// </summary>
    private static ParticipantState SpectatorState(ParticipantState state) => state switch
    {
        ParticipantState.Sos or ParticipantState.Stopped or ParticipantState.OffCourse => ParticipantState.Live,
        _ => state,
    };

    /// <summary>
    /// The nickname if the participant consented, is not a minor and has one; else the full name if consented and
    /// not a minor; else <c>"#" + bib</c> (also when the participant's info is unknown).
    /// </summary>
    private static string DisplayName(ParticipantInfo? info, string bib)
    {
        if (info is not { ShowNameToSpectators: true, IsMinor: false }) return "#" + bib;
        return string.IsNullOrWhiteSpace(info.Nickname) ? info.Name : info.Nickname;
    }
}
