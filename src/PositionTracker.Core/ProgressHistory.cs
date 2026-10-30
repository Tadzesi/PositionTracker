namespace PositionTracker.Core;

/// <summary>
/// Total distance over time for one participant, in time order. Distance never decreases, so the
/// history can be searched both by time and by distance.
/// </summary>
public sealed class ProgressHistory
{
    private readonly List<DateTimeOffset> _times = new();
    private readonly List<double> _distances = new();

    public (DateTimeOffset Time, double DistanceM)? Last => _times.Count == 0 ? null : (_times[^1], _distances[^1]);

    /// <summary>
    /// Appends a point. A distance below the last one is raised to it. A time equal to the last one updates
    /// that point; an earlier time is rejected (callers replay out-of-order input from scratch).
    /// </summary>
    public void Add(DateTimeOffset time, double totalDistanceM)
    {
        if (!double.IsFinite(totalDistanceM))
            throw new ArgumentOutOfRangeException(nameof(totalDistanceM), totalDistanceM, "Distance must be finite.");
        if (_times.Count > 0)
        {
            if (time < _times[^1])
                throw new ArgumentOutOfRangeException(nameof(time), time, "Points must be added in time order.");
            totalDistanceM = Math.Max(totalDistanceM, _distances[^1]);
            if (time == _times[^1])
            {
                _distances[^1] = totalDistanceM;
                return;
            }
        }
        _times.Add(time);
        _distances.Add(totalDistanceM);
    }

    /// <summary>
    /// The first time the distance was reached, linearly interpolated between points;
    /// <c>null</c> outside the recorded distance range.
    /// </summary>
    public DateTimeOffset? TimeAt(double totalDistanceM)
    {
        if (_times.Count == 0 || !(totalDistanceM >= _distances[0]) || totalDistanceM > _distances[^1]) return null;

        var i = FirstIndexAtOrAbove(totalDistanceM);
        if (i == 0 || _distances[i] == totalDistanceM) return _times[i];

        // _distances[i - 1] < totalDistanceM < _distances[i], so the span is positive.
        var f = (totalDistanceM - _distances[i - 1]) / (_distances[i] - _distances[i - 1]);
        var ticks = (_times[i] - _times[i - 1]).Ticks;
        return _times[i - 1] + TimeSpan.FromTicks((long)Math.Round(ticks * f));
    }

    /// <summary>
    /// Average speed between the first and last point inside <c>[until - window, until]</c>;
    /// 0 when fewer than two points fall inside.
    /// </summary>
    public double SpeedMps(DateTimeOffset until, TimeSpan window)
    {
        var first = FirstTimeIndexAtOrAfter(until - window);
        var last = FirstTimeIndexAtOrAfter(until);
        if (last < _times.Count && _times[last] == until) last++;
        last--; // last index with time <= until
        if (last - first < 1) return 0;

        var dt = (_times[last] - _times[first]).TotalSeconds;
        return dt > 0 ? (_distances[last] - _distances[first]) / dt : 0;
    }

    internal void Clear()
    {
        _times.Clear();
        _distances.Clear();
    }

    private int FirstIndexAtOrAbove(double distanceM)
    {
        int lo = 0, hi = _distances.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_distances[mid] >= distanceM) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    private int FirstTimeIndexAtOrAfter(DateTimeOffset time)
    {
        int lo = 0, hi = _times.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_times[mid] >= time) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}
