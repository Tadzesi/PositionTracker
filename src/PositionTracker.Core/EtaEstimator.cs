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
    /// Estimates when the participant reaches <paramref name="targetTotalDistanceM"/> (default: the finish), as of
    /// the newest race fix (<see cref="ParticipantSnapshot.LastRaceFixTime"/>, else <c>LastFixTime</c>, else
    /// <paramref name="now"/>): the estimate does not slide while no fixes arrive. The speed is
    /// <see cref="ParticipantSnapshot.EtaSpeedMps"/>, turned into a flat-ground speed with the grade factor of the
    /// stretch it was measured on (<see cref="WindowFactor"/>) and integrated over the grades ahead. Approximate
    /// with under 5 minutes of moving time or while the participant is Delayed or Offline. Returns null while the
    /// speed is unusable (no moving time in the window, below 0.3 m/s, NaN or infinite).
    /// </summary>
    public static EtaEstimate? Estimate(Course course, Sport sport, ParticipantSnapshot snapshot, DateTimeOffset now,
        double? targetTotalDistanceM = null)
    {
        var target = targetTotalDistanceM ?? course.TotalLengthM;
        var from = snapshot.TotalDistanceM;
        var asOf = snapshot.LastRaceFixTime ?? snapshot.LastFixTime ?? now;
        var approximate = snapshot.MovingTime < ApproximateBelow
            || snapshot.State is ParticipantState.Delayed or ParticipantState.Offline;
        if (from >= target) return new EtaEstimate(asOf, asOf, asOf, approximate);

        var speed = snapshot.EtaSpeedMps;
        if (!double.IsFinite(speed) || speed < MinSpeedMps) return null;

        var flatSpeed = speed / WindowFactor(course, sport, snapshot);
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
            asOf + TimeSpan.FromSeconds(seconds),
            asOf + TimeSpan.FromSeconds(Math.Max(0, seconds - spread)),
            asOf + TimeSpan.FromSeconds(seconds + spread),
            approximate);
    }

    /// <summary>
    /// The grade factor of the stretch the speed was measured on, <c>[EtaWindowFromM, TotalDistanceM]</c>: its
    /// length over the sum of <c>step / factor</c>, i.e. the factor that reproduces the time a constant flat
    /// speed needs for the whole stretch with the same model used ahead. Under one step of window it falls back
    /// to the grade at the participant.
    /// </summary>
    private static double WindowFactor(Course course, Sport sport, ParticipantSnapshot snapshot)
    {
        var from = snapshot.EtaWindowFromM;
        var to = snapshot.TotalDistanceM;
        if (!(to - from >= StepM)) return GradeModel.SpeedFactor(sport, snapshot.GradePercent);

        double length = 0, weighted = 0;
        for (var d = from; d < to; d += StepM)
        {
            var len = Math.Min(StepM, to - d);
            length += len;
            weighted += len / GradeModel.SpeedFactor(sport, course.GradePercentAt((d + len / 2) % course.LapLengthM));
        }
        return length / weighted;
    }
}
