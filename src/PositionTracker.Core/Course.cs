namespace PositionTracker.Core;

public sealed record Checkpoint(string Name, double LapDistanceM);

public sealed class Course
{
    private const double ClosedToleranceM = 100;
    /// <summary>Shorter courses are rejected: a lap this short cannot be matched or timed meaningfully.</summary>
    public const double MinLapLengthM = 50;
    private const double HysteresisM = 3;
    private const int SmoothingRadius = 5;
    private const double GradeWindowM = 50;

    private readonly double[] _ele;        // smoothed elevation per point (empty when no elevation)
    private readonly double[] _cumAscent;  // committed ascent per point
    private readonly double[] _cumDescent;

    public IReadOnlyList<GeoPoint> Points { get; }
    public IReadOnlyList<double> CumulativeM { get; }
    public double LapLengthM { get; }
    public int Laps { get; }
    public double TotalLengthM => LapLengthM * Laps;
    public bool IsClosed { get; }
    public bool HasElevation { get; }
    public double AscentPerLapM { get; }
    public double DescentPerLapM { get; }
    public IReadOnlyList<Checkpoint> Checkpoints { get; }
    public IReadOnlyList<double> SegmentBoundariesM { get; }
    public int SegmentCount => SegmentBoundariesM.Count - 1;

    private Course(IReadOnlyList<GeoPoint> points, int laps, IReadOnlyList<Checkpoint> checkpoints)
    {
        Points = points;
        Laps = laps;
        var cum = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
            cum[i] = cum[i - 1] + GeoMath.DistanceM(points[i - 1], points[i]);
        CumulativeM = cum;
        LapLengthM = cum[^1];
        if (!(LapLengthM >= MinLapLengthM))
            throw new ArgumentException($"A course must be at least {MinLapLengthM:F0} m long (got {LapLengthM:F1} m).", nameof(points));
        IsClosed = GeoMath.DistanceM(points[0], points[^1]) <= ClosedToleranceM;
        if (laps > 1 && !IsClosed)
            throw new ArgumentException("A course with more than one lap must be closed.", nameof(laps));

        foreach (var cp in checkpoints)
            if (!(cp.LapDistanceM > 0 && cp.LapDistanceM < LapLengthM))
                throw new ArgumentException($"Checkpoint '{cp.Name}' lies outside (0, {LapLengthM:F0}) m.", nameof(checkpoints));
        Checkpoints = checkpoints.OrderBy(c => c.LapDistanceM).ToList();
        SegmentBoundariesM = new[] { 0.0 }.Concat(Checkpoints.Select(c => c.LapDistanceM)).Append(LapLengthM).ToList();

        HasElevation = points.All(p => p.Ele.HasValue);
        _cumAscent = new double[points.Count];
        _cumDescent = new double[points.Count];
        if (HasElevation)
        {
            _ele = Smooth(points);
            // Hysteresis on the running extremum: within a climb every new maximum counts at once and a dip of
            // up to HysteresisM is ignored; a drop of more than HysteresisM below the maximum starts a descent,
            // counted from that maximum (and the other way round). Swings are counted in full, not short by up
            // to HysteresisM at every reversal as with a fixed reference that jumps in steps.
            var direction = 0;        // +1 climbing, -1 descending, 0 before the first swing
            var extreme = _ele[0];    // highest point of the climb, lowest of the descent, or the start
            for (var i = 1; i < _ele.Length; i++)
            {
                _cumAscent[i] = _cumAscent[i - 1];
                _cumDescent[i] = _cumDescent[i - 1];
                var e = _ele[i];
                if (direction > 0 ? e > extreme : e - extreme > HysteresisM)
                {
                    _cumAscent[i] += e - extreme;
                    direction = 1;
                    extreme = e;
                }
                else if (direction < 0 ? e < extreme : extreme - e > HysteresisM)
                {
                    _cumDescent[i] += extreme - e;
                    direction = -1;
                    extreme = e;
                }
            }
            AscentPerLapM = _cumAscent[^1];
            DescentPerLapM = _cumDescent[^1];
        }
        else
        {
            _ele = Array.Empty<double>();
        }
    }

    public static Course Create(IReadOnlyList<GeoPoint> points, int laps, IReadOnlyList<Checkpoint>? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException("A course needs at least two points.", nameof(points));
        if (laps < 1) throw new ArgumentOutOfRangeException(nameof(laps), "Laps must be at least 1.");
        return new Course(points, laps, checkpoints ?? Array.Empty<Checkpoint>());
    }

    public double ElevationAt(double lapDistanceM)
    {
        if (!HasElevation) return 0;
        var (i, f) = Locate(lapDistanceM);
        return Lerp(_ele[i], _ele[i + 1], f);
    }

    public GeoPoint PointAt(double lapDistanceM)
    {
        var (i, f) = Locate(lapDistanceM);
        var a = Points[i];
        var b = Points[i + 1];
        return new GeoPoint(Lerp(a.Lat, b.Lat, f), Lerp(a.Lon, b.Lon, f), HasElevation ? Lerp(_ele[i], _ele[i + 1], f) : null);
    }

    public double GradePercentAt(double lapDistanceM)
    {
        if (!HasElevation) return 0;
        var from = Math.Max(0, lapDistanceM - GradeWindowM / 2);
        var to = Math.Min(LapLengthM, lapDistanceM + GradeWindowM / 2);
        return to - from <= 0 ? 0 : (ElevationAt(to) - ElevationAt(from)) / (to - from) * 100;
    }

    public double AscentUpTo(double totalDistanceM) => Accumulated(totalDistanceM, _cumAscent, AscentPerLapM);

    public double DescentUpTo(double totalDistanceM) => Accumulated(totalDistanceM, _cumDescent, DescentPerLapM);

    private double Accumulated(double totalDistanceM, double[] cum, double perLap)
    {
        if (!HasElevation || totalDistanceM <= 0) return 0;
        totalDistanceM = Math.Min(totalDistanceM, TotalLengthM);
        var whole = Math.Min(Laps, (int)Math.Floor(totalDistanceM / LapLengthM));
        var rest = totalDistanceM - whole * LapLengthM;
        var (i, f) = Locate(rest);
        return whole * perLap + Lerp(cum[i], cum[i + 1], f);
    }

    /// <summary>Index of the segment [i, i+1] containing the clamped distance and the fraction within it.</summary>
    private (int Index, double Fraction) Locate(double lapDistanceM)
    {
        var d = Math.Clamp(lapDistanceM, 0, LapLengthM);
        var arr = (double[])CumulativeM;
        var idx = Array.BinarySearch(arr, d);
        if (idx < 0) idx = ~idx - 1;
        idx = Math.Clamp(idx, 0, arr.Length - 2);
        var span = arr[idx + 1] - arr[idx];
        return (idx, span <= 0 ? 0 : (d - arr[idx]) / span);
    }

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    private static double[] Smooth(IReadOnlyList<GeoPoint> points)
    {
        var n = points.Count;
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - SmoothingRadius), hi = Math.Min(n - 1, i + SmoothingRadius);
            double sum = 0;
            for (var j = lo; j <= hi; j++) sum += points[j].Ele!.Value;
            result[i] = sum / (hi - lo + 1);
        }
        return result;
    }
}
