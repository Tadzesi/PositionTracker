namespace PositionTracker.Core.Simulation;

public sealed record VirtualRiderProfile(
    string Id, double BaseSpeedMps, double SpeedJitter, double GpsNoiseM, int Seed,
    IReadOnlyList<(TimeSpan From, TimeSpan To)> Dropouts,
    TimeSpan? SosAt, TimeSpan? OffCourseAt, TimeSpan? OffCourseFor);

/// <summary>A fix plus the moment it reaches the server (later than its own time when queued during a dropout).</summary>
public sealed record GeneratedFix(PositionFix Fix, DateTimeOffset DeliverAt);

/// <summary>Deterministic fake GPS fixes for the demo simulator; a pure function of its inputs.</summary>
public static class VirtualRider
{
    private const double MetresPerDegLat = 111_195;
    private const double OffCourseOffsetM = 150;
    private const double MinSpeedMps = 0.5;

    public static IEnumerable<GeneratedFix> Generate(Course course, Sport sport, VirtualRiderProfile profile,
        DateTimeOffset start, TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(profile);
        if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(step), "Step must be positive.");
        return GenerateCore(course, sport, profile, start, step);
    }

    private static IEnumerable<GeneratedFix> GenerateCore(Course course, Sport sport, VirtualRiderProfile profile,
        DateTimeOffset start, TimeSpan step)
    {
        var rng = new Random(profile.Seed);
        var stepS = step.TotalSeconds;
        var sigma = double.IsFinite(profile.GpsNoiseM) ? Math.Max(0, profile.GpsNoiseM) : 0;
        var jitterAmp = double.IsFinite(profile.SpeedJitter) ? Math.Max(0, profile.SpeedJitter) : 0;
        var baseSpeed = double.IsFinite(profile.BaseSpeedMps) ? profile.BaseSpeedMps : 0;
        var accuracy = Math.Max(3, 2 * sigma);
        var total = course.TotalLengthM;

        double dist = 0;
        for (var k = 0; ; k++)
        {
            var elapsed = TimeSpan.FromTicks(step.Ticks * k);
            var finished = dist >= total;
            if (finished) dist = total;
            var lapDist = finished ? course.LapLengthM : dist % course.LapLengthM;

            // Fixed number of random draws per step keeps the sequence independent of branches.
            var jitter = (rng.NextDouble() * 2 - 1) * jitterAmp;
            var (gx, gy) = Gaussian2(rng);

            var p = course.PointAt(lapDist);
            double eastM = gx * sigma, northM = gy * sigma;
            if (profile.OffCourseAt is { } offAt && profile.OffCourseFor is { } offFor
                && elapsed >= offAt && elapsed < offAt + offFor)
            {
                var ahead = course.PointAt(Math.Min(course.LapLengthM, lapDist + 10));
                var behind = course.PointAt(Math.Max(0, lapDist - 10));
                var cosLat = Math.Cos(p.Lat * Math.PI / 180);
                var dE = (ahead.Lon - behind.Lon) * MetresPerDegLat * cosLat;
                var dN = (ahead.Lat - behind.Lat) * MetresPerDegLat;
                var len = Math.Sqrt(dE * dE + dN * dN);
                if (len > 1e-9)
                {
                    eastM += -dN / len * OffCourseOffsetM; // left-hand perpendicular
                    northM += dE / len * OffCourseOffsetM;
                }
                else
                {
                    northM += OffCourseOffsetM;
                }
            }

            var lat = p.Lat + northM / MetresPerDegLat;
            var lon = p.Lon + eastM / (MetresPerDegLat * Math.Max(0.01, Math.Cos(p.Lat * Math.PI / 180)));
            var time = start + elapsed;
            var fix = new PositionFix(time, lat, lon, accuracy);
            yield return new GeneratedFix(fix, DeliverAtFor(profile, start, elapsed, time));

            if (finished) yield break;

            var grade = course.GradePercentAt(lapDist);
            var speed = baseSpeed * GradeModel.SpeedFactor(sport, grade) * (1 + jitter);
            if (!double.IsFinite(speed) || speed < MinSpeedMps) speed = MinSpeedMps;
            dist += speed * stepS;
        }
    }

    private static DateTimeOffset DeliverAtFor(VirtualRiderProfile profile, DateTimeOffset start, TimeSpan elapsed, DateTimeOffset time)
    {
        foreach (var (from, to) in profile.Dropouts)
            if (elapsed >= from && elapsed < to) return start + to;
        return time;
    }

    private static (double, double) Gaussian2(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble(); // (0,1]
        var u2 = rng.NextDouble();
        var r = Math.Sqrt(-2.0 * Math.Log(u1));
        return (r * Math.Cos(2 * Math.PI * u2), r * Math.Sin(2 * Math.PI * u2));
    }
}
