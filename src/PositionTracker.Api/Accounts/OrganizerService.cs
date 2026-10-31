using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PositionTracker.Api.Email;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// Organizer sign-up, sign-in requests and email confirmation. The callers answer the same way whatever happens
/// here (no account enumeration): sign-up mails a confirmation to a new or unconfirmed account (the new link
/// supersedes the old one; an unconfirmed account takes over the new name, display address and language, since
/// nobody has proven ownership yet, while a confirmed one changes nothing) and a sign-in link to a confirmed one; a sign-in request
/// mails a sign-in link to an existing account and nothing to an unknown address. Mails are limited by
/// <see cref="AccountRateLimiter.TryTakeMail"/>. Each mail is added to the outbox in the same transaction as its link
/// token (and the new or updated organizer). Redeeming a sign-in link proves access to the mailbox and confirms the
/// email. Addresses and tokens are never logged.
/// </summary>
public sealed class OrganizerService(
    AppDbContext db, LinkTokenService links, AccountRateLimiter limiter, IOptions<EmailOptions> email,
    IOptions<AccountOptions> options, TimeProvider clock)
{
    /// <param name="email">A valid address as entered (trimmed), from <see cref="EmailAddress.TryParse"/>.</param>
    /// <param name="normalized">Its normalized form, from <see cref="EmailAddress.TryParse"/>.</param>
    /// <param name="ip">The requester (for the per-address-and-IP mail limit).</param>
    public async Task SignUpAsync(string name, string email, string normalized, string language, IPAddress? ip, CancellationToken ct)
    {
        if (!limiter.TryTakeMail(normalized, ip)) return;
        // A concurrent sign-up of the same address may insert first (unique index): then it is an existing account.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var organizer = await LockByEmailAsync(normalized, ct);
                if (organizer is null)
                {
                    var now = clock.GetUtcNow();
                    organizer = new OrganizerEntity
                    {
                        Id = Guid.CreateVersion7(now),
                        Name = name,
                        Email = email,
                        EmailNormalized = normalized,
                        Language = language,
                        CreatedAt = now,
                        SecurityStamp = Guid.NewGuid(),
                    };
                    db.Organizers.Add(organizer);
                    await db.SaveChangesAsync(ct);
                }
                else if (organizer.EmailConfirmedAt is null)
                {
                    // Nobody has proven ownership yet: the latest sign-up's details win (the normalized key is equal).
                    organizer.Name = name;
                    organizer.Email = email;
                    organizer.Language = language;
                }
                if (organizer.EmailConfirmedAt is null) await QueueConfirmationAsync(organizer, ct);
                else await QueueSignInAsync(organizer, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Mails a sign-in link to an existing (confirmed or not) account; nothing for an unknown address.</summary>
    public async Task RequestSignInAsync(string normalized, IPAddress? ip, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var organizer = await LockByEmailAsync(normalized, ct);
        if (organizer is null || !limiter.TryTakeMail(normalized, ip)) return;
        await QueueSignInAsync(organizer, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Redeems a confirmation link; on success the account's email is confirmed (idempotent). The organizer is returned
    /// (to start a session) only by the redemption that actually confirmed the address: the link is reusable, so a
    /// reopened one must not log anybody in.
    /// </summary>
    public async Task<(LinkStatus Status, OrganizerEntity? Organizer)> ConfirmEmailAsync(string token, CancellationToken ct)
    {
        var result = await links.RedeemAsync(LinkPurpose.OrganizerEmailConfirm, token, singleUse: false, ct);
        if (!result.IsOk) return (result.Status, null);
        var now = clock.GetUtcNow();
        var changed = await db.Organizers
            .Where(o => o.Id == result.SubjectId && o.EmailConfirmedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.EmailConfirmedAt, now), ct);
        if (changed == 0) return (LinkStatus.Ok, null);
        var organizer = await db.Organizers.AsNoTracking().SingleOrDefaultAsync(o => o.Id == result.SubjectId, ct);
        return (LinkStatus.Ok, organizer);
    }

    /// <summary>
    /// Redeems a single-use sign-in link; returns the organizer to start a session for. The link proves access to the
    /// mailbox, so an unconfirmed email becomes confirmed.
    /// </summary>
    public async Task<(LinkStatus Status, OrganizerEntity? Organizer)> RedeemSignInAsync(string token, CancellationToken ct)
    {
        var result = await links.RedeemAsync(LinkPurpose.OrganizerSignIn, token, singleUse: true, ct);
        if (!result.IsOk) return (result.Status, null);
        var now = clock.GetUtcNow();
        await db.Organizers
            .Where(o => o.Id == result.SubjectId && o.EmailConfirmedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.EmailConfirmedAt, now), ct);
        var organizer = await db.Organizers.AsNoTracking().SingleOrDefaultAsync(o => o.Id == result.SubjectId, ct);
        return organizer is null ? (LinkStatus.Unknown, null) : (LinkStatus.Ok, organizer);
    }

    /// <summary>Ends every session of the organizer: sessions carry the old security stamp.</summary>
    public Task SignOutEverywhereAsync(Guid organizerId, CancellationToken ct) =>
        db.Organizers.Where(o => o.Id == organizerId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.SecurityStamp, Guid.NewGuid()), ct);

    /// <summary>The account with this address, row-locked until the transaction ends (serializes mails per account).</summary>
    private Task<OrganizerEntity?> LockByEmailAsync(string normalized, CancellationToken ct) =>
        db.Organizers
            .FromSql($"""SELECT * FROM "Organizers" WHERE "EmailNormalized" = {normalized} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);

    private async Task QueueConfirmationAsync(OrganizerEntity organizer, CancellationToken ct)
    {
        var token = await links.IssueAsync(LinkPurpose.OrganizerEmailConfirm, organizer.Id, LinkTokenService.EmailConfirmLifetime, ct);
        Enqueue(organizer, EmailTemplates.ConfirmOrganizerEmail(organizer, Url(options.Value.ConfirmPath, token)));
    }

    private async Task QueueSignInAsync(OrganizerEntity organizer, CancellationToken ct)
    {
        var token = await links.IssueAsync(LinkPurpose.OrganizerSignIn, organizer.Id, LinkTokenService.SignInLifetime, ct);
        Enqueue(organizer, EmailTemplates.SignIn(organizer, Url(options.Value.SignInPath, token)));
    }

    private string Url(string path, string token) => EmailLinks.Build(email.Value.PublicBaseUrl, path, token);

    /// <summary>Queues the mail to the account's delivery address (local part as entered, ASCII domain).</summary>
    private void Enqueue(OrganizerEntity organizer, EmailMessage message) =>
        db.Outbox.Add(EmailOutbox.Create(message with { ToAddress = EmailAddress.ToDeliveryAddress(organizer.Email) }, null, clock.GetUtcNow()));
}
