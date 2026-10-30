namespace PositionTracker.Core.Tests;

public class SegmentBoardTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_result_of_a_single_participant_is_green()
    {
        var board = new SegmentBoard();

        Assert.Equal(SegmentColour.Green, board.Record("a", 1, 0, S(100)));
        Assert.Equal(SegmentColour.Green, board.ColourOf("a", 1, 0));
    }

    [Fact]
    public void One_participant_over_several_laps_is_never_purple()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));

        Assert.Equal(SegmentColour.Green, board.Record("a", 2, 0, S(90)));
        Assert.Equal(SegmentColour.Yellow, board.ColourOf("a", 1, 0));
    }

    [Fact]
    public void With_two_participants_the_faster_is_purple_and_the_slower_own_best_green()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));

        Assert.Equal(SegmentColour.Green, board.Record("b", 1, 0, S(120)));
        Assert.Equal(SegmentColour.Purple, board.ColourOf("a", 1, 0));
    }

    [Fact]
    public void Faster_result_recorded_second_is_purple()
    {
        var board = new SegmentBoard();
        board.Record("b", 1, 0, S(120));

        Assert.Equal(SegmentColour.Purple, board.Record("a", 1, 0, S(100)));
        Assert.Equal(SegmentColour.Green, board.ColourOf("b", 1, 0));
    }

    [Fact]
    public void Slower_participants_later_worse_result_is_yellow()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(120));

        Assert.Equal(SegmentColour.Yellow, board.Record("b", 2, 0, S(130)));
        Assert.Equal(SegmentColour.Green, board.ColourOf("b", 1, 0));
    }

    [Fact]
    public void Times_from_all_laps_of_a_segment_are_compared()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(120));

        Assert.Equal(SegmentColour.Purple, board.Record("b", 2, 0, S(95)));
        Assert.Equal(SegmentColour.Green, board.ColourOf("a", 1, 0));
        Assert.Equal(SegmentColour.Yellow, board.ColourOf("b", 1, 0));
    }

    [Fact]
    public void Segments_are_independent()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(120));

        Assert.Equal(SegmentColour.Green, board.Record("b", 1, 1, S(50)));
    }

    [Fact]
    public void Re_recording_the_same_key_replaces_the_time()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(120));

        // b's corrected time is now the best; a's old 100 s no longer leads.
        Assert.Equal(SegmentColour.Purple, board.Record("b", 1, 0, S(90)));
        Assert.Equal(SegmentColour.Green, board.ColourOf("a", 1, 0));

        // Re-recording a with a slower time: a single entry for (a, 1, 0), so it is still its own best.
        Assert.Equal(SegmentColour.Green, board.Record("a", 1, 0, S(110)));
        Assert.Equal(SegmentColour.Purple, board.ColourOf("b", 1, 0));
    }

    [Fact]
    public void Re_recording_does_not_count_as_a_second_participant()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));

        Assert.Equal(SegmentColour.Green, board.Record("a", 1, 0, S(90)));
    }

    [Fact]
    public void Purple_stays_purple_until_a_faster_time_appears()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(120));
        board.Record("c", 1, 0, S(105));
        board.Record("b", 2, 0, S(101));

        Assert.Equal(SegmentColour.Purple, board.ColourOf("a", 1, 0));

        board.Record("c", 2, 0, S(99));
        Assert.Equal(SegmentColour.Green, board.ColourOf("a", 1, 0));
        Assert.Equal(SegmentColour.Purple, board.ColourOf("c", 2, 0));
    }

    [Fact]
    public void ColourOf_an_unrecorded_result_throws()
    {
        var board = new SegmentBoard();

        Assert.Throws<KeyNotFoundException>(() => board.ColourOf("a", 1, 0));
    }

    [Fact]
    public void Two_participants_tied_for_the_fastest_time_are_both_purple()
    {
        var board = new SegmentBoard();
        board.Record("a", 1, 0, S(100));
        board.Record("b", 1, 0, S(100));
        board.Record("c", 1, 0, S(101));

        Assert.Equal(SegmentColour.Purple, board.ColourOf("a", 1, 0));
        Assert.Equal(SegmentColour.Purple, board.ColourOf("b", 1, 0));
        Assert.Equal(SegmentColour.Green, board.ColourOf("c", 1, 0));
    }
}
