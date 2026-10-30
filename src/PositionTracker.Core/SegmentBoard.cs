namespace PositionTracker.Core;

public enum SegmentColour { Yellow, Green, Purple }

/// <summary>
/// Segment times of all participants, coloured like timing sectors. A segment is a position in the lap,
/// so the times of all laps are compared. Not thread-safe.
/// </summary>
/// <remarks>
/// Purple: the fastest time of the segment, once at least two distinct participants have a result for it.
/// Green: the participant's own best for the segment (including its only result). Yellow: anything else.
/// Colours are evaluated against the board at call time.
/// </remarks>
public sealed class SegmentBoard
{
    // segment index -> (participant, lap) -> time
    private readonly Dictionary<int, Dictionary<(string ParticipantId, int Lap), TimeSpan>> _segments = new();

    /// <summary>Records a result and returns its colour. Recording the same key again replaces the time.</summary>
    public SegmentColour Record(string participantId, int lap, int segmentIndex, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        if (!_segments.TryGetValue(segmentIndex, out var results))
            _segments[segmentIndex] = results = new Dictionary<(string, int), TimeSpan>();
        results[(participantId, lap)] = time;
        return ColourOf(participantId, lap, segmentIndex);
    }

    /// <summary>The current colour of a recorded result; throws <see cref="KeyNotFoundException"/> if there is none.</summary>
    public SegmentColour ColourOf(string participantId, int lap, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        if (!_segments.TryGetValue(segmentIndex, out var results) || !results.TryGetValue((participantId, lap), out var time))
            throw new KeyNotFoundException($"No result for '{participantId}' lap {lap} segment {segmentIndex}.");

        var best = TimeSpan.MaxValue;
        var ownBest = TimeSpan.MaxValue;
        string? other = null;
        foreach (var ((id, _), t) in results)
        {
            if (t < best) best = t;
            if (id == participantId) { if (t < ownBest) ownBest = t; }
            else other ??= id;
        }

        if (other is not null && time == best) return SegmentColour.Purple;
        return time == ownBest ? SegmentColour.Green : SegmentColour.Yellow;
    }
}
