using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Accounts;

/// <summary>What an emailed link is for; a token only works for the purpose it was issued for.</summary>
public enum LinkPurpose { OrganizerEmailConfirm, OrganizerSignIn, ParticipantInvite }

/// <summary>
/// <see cref="Superseded"/>: a newer token of the same purpose and subject replaced it. <see cref="Unknown"/>: no
/// such token, or one of another purpose.
/// </summary>
public enum LinkStatus { Ok, Expired, Used, Superseded, Unknown }

/// <summary>The outcome of a redemption. <see cref="SubjectId"/> is set for every status except <see cref="LinkStatus.Unknown"/>.</summary>
public readonly record struct LinkResult(LinkStatus Status, Guid SubjectId)
{
    public bool IsOk => Status == LinkStatus.Ok;

    public static LinkResult Unknown => new(LinkStatus.Unknown, Guid.Empty);
}

/// <summary>
/// Single-purpose emailed link tokens: 32 random bytes (base64url, 43 characters); only the SHA-256 hash of the
/// token text is stored. Works on the request's <see cref="AppDbContext"/>, so a token is written in the same
/// transaction as the outbox row of the email that carries it (the caller begins the transaction; without one
/// <see cref="IssueAsync"/> uses its own so superseding and inserting stay atomic). Raw tokens are never logged.
/// </summary>
public sealed class LinkTokenService(AppDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan EmailConfirmLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    /// <summary>Longer input is never a token of ours (and is not hashed).</summary>
    private const int MaxTokenLength = 64;

    /// <summary>
    /// Issues a token valid for <paramref name="lifetime"/> and returns the raw token (for the emailed link only).
    /// Every older unused token of the same purpose and subject is revoked (<see cref="LinkStatus.Superseded"/>).
    /// </summary>
    public async Task<string> IssueAsync(LinkPurpose purpose, Guid subjectId, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var name = purpose.ToString();

        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.LinkTokens
            .Where(t => t.Purpose == name && t.SubjectId == subjectId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        db.LinkTokens.Add(new LinkTokenEntity
        {
            Id = Guid.CreateVersion7(now),
            Purpose = name,
            SubjectId = subjectId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        await db.SaveChangesAsync(ct);
        if (own is not null) await own.CommitAsync(ct);
        return raw;
    }

    /// <summary>
    /// Checks a token for <paramref name="purpose"/>. Single-use tokens are marked used atomically (of concurrent
    /// redemptions exactly one gets <see cref="LinkStatus.Ok"/>, the others <see cref="LinkStatus.Used"/>); other
    /// tokens stay valid until they expire or are superseded (the first use is recorded).
    /// Order of checks: used (single-use only), superseded, expired.
    /// </summary>
    public async Task<LinkResult> RedeemAsync(LinkPurpose purpose, string rawToken, bool singleUse, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > MaxTokenLength) return LinkResult.Unknown;
        var hash = Hash(rawToken);
        var name = purpose.ToString();
        var token = await db.LinkTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.Purpose != name) return LinkResult.Unknown;

        var now = clock.GetUtcNow();
        if (Classify(token, singleUse, now) is { } failed) return failed;

        var marked = await db.LinkTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null && t.ExpiresAt > now && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (marked == 1 || !singleUse) return new LinkResult(LinkStatus.Ok, token.SubjectId);

        // Lost a race (or the token changed meanwhile): report its state now.
        var current = await db.LinkTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id, ct);
        return Classify(current, singleUse, now) ?? new LinkResult(LinkStatus.Used, token.SubjectId);
    }

    private static LinkResult? Classify(LinkTokenEntity token, bool singleUse, DateTimeOffset now)
    {
        if (singleUse && token.UsedAt is not null) return new LinkResult(LinkStatus.Used, token.SubjectId);
        if (token.RevokedAt is not null) return new LinkResult(LinkStatus.Superseded, token.SubjectId);
        if (now >= token.ExpiresAt) return new LinkResult(LinkStatus.Expired, token.SubjectId);
        return null;
    }

    /// <summary>SHA-256 of the token text (UTF-8).</summary>
    public static byte[] Hash(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}
