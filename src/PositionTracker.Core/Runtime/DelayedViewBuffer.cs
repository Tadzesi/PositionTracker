namespace PositionTracker.Core.Runtime;

/// <summary>
/// Holds recent organizer views so spectators can be served the state of <c>delay</c> ago. Views are pushed in
/// time order (equal timestamps allowed; an older view than the newest is ignored). On push, views older than
/// twice the delay (relative to the newest view) are discarded, except the newest view that is at least
/// <c>delay</c> old, so <see cref="GetCurrent"/> does not fall back to <c>null</c> after a gap. Thread-safe.
/// </summary>
public sealed class DelayedViewBuffer
{
    private readonly object _lock = new();
    private readonly TimeSpan _delay;
    private readonly List<LiveView> _views = [];

    public DelayedViewBuffer(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        _delay = delay;
    }

    public void Push(LiveView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_lock)
        {
            if (_views.Count > 0 && view.At < _views[^1].At) return;
            _views.Add(view);

            var discardBefore = view.At - 2 * _delay;
            var delayedCutoff = view.At - _delay;
            var drop = 0;
            while (drop < _views.Count - 1
                   && _views[drop].At < discardBefore
                   && _views[drop + 1].At <= delayedCutoff)
                drop++;
            _views.RemoveRange(0, drop);
        }
    }

    /// <summary>The newest view with <c>At &lt;= now - delay</c>, or <c>null</c> if none is that old.</summary>
    public LiveView? GetCurrent(DateTimeOffset now)
    {
        var cutoff = now - _delay;
        lock (_lock)
        {
            for (var i = _views.Count - 1; i >= 0; i--)
                if (_views[i].At <= cutoff) return _views[i];
            return null;
        }
    }
}
