using PositionTracker.Core.Runtime;

namespace PositionTracker.Data.Entities;

public enum ParticipantStatus { Invited, Confirmed, Declined, EmailFailed }

/// <summary>
/// A registered participant. The email is personal data for the organizer only. <see cref="EmailNormalized"/> is
/// unique inside an event, and so is <see cref="Bib"/>. The runtime id is <see cref="Id"/> formatted <c>"D"</c>.
/// </summary>
public sealed class ParticipantEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Bib { get; set; }
    public required string Name { get; set; }
    public string? Nickname { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalized { get; set; }
    public required string Gender { get; set; }
    /// <summary><c>sk</c> or <c>en</c>.</summary>
    public required string Language { get; set; }
    public Guid? CategoryId { get; set; }
    public CategoryEntity? Category { get; set; }
    public Guid? WaveId { get; set; }
    public ParticipantStatus Status { get; set; }
    public bool IsMinor { get; set; }
    public bool ShowNameToSpectators { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A recorded consent step (location broadcasting, name display) with the text version shown.</summary>
public sealed class ConsentEntity
{
    public long Id { get; set; }
    public Guid ParticipantId { get; set; }
    public bool LocationConsent { get; set; }
    public bool ShowNameToSpectators { get; set; }
    public required string TextVersion { get; set; }
    public DateTimeOffset Time { get; set; }
}

/// <summary>An organizer's acknowledgement of an alarm, keyed on (event, participant, kind) (Plan 1 ruling 9).</summary>
public sealed class AlarmAckEntity
{
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    public AlarmKind Kind { get; set; }
    public DateTimeOffset AcknowledgedAt { get; set; }
}
