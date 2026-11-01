namespace PositionTracker.Core.Runtime;

/// <summary>
/// The settings of one event. Production code creates them through <see cref="Create"/>, which validates them; the
/// primary constructor does not validate and stays for tests and the demo simulator.
/// </summary>
public sealed record EventSettings(
    string EventId, string Name, Sport Sport, DateTimeOffset StartTime, TimeSpan SpectatorDelay,
    bool IsSimulated, TrackerOptions Tracker)
{
    public static readonly TimeSpan MinSpectatorDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxSpectatorDelay = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Settings with the sport's default <see cref="TrackerOptions"/>. Throws <see cref="ArgumentOutOfRangeException"/>
    /// unless <see cref="MinSpectatorDelay"/> &lt;= <paramref name="spectatorDelay"/> &lt;= <see cref="MaxSpectatorDelay"/>.
    /// </summary>
    public static EventSettings Create(string eventId, string name, Sport sport, DateTimeOffset startTime,
        TimeSpan spectatorDelay, bool isSimulated)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(spectatorDelay, MinSpectatorDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spectatorDelay, MaxSpectatorDelay);
        return new EventSettings(eventId, name, sport, startTime, spectatorDelay, isSimulated, TrackerOptions.Defaults(sport));
    }
}

public sealed record ParticipantInfo(
    string Id, string Bib, string Name, string? Nickname, string Category,
    bool ShowNameToSpectators, bool IsMinor, DateTimeOffset? StartTime = null);

public enum ParticipantAction { Sos, SosCleared, Pause, Resume, Dnf }

public sealed record IngestResult(int Accepted, int Rejected);

/// <summary>The outcome of <see cref="EventRuntime.IngestDetailed"/>.</summary>
/// <param name="Stored">The fixes the tracker newly stored (accepted, or skipped by the filter for now).</param>
/// <param name="At">The clock time the batch was ingested at.</param>
public sealed record IngestDetails(IngestResult Result, IReadOnlyList<PositionFix> Stored, DateTimeOffset At);
