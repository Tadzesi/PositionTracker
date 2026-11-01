using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Participants;

/// <summary>
/// The state of a participant's data link for the traffic light (participant page and the organizer's list): ages are in
/// seconds on the server clock, so the browser's own clock does not matter. Null where the event has no runtime
/// (draft, finished and rebuilt later) or the participant is not in it.
/// </summary>
public sealed record LinkHealth(
    LinkLevel Level, double? LastBatchAgoSeconds, double? LastPositionAgoSeconds, double? AccuracyM, double? ClockAheadSeconds,
    double? BatteryPct, IReadOnlyList<LinkProblem> Problems, bool EarlyOnly)
{
    public static LinkHealth? Of(EventRuntime? runtime, string participantId)
    {
        if (runtime is null || !runtime.HasParticipant(participantId)) return null;
        var link = runtime.Link(participantId);
        var now = runtime.Clock.GetUtcNow();
        return new LinkHealth(link.Level, Ago(now, link.LastBatchAt), Ago(now, link.LastAcceptedAt), link.AccuracyM,
            link.ClockAheadSeconds is { } c ? Math.Round(c) : null, link.BatteryPct, link.Problems, link.EarlyOnly);
    }

    private static double? Ago(DateTimeOffset now, DateTimeOffset? at) => at is { } t ? Math.Max(0, Math.Round((now - t).TotalSeconds)) : null;
}
