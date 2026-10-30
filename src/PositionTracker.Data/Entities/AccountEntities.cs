namespace PositionTracker.Data.Entities;

/// <summary>An organizer account. <see cref="EmailNormalized"/> (trimmed, lower case) is unique.</summary>
public sealed class OrganizerEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    /// <summary>As entered, for display and sending.</summary>
    public required string Email { get; set; }
    public required string EmailNormalized { get; set; }
    /// <summary><c>sk</c> or <c>en</c>.</summary>
    public required string Language { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EmailConfirmedAt { get; set; }
}

/// <summary>
/// A single-purpose emailed link (organizer email confirmation, organizer sign-in, participant invitation). Only the
/// SHA-256 hash of the random token is stored; <see cref="SubjectId"/> is the organizer or participant id.
/// </summary>
public sealed class LinkTokenEntity
{
    public Guid Id { get; set; }
    /// <summary>The link purpose name (defined by the account and registration code).</summary>
    public required string Purpose { get; set; }
    public Guid SubjectId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    /// <summary>Set when a newer token of the same purpose and subject replaced this one.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A participant's device bearer token, stored as its SHA-256 hash; at most one is not revoked.</summary>
public sealed class DeviceTokenEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ParticipantId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// <c>Simulated</c>: no SMTP server is configured, the message was only logged (never delivered).
/// </summary>
public enum EmailOutboxStatus { Pending, Sent, Failed, Simulated }

/// <summary>
/// A queued email; the outbox worker sends it with retries. The bodies are cleared once the row is finished (they
/// hold a link that is a credential until it expires); the row itself is deleted after the retention period.
/// </summary>
public sealed class EmailOutboxEntity
{
    public Guid Id { get; set; }
    public required string ToAddress { get; set; }
    public required string ToName { get; set; }
    public required string Subject { get; set; }
    public required string HtmlBody { get; set; }
    public required string TextBody { get; set; }
    public required string Language { get; set; }
    /// <summary>The message kind (invitation, signIn, ...); safe to log.</summary>
    public required string Purpose { get; set; }
    public string? ReplyToAddress { get; set; }
    public string? ReplyToName { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>When the row became Sent, Simulated or Failed; retention counts from here.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>The last delivery error (never contains an address, a name or the server's reply text).</summary>
    public string? LastError { get; set; }
    /// <summary>The participant whose status follows a permanent failure (invitations only).</summary>
    public Guid? ParticipantId { get; set; }
}
