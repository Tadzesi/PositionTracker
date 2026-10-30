using PositionTracker.Core.Runtime;

namespace PositionTracker.Core.Tests;

public class DelayedViewBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    private static LiveView ViewAt(double seconds, int countLive = 0) =>
        new(T0.AddSeconds(seconds), "evt-1", [], countLive, 0, 0, 0);

    [Fact]
    public void Returns_null_until_a_view_is_as_old_as_the_delay()
    {
        var buffer = new DelayedViewBuffer(Delay);
        Assert.Null(buffer.GetCurrent(T0));

        buffer.Push(ViewAt(0));

        Assert.Null(buffer.GetCurrent(T0.AddSeconds(29.9)));
        Assert.Equal(T0, buffer.GetCurrent(T0.AddSeconds(30))?.At);
    }

    [Fact]
    public void Returns_the_newest_view_not_newer_than_now_minus_delay()
    {
        var buffer = new DelayedViewBuffer(Delay);
        for (var s = 0; s <= 40; s += 5) buffer.Push(ViewAt(s));

        Assert.Equal(T0.AddSeconds(5), buffer.GetCurrent(T0.AddSeconds(37))?.At);
        Assert.Equal(T0.AddSeconds(10), buffer.GetCurrent(T0.AddSeconds(40))?.At);
        Assert.Equal(T0.AddSeconds(40), buffer.GetCurrent(T0.AddSeconds(500))?.At);
    }

    [Fact]
    public void Views_older_than_twice_the_delay_are_discarded_when_pushing()
    {
        var buffer = new DelayedViewBuffer(Delay);
        buffer.Push(ViewAt(0));
        Assert.Equal(T0, buffer.GetCurrent(T0.AddSeconds(30))?.At);

        for (var s = 10; s <= 100; s += 10) buffer.Push(ViewAt(s));

        // Kept: 40 s and newer (the newest view at least 30 s older than 100 s is 70 s).
        Assert.Null(buffer.GetCurrent(T0.AddSeconds(69)));
        Assert.Equal(T0.AddSeconds(40), buffer.GetCurrent(T0.AddSeconds(70))?.At);
    }

    [Fact]
    public void Newest_view_old_enough_is_kept_across_a_gap_so_the_view_does_not_flicker_to_null()
    {
        var buffer = new DelayedViewBuffer(Delay);
        buffer.Push(ViewAt(0));
        buffer.Push(ViewAt(100));

        Assert.Equal(T0, buffer.GetCurrent(T0.AddSeconds(100))?.At);
        Assert.Equal(T0.AddSeconds(100), buffer.GetCurrent(T0.AddSeconds(130))?.At);
    }

    [Fact]
    public void Views_with_equal_timestamps_return_the_last_pushed()
    {
        var buffer = new DelayedViewBuffer(Delay);
        buffer.Push(ViewAt(0, countLive: 1));
        buffer.Push(ViewAt(0, countLive: 2));

        Assert.Equal(2, buffer.GetCurrent(T0.AddSeconds(30))?.CountLive);
    }

    [Fact]
    public void Out_of_order_view_is_ignored()
    {
        var buffer = new DelayedViewBuffer(Delay);
        buffer.Push(ViewAt(10));
        buffer.Push(ViewAt(5));

        Assert.Null(buffer.GetCurrent(T0.AddSeconds(39)));
        Assert.Equal(T0.AddSeconds(10), buffer.GetCurrent(T0.AddSeconds(40))?.At);
    }

    [Fact]
    public void Zero_delay_returns_the_newest_view_and_negative_delay_is_rejected()
    {
        var buffer = new DelayedViewBuffer(TimeSpan.Zero);
        buffer.Push(ViewAt(0));
        buffer.Push(ViewAt(1));
        Assert.Equal(T0.AddSeconds(1), buffer.GetCurrent(T0.AddSeconds(1))?.At);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayedViewBuffer(TimeSpan.FromSeconds(-1)));
    }
}
