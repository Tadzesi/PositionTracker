namespace PositionTracker.Core;

public sealed record MatcherOptions(double ForwardWindowM = 1500, double BackwardWindowM = 50, double OffCourseThresholdM = 100);

public sealed record MatchResult(double LapProgressM, double OffCourseM, bool Matched);

/// <summary>
/// Map-matches a fix onto a course, searching only the lap-distance window
/// <c>[last - BackwardWindowM, last + ForwardWindowM]</c> (wrapping across the lap end on closed courses).
/// The window keeps the match on the current branch at self-crossings and shared roads.
/// </summary>
public static class CourseMatcher
{
    private const double TieToleranceM = 5;
    private const double BackwardPenalty = 3;
    private const double MetresPerDegLat = Math.PI / 180 * 6_371_008.8;

    /// <summary>Closest point of one segment; AtStart/AtEnd mark a projection clamped to the scanned range's ends.</summary>
    private readonly record struct Candidate(double UnwrappedM, double DistanceM, bool AtStart, bool AtEnd);

    /// <summary>
    /// Matches the fix (<paramref name="lat"/>, <paramref name="lon"/>) to a lap distance near
    /// <paramref name="lastLapProgressM"/>. Closed courses return progress in [0, LapLengthM).
    /// </summary>
    /// <remarks>
    /// <para>The matcher is stateless and only searches the window. After a dropout longer than about
    /// <c>ForwardWindowM + BackwardWindowM</c> of travel the true position lies outside it, and the result is
    /// <c>Matched=false</c> with progress unchanged. Callers own re-acquisition. They must widen
    /// <c>ForwardWindowM</c> by elapsed time × max speed since the last matched fix. After repeated unmatched
    /// fixes they must retry with <c>ForwardWindowM = LapLengthM</c> (full-lap search).</para>
    /// <para>When two passes of the course are equally close (self-crossing, shared road), the one needing the
    /// smallest move from the last progress wins; backward moves cost <c>BackwardPenalty</c> times more.
    /// At a hairpin (out-and-back turn) one fix is genuinely ambiguous. The first fix after the turn can match
    /// the outbound image, which is a one-fix stall that the next fix corrects.</para>
    /// </remarks>
    public static MatchResult Match(Course course, double lat, double lon, double lastLapProgressM, MatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(course);
        options ??= new MatcherOptions();
        var lapLength = course.LapLengthM;
        var last = course.IsClosed ? Wrap(lastLapProgressM, lapLength) : Math.Clamp(lastLapProgressM, 0, lapLength);

        // Window in unwrapped lap space; on a closed course it may extend below 0 or past the lap end.
        var from = last - options.BackwardWindowM;
        var to = last + options.ForwardWindowM;
        if (course.IsClosed)
        {
            if (to - from > lapLength) to = from + lapLength; // never scan any stretch twice
        }
        else
        {
            from = Math.Max(from, 0);
            to = Math.Min(to, lapLength);
        }

        var candidates = new List<Candidate>();
        var lapIndex = course.IsClosed && lapLength > 0 ? Math.Floor(from / lapLength) : 0;
        var lastLapIndex = course.IsClosed && lapLength > 0 ? Math.Floor(to / lapLength) : 0;
        for (var k = lapIndex; k <= lastLapIndex; k++)
        {
            var offset = k * lapLength;
            var a = Math.Max(from - offset, 0);
            var b = Math.Min(to - offset, lapLength);
            if (b >= a) Scan(course, lat, lon, a, b, offset, candidates);
        }
        if (candidates.Count == 0) return new MatchResult(lastLapProgressM, double.PositiveInfinity, false);

        var bestDistance = candidates.Min(c => c.DistanceM);
        if (bestDistance > options.OffCourseThresholdM)
            return new MatchResult(lastLapProgressM, bestDistance, false);

        // Among distinct passes of the course (local minima of distance along the course) that are as
        // close as the best, pick the smallest move from the last progress, backward moves weighted
        // by BackwardPenalty: near-zero forward motion wins at hairpins, but small backward jitter
        // beats a far-ahead image of the same spot on another pass.
        Candidate? chosen = null;
        for (var j = 0; j < candidates.Count; j++)
        {
            var c = candidates[j];
            if (c.DistanceM > bestDistance + TieToleranceM) continue;
            // A minimum clamped to a segment end continues into the neighbour when that one is closer:
            // it is the same pass, represented by the neighbour's candidate.
            if (c.AtStart && j > 0 && candidates[j - 1].DistanceM < c.DistanceM) continue;
            if (c.AtEnd && j < candidates.Count - 1 && candidates[j + 1].DistanceM < c.DistanceM) continue;
            if (chosen is null || MoveCost(c.UnwrappedM - last) < MoveCost(chosen.Value.UnwrappedM - last))
                chosen = c;
        }

        var match = chosen!.Value;
        var progress = course.IsClosed ? Wrap(match.UnwrappedM, lapLength) : match.UnwrappedM;
        return new MatchResult(progress, match.DistanceM, true);
    }

    private static double MoveCost(double deltaM) => deltaM >= 0 ? deltaM : -deltaM * BackwardPenalty;

    /// <summary>Projects the fix onto every segment overlapping lap distances [a, b], clamped to that range.</summary>
    private static void Scan(Course course, double lat, double lon, double a, double b, double offset, List<Candidate> candidates)
    {
        var pts = course.Points;
        var cum = course.CumulativeM;
        var cosLat = Math.Cos(lat * Math.PI / 180);

        for (var i = SegmentContaining(cum, a); i < pts.Count - 1 && cum[i] <= b; i++)
        {
            var span = cum[i + 1] - cum[i];
            double tLo = 0, tHi = 1;
            if (span > 0)
            {
                tLo = Math.Clamp((a - cum[i]) / span, 0, 1);
                tHi = Math.Clamp((b - cum[i]) / span, 0, 1);
            }

            // Local planar metres with the fix at the origin.
            var ax = (pts[i].Lon - lon) * cosLat * MetresPerDegLat;
            var ay = (pts[i].Lat - lat) * MetresPerDegLat;
            var bx = (pts[i + 1].Lon - lon) * cosLat * MetresPerDegLat;
            var by = (pts[i + 1].Lat - lat) * MetresPerDegLat;
            double dx = bx - ax, dy = by - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? -(ax * dx + ay * dy) / len2 : 0;
            t = Math.Clamp(t, tLo, tHi);

            var px = ax + t * dx;
            var py = ay + t * dy;
            candidates.Add(new Candidate(offset + cum[i] + t * span, Math.Sqrt(px * px + py * py), t <= tLo, t >= tHi));
        }
    }

    /// <summary>Index of the last segment start at or before <paramref name="d"/> (binary search).</summary>
    private static int SegmentContaining(IReadOnlyList<double> cum, double d)
    {
        int lo = 0, hi = cum.Count - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (cum[mid] <= d) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static double Wrap(double d, double lapLength)
    {
        if (lapLength <= 0) return 0;
        var r = d % lapLength;
        return r < 0 ? r + lapLength : r;
    }
}
