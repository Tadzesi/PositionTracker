namespace PositionTracker.Core;

public static class GradeModel
{
    /// <summary>Relative speed on a grade compared with flat ground (1.0 = flat).</summary>
    public static double SpeedFactor(Sport sport, double gradePercent) => sport switch
    {
        Sport.Cycling => Math.Clamp(1 - 0.06 * gradePercent, 0.3, 2.0),
        _ => Math.Clamp(1 - 0.03 * gradePercent, 0.4, 1.5),
    };
}

public sealed record EtaEstimate(DateTimeOffset Expected, DateTimeOffset Earliest, DateTimeOffset Latest, bool Approximate);

public static class EtaEstimator
{
    private const double StepM = 100;
    private const double MinSpeedMps = 0.3;
    private const double SpreadFraction = 0.08;
    private static readonly TimeSpan MinSpread = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApproximateBelow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Estimates when the participant reaches <paramref name="targetTotalDistanceM"/> (default: the finish).
    /// Returns null while the moving speed is unusable (standing still, below 0.3 m/s, NaN or infinite).
    /// </summary>
    public static EtaEstimate? Estimate(Course course, Sport sport, ParticipantSnapshot snapshot, DateTimeOffset now,
        double? targetTotalDistanceM = null)
    {
        var target = targetTotalDistanceM ?? course.TotalLengthM;
        var from = snapshot.TotalDistanceM;
        var approximate = snapshot.MovingTime < ApproximateBelow;
        if (from >= target) return new EtaEstimate(now, now, now, approximate);

        var speed = snapshot.MovingSpeedMps;
        if (!double.IsFinite(speed) || speed < MinSpeedMps) return null;

        var flatSpeed = speed / GradeModel.SpeedFactor(sport, snapshot.GradePercent);
        var seconds = 0.0;
        for (var d = from; d < target; d += StepM)
        {
            var len = Math.Min(StepM, target - d);
            var lapDistance = (d + len / 2) % course.LapLengthM;
            var factor = GradeModel.SpeedFactor(sport, course.GradePercentAt(lapDistance));
            seconds += len / (flatSpeed * factor);
        }

        var spread = Math.Max(seconds * SpreadFraction, MinSpread.TotalSeconds);
        return new EtaEstimate(
            now + TimeSpan.FromSeconds(seconds),
            now + TimeSpan.FromSeconds(Math.Max(0, seconds - spread)),
            now + TimeSpan.FromSeconds(seconds + spread),
            approximate);
    }
}
