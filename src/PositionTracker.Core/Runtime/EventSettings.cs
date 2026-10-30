namespace PositionTracker.Core.Runtime;

public sealed record EventSettings(
    string EventId, string Name, Sport Sport, DateTimeOffset StartTime, TimeSpan SpectatorDelay,
    bool IsSimulated, TrackerOptions Tracker);

public sealed record ParticipantInfo(
    string Id, string Bib, string Name, string? Nickname, string Category,
    bool ShowNameToSpectators, bool IsMinor);

public enum ParticipantAction { Sos, SosCleared, Pause, Resume, Dnf }

public sealed record IngestResult(int Accepted, int Rejected);
