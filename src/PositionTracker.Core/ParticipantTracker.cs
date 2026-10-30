namespace PositionTracker.Core;

public sealed record TrackerOptions(
    TimeSpan DelayedAfter, TimeSpan OfflineAfter, double StoppedSpeedMps, TimeSpan StoppedAfter,
    double OffCourseM, TimeSpan OffCourseDebounce, TimeSpan SpeedWindow, TimeSpan MovingSpeedWindow, FilterOptions Filter)
{
    public static TrackerOptions Defaults(Sport sport) => new(
        DelayedAfter: TimeSpan.FromSeconds(15),
        OfflineAfter: TimeSpan.FromMinutes(2),
        StoppedSpeedMps: 1 / 3.6,
        StoppedAfter: TimeSpan.FromMinutes(3),
        OffCourseM: 100,
        OffCourseDebounce: TimeSpan.FromSeconds(15),
        SpeedWindow: TimeSpan.FromSeconds(10),
        MovingSpeedWindow: TimeSpan.FromSeconds(60),
        Filter: FilterOptions.For(sport));
}

public enum ParticipantState { NotStarted, Live, Delayed, Offline, Stopped, OffCourse, Sos, Finished, Dnf }

public sealed record SegmentCompletion(int Lap, int SegmentIndex, TimeSpan Time);

public sealed record ParticipantSnapshot(
    DateTimeOffset? LastFixTime, double? LastLat, double? LastLon, double? LastAccuracyM,
    double TotalDistanceM, int Lap, double LapProgressM, double OffCourseM,
    double SpeedMps, double MovingSpeedMps, double AvgSpeedMps, double MaxSpeedMps, double? PaceSecPerKm,
    TimeSpan MovingTime, TimeSpan StoppedTime, double AscentM, double DescentM, double GradePercent,
    ParticipantState State, DateTimeOffset? FinishTime);

/// <param name="Accepted">Whether the fix was new and passed the filter when it arrived (see <see cref="ParticipantTracker.Apply"/>).</param>
public sealed record ApplyResult(ParticipantSnapshot Snapshot, IReadOnlyList<SegmentCompletion> Completed, bool Accepted);

/// <summary>
/// Turns one participant's fixes into course progress and motion statistics.
/// </summary>
/// <remarks>
/// <para>Every fix that passes input validation and the accuracy check is stored in device-time order.
/// The state is a fold over the stored fixes in that order: speed filter, course matching, lap counting,
/// total distance, history and motion accounting. A fix that sorts before the newest stored one triggers
/// a replay of all stored fixes into fresh state, so the result depends only on the set of fixes received,
/// never on their arrival order. Fixes rejected by the speed or duplicate check stay stored because that
/// decision depends on the fixes before them. Fixes before the start time only update the last-fix fields.</para>
/// <para>Matching keeps a lap counter and the matched lap progress (the position on the course). The
/// lap counter steps up or down whenever the position crosses the start/finish seam in either direction,
/// so jitter across the line cancels out. Total distance is the high-water mark of that position,
/// clamped to the course length, and never decreases. Only matched fixes enter the history and the
/// moving/stopped accounting, so an off-course stretch neither counts as stopped nor causes a speed spike.</para>
/// <para>SOS, pause and DNF are state inputs, not fixes. They are kept outside the fold state and survive every
/// replay. Pauses are stored as time intervals and excluded from moving and stopped time, so changing them
/// replays the fixes. The snapshot state is a pure function of the stored fixes, these inputs and <c>now</c>.</para>
/// </remarks>
public sealed class ParticipantTracker
{
    /// <summary>A matched position further than this behind the total is treated as a wrong match.</summary>
    private const double MaxRegressionM = 50;
    /// <summary>
    /// A closed-course move within this distance of a full lap forward is read as backtracking, not as a lap:
    /// GPS drift behind the anchor and returns from off-course stretches land there once the window spans
    /// the whole lap, whereas a real lap between two accepted fixes is implausible.
    /// </summary>
    private const double MaxBacktrackM = 500;
    /// <summary>Absorbs floating-point error at the edges of the matcher window.</summary>
    private const double WindowToleranceM = 1;
    private static readonly MatcherOptions DefaultMatcher = new();

    private readonly Course _course;
    private readonly DateTimeOffset _startTime;
    private readonly TrackerOptions _options;
    private readonly List<PositionFix> _fixes = new(); // sorted by FixOrder

    // State inputs from the setters, not fixes: they survive Reset() and every replay.
    private readonly List<(DateTimeOffset At, int Call, bool Paused)> _pauseSwitches = new(); // sorted by time, then call order
    private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _pauses = new(); // disjoint, sorted; open end = MaxValue
    private bool _sos;
    private DateTimeOffset? _dnfAt;

    // Segment times last reported to callers; kept across replays so only new or changed ones are reported again.
    private readonly Dictionary<(int Lap, int SegmentIndex), TimeSpan> _emitted = new();

    // Fold state; Reset() restores it before a replay.
    private PositionFix? _lastFix;       // newest fix shown to callers, including warm-up before the start
    private PositionFix? _lastAccepted;  // newest fix at or after the start that passed the filter
    private bool _lastAcceptedMatched;
    private int _lapCounter;       // 1-based; 0 just behind the start line, above Laps past the finish
    private double _lapProgressM;  // last accepted match, the window anchor for the next fix
    private double _totalM;
    private double _offCourseM;
    private DateTimeOffset _lastMatchedTime;
    private TimeSpan _movingTime;
    private TimeSpan _stoppedTime;
    private double _maxSpeedMps;
    private DateTimeOffset? _finishTime;
    private DateTimeOffset? _offCourseSince; // first fix of the current run of accepted fixes the matcher placed off course

    public ParticipantTracker(Course course, Sport sport, DateTimeOffset startTime, TrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(course);
        _course = course;
        _startTime = startTime;
        _options = options ?? TrackerOptions.Defaults(sport);
        Reset();
    }

    public ProgressHistory History { get; } = new();

    /// <summary>
    /// Adds a fix in any order and returns the snapshot at the newest accepted fix. Invalid fixes and exact
    /// resends of a stored fix leave the state unchanged. <c>Completed</c> holds the segment completions that
    /// are new or whose time changed with this fix (a late fix can move an interpolated crossing), each
    /// <c>(Lap, SegmentIndex)</c> at most once, in course order; consumers record them idempotently.
    /// <c>Accepted</c> is true when the fix was newly stored and passed the filter in the fold right after it
    /// was added; malformed fixes, accuracy failures, exact resends and speed or duplicate rejections are false.
    /// A later fix can change the filter verdict of an earlier one on replay; that is not reported again.
    /// </summary>
    public ApplyResult Apply(PositionFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        var accepted = false;
        if (IsStorable(fix))
        {
            var index = _fixes.BinarySearch(fix, FixOrder.Instance);
            if (index < 0)
            {
                index = ~index;
                _fixes.Insert(index, fix);
                accepted = index == _fixes.Count - 1 ? Step(fix) : Replay(fix);
            }
        }
        return new ApplyResult(GetSnapshot(_lastAccepted?.Time ?? _startTime), ChangedCompletions(), accepted);
    }

    /// <summary>
    /// Derives the completed segments from the history and returns those not yet reported with the same time.
    /// Boundaries are taken on the total distance, not the lap counter (which can disagree with the total near
    /// the start/finish seam): boundary i of lap n lies at <c>(n-1)·LapLength + SegmentBoundaries[i+1]</c>.
    /// A segment's time runs from the previous boundary's interpolated crossing (the start time for the first
    /// segment) to its own; a segment whose start is not covered by the history (the first fix lies beyond it)
    /// has no time.
    /// </summary>
    private IReadOnlyList<SegmentCompletion> ChangedCompletions()
    {
        List<SegmentCompletion>? changed = null;
        var segments = _course.SegmentCount;
        DateTimeOffset? previous = _startTime;
        for (var k = 0; k < _course.Laps * segments; k++)
        {
            int lap = k / segments + 1, index = k % segments;
            var boundary = Math.Min(_course.TotalLengthM, (lap - 1) * _course.LapLengthM + _course.SegmentBoundariesM[index + 1]);
            if (boundary > _totalM) break;

            var crossing = History.TimeAt(boundary);
            if (crossing is { } end && previous is { } start)
            {
                var time = end - start;
                if (!_emitted.TryGetValue((lap, index), out var reported) || reported != time)
                {
                    _emitted[(lap, index)] = time;
                    (changed ??= new List<SegmentCompletion>()).Add(new SegmentCompletion(lap, index, time));
                }
            }
            previous = crossing;
        }
        return changed ?? (IReadOnlyList<SegmentCompletion>)Array.Empty<SegmentCompletion>();
    }

    /// <summary>Raises or clears the SOS alarm. It is shown from the moment it is set, even before any fix.</summary>
    public void SetSos(bool on) => _sos = on;

    /// <summary>
    /// Records a pause switch at <paramref name="at"/>. Switches may arrive in any order; they are kept sorted by
    /// time, and switches with equal times by call order, so the later call wins (pause and unpause in the same
    /// second cancel out). A pause runs from the first pause switch to the next unpause switch. Changing the pauses
    /// replays the fixes, because paused time is neither moving nor stopped time.
    /// </summary>
    public void SetPaused(DateTimeOffset at, bool paused)
    {
        var index = _pauseSwitches.BinarySearch((at, _pauseSwitches.Count, paused));
        _pauseSwitches.Insert(~index, (at, _pauseSwitches.Count, paused));

        _pauses.Clear();
        DateTimeOffset? start = null;
        foreach (var (time, _, on) in _pauseSwitches)
        {
            if (on) start ??= time;
            else if (start is { } s)
            {
                if (time > s) _pauses.Add((s, time));
                start = null;
            }
        }
        if (start is { } open) _pauses.Add((open, DateTimeOffset.MaxValue));
        Replay();
    }

    /// <summary>Marks the participant as DNF from <paramref name="at"/> on; the earliest mark wins.</summary>
    public void MarkDnf(DateTimeOffset at) => _dnfAt = _dnfAt < at ? _dnfAt : at;

    /// <summary>Snapshot of the current state. Speeds are evaluated at the newest accepted fix.</summary>
    public ParticipantSnapshot GetSnapshot(DateTimeOffset now)
    {
        var last = _lastAccepted;
        var speed = last is null ? 0 : SpeedAt(last.Time, _options.SpeedWindow);
        var movingSpeed = last is null ? 0 : SpeedAt(last.Time, _options.MovingSpeedWindow);
        double? pace = movingSpeed > 0 && movingSpeed >= _options.StoppedSpeedMps ? 1000 / movingSpeed : null;
        var avgSpeed = _movingTime > TimeSpan.Zero ? _totalM / _movingTime.TotalSeconds : 0;

        // Report the position on the course, clamped to the race: lap 1 behind the line, the last lap past the finish.
        var lap = Math.Clamp(_lapCounter, 1, _course.Laps);
        var lapProgress = _lapCounter < 1 ? 0 : _lapCounter > _course.Laps ? _course.LapLengthM : _lapProgressM;

        var shown = _lastFix;
        return new ParticipantSnapshot(
            shown?.Time, shown?.Lat, shown?.Lon, shown?.AccuracyM,
            _totalM, lap, lapProgress, _offCourseM,
            speed, movingSpeed, avgSpeed, _maxSpeedMps, pace,
            _movingTime, _stoppedTime,
            _course.AscentUpTo(_totalM), _course.DescentUpTo(_totalM), _course.GradePercentAt(lapProgress),
            StateAt(now),
            _finishTime);
    }

    /// <summary>
    /// The state by display priority Dnf > Finished > Sos > Offline > Delayed > OffCourse > Stopped > Live.
    /// Without an accepted race fix the participant is NotStarted, unless DNF or SOS apply. Silence is measured
    /// from the newest accepted fix to <paramref name="now"/> and is Delayed/Offline only when it is strictly
    /// longer than the threshold ("within 15 s" is Live, "over 2 min" is Offline). Off course and stopped are
    /// judged at the newest accepted fix, since either silence state outranks them.
    /// </summary>
    private ParticipantState StateAt(DateTimeOffset now)
    {
        if (_dnfAt <= now) return ParticipantState.Dnf;
        if (_finishTime is not null) return ParticipantState.Finished;
        if (_sos) return ParticipantState.Sos;
        if (_lastAccepted is not { } last) return ParticipantState.NotStarted;

        var silence = now - last.Time;
        if (silence > _options.OfflineAfter) return ParticipantState.Offline;
        if (silence > _options.DelayedAfter) return ParticipantState.Delayed;
        if (last.Time - _offCourseSince > _options.OffCourseDebounce) return ParticipantState.OffCourse;
        if (!IsPausedAt(now) && IsStopped(last.Time)) return ParticipantState.Stopped;
        return ParticipantState.Live;
    }

    /// <summary>
    /// Stopped: over the last <c>StoppedAfter</c> of unpaused time before <paramref name="until"/> the total
    /// grew by less than <c>StoppedSpeedMps × StoppedAfter</c> (an average under the speed threshold). The
    /// window starts at the last history point at or before its start, so it needs a matched fix that old.
    /// Comparing totals, not per-interval speeds, keeps GPS jitter (the total is a high-water mark) and
    /// unmatched or rejected fixes in between (they leave the total unchanged) from resetting the stop.
    /// </summary>
    private bool IsStopped(DateTimeOffset until) =>
        History.DistanceAtOrBefore(UnpausedWindowStart(until, _options.StoppedAfter)) is { } from
        && _totalM - from < _options.StoppedSpeedMps * _options.StoppedAfter.TotalSeconds;

    /// <summary>The time from which the span up to <paramref name="end"/> holds <paramref name="length"/> of unpaused time.</summary>
    private DateTimeOffset UnpausedWindowStart(DateTimeOffset end, TimeSpan length)
    {
        var cursor = end;
        for (var i = _pauses.Count - 1; i >= 0; i--)
        {
            var (start, stop) = _pauses[i];
            if (start >= cursor) continue;
            if (stop < cursor)
            {
                if (cursor - stop >= length) break;
                length -= cursor - stop;
            }
            cursor = start;
        }
        return cursor - length;
    }

    private TimeSpan PausedWithin(DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        foreach (var (start, stop) in _pauses)
        {
            var a = start > from ? start : from;
            var b = stop < to ? stop : to;
            if (b > a) total += b - a;
        }
        return total;
    }

    private bool IsPausedAt(DateTimeOffset time) => _pauses.Exists(p => p.Start <= time && time < p.End);

    /// <summary>Rejects malformed input before the filter (which accepts any first fix) and fixes failing the accuracy limit.</summary>
    private bool IsStorable(PositionFix fix) =>
        double.IsFinite(fix.Lat) && double.IsFinite(fix.Lon) && Math.Abs(fix.Lat) <= 90 && Math.Abs(fix.Lon) <= 180
        && (fix.AccuracyM is null || double.IsFinite(fix.AccuracyM.Value))
        && PositionFilter.Evaluate(fix, null, _options.Filter) == FilterDecision.Accept;

    /// <summary>Refolds all stored fixes; returns whether <paramref name="watched"/> passed the filter.</summary>
    private bool Replay(PositionFix? watched = null)
    {
        Reset();
        var accepted = false;
        foreach (var fix in _fixes)
            if (Step(fix) && ReferenceEquals(fix, watched)) accepted = true;
        return accepted;
    }

    private void Reset()
    {
        _lastFix = null;
        _lastAccepted = null;
        _lastAcceptedMatched = false;
        _lapCounter = 1;
        _lapProgressM = 0;
        _totalM = 0;
        _offCourseM = 0;
        _lastMatchedTime = _startTime;
        _movingTime = TimeSpan.Zero;
        _stoppedTime = TimeSpan.Zero;
        _maxSpeedMps = 0;
        _finishTime = null;
        _offCourseSince = null;
        History.Clear();
    }

    /// <summary>Folds the next fix in time order into the state; returns false when the filter rejects it.</summary>
    private bool Step(PositionFix fix)
    {
        // Warm-up before the start: shown as the last fix, but never filtered against, matched or counted,
        // so the first fix after the start is matched from progress 0 as if the warm-up never happened.
        if (fix.Time < _startTime)
        {
            _lastFix = fix;
            return true;
        }
        if (PositionFilter.Evaluate(fix, _lastAccepted, _options.Filter) != FilterDecision.Accept) return false;
        var previousMatched = _lastAcceptedMatched;
        _lastFix = _lastAccepted = fix;
        var previous = History.Last;

        // The off-course run follows the matcher's verdict (over OffCourseM, or no candidate), not the
        // plausibility check: GPS drift behind on the course is unmatched for progress but still on course.
        _lastAcceptedMatched = Locate(fix, out var offCourse);
        if (offCourse) _offCourseSince ??= fix.Time;
        else _offCourseSince = null;
        if (!_lastAcceptedMatched) return true;

        History.Add(fix.Time, _totalM);
        if (_finishTime is null && _totalM >= _course.TotalLengthM) _finishTime = fix.Time;

        // Only between consecutive matched fixes; the filter rejects equal timestamps, so dt > 0.
        // The interval is classified by its whole progress speed; only its unpaused part is counted.
        if (previousMatched && previous is { } p && fix.Time > p.Time)
        {
            var dt = fix.Time - p.Time;
            var counted = dt - PausedWithin(p.Time, fix.Time);
            if ((_totalM - p.DistanceM) / dt.TotalSeconds >= _options.StoppedSpeedMps) _movingTime += counted;
            else _stoppedTime += counted;
        }
        _maxSpeedMps = Math.Max(_maxSpeedMps, SpeedAt(fix.Time, _options.SpeedWindow));
        return true;
    }

    /// <summary>Windowed progress speed, capped at the sport's plausible maximum.</summary>
    private double SpeedAt(DateTimeOffset time, TimeSpan window) =>
        Math.Min(History.SpeedMps(time, window), _options.Filter.MaxSpeedMps);

    /// <summary>
    /// Matches the fix near the last position and advances laps and total; returns whether the match was
    /// accepted. Re-acquisition: the forward window grows with the time since the last accepted match x the
    /// sport's max speed (at least the matcher default, at most one lap), so a rider is found again after a
    /// dropout. There is no separate full-lap retry: with the backtracking rule it could only find moves the
    /// widened window already covers. A match is accepted only if it is reachable in the elapsed time and
    /// not more than <see cref="MaxRegressionM"/> behind the total. <paramref name="offCourse"/> is the matcher's
    /// verdict alone: no pass of the course within <c>OffCourseM</c> inside the window.
    /// </summary>
    private bool Locate(PositionFix fix, out bool offCourse)
    {
        var elapsedS = Math.Max(0, (fix.Time - _lastMatchedTime).TotalSeconds);
        var reachM = Math.Min(_course.LapLengthM, Math.Max(DefaultMatcher.ForwardWindowM, elapsedS * _options.Filter.MaxSpeedMps));
        var window = DefaultMatcher with { ForwardWindowM = reachM, OffCourseThresholdM = _options.OffCourseM };

        var result = CourseMatcher.Match(_course, fix.Lat, fix.Lon, _lapProgressM, window);
        if (double.IsFinite(result.OffCourseM)) _offCourseM = result.OffCourseM;
        offCourse = !result.Matched;
        if (!TryAdvance(result, reachM)) return false;
        _lastMatchedTime = fix.Time;
        return true;
    }

    /// <summary>Applies a match if plausible; returns false (state unchanged) otherwise.</summary>
    private bool TryAdvance(MatchResult match, double reachM)
    {
        if (!match.Matched) return false;

        var move = match.LapProgressM - _lapProgressM;
        if (_course.IsClosed)
        {
            // Normalise into [-backtrack, L - backtrack): a move close to a full lap forward is backtracking.
            var backtrack = Math.Min(MaxBacktrackM, _course.LapLengthM / 2);
            move = Mod(move + backtrack, _course.LapLengthM) - backtrack;
        }
        var position = Position + move;
        if (move > reachM + WindowToleranceM || position < _totalM - MaxRegressionM) return false;

        if (_course.IsClosed)
            _lapCounter += (int)Math.Round((_lapProgressM + move - match.LapProgressM) / _course.LapLengthM);
        _lapProgressM = match.LapProgressM;
        _totalM = Math.Clamp(Math.Max(_totalM, Position), 0, _course.TotalLengthM);
        return true;
    }

    /// <summary>Signed distance along the race: negative just behind the start line.</summary>
    private double Position => (_lapCounter - 1) * _course.LapLengthM + _lapProgressM;

    private static double Mod(double x, double m)
    {
        var r = x % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>Device time, then coordinates and accuracy, so fixes sharing a timestamp still sort deterministically.</summary>
    private sealed class FixOrder : IComparer<PositionFix>
    {
        public static readonly FixOrder Instance = new();

        public int Compare(PositionFix? x, PositionFix? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var c = x.Time.CompareTo(y.Time);
            if (c == 0) c = x.Lat.CompareTo(y.Lat);
            if (c == 0) c = x.Lon.CompareTo(y.Lon);
            return c != 0 ? c : Nullable.Compare(x.AccuracyM, y.AccuracyM);
        }
    }
}
