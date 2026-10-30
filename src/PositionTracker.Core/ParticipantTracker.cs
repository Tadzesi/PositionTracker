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

public sealed record ApplyResult(ParticipantSnapshot Snapshot, IReadOnlyList<SegmentCompletion> Completed);

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
    /// resends of a stored fix leave the state unchanged. <c>Completed</c> is empty until segments are tracked.
    /// </summary>
    public ApplyResult Apply(PositionFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        if (IsStorable(fix))
        {
            var index = _fixes.BinarySearch(fix, FixOrder.Instance);
            if (index < 0)
            {
                index = ~index;
                _fixes.Insert(index, fix);
                if (index == _fixes.Count - 1) Step(fix);
                else Replay();
            }
        }
        return new ApplyResult(GetSnapshot(_lastAccepted?.Time ?? _startTime), Array.Empty<SegmentCompletion>());
    }

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
            last is null ? ParticipantState.NotStarted : ParticipantState.Live,
            _finishTime);
    }

    /// <summary>Rejects malformed input before the filter (which accepts any first fix) and fixes failing the accuracy limit.</summary>
    private bool IsStorable(PositionFix fix) =>
        double.IsFinite(fix.Lat) && double.IsFinite(fix.Lon) && Math.Abs(fix.Lat) <= 90 && Math.Abs(fix.Lon) <= 180
        && (fix.AccuracyM is null || double.IsFinite(fix.AccuracyM.Value))
        && PositionFilter.Evaluate(fix, null, _options.Filter) == FilterDecision.Accept;

    private void Replay()
    {
        Reset();
        foreach (var fix in _fixes) Step(fix);
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
        History.Clear();
    }

    /// <summary>Folds the next fix in time order into the state.</summary>
    private void Step(PositionFix fix)
    {
        // Warm-up before the start: shown as the last fix, but never filtered against, matched or counted,
        // so the first fix after the start is matched from progress 0 as if the warm-up never happened.
        if (fix.Time < _startTime)
        {
            _lastFix = fix;
            return;
        }
        if (PositionFilter.Evaluate(fix, _lastAccepted, _options.Filter) != FilterDecision.Accept) return;
        var previousMatched = _lastAcceptedMatched;
        _lastFix = _lastAccepted = fix;
        var previous = History.Last;

        _lastAcceptedMatched = Locate(fix);
        if (!_lastAcceptedMatched) return;

        History.Add(fix.Time, _totalM);
        if (_finishTime is null && _totalM >= _course.TotalLengthM) _finishTime = fix.Time;

        // Only between consecutive matched fixes; the filter rejects equal timestamps, so dt > 0.
        if (previousMatched && previous is { } p && fix.Time > p.Time)
        {
            var dt = fix.Time - p.Time;
            if ((_totalM - p.DistanceM) / dt.TotalSeconds >= _options.StoppedSpeedMps) _movingTime += dt;
            else _stoppedTime += dt;
        }
        _maxSpeedMps = Math.Max(_maxSpeedMps, SpeedAt(fix.Time, _options.SpeedWindow));
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
    /// not more than <see cref="MaxRegressionM"/> behind the total.
    /// </summary>
    private bool Locate(PositionFix fix)
    {
        var elapsedS = Math.Max(0, (fix.Time - _lastMatchedTime).TotalSeconds);
        var reachM = Math.Min(_course.LapLengthM, Math.Max(DefaultMatcher.ForwardWindowM, elapsedS * _options.Filter.MaxSpeedMps));
        var window = DefaultMatcher with { ForwardWindowM = reachM, OffCourseThresholdM = _options.OffCourseM };

        var result = CourseMatcher.Match(_course, fix.Lat, fix.Lon, _lapProgressM, window);
        if (double.IsFinite(result.OffCourseM)) _offCourseM = result.OffCourseM;
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
