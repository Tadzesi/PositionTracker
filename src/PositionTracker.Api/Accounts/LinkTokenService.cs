using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// What an emailed link is for; a token only works for the purpose it was issued for. <see cref="OrganizerCode"/> is the
/// six-digit code mailed with a confirmation or sign-in link, typed on the page where the organizer asked for the mail (so a
/// phone that opens the link inside the mail app is not needed).
/// </summary>
public enum LinkPurpose { OrganizerEmailConfirm, OrganizerSignIn, ParticipantInvite, OrganizerCode }

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
public sealed class LinkTokenService(AppDbContext db, TimeProvider clock, IOptions<AccountOptions>? accounts = null)
{
    /// <summary>
    /// Key of the code hashes (from <see cref="AccountOptions.RateLimitSalt"/>, a server secret): a six-digit code has only a
    /// million values, so a plain hash in the database could be reversed at once by anyone who can read the table.
    /// </summary>
    private readonly byte[] _codeKey = SHA256.HashData(Encoding.UTF8.GetBytes("code-hash\n" + (accounts?.Value.RateLimitSalt ?? "")));

    public static readonly TimeSpan EmailConfirmLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);
    /// <summary>A code mailed with a sign-in link lives as long as the link.</summary>
    public static readonly TimeSpan SignInCodeLifetime = SignInLifetime;
    /// <summary>A code mailed with a confirmation link is for the sign-up page just left open: an hour, not the link's 14 days.</summary>
    public static readonly TimeSpan ConfirmCodeLifetime = TimeSpan.FromHours(1);
    /// <summary>An invitation link stays valid at least this long after the event's start.</summary>
    public static readonly TimeSpan InviteAfterStart = TimeSpan.FromDays(7);

    /// <summary>Expiry of an invitation issued at <paramref name="now"/>: the later of the lifetime and start + <see cref="InviteAfterStart"/>.</summary>
    public static DateTimeOffset InviteExpiry(DateTimeOffset now, DateTimeOffset eventStart)
    {
        var byLifetime = now + InviteLifetime;
        var byStart = eventStart + InviteAfterStart;
        return byStart > byLifetime ? byStart : byLifetime;
    }

    /// <summary>Test seam: runs between the read and the conditional update of <see cref="RedeemAsync"/> (token id).</summary>
    internal Func<Guid, Task>? BeforeMarkUsed { get; set; }

    /// <summary>Longer input is never a token of ours (and is not hashed).</summary>
    private const int MaxTokenLength = 64;

    /// <summary>
    /// Issues a token valid for <paramref name="lifetime"/> and returns the raw token (for the emailed link only).
    /// Every older token of the same purpose and subject, opened or not, is revoked (<see cref="LinkStatus.Superseded"/>),
    /// except for <see cref="LinkPurpose.OrganizerSignIn"/>: sign-in links are never superseded by a newer request
    /// (controller ruling P10); each lives 15 minutes and is single use.
    /// </summary>
    public async Task<string> IssueAsync(LinkPurpose purpose, Guid subjectId, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var name = purpose.ToString();

        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (purpose != LinkPurpose.OrganizerSignIn)
            await db.LinkTokens
                .Where(t => t.Purpose == name && t.SubjectId == subjectId && t.RevokedAt == null)
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
    /// Issues a six-digit <see cref="LinkPurpose.OrganizerCode"/> of <paramref name="subjectId"/>, single use, valid for
    /// <paramref name="lifetime"/>; returns the code (for the mail only). Older codes stay valid until they expire (two mails asked
    /// for in a row both work). Stored as the hash of subject and code, so a code only works for its own account. The guessing of
    /// codes is limited by the caller (<see cref="AccountRateLimiter.TryTakeCodeAttempt"/>).
    /// </summary>
    public async Task<string> IssueCodeAsync(Guid subjectId, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        string code;
        byte[] hash;
        var attempts = 0;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            hash = CodeHash(subjectId, code);
        }
        while (await db.LinkTokens.AnyAsync(t => t.TokenHash == hash, ct) && ++attempts < 10); // the same code of the same account again
        db.LinkTokens.Add(new LinkTokenEntity
        {
            Id = Guid.CreateVersion7(now), Purpose = nameof(LinkPurpose.OrganizerCode), SubjectId = subjectId, TokenHash = hash,
            CreatedAt = now, ExpiresAt = now + lifetime,
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>Redeems a code of <paramref name="subjectId"/> (single use); anything but six digits is <see cref="LinkStatus.Unknown"/>.</summary>
    public Task<LinkResult> RedeemCodeAsync(Guid subjectId, string? code, CancellationToken ct)
    {
        code = (code ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return Task.FromResult(LinkResult.Unknown);
        return RedeemHashAsync(LinkPurpose.OrganizerCode, CodeHash(subjectId, code), singleUse: true, ct);
    }

    private byte[] CodeHash(Guid subjectId, string code) =>
        HMACSHA256.HashData(_codeKey, Encoding.UTF8.GetBytes($"code:{subjectId:N}:{code}"));

    /// <summary>
    /// Revokes every live code of <paramref name="subjectId"/> (too many wrong guesses): the account has to ask for a new mail,
    /// which is rate limited, so guessing is bounded by the mails, not by an in-memory counter.
    /// </summary>
    public Task<int> RevokeCodesAsync(Guid subjectId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var purpose = nameof(LinkPurpose.OrganizerCode);
        return db.LinkTokens
            .Where(t => t.SubjectId == subjectId && t.Purpose == purpose && t.RevokedAt == null && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
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
        return await RedeemHashAsync(purpose, Hash(rawToken), singleUse, ct);
    }

    private async Task<LinkResult> RedeemHashAsync(LinkPurpose purpose, byte[] hash, bool singleUse, CancellationToken ct)
    {
        var name = purpose.ToString();
        var token = await db.LinkTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.Purpose != name) return LinkResult.Unknown;

        var now = clock.GetUtcNow();
        if (Classify(token, singleUse, now) is { } failed) return failed;

        if (!singleUse && token.UsedAt is not null) return new LinkResult(LinkStatus.Ok, token.SubjectId); // reopened
        if (BeforeMarkUsed is { } hook) await hook(token.Id);
        var marked = await db.LinkTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null && t.ExpiresAt > now && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (marked == 1) return new LinkResult(LinkStatus.Ok, token.SubjectId);

        // The conditional update lost a race (used, superseded meanwhile): report the token's real state now. A
        // reusable token that another request just opened is still fine.
        var current = await db.LinkTokens.AsNoTracking().SingleAsync(t => t.Id == token.Id, ct);
        return Classify(current, singleUse, now)
            ?? new LinkResult(singleUse ? LinkStatus.Used : LinkStatus.Ok, token.SubjectId);
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
