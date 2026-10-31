using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Device tokens of database events (controller ruling P4), on the request's <see cref="AppDbContext"/> (so they are
/// issued and revoked in the caller's transaction). A token is 32 random bytes, base64url; only its SHA-256 is stored
/// (<see cref="DeviceTokenEntity"/>), so the raw token exists only in the answer of <see cref="IssueAsync"/>. Issuing
/// revokes every earlier token of the participant; a revoked token never resolves again. Ids are the canonical
/// runtime ids (Guid <c>"D"</c>, ruling P5). Raw tokens are never logged.
/// </summary>
public sealed class DbTokenService(AppDbContext db, TimeProvider clock) : ITokenService
{
    /// <summary>Longer input is never a token of ours (and is not hashed).</summary>
    private const int MaxTokenLength = 64;

    public async Task<string> IssueAsync(string eventId, string participantId, CancellationToken ct)
    {
        var ev = ParseId(eventId, nameof(eventId));
        var participant = ParseId(participantId, nameof(participantId));
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await RevokeCoreAsync(ev, participant, now, ct);
        db.DeviceTokens.Add(new DeviceTokenEntity
        {
            Id = Guid.CreateVersion7(now), EventId = ev, ParticipantId = participant, TokenHash = Hash(raw), IssuedAt = now,
        });
        await db.SaveChangesAsync(ct);
        if (own is not null) await own.CommitAsync(ct);
        return raw;
    }

    public async Task<(string EventId, string ParticipantId)?> ResolveAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > MaxTokenLength) return null;
        var hash = Hash(rawToken);
        var row = await db.DeviceTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .Select(t => new { t.EventId, t.ParticipantId, t.TokenHash })
            .SingleOrDefaultAsync(ct);
        return row is not null && CryptographicOperations.FixedTimeEquals(row.TokenHash, hash)
            ? (row.EventId.ToString("D"), row.ParticipantId.ToString("D"))
            : null;
    }

    public Task RevokeAsync(string eventId, string participantId, CancellationToken ct) =>
        RevokeCoreAsync(ParseId(eventId, nameof(eventId)), ParseId(participantId, nameof(participantId)), clock.GetUtcNow(), ct);

    /// <summary>Whether <paramref name="id"/> is a canonical database id (Guid <c>"D"</c>, lower case).</summary>
    public static bool IsDatabaseId(string? id) =>
        Guid.TryParseExact(id, "D", out var guid) && string.Equals(id, guid.ToString("D"), StringComparison.Ordinal);

    private Task<int> RevokeCoreAsync(Guid eventId, Guid participantId, DateTimeOffset now, CancellationToken ct) =>
        db.DeviceTokens
            .Where(t => t.EventId == eventId && t.ParticipantId == participantId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private static Guid ParseId(string id, string name) =>
        IsDatabaseId(id) ? Guid.ParseExact(id, "D") : throw new ArgumentException("Not a database id.", name);

    private static byte[] Hash(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}

/// <summary>
/// The registered <see cref="ITokenService"/> (scoped): database events (canonical Guid ids) use
/// <see cref="DbTokenService"/>, simulated events the in-memory <see cref="TokenService"/>, which never writes a row.
/// Resolving tries memory first, then the database; revoking clears both.
/// <para>The device ingest still resolves through the in-memory <see cref="TokenService"/> only; switching it to this
/// service (database tokens surviving restarts) is Task 7.</para>
/// </summary>
public sealed class DeviceTokens(TokenService memory, DbTokenService database) : ITokenService
{
    public Task<string> IssueAsync(string eventId, string participantId, CancellationToken ct) =>
        DbTokenService.IsDatabaseId(eventId)
            ? database.IssueAsync(eventId, participantId, ct)
            : Task.FromResult(memory.Issue(eventId, participantId));

    public async Task<(string EventId, string ParticipantId)?> ResolveAsync(string rawToken, CancellationToken ct) =>
        memory.Resolve(rawToken) ?? await database.ResolveAsync(rawToken, ct);

    public async Task RevokeAsync(string eventId, string participantId, CancellationToken ct)
    {
        memory.Revoke(eventId, participantId);
        if (DbTokenService.IsDatabaseId(eventId) && DbTokenService.IsDatabaseId(participantId))
            await database.RevokeAsync(eventId, participantId, ct);
    }
}
