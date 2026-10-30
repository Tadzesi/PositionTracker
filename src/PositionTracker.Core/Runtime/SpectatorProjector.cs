namespace PositionTracker.Core.Runtime;

/// <summary>
/// A participant as spectators see it: race progress only. Device and safety data (battery, GPS accuracy, queue
/// length, last signal, alarms) and the ascent/descent/grade fields of <see cref="ParticipantView"/> are absent.
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
            .Select(p => new SpectatorParticipantView(
                p.ParticipantId, p.Bib, DisplayName(participants.GetValueOrDefault(p.ParticipantId), p.Bib),
                p.Category, p.Rank, p.Lap, p.TotalDistanceM,
                SpectatorPosition(p).Lat, SpectatorPosition(p).Lon, p.SpeedMps, p.PaceSecPerKm,
                SpectatorState(p.State),
                p.GapToLeader, p.GapToAhead, p.Eta, p.Segments))
            .ToList());
    }

    /// <summary>
    /// The position spectators see, only inside the race window: none before the start (warm-up) and none for a
    /// DNF participant (a person who left the race is not tracked publicly); a finisher stays frozen at the
    /// position of the finish (<see cref="ParticipantView.FinishLat"/>), never the cool-down or the way home.
    /// </summary>
    private static (double? Lat, double? Lon) SpectatorPosition(ParticipantView p) => p.State switch
    {
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
