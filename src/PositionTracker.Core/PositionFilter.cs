namespace PositionTracker.Core;

public sealed record FilterOptions(double MaxAccuracyM, double MaxSpeedMps)
{
    public static FilterOptions For(Sport sport) => sport switch
    {
        Sport.Running => new FilterOptions(50, 10),
        Sport.Cycling => new FilterOptions(50, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(sport), sport, null),
    };
}

public enum FilterDecision { Accept, RejectAccuracy, RejectImpossibleSpeed, RejectDuplicate }

public static class PositionFilter
{
    /// <summary>
    /// Order: accuracy, duplicate (equal Time), speed (distance / |dt|).
    /// An equal timestamp is always a duplicate, so dt is never zero when speed is computed.
    /// </summary>
    public static FilterDecision Evaluate(PositionFix fix, PositionFix? previousAccepted, FilterOptions options)
    {
        if (fix.AccuracyM is { } accuracy && !(accuracy <= options.MaxAccuracyM))
            return FilterDecision.RejectAccuracy;

        if (previousAccepted is null)
            return FilterDecision.Accept;

        if (fix.Time == previousAccepted.Time)
            return FilterDecision.RejectDuplicate;

        var dtSeconds = Math.Abs((fix.Time - previousAccepted.Time).TotalSeconds);
        var distanceM = GeoMath.DistanceM(previousAccepted.Lat, previousAccepted.Lon, fix.Lat, fix.Lon);
        var speed = distanceM / dtSeconds;

        // Negated comparison also rejects NaN / non-finite values.
        return speed <= options.MaxSpeedMps ? FilterDecision.Accept : FilterDecision.RejectImpossibleSpeed;
    }
}
