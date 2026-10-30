namespace PositionTracker.Core.Tests;

public class RankingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>History at a constant speed from T0 and 0 m, a point every 100 m up to <paramref name="untilM"/>.</summary>
    private static ProgressHistory ConstantSpeed(double speedMps, double untilM)
    {
        var h = new ProgressHistory();
        for (var d = 0.0; d < untilM; d += 100) h.Add(T0.AddSeconds(d / speedMps), d);
        h.Add(T0.AddSeconds(untilM / speedMps), untilM);
        return h;
    }

    private static RankInput Rider(string id, double speedMps, double totalM, DateTimeOffset? finish = null)
    {
        var history = ConstantSpeed(speedMps, totalM);
        return new RankInput(id, totalM, history.Last!.Value.Time, finish, history);
    }

    [Fact]
    public void Ranks_by_distance_and_breaks_a_tie_by_earlier_arrival()
    {
        // Ids sort against the expected order, so only the arrival time can break the tie correctly.
        var leader = Rider("c-leader", 10, 3_000);
        var fast = Rider("b-fast", 8, 2_500);   // reaches 2 500 m after 312.5 s
        var slow = Rider("a-slow", 5, 2_500);   // reaches 2 500 m after 500 s

        var ranked = Ranking.Rank(new[] { slow, fast, leader });

        Assert.Equal(new[] { "c-leader", "b-fast", "a-slow" }, ranked.Select(r => r.ParticipantId));
        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(r => r.Rank));
    }

    [Fact]
    public void Gap_to_leader_is_last_time_minus_leader_time_at_that_distance()
    {
        var leader = Rider("leader", 10, 3_000);
        var second = Rider("second", 8, 2_500);
        var third = Rider("third", 5, 2_500);

        var ranked = Ranking.Rank(new[] { leader, second, third });

        Assert.Equal(TimeSpan.Zero, ranked[0].GapToLeader);
        Assert.Equal(TimeSpan.Zero, ranked[0].GapToAhead);
        Assert.Equal(0, ranked[0].GapToLeaderM);

        var expected = second.LastFixTime!.Value - leader.History.TimeAt(2_500)!.Value; // 312.5 s - 250 s
        Assert.NotNull(ranked[1].GapToLeader);
        Assert.InRange((ranked[1].GapToLeader!.Value - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.InRange(ranked[1].GapToLeader!.Value.TotalSeconds, 62.499, 62.501);
        Assert.Equal(ranked[1].GapToLeader, ranked[1].GapToAhead);
        Assert.Equal(500, ranked[1].GapToLeaderM);

        // The third is measured against the second, who reached 2 500 m at 312.5 s.
        Assert.InRange(ranked[2].GapToAhead!.Value.TotalSeconds, 187.499, 187.501);
        Assert.InRange(ranked[2].GapToLeader!.Value.TotalSeconds, 249.999, 250.001);
    }

    [Fact]
    public void Finishers_rank_above_every_unfinished_participant_ordered_by_finish_time()
    {
        var rolling = Rider("rolling", 12, 3_000);
        var lateFinisher = Rider("late", 5, 2_000, finish: T0.AddSeconds(400));
        var earlyFinisher = Rider("early", 8, 2_000, finish: T0.AddSeconds(250));

        var ranked = Ranking.Rank(new[] { rolling, lateFinisher, earlyFinisher });

        Assert.Equal(new[] { "early", "late", "rolling" }, ranked.Select(r => r.ParticipantId));
        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(r => r.Rank));
    }

    [Fact]
    public void Participant_without_fixes_ranks_last_with_null_gaps()
    {
        var none = new RankInput("a-none", 0, null, null, new ProgressHistory());
        var standing = Rider("b-standing", 5, 0);
        var moving = Rider("c-moving", 5, 1_000);

        var ranked = Ranking.Rank(new[] { none, standing, moving });

        Assert.Equal(new[] { "c-moving", "b-standing", "a-none" }, ranked.Select(r => r.ParticipantId));
        Assert.Equal(3, ranked[2].Rank);
        Assert.Null(ranked[2].GapToLeader);
        Assert.Null(ranked[2].GapToAhead);
    }

    [Fact]
    public void Empty_input_gives_empty_ranking()
    {
        Assert.Empty(Ranking.Rank(Array.Empty<RankInput>()));
    }

    [Fact]
    public void Dnf_rider_in_front_is_ranked_last_and_is_never_the_leader_or_the_one_ahead()
    {
        var dnf = Rider("a-dnf", 10, 9_000) with { Dnf = true };
        var first = Rider("b-first", 8, 3_000);
        var second = Rider("c-second", 6, 2_000);

        var ranked = Ranking.Rank(new[] { dnf, second, first });

        Assert.Equal(new[] { "b-first", "c-second", "a-dnf" }, ranked.Select(r => r.ParticipantId));
        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(r => r.Rank));
        Assert.Equal(TimeSpan.Zero, ranked[0].GapToLeader);
        var expected = second.LastFixTime!.Value - first.History.TimeAt(2_000)!.Value;
        Assert.Equal(expected, ranked[1].GapToLeader);
        Assert.Equal(expected, ranked[1].GapToAhead);
        Assert.Null(ranked[2].GapToLeader);
        Assert.Null(ranked[2].GapToAhead);
    }

    [Fact]
    public void Dnf_riders_come_after_riders_without_fixes_ordered_by_distance_and_finishers_stay_first()
    {
        var finisher = Rider("z-finisher", 10, 5_000, finish: T0.AddSeconds(500));
        var riding = Rider("y-riding", 8, 3_000);
        var idle = new RankInput("x-idle", 0, null, null, new ProgressHistory());
        var dnfNear = Rider("a-dnf-near", 5, 1_000) with { Dnf = true };
        var dnfFar = Rider("b-dnf-far", 9, 4_500) with { Dnf = true };
        var dnfFinished = Rider("c-dnf-finished", 11, 5_000, finish: T0.AddSeconds(455)) with { Dnf = true };

        var ranked = Ranking.Rank(new[] { dnfNear, idle, dnfFinished, riding, dnfFar, finisher });

        Assert.Equal(new[] { "z-finisher", "y-riding", "x-idle", "c-dnf-finished", "b-dnf-far", "a-dnf-near" },
            ranked.Select(r => r.ParticipantId));
        Assert.All(ranked.Skip(3), r => Assert.Null(r.GapToLeader));
        Assert.All(ranked.Skip(3), r => Assert.Null(r.GapToAhead));
    }

    [Fact]
    public void Two_finishers_are_ordered_by_finish_time_and_gapped_by_finish_times()
    {
        // Both histories end at the finish; the second crossed 1.5 s later.
        var first = Rider("b-first", 10, 5_000, finish: T0.AddSeconds(500));
        var second = Rider("a-second", 10, 5_000, finish: T0.AddSeconds(501.5));
        var secondHistory = new ProgressHistory();
        secondHistory.Add(T0, 0);
        secondHistory.Add(T0.AddSeconds(501.5), 5_000);
        second = second with { History = secondHistory, LastFixTime = T0.AddSeconds(501.5) };

        var ranked = Ranking.Rank(new[] { second, first });

        Assert.Equal(new[] { "b-first", "a-second" }, ranked.Select(r => r.ParticipantId));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ranked[1].GapToLeader);
        Assert.Equal(TimeSpan.FromSeconds(1.5), ranked[1].GapToAhead);
    }
}
