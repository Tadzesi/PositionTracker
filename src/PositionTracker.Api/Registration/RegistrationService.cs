using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Email;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Registration;

/// <summary>
/// Participant registration by the organizer (scoped, on the request's <see cref="AppDbContext"/>). Every operation is
/// limited to the events of <c>organizerId</c>: another organizer's event or participant, an unknown id, a
/// non-canonical id and a simulated event are all 404 <c>not_found</c>.
/// <para><b>Serialization:</b> every write runs in a transaction that first locks the event row
/// (<c>SELECT ... FOR UPDATE</c>), and so does going live (<see cref="EventService"/>), so concurrent registrations of
/// one event and its go-live are serialized: the next free bib (highest numeric bib + 1), the duplicate checks, the
/// participant cap and the participant set of a new runtime are decided on a stable set, and a write after go-live sees
/// <c>Live</c>. The unique indexes (event + normalized email, event + bib) stay the last line of defence and map, by
/// constraint name, to the same 409s.</para>
/// <para><b>Mail:</b> every invitation (create, email change, resend, re-issue, reminder) issues a new
/// <see cref="LinkPurpose.ParticipantInvite"/> link that supersedes the previous one, withdraws the participant's
/// invitations that were never attempted, and queues the mail in the participant's language to the delivery address,
/// all in the transaction of the change. It takes one unit of the <see cref="InvitationLimiter"/> budgets first
/// (given back when the change does not commit); over a budget the transaction is rolled back and nothing changes
/// (429 <c>rate_limited</c>), except that a re-issue still revokes the device token (and answers
/// <c>mailQueued:false</c>).</para>
/// <para>Participants registered while the event is live are not added to the runtime here: they join it when they
/// confirm (Task 7). Edits of a participant who is in the runtime update it (<c>UpdateParticipant</c>) after the
/// commit. Nothing personal and no token is logged.</para>
/// </summary>
public sealed class RegistrationService(
    AppDbContext db, LinkTokenService links, ITokenService deviceTokens, InvitationLimiter limiter, EventRegistry registry,
    IEmailMode emailMode, IOptions<EmailOptions> email, IOptions<RegistrationOptions> options, TimeProvider clock)
{
    public const int MaxNameLength = 200;
    public const int MaxNicknameLength = 30;
    public const int MaxBibLength = 16;
    public const int MaxQueryLength = 200;
    /// <summary>Reminders per transaction: a failing commit gives back at most this many budget units' worth of work.</summary>
    public const int RemindChunk = 50;
    private const string BibIndex = "IX_Participants_EventId_Bib";
    private const string EmailIndex = "IX_Participants_EventId_EmailNormalized";
    private static readonly string InvitePurpose = LinkPurpose.ParticipantInvite.ToString();

    // ----- create -----

    public async Task<EventResult<CreatedParticipant>> CreateAsync(Guid organizerId, string eventId, CreateParticipantRequest request, CancellationToken ct)
    {
        if (!TextRules.TryLine(request.Name, MaxNameLength, out var name)) return RegistrationErrors.InvalidName;
        if (!EmailAddress.TryParse(request.Email, out var address, out var normalized)) return RegistrationErrors.InvalidEmail;
        if (ParseGender(request.Gender) is not { } gender) return RegistrationErrors.InvalidGender;
        if (ParseLanguage(request.Language) is not { } language) return RegistrationErrors.InvalidLanguage;
        string? bib = null; // empty or absent: the next free number
        if (!string.IsNullOrWhiteSpace(request.Bib) && (bib = ParseBib(request.Bib)) is null) return RegistrationErrors.InvalidBib;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockEventAsync(organizerId, eventId, ct) is not { } ev) return EventErrors.NotFound;
        if (ev.State == EventState.Finished) return RegistrationErrors.EventFinished;
        var (category, categoryError) = await CategoryAsync(ev.Id, request.Category, ct);
        if (categoryError is not null) return categoryError;
        var (wave, waveError) = await WaveAsync(ev.Id, request.Wave, ct);
        if (waveError is not null) return waveError;
        var max = options.Value.MaxParticipantsPerEvent;
        if (await db.Participants.CountAsync(p => p.EventId == ev.Id, ct) >= max) return RegistrationErrors.ParticipantLimit(max);
        if (await db.Participants.AnyAsync(p => p.EventId == ev.Id && p.EmailNormalized == normalized, ct))
            return RegistrationErrors.EmailDuplicate;
        if (bib is not null && await db.Participants.AnyAsync(p => p.EventId == ev.Id && p.Bib == bib, ct))
            return RegistrationErrors.BibTaken;
        bib ??= await NextBibAsync(ev.Id, ct);

        var now = clock.GetUtcNow();
        var participant = new ParticipantEntity
        {
            Id = Guid.CreateVersion7(now), EventId = ev.Id, Bib = bib, Name = name, Email = address, EmailNormalized = normalized,
            Gender = gender, Language = language, CategoryId = category?.Id, WaveId = wave?.Id, Status = ParticipantStatus.Invited,
            CreatedAt = now,
        };
        var error = await WithBudgetAsync(organizerId, normalized, () => SaveAsync(async () =>
        {
            db.Participants.Add(participant);
            await db.SaveChangesAsync(ct);
            await QueueInvitationAsync(participant, ev, await OrganizerAsync(organizerId, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }));
        if (error is not null) return error;
        return Ok(new CreatedParticipant(Id(participant.Id), participant.Bib, participant.Status.ToString(), emailMode.Mode));
    }

    // ----- list -----

    public async Task<EventResult<ParticipantList>> ListAsync(Guid organizerId, string eventId, string? status, string? category,
        string? q, CancellationToken ct)
    {
        ParticipantStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (ParseStatus(status) is not { } parsed) return RegistrationErrors.InvalidStatus;
            statusFilter = parsed;
        }
        var text = (q ?? "").Trim();
        if (text.Length > MaxQueryLength) return RegistrationErrors.InvalidQuery;
        if (await FindEventAsync(organizerId, eventId, ct) is not { } ev) return EventErrors.NotFound;

        var counts = await db.Participants.AsNoTracking().Where(p => p.EventId == ev.Id)
            .GroupBy(p => p.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(ParticipantStatus s) => counts.SingleOrDefault(c => c.Status == s)?.Count ?? 0;

        var query = db.Participants.AsNoTracking().Include(p => p.Category).Where(p => p.EventId == ev.Id);
        if (statusFilter is { } st) query = query.Where(p => p.Status == st);
        if (!string.IsNullOrWhiteSpace(category))
        {
            var name = category.Trim().ToLowerInvariant();
            query = query.Where(p => p.Category != null && p.Category.Name.ToLower() == name);
        }
        if (text.Length > 0)
        {
            var lower = text.ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.EmailNormalized.Contains(lower)
                || p.Bib.ToLower().Contains(lower) || (p.Nickname != null && p.Nickname.ToLower().Contains(lower)));
        }
        var rows = await query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct);
        var waves = await WaveNamesAsync(ev.Id, ct);
        return Ok(new ParticipantList(emailMode.Mode, ev.State,
            new StatusCounts(counts.Sum(c => c.Count), Count(ParticipantStatus.Confirmed), Count(ParticipantStatus.Invited),
                Count(ParticipantStatus.Declined), Count(ParticipantStatus.EmailFailed)),
            rows.Select(p => View(p, waves)).ToList()));
    }

    // ----- edit -----

    public async Task<EventResult<ParticipantView>> UpdateAsync(Guid organizerId, string eventId, string participantId,
        UpdateParticipantRequest request, CancellationToken ct)
    {
        string? name = null, address = null, normalized = null, gender = null, language = null, bib = null, nickname = null;
        if (request.Name is not null && !TextRules.TryLine(request.Name, MaxNameLength, out name)) return RegistrationErrors.InvalidName;
        if (request.Email is not null && !EmailAddress.TryParse(request.Email, out address, out normalized)) return RegistrationErrors.InvalidEmail;
        if (request.Gender is not null && (gender = ParseGender(request.Gender)) is null) return RegistrationErrors.InvalidGender;
        if (request.Language is not null && (language = ParseLanguage(request.Language)) is null) return RegistrationErrors.InvalidLanguage;
        // Empty or absent: the bib stays.
        if (!string.IsNullOrWhiteSpace(request.Bib) && (bib = ParseBib(request.Bib)) is null) return RegistrationErrors.InvalidBib;
        if (!string.IsNullOrWhiteSpace(request.Nickname) && !TextRules.TryLine(request.Nickname, MaxNicknameLength, out nickname))
            return RegistrationErrors.InvalidNickname;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockEventAsync(organizerId, eventId, ct) is not { } ev) return RegistrationErrors.ParticipantNotFound;
        if (await FindParticipantAsync(ev.Id, participantId, ct) is not { } p) return RegistrationErrors.ParticipantNotFound;
        if (ev.State == EventState.Finished) return RegistrationErrors.EventFinished;

        if (request.Category is not null)
        {
            var (category, error) = await CategoryAsync(ev.Id, request.Category, ct);
            if (error is not null) return error;
            p.CategoryId = category?.Id;
            p.Category = category;
        }
        if (request.Wave is not null)
        {
            var (wave, error) = await WaveAsync(ev.Id, request.Wave, ct);
            if (error is not null) return error;
            p.WaveId = wave?.Id;
        }
        if (bib is not null && bib != p.Bib)
        {
            if (await db.Participants.AnyAsync(x => x.EventId == ev.Id && x.Id != p.Id && x.Bib == bib, ct)) return RegistrationErrors.BibTaken;
            p.Bib = bib;
        }
        if (name is not null) p.Name = name;
        if (gender is not null) p.Gender = gender;
        if (language is not null) p.Language = language;
        if (request.Nickname is not null) p.Nickname = nickname;
        if (request.HideNickname is { } hide) p.NicknameHidden = hide;

        var mail = false;
        if (normalized is not null && normalized != p.EmailNormalized)
        {
            if (await db.Participants.AnyAsync(x => x.EventId == ev.Id && x.Id != p.Id && x.EmailNormalized == normalized, ct))
                return RegistrationErrors.EmailDuplicate;
            p.EmailNormalized = normalized;
            if (p.Status == ParticipantStatus.EmailFailed) p.Status = ParticipantStatus.Invited;
            mail = true;
        }
        if (address is not null) p.Email = address; // a new display form of the same mailbox needs no new link

        Task<EventError?> Save() => SaveAsync(async () =>
        {
            await db.SaveChangesAsync(ct);
            if (mail) await QueueInvitationAsync(p, ev, await OrganizerAsync(organizerId, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        var saveError = mail ? await WithBudgetAsync(organizerId, p.EmailNormalized, Save) : await Save();
        if (saveError is not null) return saveError;

        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        if (registry.Get(runtimeId) is { } runtime && !registry.IsFinished(runtimeId) && runtime.HasParticipant(Id(p.Id)))
        {
            try
            {
                runtime.UpdateParticipant(RuntimeFactory.ParticipantInfoOf(p, await RuntimeFactory.WaveStartAsync(db, p, ct)));
            }
            catch (KeyNotFoundException)
            {
                // Removed meanwhile: nothing to update.
            }
        }
        return Ok(View(p, await WaveNamesAsync(ev.Id, ct)) with { EmailMode = emailMode.Mode });
    }

    // ----- delete -----

    public async Task<EventError?> DeleteAsync(Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockEventAsync(organizerId, eventId, ct) is not { } ev) return RegistrationErrors.ParticipantNotFound;
        if (await FindParticipantAsync(ev.Id, participantId, ct) is not { } p) return RegistrationErrors.ParticipantNotFound;
        if (ev.State == EventState.Live) return RegistrationErrors.EventLive;
        if (ev.State == EventState.Finished) return RegistrationErrors.EventFinished;
        await WithdrawUnsentInvitationsAsync(p.Id, ct);
        await db.LinkTokens.Where(t => t.Purpose == InvitePurpose && t.SubjectId == p.Id).ExecuteDeleteAsync(ct);
        db.Participants.Remove(p); // device tokens, consents and history go with it (cascade)
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // ----- mail -----

    /// <summary>
    /// Mails a new link (the old one is superseded); an <c>EmailFailed</c> participant becomes <c>Invited</c>. A declined
    /// participant is 409 <c>participant_declined</c>.
    /// </summary>
    public async Task<EventResult<MailQueued>> ResendAsync(Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (ev, p, refused) = await MailTargetAsync(organizerId, eventId, participantId, ct);
        if (refused is not null) return refused;
        var error = await WithBudgetAsync(organizerId, p!.EmailNormalized, async () =>
        {
            if (p.Status == ParticipantStatus.EmailFailed) p.Status = ParticipantStatus.Invited;
            await QueueInvitationAsync(p, ev!, await OrganizerAsync(organizerId, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        });
        return error ?? Ok(new MailQueued(emailMode.Mode));
    }

    /// <summary>
    /// Revokes the participant's device token at once, always, and mails a new link, from which the participant gets a
    /// new device token after consent (Task 7). Over an invitation limit the revocation is still committed and the
    /// answer says <c>mailQueued:false</c> (resend later). The answer never holds a token.
    /// </summary>
    public async Task<EventResult<TokenReissued>> ReissueTokenAsync(Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (ev, p, refused) = await MailTargetAsync(organizerId, eventId, participantId, ct);
        if (refused is not null) return refused;
        await deviceTokens.RevokeAsync(RuntimeFactory.RuntimeId(ev!.Id), Id(p!.Id), ct);
        if (!limiter.TryTake(organizerId, p.EmailNormalized))
        {
            await tx.CommitAsync(ct);
            return Ok(new TokenReissued(true, false, emailMode.Mode));
        }
        try
        {
            if (p.Status == ParticipantStatus.EmailFailed) p.Status = ParticipantStatus.Invited;
            await QueueInvitationAsync(p, ev, await OrganizerAsync(organizerId, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            limiter.Refund(organizerId, p.EmailNormalized);
            throw;
        }
        return Ok(new TokenReissued(true, true, emailMode.Mode));
    }

    /// <summary>
    /// Mails a new link to every <c>Invited</c> participant who is due, as far as the invitation budgets allow. With a
    /// confirmation deadline (ruling P12): 409 <c>remind_too_early</c> before it; after it, everyone whose current link
    /// was issued before the deadline (so once each). Without one: everyone whose current link is older than
    /// <see cref="RegistrationOptions.RemindMinAgeHours"/>. Works in transactions of <see cref="RemindChunk"/>
    /// participants (each re-locking the event), so a failing commit loses only its own chunk and gives its budget units
    /// back.
    /// </summary>
    public async Task<EventResult<Reminded>> RemindAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (await FindEventAsync(organizerId, eventId, ct) is not { } found) return EventErrors.NotFound;
        if (found.State == EventState.Finished) return RegistrationErrors.EventFinished;
        var now = clock.GetUtcNow();
        DateTimeOffset issuedBefore;
        if (found.ConfirmationDeadline is { } deadline)
        {
            if (now < deadline) return RegistrationErrors.RemindTooEarly;
            issuedBefore = deadline;
        }
        else
        {
            issuedBefore = now - TimeSpan.FromHours(options.Value.RemindMinAgeHours);
        }
        var organizer = await OrganizerAsync(organizerId, ct);

        var seen = new List<Guid>();
        int sent = 0, limited = 0;
        while (true)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await LockEventAsync(organizerId, eventId, ct) is not { State: not EventState.Finished } ev) break;
            var batch = await db.Participants
                .Where(p => p.EventId == ev.Id && p.Status == ParticipantStatus.Invited && !seen.Contains(p.Id)
                    && !db.LinkTokens.Any(t => t.Purpose == InvitePurpose && t.SubjectId == p.Id && t.RevokedAt == null && t.CreatedAt > issuedBefore))
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .Take(RemindChunk)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            var taken = new List<string>();
            try
            {
                foreach (var p in batch)
                {
                    seen.Add(p.Id);
                    if (!limiter.TryTake(organizerId, p.EmailNormalized))
                    {
                        limited++;
                        continue;
                    }
                    taken.Add(p.EmailNormalized);
                    await QueueInvitationAsync(p, ev, organizer, ct);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                foreach (var address in taken) limiter.Refund(organizerId, address);
                throw;
            }
            sent += taken.Count;
            db.ChangeTracker.Clear();
        }
        return Ok(new Reminded(sent, limited, emailMode.Mode));
    }

    /// <summary>The locked event and the participant to mail, or the refusal (404, finished, declined).</summary>
    private async Task<(EventEntity? Event, ParticipantEntity? Participant, EventError? Refused)> MailTargetAsync(
        Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        if (await LockEventAsync(organizerId, eventId, ct) is not { } ev) return (null, null, RegistrationErrors.ParticipantNotFound);
        if (await FindParticipantAsync(ev.Id, participantId, ct) is not { } p) return (null, null, RegistrationErrors.ParticipantNotFound);
        if (ev.State == EventState.Finished) return (null, null, RegistrationErrors.EventFinished);
        if (p.Status == ParticipantStatus.Declined) return (null, null, RegistrationErrors.ParticipantDeclined);
        return (ev, p, null);
    }

    /// <summary>
    /// Takes one invitation unit (429 <c>rate_limited</c> when none is left) and runs <paramref name="work"/>; the unit is
    /// given back when the work returns an error or throws (the mail was not queued).
    /// </summary>
    private async Task<EventError?> WithBudgetAsync(Guid organizerId, string normalized, Func<Task<EventError?>> work)
    {
        if (!limiter.TryTake(organizerId, normalized)) return RegistrationErrors.RateLimited;
        try
        {
            var error = await work();
            if (error is not null) limiter.Refund(organizerId, normalized);
            return error;
        }
        catch
        {
            limiter.Refund(organizerId, normalized);
            throw;
        }
    }

    /// <summary>
    /// Withdraws the participant's never-attempted invitations, issues a new link (superseding the old one) and queues
    /// the invitation in the participant's language to the delivery address (local part as typed, ASCII domain).
    /// </summary>
    private async Task QueueInvitationAsync(ParticipantEntity p, EventEntity ev, OrganizerEntity organizer, CancellationToken ct)
    {
        await WithdrawUnsentInvitationsAsync(p.Id, ct);
        var token = await links.IssueAsync(LinkPurpose.ParticipantInvite, p.Id, LinkTokenService.InviteLifetime, ct);
        var url = EmailLinks.Build(email.Value.PublicBaseUrl, options.Value.InvitePath, token);
        var message = EmailTemplates.Invitation(p, ev, organizer, url, p.Language);
        message = message with
        {
            ToAddress = EmailAddress.ToDeliveryAddress(p.Email),
            ReplyToAddress = message.ReplyToAddress is null ? null : DeliveryOrNull(organizer.Email),
        };
        db.Outbox.Add(EmailOutbox.Create(message, p.Id, clock.GetUtcNow()));
    }

    private Task<OrganizerEntity> OrganizerAsync(Guid organizerId, CancellationToken ct) =>
        db.Organizers.AsNoTracking().SingleAsync(o => o.Id == organizerId, ct);

    /// <summary>
    /// Deletes the participant's pending invitations the worker has not attempted yet (their link is superseded anyway).
    /// A row being sent right now has an attempt counted and stays; a late failure of it no longer marks the participant
    /// once the address changed (<see cref="EmailOutboxWorker"/>).
    /// </summary>
    private Task<int> WithdrawUnsentInvitationsAsync(Guid participantId, CancellationToken ct) =>
        db.Outbox
            .Where(m => m.ParticipantId == participantId && m.Purpose == EmailPurpose.Invitation
                && m.Status == EmailOutboxStatus.Pending && m.Attempts == 0)
            .ExecuteDeleteAsync(ct);

    // ----- helpers -----

    /// <summary>The organizer's event, row-locked until the transaction ends; null for anything else.</summary>
    private async Task<EventEntity?> LockEventAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (!EventService.TryParseId(eventId, out var id)) return null;
        return await db.Events
            .FromSql($"""SELECT * FROM "Events" WHERE "Id" = {id} AND "OrganizerId" = {organizerId} FOR UPDATE""")
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);
    }

    private async Task<EventEntity?> FindEventAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (!EventService.TryParseId(eventId, out var id)) return null;
        return await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId, ct);
    }

    private async Task<ParticipantEntity?> FindParticipantAsync(Guid eventId, string participantId, CancellationToken ct)
    {
        if (!EventService.TryParseId(participantId, out var id)) return null;
        return await db.Participants.Include(p => p.Category).SingleOrDefaultAsync(p => p.Id == id && p.EventId == eventId, ct);
    }

    /// <summary>The category of this name (ignoring case); null and no error for none/empty.</summary>
    private async Task<(CategoryEntity? Category, EventError? Error)> CategoryAsync(Guid eventId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        var wanted = name.Trim();
        var all = await db.Categories.Where(c => c.EventId == eventId).ToListAsync(ct);
        var match = all.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
        return match is null ? (null, RegistrationErrors.UnknownCategory) : (match, null);
    }

    private async Task<(WaveEntity? Wave, EventError? Error)> WaveAsync(Guid eventId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        var wanted = name.Trim();
        var all = await db.Waves.AsNoTracking().Where(w => w.EventId == eventId).ToListAsync(ct);
        var match = all.FirstOrDefault(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase));
        return match is null ? (null, RegistrationErrors.UnknownWave) : (match, null);
    }

    private async Task<Dictionary<Guid, string>> WaveNamesAsync(Guid eventId, CancellationToken ct) =>
        await db.Waves.AsNoTracking().Where(w => w.EventId == eventId).ToDictionaryAsync(w => w.Id, w => w.Name, ct);

    /// <summary>The highest numeric bib of the event plus one (1 for none); called with the event row locked.</summary>
    private async Task<string> NextBibAsync(Guid eventId, CancellationToken ct)
    {
        var bibs = await db.Participants.Where(p => p.EventId == eventId).Select(p => p.Bib).ToListAsync(ct);
        long max = 0;
        foreach (var b in bibs)
            if (b.Length <= 15 && b.All(char.IsAsciiDigit) && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                max = Math.Max(max, n);
        return (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Runs the saving steps; a race on the bib or email unique index maps to its 409 (others are rethrown).</summary>
    private static async Task<EventError?> SaveAsync(Func<Task> save)
    {
        try
        {
            await save();
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName is BibIndex or EmailIndex)
        {
            return pg.ConstraintName == BibIndex ? RegistrationErrors.BibTaken : RegistrationErrors.EmailDuplicate;
        }
    }

    private static ParticipantView View(ParticipantEntity p, IReadOnlyDictionary<Guid, string> waves) =>
        new(Id(p.Id), p.Bib, p.Name, p.Nickname, p.NicknameHidden, p.Email, p.Gender, p.Language, p.Category?.Name,
            p.WaveId is { } w && waves.TryGetValue(w, out var wave) ? wave : null, p.Status.ToString(), p.IsMinor, p.CreatedAt);

    private static string Id(Guid id) => id.ToString("D");

    private static EventResult<T> Ok<T>(T value) => new(value, null);

    private static string? DeliveryOrNull(string address)
    {
        try
        {
            return EmailAddress.ToDeliveryAddress(address);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ParseGender(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "female" => "female",
        "male" => "male",
        _ => null,
    };

    private static string? ParseLanguage(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "sk" => "sk",
        "en" => "en",
        _ => null,
    };

    /// <summary>1–16 ASCII letters, digits and hyphens, starting with a letter or digit; letters upper-cased.</summary>
    private static string? ParseBib(string value)
    {
        var bib = value.Trim().ToUpperInvariant();
        if (bib.Length is 0 or > MaxBibLength || !char.IsAsciiLetterOrDigit(bib[0])) return null;
        return bib.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? bib : null;
    }

    private static ParticipantStatus? ParseStatus(string value)
    {
        var trimmed = value.Trim();
        foreach (var s in Enum.GetValues<ParticipantStatus>())
            if (string.Equals(s.ToString(), trimmed, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
