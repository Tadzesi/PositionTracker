namespace PositionTracker.Data.Entities;

/// <summary>
/// Time an organizer spent on the site on one day (UTC). Credited from the heartbeats the open page sends while the tab is
/// visible and was used: the gap to the previous heartbeat counts, up to a cap, so a closed tab adds nothing.
/// </summary>
public sealed class OrganizerActivityEntity
{
    public Guid OrganizerId { get; set; }
    public DateOnly Day { get; set; }
    public int Seconds { get; set; }
    public DateTimeOffset LastBeatAt { get; set; }
}

/// <summary>A change an administrator made while acting as an organizer.</summary>
public sealed class AdminAuditEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid AdminId { get; set; }
    public Guid OrganizerId { get; set; }
    public required string Method { get; set; }
    /// <summary>The request path (ids only, no query string, no body).</summary>
    public required string Path { get; set; }
}
