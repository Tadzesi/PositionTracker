using System.Text.Json.Serialization;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Registration;

// Requests (strings validated by hand, so the error names the field). Category and wave are given by name (unique per
// event, ignoring case).

public sealed record CreateParticipantRequest(
    string? Name, string? Email, string? Gender, string? Language, string? Category, string? Wave, string? Bib);

/// <summary>Null (absent) keeps a value; an empty <c>category</c>, <c>wave</c> or <c>nickname</c> clears it.</summary>
public sealed record UpdateParticipantRequest(
    string? Name, string? Email, string? Gender, string? Language, string? Category, string? Wave, string? Bib,
    string? Nickname, bool? HideNickname);

// Responses. Status values are the stored names: Invited, Confirmed, Declined, EmailFailed. Every organizer answer
// that may have sent mail carries emailMode ("simulated" or "smtp", controller ruling P8).

public sealed record CreatedParticipant(string ParticipantId, string Bib, string Status, string EmailMode);

/// <param name="EmailMode">Only in the answer of an edit (the list carries it once).</param>
public sealed record ParticipantView(
    string ParticipantId, string Bib, string Name, string? Nickname, bool NicknameHidden, string Email, string Gender,
    string Language, string? Category, string? Wave, string Status, bool IsMinor,
    [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EmailMode = null);

public sealed record StatusCounts(int All, int Confirmed, int Invited, int Declined, int EmailFailed);

/// <param name="Counts">For the whole event, whatever the filter.</param>
public sealed record ParticipantList(string EmailMode, EventState EventState, StatusCounts Counts, IReadOnlyList<ParticipantView> Participants);

public sealed record MailQueued(string EmailMode);

/// <summary>The answer of a re-issue: never a token (the participant gets the new link by email).</summary>
public sealed record TokenReissued(bool DeviceTokenIssued, string EmailMode);

/// <param name="Limited">Due participants not reminded because an hourly invitation limit was reached.</param>
public sealed record Reminded(int Sent, int Limited, string EmailMode);

/// <summary>The errors of the registration API (shape and helper of <see cref="EventErrors"/>).</summary>
public static class RegistrationErrors
{
    public static readonly EventError ParticipantNotFound = new(StatusCodes.Status404NotFound, "not_found",
        "Podujatie alebo účastník neexistuje.", "The event or participant does not exist.");

    public static readonly EventError BibTaken = new(StatusCodes.Status409Conflict, "bib_taken",
        "Toto štartové číslo už má iný účastník podujatia.", "Another participant of the event already has this bib number.", "bib");

    public static readonly EventError EmailDuplicate = new(StatusCodes.Status409Conflict, "email_duplicate",
        "Účastník s touto e-mailovou adresou je už v podujatí zaregistrovaný.",
        "A participant with this email address is already registered for the event.", "email");

    public static EventError ParticipantLimit(int max) => new(StatusCodes.Status409Conflict, "participant_limit",
        $"Podujatie môže mať najviac {max} účastníkov.", $"An event can have at most {max} participants.");

    public static readonly EventError EventLive = new(StatusCodes.Status409Conflict, "event_live",
        "Podujatie beží; účastníka už nemožno odstrániť. Označte ho namiesto toho ako DNF (odstúpil).",
        "The event is live; the participant can no longer be removed. Mark them as DNF (withdrawn) instead.");

    public static readonly EventError EventFinished = new(StatusCodes.Status409Conflict, "event_finished",
        "Podujatie už skončilo; registrácie už nemožno meniť.", "The event has finished; registrations can no longer be changed.");

    public static readonly EventError RateLimited = new(StatusCodes.Status429TooManyRequests, "rate_limited",
        "Bol dosiahnutý hodinový limit pozvánok. Skúste to, prosím, neskôr.",
        "The hourly limit of invitation emails has been reached. Please try again later.");

    public static readonly EventError InvalidName = Validation("name",
        "Zadajte meno (najviac 200 znakov, bez riadiacich a smerových znakov).",
        "Please enter a name (at most 200 characters, no control or bidirectional formatting characters).");

    public static readonly EventError InvalidEmail = Validation("email",
        "Zadajte jednu platnú e-mailovú adresu (najviac 254 znakov).",
        "Please enter a single valid email address (at most 254 characters).");

    public static readonly EventError InvalidGender = Validation("gender",
        "Pohlavie musí byť „female“ alebo „male“.", "The gender must be \"female\" or \"male\".");

    public static readonly EventError InvalidLanguage = Validation("language",
        "Jazyk musí byť „sk“ alebo „en“.", "The language must be \"sk\" or \"en\".");

    public static readonly EventError UnknownCategory = Validation("category",
        "Podujatie nemá kategóriu s týmto názvom.", "The event has no category of this name.");

    public static readonly EventError UnknownWave = Validation("wave",
        "Podujatie nemá vlnu s týmto názvom.", "The event has no wave of this name.");

    public static readonly EventError InvalidBib = Validation("bib",
        "Štartové číslo má 1 až 16 znakov: písmená a číslice bez diakritiky a pomlčky, začína písmenom alebo číslicou.",
        "A bib has 1 to 16 characters: ASCII letters, digits and hyphens, starting with a letter or digit.");

    public static readonly EventError InvalidNickname = Validation("nickname",
        "Prezývka má najviac 30 znakov, bez riadiacich a smerových znakov.",
        "A nickname has at most 30 characters, no control or bidirectional formatting characters.");

    public static readonly EventError InvalidStatus = Validation("status",
        "Stav musí byť Invited, Confirmed, Declined alebo EmailFailed.",
        "The status must be Invited, Confirmed, Declined or EmailFailed.");

    public static readonly EventError InvalidQuery = Validation("q",
        "Hľadaný text má najviac 200 znakov.", "The search text has at most 200 characters.");

    private static EventError Validation(string field, string sk, string en) =>
        new(StatusCodes.Status400BadRequest, "validation_failed", sk, en, field);
}
