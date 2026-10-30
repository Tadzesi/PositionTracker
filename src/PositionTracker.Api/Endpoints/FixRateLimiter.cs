namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Per-token budget of fixes: <see cref="FixesPerMinute"/> per fixed one-minute window on the injected
/// <see cref="TimeProvider"/>, on top of the request limit (a request may carry up to 500 fixes, so the request
/// limit alone would allow 30 000 fixes a minute). A batch is taken all or nothing: if it does not fit into the
/// rest of the current window, none of it is taken and the caller is told when the window ends, so the endpoint
/// answers 429 with <c>Retry-After</c> and the client resends the same batch then (no fix is dropped silently).
/// A batch is never larger than the budget, so it always fits into a fresh window. Thread-safe.
/// </summary>
public sealed class FixRateLimiter(TimeProvider clock)
{
    public const int FixesPerMinute = 600;
    private const int PruneAbove = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Used)> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes all <paramref name="requested"/> fixes from the budget of <paramref name="key"/> and returns true, or
    /// takes nothing and returns false with <paramref name="retryAfter"/> set to the time until the current window
    /// ends (the whole batch fits then). Throws <see cref="ArgumentOutOfRangeException"/> for more than
    /// <see cref="FixesPerMinute"/>, which could never be granted.
    /// </summary>
    public bool TryTake(string key, int requested, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requested, FixesPerMinute);
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (_windows.Count > PruneAbove)
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList())
                    _windows.Remove(stale);

            if (!_windows.TryGetValue(key, out var window) || now - window.Start >= Window || now < window.Start)
                window = (now, 0);
            if (window.Used + requested > FixesPerMinute)
            {
                _windows[key] = window;
                retryAfter = window.Start + Window - now;
                return false;
            }
            _windows[key] = (window.Start, window.Used + requested);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
