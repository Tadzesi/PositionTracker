using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.OwnTracks;
using PositionTracker.Api.Persistence;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Participants;

/// <summary>
/// What a participant does through the invitation link (scoped, on the request's <see cref="AppDbContext"/>). The link
/// token is the only credential: <see cref="LinkTokenService"/> maps it to the participant (unknown 404
/// <c>link_invalid</c>, superseded 410 <c>link_superseded</c>, expired 400 <c>link_expired</c>). The link is reusable
/// until it expires or a newer one replaces it.
/// <para><b>Serialization:</b> every write runs in a transaction that first locks the event row (<c>FOR UPDATE</c>), like
/// the organizer's registration writes and going live, so a confirmation and the go-live of the event never miss each
/// other: a confirmation committed before go-live is in the new runtime, one that sees <c>Live</c> adds the participant
/// to the registered runtime itself (after the commit).</para>
/// <para>Nothing personal and no token is logged.</para>
/// </summary>
public sealed class ParticipantService(AppDbContext db, LinkTokenService links, ITokenService deviceTokens, EventRegistry registry, TimeProvider clock)
{
    public const int MaxNicknameLength = 30;

    /// <summary>The version of the consent text the participant saw; stored with every consent.</summary>
    public const string ConsentTextVersion = "2026-10-31.1";

    // ----- view -----

    public async Task<EventResult<LinkView>> GetAsync(string linkToken, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;
        var p = await db.Participants.AsNoTracking().Include(x => x.Category).SingleOrDefaultAsync(x => x.Id == participantId, ct);
        var ev = p is null ? null : await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == p.EventId, ct);
        if (p is null || ev is null) return ParticipantErrors.LinkInvalid;
        return new EventResult<LinkView>(new LinkView(
            new LinkEvent(ev.Name, ev.Location, UtcIso(ev.StartTime), ev.Sport.ToString().ToLowerInvariant()),
            new LinkMe(p.Name, p.Bib, p.Category?.Name, p.Gender, p.Nickname, p.Status.ToString(),
                new LinkLocked(Started(ev))),
            p.Language), null);
    }

    // ----- confirm, decline, profile -----

    public async Task<EventResult<StatusAnswer>> ConfirmAsync(string linkToken, ProfileRequest request, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;
        var (nickname, gender, invalid) = ParseProfile(request);
        if (invalid is not null) return invalid;

        await using var locked = await LockAsync(participantId, ct);
        if (locked is null) return ParticipantErrors.LinkInvalid;
        var (ev, p) = (locked.Event, locked.Participant);
        if (ev.State == EventState.Finished) return ParticipantErrors.EventFinished;
        if (ApplyProfile(ev, p, request, nickname, gender) is { } refused) return refused;
        p.Status = ParticipantStatus.Confirmed;
        await db.SaveChangesAsync(ct);
        await locked.Transaction.CommitAsync(ct);
        SyncRuntime(ev, p);
        return Ok(new StatusAnswer(p.Status.ToString()));
    }

    public async Task<EventResult<StatusAnswer>> DeclineAsync(string linkToken, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;

        await using var locked = await LockAsync(participantId, ct);
        if (locked is null) return ParticipantErrors.LinkInvalid;
        var (ev, p) = (locked.Event, locked.Participant);
        if (ev.State == EventState.Finished) return ParticipantErrors.EventFinished;
        if (p.Status == ParticipantStatus.Declined) return Ok(new StatusAnswer(p.Status.ToString())); // idempotent
        if (Started(ev)) return ParticipantErrors.DeclineLocked;

        p.Status = ParticipantStatus.Declined;
        await deviceTokens.RevokeAsync(RuntimeFactory.RuntimeId(ev.Id), Id(p.Id), ct);
        await db.SaveChangesAsync(ct);
        await locked.Transaction.CommitAsync(ct);
        SyncRuntime(ev, p);
        return Ok(new StatusAnswer(p.Status.ToString()));
    }

    public async Task<EventResult<StatusAnswer>> UpdateProfileAsync(string linkToken, ProfileRequest request, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;
        var (nickname, gender, invalid) = ParseProfile(request);
        if (invalid is not null) return invalid;

        await using var locked = await LockAsync(participantId, ct);
        if (locked is null) return ParticipantErrors.LinkInvalid;
        var (ev, p) = (locked.Event, locked.Participant);
        if (ev.State == EventState.Finished) return ParticipantErrors.EventFinished;
        if (ApplyProfile(ev, p, request, nickname, gender) is { } refused) return refused;
        await db.SaveChangesAsync(ct);
        await locked.Transaction.CommitAsync(ct);
        SyncRuntime(ev, p);
        return Ok(new StatusAnswer(p.Status.ToString()));
    }

    // ----- consent and device token -----

    /// <summary>
    /// Stores the consent (time, text version) and issues the device token, which revokes the earlier one; the raw
    /// token is in this answer only. Needs a confirmed participant, <c>locationConsent:true</c>, and for a minor
    /// <c>showNameToSpectators:false</c>.
    /// </summary>
    public async Task<EventResult<ConsentAnswer>> ConsentAsync(string linkToken, ConsentRequest request, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;

        await using var locked = await LockAsync(participantId, ct);
        if (locked is null) return ParticipantErrors.LinkInvalid;
        var (ev, p) = (locked.Event, locked.Participant);
        if (ev.State == EventState.Finished) return ParticipantErrors.EventFinished;
        if (p.Status != ParticipantStatus.Confirmed) return ParticipantErrors.NotConfirmed;
        if (request.LocationConsent != true) return ParticipantErrors.LocationConsentRequired;
        var show = request.ShowNameToSpectators ?? false;
        if (show && p.IsMinor) return ParticipantErrors.MinorNameHidden;

        db.Consents.Add(new ConsentEntity
        {
            ParticipantId = p.Id, LocationConsent = true, ShowNameToSpectators = show, TextVersion = ConsentTextVersion,
            Time = clock.GetUtcNow(),
        });
        p.ShowNameToSpectators = show;
        await db.SaveChangesAsync(ct);
        var token = await deviceTokens.IssueAsync(RuntimeFactory.RuntimeId(ev.Id), Id(p.Id), ct);
        await locked.Transaction.CommitAsync(ct);
        SyncRuntime(ev, p);
        return Ok(new ConsentAnswer(token, RuntimeFactory.RuntimeId(ev.Id)));
    }

    // ----- OwnTracks setup -----

    /// <summary>
    /// The OwnTracks configuration of a participant who has consented. The raw device token is not stored, so the page
    /// sends the token it got from <c>consent</c>; it must be the participant's current one (else 409
    /// <c>consent_required</c>). Nothing new is issued.
    /// </summary>
    public async Task<EventResult<OwnTracksSetup>> OwnTracksConfigAsync(string linkToken, OwnTracksConfigRequest request, string baseUrl, CancellationToken ct)
    {
        var (participantId, error) = await RedeemAsync(linkToken, ct);
        if (error is not null) return error;
        var p = await db.Participants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == participantId, ct);
        var ev = p is null ? null : await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == p.EventId, ct);
        if (p is null || ev is null) return ParticipantErrors.LinkInvalid;
        if (ev.State == EventState.Finished) return ParticipantErrors.EventFinished;
        if (p.Status != ParticipantStatus.Confirmed) return ParticipantErrors.NotConfirmed;
        var resolved = string.IsNullOrEmpty(request.DeviceToken) ? null : await deviceTokens.ResolveAsync(request.DeviceToken, ct);
        if (resolved is not { } r || r.EventId != RuntimeFactory.RuntimeId(ev.Id) || r.ParticipantId != Id(p.Id))
            return ParticipantErrors.ConsentRequired;
        var config = OwnTracksConfigBuilder.Build(baseUrl, p.Bib, request.DeviceToken!, OwnTracksConfigBuilder.TrackerId(p.Bib), EventTuning.For(ev.Sport));
        return Ok(new OwnTracksSetup(config.DeepLink, config.OtrcJson, config.QrPayload));
    }

    // ----- helpers -----

    private async Task<(Guid Id, EventError? Error)> RedeemAsync(string linkToken, CancellationToken ct)
    {
        var result = await links.RedeemAsync(LinkPurpose.ParticipantInvite, linkToken, singleUse: false, ct);
        return result.Status switch
        {
            LinkStatus.Ok => (result.SubjectId, null),
            LinkStatus.Superseded => (Guid.Empty, ParticipantErrors.LinkSuperseded),
            LinkStatus.Expired => (Guid.Empty, ParticipantErrors.LinkExpired),
            _ => (Guid.Empty, ParticipantErrors.LinkInvalid),
        };
    }

    /// <summary>Starts the transaction, locks the participant's event row and loads the participant; null when either is gone.</summary>
    private async Task<Locked?> LockAsync(Guid participantId, CancellationToken ct)
    {
        var eventId = await db.Participants.AsNoTracking().Where(x => x.Id == participantId)
            .Select(x => (Guid?)x.EventId).SingleOrDefaultAsync(ct);
        if (eventId is not { } id) return null;
        var tx = await db.Database.BeginTransactionAsync(ct);
        var ev = await db.Events.FromSql($"""SELECT * FROM "Events" WHERE "Id" = {id} FOR UPDATE""").AsNoTracking().SingleOrDefaultAsync(ct);
        var p = ev is null ? null
            : await db.Participants.Include(x => x.Category).SingleOrDefaultAsync(x => x.Id == participantId && x.EventId == id, ct);
        if (ev is null || p is null)
        {
            await tx.DisposeAsync();
            return null;
        }
        return new Locked(tx, ev, p);
    }

    private static (string? Nickname, string? Gender, EventError? Error) ParseProfile(ProfileRequest request)
    {
        string? nickname = null;
        if (request.Nickname is not null && !string.IsNullOrWhiteSpace(request.Nickname)
            && !TextRules.TryLine(request.Nickname, MaxNicknameLength, out nickname))
            return (null, null, ParticipantErrors.InvalidNickname);
        string? gender = null;
        if (request.Gender is not null && (gender = ParseGender(request.Gender)) is null)
            return (null, null, ParticipantErrors.InvalidGender);
        return (nickname, gender, null);
    }

    /// <summary>Applies nickname and gender; a changed gender after the start is 409 <c>gender_locked</c> (nothing is applied then).</summary>
    private EventError? ApplyProfile(EventEntity ev, ParticipantEntity p, ProfileRequest request, string? nickname, string? gender)
    {
        if (gender is not null && gender != p.Gender && Started(ev)) return ParticipantErrors.GenderLocked;
        if (gender is not null) p.Gender = gender;
        if (request.Nickname is not null) p.Nickname = nickname; // empty clears
        return null;
    }

    private bool Started(EventEntity ev) => clock.GetUtcNow() >= ev.StartTime;

    /// <summary>
    /// Keeps the runtime of a live event consistent with the committed row: a confirmed participant is added or updated;
    /// a declined one (only possible before the start) is hidden from the spectators and marked DNF in memory.
    /// </summary>
    private void SyncRuntime(EventEntity ev, ParticipantEntity p)
    {
        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        if (registry.Get(runtimeId) is not { } runtime || registry.IsFinished(runtimeId)) return;
        var id = Id(p.Id);
        try
        {
            if (p.Status == ParticipantStatus.Confirmed)
            {
                var info = RuntimeFactory.ParticipantInfoOf(p);
                if (runtime.HasParticipant(id)) runtime.UpdateParticipant(info);
                else runtime.AddParticipant(info);
            }
            else if (runtime.HasParticipant(id))
            {
                runtime.UpdateParticipant(RuntimeFactory.ParticipantInfoOf(p) with { ShowNameToSpectators = false });
                runtime.Act(id, ParticipantAction.Dnf);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            // Raced with another change of the same participant: the row is the truth, the runtime catches up at restart.
        }
    }

    private static string? ParseGender(string value) => value.Trim().ToLowerInvariant() switch
    {
        "female" => "female",
        "male" => "male",
        _ => null,
    };

    private static string UtcIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Id(Guid id) => id.ToString("D");

    private static EventResult<T> Ok<T>(T value) => new(value, null);

    private sealed class Locked(IDbContextTransaction transaction, EventEntity ev, ParticipantEntity participant) : IAsyncDisposable
    {
        public IDbContextTransaction Transaction { get; } = transaction;
        public EventEntity Event { get; } = ev;
        public ParticipantEntity Participant { get; } = participant;

        public ValueTask DisposeAsync() => Transaction.DisposeAsync();
    }
}

public sealed record ProfileRequest(string? Nickname, string? Gender);

public sealed record ConsentRequest(bool? LocationConsent, bool? ShowNameToSpectators);

public sealed record OwnTracksConfigRequest(string? DeviceToken);

public sealed record OwnTracksSetup(string DeepLink, System.Text.Json.Nodes.JsonObject OtrcJson, string QrPayload);

public sealed record StatusAnswer(string Status);

/// <param name="DeviceToken">The raw device token: returned this once, never again.</param>
public sealed record ConsentAnswer(string DeviceToken, string EventId);

public sealed record LinkEvent(string Name, string Location, string StartTime, string Sport);

public sealed record LinkLocked(bool Gender);

public sealed record LinkMe(string Name, string Bib, string? Category, string Gender, string? Nickname, string Status, LinkLocked Locked);

public sealed record LinkView(LinkEvent Event, LinkMe Me, string Language);

/// <summary>The errors of the participant link API (shape and helper of <see cref="EventErrors"/>).</summary>
public static class ParticipantErrors
{
    public static readonly EventError LinkInvalid = new(StatusCodes.Status404NotFound, "link_invalid",
        "Tento odkaz neexistuje. Skontrolujte, prosím, odkaz v e-maile.", "This link does not exist. Please check the link in your email.");

    public static readonly EventError LinkSuperseded = new(StatusCodes.Status410Gone, "link_superseded",
        "Tento odkaz nahradil novší. Použite, prosím, odkaz z najnovšieho e-mailu.",
        "A newer link has replaced this one. Please use the link from the latest email.");

    public static readonly EventError LinkExpired = new(StatusCodes.Status400BadRequest, "link_expired",
        "Platnosť odkazu vypršala. Požiadajte organizátora o nový odkaz.", "This link has expired. Please ask the organizer for a new link.");

    public static readonly EventError EventFinished = new(StatusCodes.Status409Conflict, "event_finished",
        "Podujatie už skončilo; zmeny už nie sú možné.", "The event has finished; changes are no longer possible.");

    public static readonly EventError NotConfirmed = new(StatusCodes.Status409Conflict, "not_confirmed",
        "Najprv potvrďte účasť.", "Please confirm your participation first.");

    public static readonly EventError ConsentRequired = new(StatusCodes.Status409Conflict, "consent_required",
        "Najprv udeľte súhlas so zdieľaním polohy; nastavenie OwnTracks potrebuje token zariadenia z tohto kroku.",
        "Please give your consent to share your location first; the OwnTracks setup needs the device token from that step.", "deviceToken");

    public static readonly EventError DeclineLocked = new(StatusCodes.Status409Conflict, "decline_locked",
        "Podujatie už začalo; účasť už nemožno odmietnuť. Kontaktujte organizátora.",
        "The event has started; participation can no longer be declined. Please contact the organizer.");

    public static readonly EventError GenderLocked = new(StatusCodes.Status409Conflict, "gender_locked",
        "Podujatie už začalo; pohlavie už nemožno zmeniť. Kontaktujte organizátora.",
        "The event has started; the gender can no longer be changed. Please contact the organizer.", "gender");

    public static readonly EventError MinorNameHidden = new(StatusCodes.Status409Conflict, "minor_name_hidden",
        "Meno maloletého účastníka sa divákom nezobrazuje.", "The name of a minor is not shown to spectators.", "showNameToSpectators");

    public static readonly EventError InvalidNickname = new(StatusCodes.Status400BadRequest, "validation_failed",
        "Prezývka má najviac 30 znakov, bez riadiacich a smerových znakov.",
        "A nickname has at most 30 characters, no control or bidirectional formatting characters.", "nickname");

    public static readonly EventError InvalidGender = new(StatusCodes.Status400BadRequest, "validation_failed",
        "Pohlavie musí byť „female“ alebo „male“.", "The gender must be \"female\" or \"male\".", "gender");

    public static readonly EventError LocationConsentRequired = new(StatusCodes.Status400BadRequest, "validation_failed",
        "Bez súhlasu so zdieľaním polohy nemožno sledovanie spustiť.", "Tracking cannot start without consent to share your location.",
        "locationConsent");

    public static readonly EventError RateLimited = new(StatusCodes.Status429TooManyRequests, "rate_limited",
        "Príliš veľa požiadaviek. Skúste to, prosím, o minútu.", "Too many requests. Please try again in a minute.");
}
