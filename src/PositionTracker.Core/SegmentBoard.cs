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

    /// <summary>
    /// The colours of the board as it is now, computed in one pass over all results; reading a colour from it costs a
    /// lookup, where <see cref="ColourOf"/> walks every result of the segment. The returned object does not follow later
    /// changes and is safe to read from any thread.
    /// </summary>
    public SegmentColours Snapshot()
    {
        var best = new Dictionary<int, (TimeSpan Best, bool Shared)>(_segments.Count);
        var ownBest = new Dictionary<(string ParticipantId, int SegmentIndex), TimeSpan>(); // string equality is ordinal
        foreach (var (segment, results) in _segments)
        {
            var segmentBest = TimeSpan.MaxValue;
            string? first = null;
            var shared = false;
            foreach (var ((id, _), t) in results)
            {
                if (t < segmentBest) segmentBest = t;
                if (first is null) first = id;
                else if (!shared && id != first) shared = true;
                var key = (id, segment);
                if (!ownBest.TryGetValue(key, out var own) || t < own) ownBest[key] = t;
            }
            best[segment] = (segmentBest, shared);
        }
        return new SegmentColours(best, ownBest);
    }
}

/// <summary>The colours of a <see cref="SegmentBoard"/> at one moment (<see cref="SegmentBoard.Snapshot"/>).</summary>
public sealed class SegmentColours
{
    private readonly Dictionary<int, (TimeSpan Best, bool Shared)> _best;
    private readonly Dictionary<(string ParticipantId, int SegmentIndex), TimeSpan> _ownBest;

    internal SegmentColours(Dictionary<int, (TimeSpan Best, bool Shared)> best,
        Dictionary<(string ParticipantId, int SegmentIndex), TimeSpan> ownBest)
    {
        _best = best;
        _ownBest = ownBest;
    }

    /// <summary>
    /// The colour of a result of <paramref name="time"/> recorded for the participant on the segment, by the same rules as
    /// <see cref="SegmentBoard.ColourOf"/>; throws <see cref="KeyNotFoundException"/> when the participant has no result there.
    /// </summary>
    public SegmentColour ColourOf(string participantId, int segmentIndex, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        if (!_best.TryGetValue(segmentIndex, out var segment) || !_ownBest.TryGetValue((participantId, segmentIndex), out var own))
            throw new KeyNotFoundException($"No result for '{participantId}' segment {segmentIndex}.");
        if (segment.Shared && time == segment.Best) return SegmentColour.Purple;
        return time == own ? SegmentColour.Green : SegmentColour.Yellow;
    }
}
