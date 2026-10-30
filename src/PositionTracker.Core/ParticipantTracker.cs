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

/// <param name="StoppedSince">
/// Set while the stopped condition holds (even when a higher-priority state is shown): the first matched fix
/// within a 15 m tolerance of the current total (GPS drift of a standing receiver stays inside it), no later than
/// the start of the stopped window; moved forward to the end of a pause that ended later, since a pause
/// suppresses Stopped.
/// </param>
/// <param name="OffCourseSince">
/// Set while the debounced off-course condition holds (even when a higher-priority state is shown): the first
/// fix of the current off-course run, not the moment the debounce elapsed.
/// </param>
public sealed record ParticipantSnapshot(
    DateTimeOffset? LastFixTime, double? LastLat, double? LastLon, double? LastAccuracyM,
    double TotalDistanceM, int Lap, double LapProgressM, double OffCourseM,
    double SpeedMps, double MovingSpeedMps, double AvgSpeedMps, double MaxSpeedMps, double? PaceSecPerKm,
    TimeSpan MovingTime, TimeSpan StoppedTime, double AscentM, double DescentM, double GradePercent,
    ParticipantState State, DateTimeOffset? FinishTime, DateTimeOffset? StoppedSince, DateTimeOffset? OffCourseSince);

/// <param name="Accepted">Whether the fix was new and passed the filter (see <see cref="ParticipantTracker.ApplyMany"/>).</param>
public sealed record ApplyResult(ParticipantSnapshot Snapshot, IReadOnlyList<SegmentCompletion> Completed, bool Accepted);

/// <param name="Accepted">Fixes of the batch that were new and passed the filter (see <see cref="ParticipantTracker.ApplyMany"/>).</param>
/// <param name="Rejected">All other fixes of the batch.</param>
public sealed record ApplyManyResult(ParticipantSnapshot Snapshot, IReadOnlyList<SegmentCompletion> Completed, int Accepted, int Rejected);

/// <summary>
/// Turns one participant's fixes into course progress and motion statistics.
/// </summary>
/// <remarks>
/// <para>Every fix that passes input validation and the accuracy check is stored in device-time order, at most
/// one per second of device time and at most <see cref="MaxStoredFixes"/> in all.
/// The state is a fold over the stored fixes in that order: speed filter, course matching, lap counting,
/// total distance, history and motion accounting. A fix that sorts before the newest stored one triggers
/// a replay of all stored fixes into fresh state, so the result depends only on the set of fixes received,
/// never on their arrival order. Fixes rejected by the speed or duplicate check stay stored because that
/// decision depends on the fixes before them. Fixes before the start time only update the last-fix fields.</para>
/// <para>Matching keeps a lap counter and the matched lap progress (the position on the course). The
/// lap counter steps up or down whenever the position crosses the start/finish seam in either direction,
/// so jitter across the line cancels out. After a signal gap long enough to hide whole laps the number of
/// laps is chosen from the expected advance (see <c>WholeLaps</c>). Total distance is the high-water mark of that position,
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
    /// <summary>
    /// A stop starts at the first matched fix within this distance below the current total. The total is a
    /// high-water mark, so GPS drift of a standing receiver only raises it by the largest forward excursion
    /// (typically under 10 m), not by the sum of the jitter; 15 m absorbs that while a rider moving off again
    /// leaves it within seconds. It is well inside the 50 m the stopped rule allows over 3 minutes.
    /// </summary>
    private const double StoppedToleranceM = 15;
    /// <summary>Absorbs floating-point error at the edges of the matcher window.</summary>
    private const double WindowToleranceM = 1;
    /// <summary>
    /// Window of the moving speed that predicts the advance during a signal gap (see <see cref="WholeLaps"/>) and
    /// that the ETA uses: spec 5.2 asks for a rolling average over the last 5 to 10 minutes of moving speed.
    /// </summary>
    public static readonly TimeSpan EtaSpeedWindow = TimeSpan.FromMinutes(7);
    /// <summary>
    /// Stored fixes per participant at most; further new fixes are rejected. At one fix per second that is over
    /// five hours, at the 5 s send interval more than a day, and a full replay stays in the tens of milliseconds.
    /// </summary>
    public const int MaxStoredFixes = 20_000;
    private static readonly MatcherOptions DefaultMatcher = new();

    private readonly Course _course;
    private readonly Sport _sport;
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
    private DateTimeOffset _observedTime;     // newest accepted race fix that went through matching
    private double _observedPositionM;        // race position believed at _observedTime (see WholeLaps)
    private TimeSpan _movingTime;
    private TimeSpan _stoppedTime;
    private double _maxSpeedMps;
    private DateTimeOffset? _finishTime;
    private DateTimeOffset? _offCourseSince; // first fix of the current run of accepted fixes the matcher placed off course

    public ParticipantTracker(Course course, Sport sport, DateTimeOffset startTime, TrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(course);
        _course = course;
        _sport = sport;
        _startTime = startTime;
        _options = options ?? TrackerOptions.Defaults(sport);
        Reset();
    }

    public ProgressHistory History { get; } = new();

    /// <summary>Number of full replays so far; lets tests check that a batch costs at most one.</summary>
    internal int ReplayCount { get; private set; }

    /// <summary>Number of stored fixes (see <see cref="MaxStoredFixes"/>).</summary>
    internal int StoredFixCount => _fixes.Count;

    /// <summary>Adds one fix; see <see cref="ApplyMany"/>.</summary>
    public ApplyResult Apply(PositionFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        var result = ApplyMany(new[] { fix });
        return new ApplyResult(result.Snapshot, result.Completed, result.Accepted == 1);
    }

    /// <summary>
    /// Adds fixes in any order and returns the snapshot at the newest accepted fix. Invalid fixes and exact
    /// resends of a stored fix leave the state unchanged. At most one fix per second of device time is stored:
    /// within a second the first in fix order (time, then coordinates) wins, also when it arrives later and
    /// replaces the stored one, so the stored set does not depend on arrival order. Beyond
    /// <see cref="MaxStoredFixes"/> new fixes are rejected. Fixes that extend the stored sequence are folded in
    /// directly; once one sorts before a stored fix, the rest are only inserted and all stored fixes are
    /// replayed once at the end. <c>Completed</c> holds the segment completions that are new or whose time
    /// changed with this batch (a late fix can move an interpolated crossing), each <c>(Lap, SegmentIndex)</c> at
    /// most once, in course order; consumers record them idempotently. <c>Accepted</c> counts the fixes that were
    /// newly stored and passed the filter in the fold after the batch; malformed fixes, accuracy failures, exact
    /// resends, fixes losing their second to another one, fixes over the cap and speed or duplicate rejections
    /// are <c>Rejected</c>. A later batch can change the filter verdict
    /// of an earlier fix on replay; that is not reported again.
    /// </summary>
    public ApplyManyResult ApplyMany(IReadOnlyList<PositionFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        foreach (var fix in fixes) ArgumentNullException.ThrowIfNull(fix, nameof(fixes));

        var verdicts = new Dictionary<PositionFix, bool>(ReferenceEqualityComparer.Instance); // newly stored fixes
        var replay = false;
        var rejected = 0;
        foreach (var fix in fixes)
        {
            var index = IsStorable(fix) ? _fixes.BinarySearch(fix, FixOrder.Instance) : 0;
            if (index >= 0) // not storable, or an exact resend of a stored fix
            {
                rejected++;
                continue;
            }
            index = ~index;

            // At most one stored fix per second of device time: the first in fix order.
            var second = SecondOf(fix);
            if (index > 0 && SecondOf(_fixes[index - 1]) == second)
            {
                rejected++; // the stored one sorts first and stays
                continue;
            }
            if (index < _fixes.Count && SecondOf(_fixes[index]) == second)
            {
                if (verdicts.Remove(_fixes[index])) rejected++; // evicts a fix of this batch
                _fixes[index] = fix;
                replay = true;
                verdicts[fix] = false;
                continue;
            }
            if (_fixes.Count >= MaxStoredFixes)
            {
                rejected++;
                continue;
            }

            _fixes.Insert(index, fix);
            replay |= index != _fixes.Count - 1;
            verdicts[fix] = !replay && Step(fix);
        }
        if (replay) Replay(verdicts);

        var accepted = verdicts.Values.Count(v => v);
        return new ApplyManyResult(GetSnapshot(_lastAccepted?.Time ?? _startTime), ChangedCompletions(),
            accepted, rejected + verdicts.Count - accepted);
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
    /// replays the fixes, because paused time is neither moving nor stopped time; a switch that leaves the pause
    /// intervals unchanged (pausing while paused, resuming while running) is recorded but replays nothing.
    /// </summary>
    public void SetPaused(DateTimeOffset at, bool paused)
    {
        var index = _pauseSwitches.BinarySearch((at, _pauseSwitches.Count, paused));
        _pauseSwitches.Insert(~index, (at, _pauseSwitches.Count, paused));

        var pauses = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        DateTimeOffset? start = null;
        foreach (var (time, _, on) in _pauseSwitches)
        {
            if (on) start ??= time;
            else if (start is { } s)
            {
                if (time > s) pauses.Add((s, time));
                start = null;
            }
        }
        if (start is { } open) pauses.Add((open, DateTimeOffset.MaxValue));

        // A switch to the state already in force (a repeated pause or resume) changes no interval: no replay.
        if (pauses.SequenceEqual(_pauses)) return;
        _pauses.Clear();
        _pauses.AddRange(pauses);
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
        DateTimeOffset? stoppedSince = last is not null && IsStoppedAt(last, now) ? StoppedSince(last, now) : null;
        DateTimeOffset? offCourseSince = last is not null && IsOffCourse(last) ? _offCourseSince : null;
        return new ParticipantSnapshot(
            shown?.Time, shown?.Lat, shown?.Lon, shown?.AccuracyM,
            _totalM, lap, lapProgress, _offCourseM,
            speed, movingSpeed, avgSpeed, _maxSpeedMps, pace,
            _movingTime, _stoppedTime,
            _course.AscentUpTo(_totalM), _course.DescentUpTo(_totalM), _course.GradePercentAt(lapProgress),
            StateAt(now),
            _finishTime, stoppedSince, offCourseSince);
    }

    /// <summary>
    /// The state by display priority Dnf > Finished > Sos > Offline > Delayed > OffCourse > Stopped > Live.
    /// Without an accepted race fix the participant is NotStarted, unless DNF or SOS apply. Silence is measured
    /// from the newest accepted fix to <paramref name="now"/> and is Delayed/Offline only when it is strictly
    /// longer than the threshold ("within 15 s" is Live, "over 2 min" is Offline), never negative. Off course and stopped are
    /// judged at the newest accepted fix, since either silence state outranks them.
    /// </summary>
    private ParticipantState StateAt(DateTimeOffset now)
    {
        if (_dnfAt <= now) return ParticipantState.Dnf;
        if (_finishTime is not null) return ParticipantState.Finished;
        if (_sos) return ParticipantState.Sos;
        if (_lastAccepted is not { } last) return ParticipantState.NotStarted;

        var silence = now > last.Time ? now - last.Time : TimeSpan.Zero; // a fix ahead of now is not negative silence
        if (silence > _options.OfflineAfter) return ParticipantState.Offline;
        if (silence > _options.DelayedAfter) return ParticipantState.Delayed;
        if (IsOffCourse(last)) return ParticipantState.OffCourse;
        if (IsStoppedAt(last, now)) return ParticipantState.Stopped;
        return ParticipantState.Live;
    }

    private bool IsOffCourse(PositionFix last) => last.Time - _offCourseSince > _options.OffCourseDebounce;

    private bool IsStoppedAt(PositionFix last, DateTimeOffset now) => !IsPausedAt(now) && IsStopped(last.Time);

    /// <summary>
    /// Start of the current stop, valid while the stopped condition holds: the first matched fix within
    /// <see cref="StoppedToleranceM"/> of the current total, capped at the start of the stopped window (a slow
    /// crawl drifts out of the tolerance, but the whole window was already slow). A pause that ended after that
    /// and by <paramref name="now"/> moves it to the pause end, because Stopped is not shown while paused. A pure
    /// function of the history, the pauses and <paramref name="now"/>, so replays give the same value.
    /// </summary>
    private DateTimeOffset StoppedSince(PositionFix last, DateTimeOffset now)
    {
        var windowStart = UnpausedWindowStart(last.Time, _options.StoppedAfter);
        var since = History.FirstPointAtOrAbove(_totalM - StoppedToleranceM) is { } first && first < windowStart
            ? first
            : windowStart;
        foreach (var (_, end) in _pauses)
            if (end <= now && end > since) since = end;
        return since;
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

    private static long SecondOf(PositionFix fix) => Math.DivRem(fix.Time.UtcTicks, TimeSpan.TicksPerSecond, out _);

    /// <summary>Rejects malformed input before the filter (which accepts any first fix) and fixes failing the accuracy limit.</summary>
    private bool IsStorable(PositionFix fix) =>
        double.IsFinite(fix.Lat) && double.IsFinite(fix.Lon) && Math.Abs(fix.Lat) <= 90 && Math.Abs(fix.Lon) <= 180
        && (fix.AccuracyM is null || double.IsFinite(fix.AccuracyM.Value))
        && PositionFilter.Evaluate(fix, null, _options.Filter) == FilterDecision.Accept;

    /// <summary>Refolds all stored fixes; records the filter verdict of each fix in <paramref name="verdicts"/>.</summary>
    private void Replay(Dictionary<PositionFix, bool>? verdicts = null)
    {
        ReplayCount++;
        Reset();
        foreach (var fix in _fixes)
        {
            var accepted = Step(fix);
            if (verdicts is not null && verdicts.ContainsKey(fix)) verdicts[fix] = accepted;
        }
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
        _observedTime = _startTime;
        _observedPositionM = 0;
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
    /// dropout. There is no separate full-lap retry: the matcher's window already covers the whole lap once the
    /// reach exceeds it, and the number of whole laps is decided by <see cref="TryAdvance"/>. A match is
    /// accepted only if it is reachable in the elapsed time and not more than <see cref="MaxRegressionM"/> behind
    /// the total. <paramref name="offCourse"/> is the matcher's verdict alone: no pass of the course within
    /// <c>OffCourseM</c> inside the window. Every fix that gets here is an observation for <see cref="WholeLaps"/>.
    /// </summary>
    private bool Locate(PositionFix fix, out bool offCourse)
    {
        var elapsedS = Math.Max(0, (fix.Time - _lastMatchedTime).TotalSeconds);
        var reachM = Math.Max(DefaultMatcher.ForwardWindowM, elapsedS * _options.Filter.MaxSpeedMps);
        var window = DefaultMatcher with
        {
            ForwardWindowM = Math.Min(_course.LapLengthM, reachM),
            OffCourseThresholdM = _options.OffCourseM,
        };

        var result = CourseMatcher.Match(_course, fix.Lat, fix.Lon, _lapProgressM, window);
        if (double.IsFinite(result.OffCourseM)) _offCourseM = result.OffCourseM;
        offCourse = !result.Matched;
        var advanced = TryAdvance(fix.Time, result, elapsedS, reachM);
        _observedTime = fix.Time;
        if (advanced) _lastMatchedTime = fix.Time;
        return advanced;
    }

    /// <summary>
    /// Applies a match if plausible; returns false (state unchanged apart from the observed position) otherwise.
    /// On a closed course the move is first normalised into <c>[-backtrack, L - backtrack)</c>: a move close to a
    /// full lap forward is backtracking. Once the time since the last accepted match at the sport's max speed
    /// could cover more than that range, whole laps may have been ridden unseen and <see cref="WholeLaps"/>
    /// decides how many.
    /// </summary>
    private bool TryAdvance(DateTimeOffset time, MatchResult match, double elapsedS, double reachM)
    {
        if (!match.Matched) return false;

        var move = match.LapProgressM - _lapProgressM;
        if (_course.IsClosed)
        {
            var backtrack = Math.Min(MaxBacktrackM, _course.LapLengthM / 2);
            move = Mod(move + backtrack, _course.LapLengthM) - backtrack;
            if (elapsedS * _options.Filter.MaxSpeedMps > _course.LapLengthM - backtrack && History.Last is not null)
                move = WholeLaps(move, time, reachM, backtrack);
        }
        var position = Position + move;
        if (move > reachM + WindowToleranceM) return false;
        if (position < _totalM - MaxRegressionM)
        {
            _observedPositionM = position; // seen behind: drift or a real step back, not a lap ahead
            return false;
        }

        if (_course.IsClosed)
            _lapCounter += (int)Math.Round((_lapProgressM + move - match.LapProgressM) / _course.LapLengthM);
        _lapProgressM = match.LapProgressM;
        _totalM = Math.Clamp(Math.Max(_totalM, Position), 0, _course.TotalLengthM);
        _observedPositionM = Position;
        return true;
    }

    /// <summary>
    /// Lap-count recovery after a signal gap on a loop shorter than the reach: chooses the move
    /// <c>move + k·L</c> (k ≥ 0, so never further back than the normalised move) that is closest to the expected
    /// move, within the reach and not past the finish by more than <paramref name="backtrackM"/>. The expected
    /// position is the position believed at the newest observation (any accepted race fix: a match, a match
    /// refused as too far behind, or an off-course fix, which keeps the position) plus the predicted speed times
    /// the unpaused time since that observation. Fixes that keep arriving therefore pin the expectation: drift
    /// behind a standing rider or a long off-course stretch never gains a lap, only an unobserved gap can.
    /// Requires an earlier matched fix (without one the normalised move is kept). Predicted speed:
    /// <see cref="ExpectedSpeedMps"/>. A pure function of the fold state, so replays give the same choice.
    /// </summary>
    private double WholeLaps(double move, DateTimeOffset time, double reachM, double backtrackM)
    {
        var unobserved = time - _observedTime - PausedWithin(_observedTime, time);
        var expectedMove = _observedPositionM + ExpectedSpeedMps() * Math.Max(0, unobserved.TotalSeconds) - Position;
        var best = move;
        for (var candidate = move + _course.LapLengthM;
             candidate <= reachM + WindowToleranceM && Position + candidate <= _course.TotalLengthM + backtrackM;
             candidate += _course.LapLengthM)
        {
            if (Math.Abs(candidate - expectedMove) < Math.Abs(best - expectedMove)) best = candidate;
        }
        return best;
    }

    /// <summary>
    /// The speed expected during a gap: the moving speed over <see cref="EtaSpeedWindow"/> up to the last accepted
    /// match; without moving time there, the average moving speed of the whole ride; without either, the sport's
    /// typical speed (running 3 m/s, cycling 7 m/s). Capped at the sport's max speed.
    /// </summary>
    private double ExpectedSpeedMps()
    {
        var recent = History.MovingSpeed(_lastMatchedTime, EtaSpeedWindow, _options.StoppedSpeedMps, PausedWithin).SpeedMps;
        if (recent > 0) return Math.Min(recent, _options.Filter.MaxSpeedMps);
        var average = _movingTime > TimeSpan.Zero ? _totalM / _movingTime.TotalSeconds : 0;
        if (average >= _options.StoppedSpeedMps) return Math.Min(average, _options.Filter.MaxSpeedMps);
        return _sport == Sport.Running ? 3.0 : 7.0;
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
