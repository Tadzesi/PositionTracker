using System.Runtime.CompilerServices;

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
        var seconds = FlatDistance(course, sport, from, target) / flatSpeed;

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

        return (to - from) / FlatDistance(course, sport, from, to);
    }

    /// <summary>
    /// The flat-ground distance equivalent to the course between two total distances: <c>∫ dx / factor(grade(x))</c>,
    /// the time a constant flat speed of 1 m/s needs for it. Read from a per-lap prefix table, so the cost does not grow
    /// with the distance left (a long multi-lap race once spent most of the live view walking the course in 100 m steps).
    /// </summary>
    private static double FlatDistance(Course course, Sport sport, double from, double to)
    {
        var table = Tables.GetValue(course, _ => new LapTables()).For(course, sport);
        return Cumulative(course, table, to) - Cumulative(course, table, from);
    }

    private static double Cumulative(Course course, double[] table, double totalM)
    {
        var lap = course.LapLengthM;
        var whole = Math.Floor(totalM / lap);
        var x = totalM - whole * lap;
        var cells = table.Length - 1;
        var i = Math.Min((int)(x / TableStepM), cells - 1);
        var cellStart = i * TableStepM;
        var cellLength = Math.Min(TableStepM, lap - cellStart);
        var within = cellLength > 0 ? Math.Clamp((x - cellStart) / cellLength, 0, 1) : 0;
        return whole * table[cells] + table[i] + (table[i + 1] - table[i]) * within;
    }

    private const double TableStepM = 10;

    private static readonly ConditionalWeakTable<Course, LapTables> Tables = new();

    /// <summary>Per sport, the running sum of <c>step / factor</c> over one lap in <see cref="TableStepM"/> cells.</summary>
    private sealed class LapTables
    {
        private readonly double[]?[] _bySport = new double[]?[Enum.GetValues<Sport>().Length];

        public double[] For(Course course, Sport sport)
        {
            var index = (int)sport;
            if (Volatile.Read(ref _bySport[index]) is { } built) return built;
            var lap = course.LapLengthM;
            var cells = Math.Max(1, (int)Math.Ceiling(lap / TableStepM));
            var table = new double[cells + 1];
            for (var i = 0; i < cells; i++)
            {
                var start = i * TableStepM;
                var len = Math.Min(TableStepM, lap - start);
                table[i + 1] = table[i] + (len > 0 ? len / GradeModel.SpeedFactor(sport, course.GradePercentAt(start + len / 2)) : 0);
            }
            Volatile.Write(ref _bySport[index], table); // a race builds the same table twice, harmlessly
            return table;
        }
    }
}
