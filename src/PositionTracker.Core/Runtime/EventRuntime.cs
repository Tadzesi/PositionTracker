namespace PositionTracker.Core.Runtime;

/// <summary>
/// In-memory state of one event: a tracker per participant, a shared segment board and the latest device
/// status. Thread-safe: one lock guards all mutation and view building.
/// </summary>
/// <remarks>
/// Views are built from <see cref="ParticipantTracker.GetSnapshot"/> at the clock's current time, never from
/// the snapshot returned by <see cref="ParticipantTracker.Apply"/> (that one is taken at the newest fix and
/// misses later silence or DNF). <see cref="Alarm.Since"/> is the time the alarm condition began, stable across
/// views: SOS from the <see cref="ParticipantAction.Sos"/> action (raised whatever state is shown, since an SOS
/// after finishing or DNF is still real), Stopped and OffCourse from the snapshot's
/// <see cref="ParticipantSnapshot.StoppedSince"/> and <see cref="ParticipantSnapshot.OffCourseSince"/>, Offline
/// from the newest fix plus <see cref="TrackerOptions.OfflineAfter"/>, LowBattery from the first ingest that
/// reported under 20 %, LapsInferred from <see cref="ParticipantSnapshot.LapsInferredAt"/> (raised whatever state
/// is shown, including Finished, since that finish may be a lap early). Stopped, OffCourse and Offline follow the
/// shown state. Each kind appears at most once per participant, so (participant, kind) identifies an alarm.
/// The ETA is null while a finish is pending (<see cref="ParticipantSnapshot.FinishPending"/>).
/// </remarks>
public sealed class EventRuntime
{
    private const double LowBatteryBelowPct = 20;
    /// <summary>A fix more than this ahead of the event clock is rejected: a wrong device clock, not a position.</summary>
    public static readonly TimeSpan MaxFixAhead = TimeSpan.FromMinutes(5);
    /// <summary>A fix more than this before the start time is rejected (warm-up is only shown, never older data).</summary>
    public static readonly TimeSpan MaxFixBeforeStart = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly SegmentBoard _board = new();
    private readonly Dictionary<string, Entry> _participants = new(StringComparer.Ordinal);
    private long _participantsVersion;

    public EventRuntime(EventSettings settings, Course course, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(clock);
        Settings = settings;
        Course = course;
        _clock = clock;
    }

    private volatile EventSettings _settings = null!;

    /// <summary>The settings; only the name can change while the event runs (<see cref="Rename"/>).</summary>
    public EventSettings Settings { get => _settings; private set => _settings = value; }

    /// <summary>The organizer renamed the event: spectators see the new name from now on.</summary>
    public void Rename(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Settings = Settings with { Name = name };
    }

    public Course Course { get; }

    /// <summary>
    /// The event's clock: the time its views and states are evaluated at. For a simulated event it runs
    /// accelerated, so anything comparing against <see cref="LiveView.At"/> (such as the spectator delay) must use it.
    /// </summary>
    public TimeProvider Clock => _clock;

    /// <summary>Registers a participant; throws <see cref="ArgumentException"/> if the id is already taken.</summary>
    public void AddParticipant(ParticipantInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_lock)
        {
            if (_participants.ContainsKey(info.Id))
                throw new ArgumentException($"Participant '{info.Id}' already exists.", nameof(info));
            _participants[info.Id] = new Entry(info,
                new ParticipantTracker(Course, Settings.Sport, info.StartTime ?? Settings.StartTime, Settings.Tracker));
            Interlocked.Increment(ref _participantsVersion);
        }
    }

    /// <summary>
    /// Replaces the info of the participant with <c>info.Id</c> (name, nickname, consent, ...); the tracker and its
    /// state are kept. Bumps <see cref="ParticipantsVersion"/>, so the cached spectator projection follows on its
    /// next refresh. Throws <see cref="KeyNotFoundException"/> for an unknown id.
    /// </summary>
    public void UpdateParticipant(ParticipantInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_lock)
        {
            var entry = Find(info.Id);
            entry.Info = info;
            entry.Tracker.SetStartTime(info.StartTime ?? Settings.StartTime);
            Interlocked.Increment(ref _participantsVersion);
        }
    }

    /// <summary>
    /// Increases whenever the participant infos change (any <see cref="AddParticipant"/> or
    /// <see cref="UpdateParticipant"/>); read without the lock,
    /// so callers can cache data derived from <see cref="Participants"/>. Read it before <see cref="Participants"/>
    /// so a concurrent change is seen as stale, never missed.
    /// </summary>
    public long ParticipantsVersion => Interlocked.Read(ref _participantsVersion);

    /// <summary>A snapshot of the registered participants' info, keyed by participant id.</summary>
    public IReadOnlyDictionary<string, ParticipantInfo> Participants
    {
        get
        {
            lock (_lock) return _participants.ToDictionary(p => p.Key, p => p.Value.Info, StringComparer.Ordinal);
        }
    }

    /// <summary>Whether a participant with this id is registered.</summary>
    public bool HasParticipant(string participantId)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        lock (_lock) return _participants.ContainsKey(participantId);
    }

    /// <summary>
    /// Applies the batch's fixes in time order with at most one replay and stores its device status. Fixes more
    /// than <see cref="MaxFixAhead"/> ahead of the clock or more than <see cref="MaxFixBeforeStart"/> before the
    /// start are rejected without being stored. The status is the device's state as of sending; it carries no
    /// timestamp, so the last batch to arrive wins, even if it holds older fixes. A batch without status keeps the
    /// previous one. Throws <see cref="KeyNotFoundException"/> for an unknown participant.
    /// </summary>
    public IngestResult Ingest(string participantId, PositionBatch batch) => IngestDetailed(participantId, batch).Result;

    /// <summary>
    /// <see cref="Ingest"/>, also reporting what a persistence layer needs to rebuild the same state:
    /// <see cref="IngestDetails.Stored"/> (the fixes the tracker newly stored, see <see cref="ApplyManyResult.Stored"/>)
    /// and <see cref="IngestDetails.At"/> (the single clock reading this call used, for the time-out checks and the
    /// low-battery start). Replaying the stored fixes and the batch's status at that time gives the same state.
    /// </summary>
    public IngestDetails IngestDetailed(string participantId, PositionBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_lock)
        {
            var entry = Find(participantId);
            var now = _clock.GetUtcNow();
            var latest = now + MaxFixAhead;
            var earliest = (entry.Info.StartTime ?? Settings.StartTime) - MaxFixBeforeStart;
            foreach (var fix in batch.Fixes) ArgumentNullException.ThrowIfNull(fix, nameof(batch));
            var timely = batch.Fixes.Where(f => f.Time <= latest && f.Time >= earliest).OrderBy(f => f.Time).ToList();
            var result = entry.Tracker.ApplyMany(timely);
            foreach (var c in result.Completed)
            {
                _board.Record(participantId, c.Lap, c.SegmentIndex, c.Time);
                entry.Completions[(c.Lap, c.SegmentIndex)] = c.Time;
            }

            if (batch.Status is { } status)
            {
                entry.Status = status;
                if (status.BatteryPct < LowBatteryBelowPct) entry.LowBatterySince ??= now;
                else entry.LowBatterySince = null;
            }
            return new IngestDetails(
                new IngestResult(result.Accepted, result.Rejected + batch.Fixes.Count - timely.Count), result.Stored, now);
        }
    }

    /// <summary>
    /// Applies an organizer or device action at the current time and returns that time; unknown ids throw
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    public DateTimeOffset Act(string participantId, ParticipantAction action)
    {
        lock (_lock)
        {
            var entry = Find(participantId);
            var now = _clock.GetUtcNow();
            switch (action)
            {
                case ParticipantAction.Sos:
                    entry.Tracker.SetSos(true);
                    entry.SosSince ??= now;
                    break;
                case ParticipantAction.SosCleared:
                    entry.Tracker.SetSos(false);
                    entry.SosSince = null;
                    break;
                case ParticipantAction.Pause:
                    entry.Tracker.SetPaused(now, true);
                    break;
                case ParticipantAction.Resume:
                    entry.Tracker.SetPaused(now, false);
                    break;
                case ParticipantAction.Dnf:
                    entry.Tracker.MarkDnf(now);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
            return now;
        }
    }

    /// <summary>The organizer view at the current time: every participant in rank order, unranked ones last.</summary>
    public LiveView BuildLiveView()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var snapshots = _participants.ToDictionary(p => p.Key, p => p.Value.Tracker.GetSnapshot(now), StringComparer.Ordinal);
            var ranking = Ranking.Rank(_participants.Values
                .Select(e => new RankInput(e.Info.Id, snapshots[e.Info.Id].TotalDistanceM,
                    snapshots[e.Info.Id].LastRaceFixTime, snapshots[e.Info.Id].FinishTime, e.Tracker.History,
                    snapshots[e.Info.Id].State == ParticipantState.Dnf, e.Info.StartTime ?? Settings.StartTime))
                .ToList());

            var views = ranking
                .Select(r => BuildView(_participants[r.ParticipantId], snapshots[r.ParticipantId], r, now))
                .ToList();

            int Count(params ParticipantState[] states) => views.Count(v => states.Contains(v.State));
            return new LiveView(now, Settings.EventId, views,
                Count(ParticipantState.Live, ParticipantState.Stopped, ParticipantState.OffCourse, ParticipantState.Sos),
                Count(ParticipantState.Delayed),
                Count(ParticipantState.Offline),
                Count(ParticipantState.Dnf));
        }
    }

    private ParticipantView BuildView(Entry entry, ParticipantSnapshot s, RankedParticipant ranked, DateTimeOffset now)
    {
        var id = entry.Info.Id;
        var hasFix = s.LastRaceFixTime is not null; // warm-up fixes before the start do not rank
        // Lap and lap progress follow the total, not the lap counter, which can step back behind the start/finish
        // line while the total (a high-water mark) is already in the next lap.
        var lap = Math.Clamp((int)Math.Floor(s.TotalDistanceM / Course.LapLengthM) + 1, 1, Course.Laps);
        var lapProgress = s.TotalDistanceM - (lap - 1) * Course.LapLengthM;
        var eta = s.State is ParticipantState.Finished or ParticipantState.Dnf || s.FinishPending
            ? null
            : EtaEstimator.Estimate(Course, Settings.Sport, s, now);
        var segments = entry.Completions
            .OrderBy(c => c.Key.Lap).ThenBy(c => c.Key.SegmentIndex)
            .Select(c => new SegmentResult(c.Key.Lap, c.Key.SegmentIndex, c.Value,
                _board.ColourOf(id, c.Key.Lap, c.Key.SegmentIndex)))
            .ToList();

        return new ParticipantView(
            id, entry.Info.Bib, entry.Info.Name, entry.Info.Category,
            hasFix ? ranked.Rank : null, lap,
            s.TotalDistanceM, lapProgress, s.LastLat, s.LastLon,
            s.SpeedMps, s.AvgSpeedMps, s.PaceSecPerKm, s.AscentM, s.DescentM, s.GradePercent,
            s.State,
            hasFix ? ranked.GapToLeader : null, hasFix ? ranked.GapToAhead : null, eta,
            segments,
            entry.Status?.BatteryPct, s.LastAccuracyM, entry.Status?.QueueLength ?? 0,
            s.LastFixTime is { } last ? (now > last ? now - last : TimeSpan.Zero) : null,
            Alarms(entry, s, now),
            s.FinishLat, s.FinishLon, s.LapsInferred, s.LastRaceFixTime);
    }

    private List<Alarm> Alarms(Entry entry, ParticipantSnapshot s, DateTimeOffset now)
    {
        var alarms = new List<Alarm>();
        if (entry.SosSince is { } sosSince) alarms.Add(new Alarm(AlarmKind.Sos, sosSince));
        switch (s.State)
        {
            case ParticipantState.Stopped:
                alarms.Add(new Alarm(AlarmKind.Stopped, s.StoppedSince ?? now));
                break;
            case ParticipantState.OffCourse:
                alarms.Add(new Alarm(AlarmKind.OffCourse, s.OffCourseSince ?? now));
                break;
            case ParticipantState.Offline:
                alarms.Add(new Alarm(AlarmKind.Offline, s.LastFixTime + Settings.Tracker.OfflineAfter ?? now));
                break;
        }
        if (entry.Status?.BatteryPct < LowBatteryBelowPct)
            alarms.Add(new Alarm(AlarmKind.LowBattery, entry.LowBatterySince ?? now));
        if (s.LapsInferredAt is { } inferredAt) alarms.Add(new Alarm(AlarmKind.LapsInferred, inferredAt));
        return alarms;
    }

    private Entry Find(string participantId)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        return _participants.TryGetValue(participantId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Unknown participant '{participantId}'.");
    }

    private sealed class Entry(ParticipantInfo info, ParticipantTracker tracker)
    {
        public ParticipantInfo Info { get; set; } = info;
        public ParticipantTracker Tracker { get; } = tracker;
        /// <summary>Latest segment time per (lap, segment); new or changed completions replace by key.</summary>
        public Dictionary<(int Lap, int SegmentIndex), TimeSpan> Completions { get; } = new();
        public DeviceStatus? Status { get; set; }
        public DateTimeOffset? SosSince { get; set; }
        public DateTimeOffset? LowBatterySince { get; set; }
    }
}
