namespace PositionTracker.Core;

/// <summary>One participant's standing as input to <see cref="Ranking.Rank"/>.</summary>
/// <param name="History">The participant's total distance over time.</param>
/// <param name="Dnf">The participant did not finish (state <see cref="ParticipantState.Dnf"/>).</param>
/// <param name="Start">
/// When this participant started (their wave); times are compared as time since this start. <c>null</c> counts as one shared
/// start, so inputs without it behave as before.
/// </param>
public sealed record RankInput(
    string ParticipantId, double TotalDistanceM, DateTimeOffset? LastFixTime, DateTimeOffset? FinishTime, ProgressHistory History,
    bool Dnf = false, DateTimeOffset? Start = null);

public sealed record RankedParticipant(string ParticipantId, int Rank, TimeSpan? GapToLeader, TimeSpan? GapToAhead, double GapToLeaderM);

/// <summary>Orders participants and computes time gaps from the progress histories.</summary>
public static class Ranking
{
    /// <summary>
    /// Finishers first, by net finish time (<c>FinishTime</c> minus <c>Start</c>); then participants with fixes by <c>TotalDistanceM</c> descending,
    /// equal distances by the shorter net time at that distance; then participants without fixes; DNF participants
    /// last as a group of their own, by <c>TotalDistanceM</c> descending (a DNF after finishing included).
    /// Remaining ties fall back to ordinal participant id order so the result is deterministic.
    /// </summary>
    /// <remarks>
    /// The time gap of a participant to another one ahead is the participant's own net time at its distance
    /// (<c>FinishTime</c> for a finisher, otherwise <c>LastFixTime</c>, minus its <c>Start</c>) minus the net time the one ahead
    /// needed to reach that distance (<c>History.TimeAt</c>, interpolated, minus the other's <c>Start</c>); <c>null</c> when either
    /// time is missing. Without waves this is the plain difference of the two times. The
    /// leader's gaps are zero. A DNF participant is never the leader nor the participant ahead of anyone, and its
    /// own time gaps are <c>null</c> (all gaps are <c>null</c> when every participant is DNF).
    /// </remarks>
    public static IReadOnlyList<RankedParticipant> Rank(IReadOnlyList<RankInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var ordered = inputs
            .OrderBy(Group)
            .ThenBy(p => p.Dnf ? TimeSpan.MinValue : Net(p, p.FinishTime) ?? TimeSpan.MinValue)
            .ThenByDescending(p => p.TotalDistanceM)
            .ThenBy(p => Net(p, p.History.TimeAt(p.TotalDistanceM)) ?? TimeSpan.MaxValue)
            .ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
            .ToList();

        var result = new List<RankedParticipant>(ordered.Count);
        var leader = ordered.Count > 0 && !ordered[0].Dnf ? ordered[0] : null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var gapM = leader is null ? 0 : leader.TotalDistanceM - p.TotalDistanceM;
            if (p.Dnf || leader is null)
                result.Add(new RankedParticipant(p.ParticipantId, i + 1, null, null, gapM));
            else if (i == 0)
                result.Add(new RankedParticipant(p.ParticipantId, 1, TimeSpan.Zero, TimeSpan.Zero, 0));
            else
                result.Add(new RankedParticipant(
                    p.ParticipantId, i + 1, GapBehind(p, leader), GapBehind(p, ordered[i - 1]), gapM));
        }
        return result;
    }

    /// <summary>0 = finished, 1 = has fixes, 2 = no fixes, 3 = DNF.</summary>
    private static int Group(RankInput p) =>
        p.Dnf ? 3 : p.FinishTime is not null ? 0 : p.LastFixTime is not null ? 1 : 2;

    private static TimeSpan? GapBehind(RankInput p, RankInput ahead) =>
        Net(p, p.FinishTime ?? p.LastFixTime) is { } own && Net(ahead, ahead.History.TimeAt(p.TotalDistanceM)) is { } reached
            ? own - reached
            : null;

    /// <summary>Time since the participant's own start; the clock time itself when no start is given.</summary>
    private static TimeSpan? Net(RankInput p, DateTimeOffset? time) =>
        time is { } t ? t - (p.Start ?? DateTimeOffset.UnixEpoch) : null;
}
