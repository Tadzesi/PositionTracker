namespace PositionTracker.Api.Auth;

/// <summary>
/// Participant device bearer tokens (controller ruling P4): <c>Issue</c> returns the raw token once and revokes any
/// earlier token of the participant; <c>Resolve</c> maps a raw token to its event and participant (null when unknown or
/// revoked, compared in constant time on the SHA-256 hash); <c>Revoke</c> invalidates the participant's token at once.
/// Implementations: <see cref="TokenService"/> (in memory, simulated events), <see cref="DbTokenService"/> (database,
/// real events) and <see cref="DeviceTokens"/>, which routes between them (the one registered as this interface).
/// </summary>
public interface ITokenService
{
    Task<string> IssueAsync(string eventId, string participantId, CancellationToken ct);

    Task<(string EventId, string ParticipantId)?> ResolveAsync(string rawToken, CancellationToken ct);

    Task RevokeAsync(string eventId, string participantId, CancellationToken ct);
}
