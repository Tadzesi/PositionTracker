namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Per-token budget of fixes: <see cref="FixesPerMinute"/> per fixed one-minute window on the injected
/// <see cref="TimeProvider"/>, on top of the request limit (a request may carry up to 500 fixes, so the request
/// limit alone would allow 30 000 fixes a minute). Fixes over the budget are not ingested; the endpoint counts
/// them as rejected and still answers 202, so a well-behaved client is never pushed into a retry loop.
/// Thread-safe.
/// </summary>
public sealed class FixRateLimiter(TimeProvider clock)
{
    public const int FixesPerMinute = 600;
    private const int PruneAbove = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Used)> _windows = new(StringComparer.Ordinal);

    /// <summary>Takes up to <paramref name="requested"/> fixes from the budget of <paramref name="key"/>; returns how many were granted.</summary>
    public int Take(string key, int requested)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (_windows.Count > PruneAbove)
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList())
                    _windows.Remove(stale);

            if (!_windows.TryGetValue(key, out var window) || now - window.Start >= Window || now < window.Start)
                window = (now, 0);
            var granted = Math.Min(requested, FixesPerMinute - window.Used);
            _windows[key] = (window.Start, window.Used + granted);
            return granted;
        }
    }
}
