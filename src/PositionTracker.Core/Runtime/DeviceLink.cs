namespace PositionTracker.Core.Runtime;

/// <summary>How well a participant's data reaches the server: the traffic light shown to the participant and the organizer.</summary>
/// <remarks>
/// <see cref="Waiting"/>: nothing received yet and the start has not come. <see cref="Good"/>: positions are stored.
/// <see cref="Warning"/>: data arrive, but with a problem (<see cref="DeviceLink.Problems"/>). <see cref="Silent"/>: nothing
/// received for a while (or nothing at all after the start).
/// </remarks>
public enum LinkLevel { Waiting, Good, Warning, Silent }

/// <summary>What is wrong with a device that is sending.</summary>
public enum LinkProblem { Clock, Accuracy, LowBattery, Rejected }

/// <param name="LastBatchAt">When the server last received anything from the device (positions or status), server clock.</param>
/// <param name="LastAcceptedAt">When the server last stored a position of the device.</param>
/// <param name="AccuracyM">Accuracy of the newest position received.</param>
/// <param name="EarlyOnly">The device reaches the server, but its positions are still too long before the start to be stored.</param>
/// <param name="ClockAheadSeconds">How far the newest position's time is ahead of the server (negative: behind).</param>
public sealed record DeviceLink(
    LinkLevel Level, DateTimeOffset? LastBatchAt, DateTimeOffset? LastAcceptedAt, double? AccuracyM, double? ClockAheadSeconds,
    double? BatteryPct, IReadOnlyList<LinkProblem> Problems, bool EarlyOnly = false);

/// <summary>The rules behind <see cref="DeviceLink"/>; kept apart from the runtime so they can be tested on their own.</summary>
public static class DeviceLinkRules
{
    /// <summary>No data for this long after the last batch: <see cref="LinkLevel.Silent"/>.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(90);

    /// <summary>A newest position this far ahead of the server means the device clock is off (the server still takes up to 5 minutes).</summary>
    public const double ClockWarnSeconds = 20;

    /// <summary>A rejection counts as current for this long.</summary>
    public static readonly TimeSpan RejectionCurrent = TimeSpan.FromSeconds(60);

    public static DeviceLink Evaluate(
        DateTimeOffset now, DateTimeOffset start, DateTimeOffset? lastBatchAt, DateTimeOffset? lastAcceptedAt,
        double? accuracyM, double? clockAheadSeconds, double? batteryPct, DateTimeOffset? lastRejectAt, LinkProblem? rejectKind,
        DateTimeOffset? lastEarlyAt = null)
    {
        List<LinkProblem> problems = [];
        if (lastBatchAt is not { } batch)
            return new DeviceLink(now < start ? LinkLevel.Waiting : LinkLevel.Silent, null, null, null, null, null, problems);

        if (now - batch > SilentAfter)
            return new DeviceLink(LinkLevel.Silent, batch, lastAcceptedAt, accuracyM, clockAheadSeconds, batteryPct, problems);

        if (clockAheadSeconds > ClockWarnSeconds) problems.Add(LinkProblem.Clock);
        if (lastRejectAt is { } rejected && now - rejected <= RejectionCurrent
            && (lastAcceptedAt is null || lastAcceptedAt < rejected))
            problems.Add(rejectKind is LinkProblem.Accuracy or LinkProblem.Clock ? rejectKind.Value : LinkProblem.Rejected);
        if (batteryPct < 20) problems.Add(LinkProblem.LowBattery);

        var stored = lastAcceptedAt is { } a && now - a <= SilentAfter;
        var early = !stored && lastEarlyAt is { } e && now - e <= SilentAfter;
        if (!stored && !early && problems.Count == 0) problems.Add(LinkProblem.Rejected);
        var level = problems.Count > 0 ? LinkLevel.Warning : LinkLevel.Good;
        return new DeviceLink(level, batch, lastAcceptedAt, accuracyM, clockAheadSeconds, batteryPct, problems.Distinct().ToList(), early);
    }
}
