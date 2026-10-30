using System.Security.Cryptography;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;

namespace PositionTracker.Api.Simulation;

public sealed record DemoRequest(
    int Participants = 30, double TimeScale = 20, int Laps = 3, string? GpxBase64 = null, Sport Sport = Sport.Cycling);

public sealed record DemoResponse(string EventId, string OrganizerUrl, string PublicUrl);

/// <summary>A running simulated event as listed by <c>GET /api/demo/events</c>.</summary>
/// <param name="StartedAt">The event's start time; simulated time runs from here.</param>
/// <param name="Finished">Whether every rider has delivered its last fix; the event stays registered until deleted.</param>
public sealed record DemoEventInfo(
    string EventId, int Participants, double TimeScale, DateTimeOffset StartedAt, TimeSpan SimulatedElapsed, bool Finished);

/// <summary>A demo request the service refuses; <see cref="StatusCode"/> is 400 (invalid) or 409 (too many running).</summary>
public sealed class DemoRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Runs simulated demo events through the same ingest path as real phones. Each simulated event is a normal
/// <see cref="EventRuntime"/> in the <see cref="EventRegistry"/> (flagged <see cref="EventSettings.IsSimulated"/>)
/// with its own <see cref="ScaledTimeProvider"/>, so states and views use simulated time. Every participant gets
/// a token and a <see cref="VirtualRider"/>; its fixes are generated lazily and delivered in groups of equal
/// <see cref="GeneratedFix.DeliverAt"/> (one batch per group, as a phone would send them) through
/// <see cref="EventRuntime.Ingest"/> once simulated time reaches them. The background loop advances every
/// running simulation each <see cref="TickInterval"/> on the injected <see cref="TimeProvider"/> to its scaled
/// time (real elapsed x time scale); <see cref="Step"/> drives a simulation manually instead. Either way the
/// event's clock is pinned to the time fixes were last delivered for. Thread-safe.
/// </summary>
public sealed class SimulationService(
    EventRegistry registry, TokenService tokens, TimeProvider clock, ILogger<SimulationService> logger) : BackgroundService
{
    public const int MaxParticipants = 100;
    public const double MaxTimeScale = 200;
    public const int MaxLaps = 10;
    public const int MaxSimulatedEvents = 3;
    public const int MaxGpxBytes = 1024 * 1024;

    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>Time between two generated fixes of a rider (the phone's send interval).</summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SpectatorDelay = TimeSpan.FromSeconds(30);

    private static readonly double[] CheckpointsM = [3_000, 8_000];
    private static readonly string[] FirstNames = ["Peter", "Jana", "Martin", "Eva", "Tomas", "Lucia", "Marek", "Zuzana", "Jakub", "Katarina", "Michal"];
    private static readonly string[] LastNames = ["Novak", "Horvath", "Kovac", "Varga", "Toth", "Nagy", "Balaz", "Molnar", "Simko", "Kral", "Urban", "Hudak", "Polak"];
    private static readonly string[] Categories = ["Open", "Masters", "Women"];

    private readonly object _lock = new();
    private readonly Dictionary<string, Simulation> _simulations = new(StringComparer.Ordinal);

    /// <summary>
    /// Default profiles for <paramref name="participants"/> riders: base speeds spread evenly over 6 to 10 m/s
    /// (cycling) or 3 to 4.5 m/s (running), 5 % jitter, 5 m GPS noise. Rider 1 raises SOS at 40 min, rider 2 goes
    /// 150 m off course for 120 s from 25 min, riders 3 and 4 lose signal from 30 to 34 min (fewer riders, fewer
    /// of these roles; SOS is always present).
    /// </summary>
    public static IReadOnlyList<VirtualRiderProfile> DefaultProfiles(int participants, Sport sport)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participants, 1);
        var (min, max) = sport == Sport.Running ? (3.0, 4.5) : (6.0, 10.0);
        var dropout = new[] { (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(34)) };
        return Enumerable.Range(0, participants)
            .Select(i => new VirtualRiderProfile(
                ParticipantId(i),
                participants == 1 ? (min + max) / 2 : min + (max - min) * i / (participants - 1),
                SpeedJitter: 0.05, GpsNoiseM: 5, Seed: 1000 + i,
                Dropouts: i is 2 or 3 ? dropout : [],
                SosAt: i == 0 ? TimeSpan.FromMinutes(40) : null,
                OffCourseAt: i == 1 ? TimeSpan.FromMinutes(25) : null,
                OffCourseFor: i == 1 ? TimeSpan.FromSeconds(120) : null))
            .ToList();
    }

    /// <summary>Starts a simulated event with <see cref="DefaultProfiles"/>; see <see cref="StartWithProfiles"/>.</summary>
    public DemoResponse Start(DemoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return StartWithProfiles(request, DefaultProfiles(request.Participants, request.Sport));
    }

    /// <summary>
    /// Creates a simulated event with one participant per profile (participant id = profile id; the count must
    /// equal <see cref="DemoRequest.Participants"/>), registers it, issues the tokens and starts feeding it.
    /// Throws <see cref="DemoRequestException"/> for an invalid request or GPX (400) or when
    /// <see cref="MaxSimulatedEvents"/> are already running (409).
    /// </summary>
    public DemoResponse StartWithProfiles(DemoRequest request, IReadOnlyList<VirtualRiderProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);
        Validate(request);
        if (profiles.Count != request.Participants)
            throw new ArgumentException("Expected one profile per participant.", nameof(profiles));
        var course = BuildCourse(request);

        var now = clock.GetUtcNow();
        var start = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var simClock = new ScaledTimeProvider(clock, start, request.TimeScale);
        simClock.SetSimulatedElapsed(TimeSpan.Zero); // the event's time moves only when fixes are delivered
        var eventId = "demo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var settings = new EventSettings(eventId, $"Demo ({profiles.Count} participants, x{request.TimeScale})",
            request.Sport, start, SpectatorDelay, IsSimulated: true, TrackerOptions.Defaults(request.Sport));
        var runtime = new EventRuntime(settings, course, simClock);
        var riders = profiles.Select((p, i) =>
        {
            var info = Participant(p.Id, i, profiles.Count);
            runtime.AddParticipant(info);
            return new Rider(p, VirtualRider.Generate(course, request.Sport, p, start, SendInterval).GetEnumerator(),
                batteryStartPct: 100 - i * 17 % 60);
        }).ToList();
        var simulation = new Simulation(runtime, simClock, riders);

        lock (_lock)
        {
            if (_simulations.Count >= MaxSimulatedEvents)
                throw new DemoRequestException(StatusCodes.Status409Conflict,
                    $"At most {MaxSimulatedEvents} simulated events can run at once; delete one first.");
            registry.Add(runtime);
            foreach (var rider in riders) rider.Token = tokens.Issue(eventId, rider.Profile.Id);
            _simulations.Add(eventId, simulation);
        }
        return new DemoResponse(eventId, $"/api/events/{eventId}/live", $"/api/events/{eventId}/public");
    }

    /// <summary>
    /// Test and manual hook: sets the event's simulated elapsed time and delivers every fix due by then. From the
    /// first call on, the background loop leaves the event alone. Time must not go backwards. Unknown or stopped
    /// events throw <see cref="KeyNotFoundException"/>.
    /// </summary>
    public void Step(string eventId, TimeSpan simulatedTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(simulatedTime, TimeSpan.Zero);
        var simulation = Find(eventId) ?? throw new KeyNotFoundException($"Unknown simulated event '{eventId}'.");
        lock (simulation)
        {
            if (simulation.Stopped) throw new KeyNotFoundException($"Unknown simulated event '{eventId}'.");
            if (simulatedTime < simulation.DeliveredUntil)
                throw new ArgumentOutOfRangeException(nameof(simulatedTime), "Simulated time must not go backwards.");
            simulation.Manual = true;
            Advance(simulation, simulatedTime);
        }
    }

    /// <summary>Removes a simulated event from the registry, revokes its tokens and stops feeding it; false if unknown.</summary>
    public bool Stop(string eventId)
    {
        Simulation? simulation;
        lock (_lock)
        {
            if (!_simulations.Remove(eventId, out simulation)) return false;
            registry.Remove(eventId);
        }
        lock (simulation)
        {
            simulation.Stopped = true;
            foreach (var rider in simulation.Riders) tokens.Revoke(eventId, rider.Profile.Id);
        }
        return true;
    }

    /// <summary>The running simulated events (never real ones), oldest first.</summary>
    public IReadOnlyList<DemoEventInfo> List()
    {
        List<Simulation> all;
        lock (_lock) all = _simulations.Values.ToList();
        return all
            .Select(s => new DemoEventInfo(s.Runtime.Settings.EventId, s.Riders.Count, s.Clock.Scale,
                s.Runtime.Settings.StartTime, s.Clock.Elapsed, s.Finished))
            .OrderBy(i => i.StartedAt).ThenBy(i => i.EventId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The token the simulated phone of a participant uses (for tests).</summary>
    internal string TokenOf(string eventId, string participantId) =>
        Find(eventId)?.Riders.SingleOrDefault(r => r.Profile.Id == participantId)?.Token
        ?? throw new KeyNotFoundException($"Unknown simulated participant '{participantId}'.");

    /// <summary>
    /// Advances every running, unfinished simulation not driven by <see cref="Step"/> to its clock's
    /// <see cref="ScaledTimeProvider.ScaledElapsed"/>; a failure is logged per event and the others still advance.
    /// </summary>
    public void AdvanceAll()
    {
        List<Simulation> all;
        lock (_lock) all = _simulations.Values.ToList();
        foreach (var simulation in all)
        {
            try
            {
                lock (simulation)
                {
                    if (simulation.Stopped || simulation.Finished || simulation.Manual) continue;
                    var elapsed = simulation.Clock.ScaledElapsed;
                    if (elapsed > simulation.DeliveredUntil) Advance(simulation, elapsed);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Simulation step failed for event {EventId}.", simulation.Runtime.Settings.EventId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                AdvanceAll();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Pins the event's clock to <paramref name="elapsed"/>, delivers the fixes due by then and raises due SOS
    /// alarms; the caller holds the simulation's lock.
    /// </summary>
    private static void Advance(Simulation simulation, TimeSpan elapsed)
    {
        simulation.Clock.SetSimulatedElapsed(elapsed);
        var runtime = simulation.Runtime;
        var now = runtime.Settings.StartTime + elapsed;
        var batch = new List<PositionFix>();
        foreach (var rider in simulation.Riders)
        {
            var id = rider.Profile.Id;
            DateTimeOffset? groupAt = null;
            while (rider.Peek() is { } next && next.DeliverAt <= now)
            {
                if (groupAt is { } at && next.DeliverAt != at) Send(runtime, rider, batch);
                groupAt = next.DeliverAt;
                batch.Add(next.Fix);
                rider.Take();
            }
            if (batch.Count > 0) Send(runtime, rider, batch);

            if (!rider.SosRaised && rider.Profile.SosAt is { } sosAt && elapsed >= sosAt)
            {
                runtime.Act(id, ParticipantAction.Sos);
                rider.SosRaised = true;
            }
        }
        simulation.DeliveredUntil = elapsed;
        simulation.Finished = simulation.Riders.All(r => r.Peek() is null);
    }

    /// <summary>Ingests one batch with a device status (slowly draining battery, queue of the batch) and clears it.</summary>
    private static void Send(EventRuntime runtime, Rider rider, List<PositionFix> batch)
    {
        var minutes = (batch[^1].Time - runtime.Settings.StartTime).TotalMinutes;
        var battery = Math.Clamp(rider.BatteryStartPct - 0.5 * minutes, 1, 100);
        var status = new DeviceStatus(Math.Round(battery), Charging: false, QueueLength: batch.Count - 1, InForeground: true);
        runtime.Ingest(rider.Profile.Id, new PositionBatch(batch.ToList(), status));
        batch.Clear();
    }

    private Simulation? Find(string eventId)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        lock (_lock) return _simulations.GetValueOrDefault(eventId);
    }

    private static void Validate(DemoRequest request)
    {
        if (request.Participants is < 1 or > MaxParticipants)
            throw Invalid($"'participants' must be between 1 and {MaxParticipants}.");
        if (!double.IsFinite(request.TimeScale) || request.TimeScale <= 0 || request.TimeScale > MaxTimeScale)
            throw Invalid($"'timeScale' must be greater than 0 and at most {MaxTimeScale}.");
        if (request.Laps is < 1 or > MaxLaps)
            throw Invalid($"'laps' must be between 1 and {MaxLaps}.");
        if (!Enum.IsDefined(request.Sport))
            throw Invalid("'sport' must be 'cycling' or 'running'.");
    }

    /// <summary>
    /// The default 12.5 km loop or the decoded GPX, with checkpoints at 3 000 m and 8 000 m of the lap where they
    /// lie inside it.
    /// </summary>
    private static Course BuildCourse(DemoRequest request)
    {
        var points = request.GpxBase64 is { } gpx ? ParseGpx(gpx) : DemoCourses.Loop();
        var lapLength = Course.Create(points, 1).LapLengthM;
        var checkpoints = CheckpointsM
            .Where(d => d < lapLength)
            .Select((d, i) => new Checkpoint($"CP{i + 1}", d))
            .ToList();
        try
        {
            return Course.Create(points, request.Laps, checkpoints);
        }
        catch (ArgumentException ex)
        {
            throw Invalid($"Invalid course: {ex.Message}");
        }
    }

    private static IReadOnlyList<GeoPoint> ParseGpx(string base64)
    {
        if ((long)base64.Length * 3 / 4 > MaxGpxBytes + 2)
            throw Invalid($"The GPX must not exceed {MaxGpxBytes} bytes.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw Invalid("'gpxBase64' is not valid base64.");
        }
        if (bytes.Length > MaxGpxBytes) throw Invalid($"The GPX must not exceed {MaxGpxBytes} bytes.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return GpxParser.Parse(stream);
        }
        catch (GpxFormatException ex)
        {
            throw Invalid($"Invalid GPX: {ex.Message}");
        }
    }

    private static DemoRequestException Invalid(string message) => new(StatusCodes.Status400BadRequest, message);

    private static string ParticipantId(int index) => $"sim-{index + 1:000}";

    /// <summary>Generated identity: every tenth participant hides the name from spectators; the last one is a minor.</summary>
    private static ParticipantInfo Participant(string id, int index, int count)
    {
        var isMinor = count > 1 && index == count - 1;
        var name = $"{FirstNames[index % FirstNames.Length]} {LastNames[index % LastNames.Length]}";
        return new ParticipantInfo(id, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), name,
            Nickname: null, isMinor ? "Junior" : Categories[index % Categories.Length],
            ShowNameToSpectators: index % 10 != 9, IsMinor: isMinor);
    }

    private sealed class Simulation(EventRuntime runtime, ScaledTimeProvider clock, IReadOnlyList<Rider> riders)
    {
        public EventRuntime Runtime { get; } = runtime;
        public ScaledTimeProvider Clock { get; } = clock;
        public IReadOnlyList<Rider> Riders { get; } = riders;
        /// <summary>Simulated time up to which fixes were delivered.</summary>
        public TimeSpan DeliveredUntil { get; set; }
        public bool Finished { get; set; }
        public bool Stopped { get; set; }
        /// <summary>Driven by <see cref="SimulationService.Step"/>; the background loop skips it.</summary>
        public bool Manual { get; set; }
    }

    /// <summary>
    /// One simulated phone. Fixes are pulled lazily from the generator, which yields non-decreasing
    /// <see cref="GeneratedFix.DeliverAt"/> for non-overlapping dropouts (queued fixes are released at the end of
    /// their dropout, before the fixes that follow it).
    /// </summary>
    private sealed class Rider(VirtualRiderProfile profile, IEnumerator<GeneratedFix> fixes, double batteryStartPct)
    {
        private GeneratedFix? _next;
        private bool _exhausted;

        public VirtualRiderProfile Profile { get; } = profile;
        public double BatteryStartPct { get; } = batteryStartPct;
        public string Token { get; set; } = "";
        public bool SosRaised { get; set; }

        /// <summary>The next undelivered fix, or <c>null</c> once the rider finished.</summary>
        public GeneratedFix? Peek()
        {
            if (_next is null && !_exhausted)
            {
                if (fixes.MoveNext()) _next = fixes.Current;
                else
                {
                    _exhausted = true;
                    fixes.Dispose();
                }
            }
            return _next;
        }

        public void Take() => _next = null;
    }
}
