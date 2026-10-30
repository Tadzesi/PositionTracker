namespace PositionTracker.Core;

/// <summary>One participant's standing as input to <see cref="Ranking.Rank"/>.</summary>
/// <param name="History">The participant's total distance over time.</param>
public sealed record RankInput(
    string ParticipantId, double TotalDistanceM, DateTimeOffset? LastFixTime, DateTimeOffset? FinishTime, ProgressHistory History);

public sealed record RankedParticipant(string ParticipantId, int Rank, TimeSpan? GapToLeader, TimeSpan? GapToAhead, double GapToLeaderM);

/// <summary>Orders participants and computes time gaps from the progress histories.</summary>
public static class Ranking
{
    /// <summary>
    /// Finishers first, by <c>FinishTime</c>; then participants with fixes by <c>TotalDistanceM</c> descending,
    /// equal distances by the earlier time at that distance; participants without fixes last. Remaining ties
    /// fall back to ordinal participant id order so the result is deterministic.
    /// </summary>
    /// <remarks>
    /// The time gap of a participant to another one ahead is the participant's own time at its distance
    /// (<c>FinishTime</c> for a finisher, otherwise <c>LastFixTime</c>) minus the time the one ahead reached
    /// that distance (<c>History.TimeAt</c>, interpolated); <c>null</c> when either time is missing. The
    /// leader's gaps are zero.
    /// </remarks>
    public static IReadOnlyList<RankedParticipant> Rank(IReadOnlyList<RankInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var ordered = inputs
            .OrderBy(Group)
            .ThenBy(p => p.FinishTime ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.TotalDistanceM)
            .ThenBy(p => p.History.TimeAt(p.TotalDistanceM) ?? DateTimeOffset.MaxValue)
            .ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
            .ToList();

        var result = new List<RankedParticipant>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (i == 0)
            {
                result.Add(new RankedParticipant(p.ParticipantId, 1, TimeSpan.Zero, TimeSpan.Zero, 0));
                continue;
            }
            var leader = ordered[0];
            result.Add(new RankedParticipant(
                p.ParticipantId, i + 1, GapBehind(p, leader), GapBehind(p, ordered[i - 1]),
                leader.TotalDistanceM - p.TotalDistanceM));
        }
        return result;
    }

    /// <summary>0 = finished, 1 = has fixes, 2 = no fixes.</summary>
    private static int Group(RankInput p) => p.FinishTime is not null ? 0 : p.LastFixTime is not null ? 1 : 2;

    private static TimeSpan? GapBehind(RankInput p, RankInput ahead) =>
        (p.FinishTime ?? p.LastFixTime) is { } own && ahead.History.TimeAt(p.TotalDistanceM) is { } reached
            ? own - reached
            : null;
}
