using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Events;

/// <summary>An error of the event API: HTTP status, code, Slovak and English text and the offending field (if any).</summary>
public sealed record EventError(int Status, string Code, string Sk, string En, string? Field = null)
{
    public EventError WithField(string field) => this with { Field = field };
}

/// <summary>
/// The errors of the organizer event API and of the device endpoints for finished events, in the shared error shape
/// <c>{"code","message":{"sk","en"},"field"?}</c> (<see cref="ApiError"/>).
/// </summary>
public static class EventErrors
{
    public static readonly EventError NotFound = new(StatusCodes.Status404NotFound, "not_found",
        "Podujatie neexistuje.", "The event does not exist.");

    public static readonly EventError Conflict = new(StatusCodes.Status409Conflict, "conflict",
        "Podujatie medzitým zmenila iná požiadavka (súčasne). Načítajte ho, prosím, znova a zopakujte zmenu.",
        "The event was changed by another request at the same time. Please reload it and repeat the change.");

    public static readonly EventError InvalidTransition = new(StatusCodes.Status409Conflict, "invalid_transition",
        "Tento prechod stavu nie je povolený (poradie je návrh → pripravené → živé → skončené).",
        "This state change is not allowed (the order is draft → ready → live → finished).");

    public static readonly EventError CourseMissing = new(StatusCodes.Status409Conflict, "course_missing",
        "Podujatie ešte nemá trasu. Najprv nahrajte súbor GPX.",
        "The event has no course yet. Please upload a GPX file first.");

    public static readonly EventError NoConfirmedParticipants = new(StatusCodes.Status409Conflict, "no_confirmed_participants",
        "Podujatie nemá žiadneho potvrdeného účastníka.",
        "The event has no confirmed participant.");

    public static readonly EventError EventLocked = new(StatusCodes.Status409Conflict, "event_locked",
        "Podujatie už beží alebo skončilo; trasu, kategórie a vlny už nemožno meniť.",
        "The event is live or finished; its course, categories and waves can no longer be changed.");

    public static readonly EventError EventFinished = new(StatusCodes.Status409Conflict, "event_finished",
        "Podujatie už skončilo; polohy a akcie sa už neprijímajú.",
        "The event has finished; positions and actions are no longer accepted.");

    public static readonly EventError GpxInvalid = new(StatusCodes.Status400BadRequest, "gpx_invalid",
        "Súbor GPX je neplatný (očakáva sa base64 dokumentu GPX s aspoň dvoma bodmi trasy).",
        "The GPX file is invalid (expected the base64 of a GPX document with at least two track points).", "gpxBase64");

    public static readonly EventError GpxTooLarge = new(StatusCodes.Status413PayloadTooLarge, "gpx_too_large",
        "Súbor GPX je väčší ako 1 MB.", "The GPX file is larger than 1 MB.", "gpxBase64");

    public static readonly EventError CourseTooShort = new(StatusCodes.Status400BadRequest, "course_too_short",
        "Trasa je príliš krátka (najmenej 50 m na kolo).", "The course is too short (at least 50 m per lap).", "gpxBase64");

    public static readonly EventError CourseNotClosed = new(StatusCodes.Status400BadRequest, "course_not_closed",
        "Trasa s viacerými kolami musí byť uzavretá (koniec najviac 100 m od štartu).",
        "A course with more than one lap must be closed (the end at most 100 m from the start).", "laps");

    public static readonly EventError CheckpointInvalid = new(StatusCodes.Status400BadRequest, "checkpoint_invalid",
        "Kontrolný bod je neplatný: potrebuje názov (najviac 60 znakov), musí ležať vnútri kola a názvy aj vzdialenosti sa nesmú opakovať.",
        "A checkpoint is invalid: it needs a name (at most 60 characters), must lie inside the lap, and names and distances must not repeat.",
        "checkpoints");

    public static readonly EventError DelayInvalid = new(StatusCodes.Status400BadRequest, "delay_invalid",
        "Oneskorenie pre divákov musí byť 5 až 300 sekúnd.",
        "The spectator delay must be between 5 and 300 seconds.", "spectatorDelaySeconds");

    public static readonly EventError LapsInvalid = new(StatusCodes.Status400BadRequest, "laps_invalid",
        "Počet kôl musí byť 1 až 10.", "The number of laps must be between 1 and 10.", "laps");

    public static readonly EventError RuntimeFailed = new(StatusCodes.Status500InternalServerError, "runtime_failed",
        "Podujatie sa nepodarilo spustiť; zostáva v stave pripravené. Skúste to, prosím, znova alebo kontaktujte prevádzkovateľa.",
        "The event could not be started; it stays ready. Please try again or contact the operator.");

    public static readonly EventError Unavailable = new(StatusCodes.Status503ServiceUnavailable, "unavailable",
        "Správa podujatí je momentálne nedostupná. Skúste to, prosím, neskôr.",
        "Event management is not available at the moment. Please try again later.");

    public static readonly EventError InvalidBody = new(StatusCodes.Status400BadRequest, "validation_failed",
        "Požiadavka nemá platný formát.", "The request is not valid JSON of the expected shape.", "body");

    public static readonly EventError BodyTooLarge = new(StatusCodes.Status413PayloadTooLarge, "validation_failed",
        "Požiadavka je príliš veľká.", "The request is too large.", "body");

    public static EventError InvalidName(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Zadajte názov (najviac 120 znakov, bez riadiacich a smerových znakov).",
        "Please enter a name (at most 120 characters, no control or bidirectional formatting characters).", field);

    public static EventError InvalidLocation(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Zadajte miesto (najviac 120 znakov, bez riadiacich a smerových znakov).",
        "Please enter a location (at most 120 characters, no control or bidirectional formatting characters).", field);

    public static EventError StartNotInFuture(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Začiatok musí byť v budúcnosti, v UTC vo formáte ISO 8601 so „Z“ (napr. 2026-11-07T10:00:00Z).",
        "The start must be in the future, in UTC as ISO 8601 with \"Z\" (e.g. 2026-11-07T10:00:00Z).", field);

    public static EventError InvalidSport(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Šport musí byť „running“ alebo „cycling“.", "The sport must be \"running\" or \"cycling\".", field);

    public static EventError InvalidState(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Stav musí byť „ready“, „live“ alebo „finished“.", "The state must be \"ready\", \"live\" or \"finished\".", field);

    public static EventError InvalidGroupName(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Zadajte názov (najviac 60 znakov, bez riadiacich a smerových znakov); názvy sa nesmú opakovať (bez ohľadu na veľkosť písmen).",
        "Please enter a name (at most 60 characters, no control or bidirectional formatting characters); names must be unique (ignoring case).",
        field);

    public static EventError InvalidGender(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Pohlavie kategórie musí byť „female“, „male“ alebo prázdne.",
        "The category gender must be \"female\", \"male\" or empty.", field);

    public static EventError InvalidAge(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Vek musí byť 0 až 120 rokov a najnižší vek nesmie byť vyšší ako najvyšší.",
        "Ages must be between 0 and 120 and the minimum must not exceed the maximum.", field);

    public static EventError InvalidWaveStart(string field) => new(StatusCodes.Status400BadRequest, "validation_failed",
        "Štart vlny musí byť v UTC (ISO 8601 so „Z“), najskôr pri štarte podujatia a najneskôr 24 hodín po ňom.",
        "A wave must start in UTC (ISO 8601 with \"Z\"), not before the event start and at most 24 hours after it.", field);

    public static EventError TooMany(string field, int max) => new(StatusCodes.Status400BadRequest, "validation_failed",
        $"Najviac {max} položiek.", $"At most {max} entries.", field);

    public static IResult Result(EventError error) =>
        Results.Json(new ApiError(error.Code, new LocalizedText(error.Sk, error.En), error.Field), statusCode: error.Status);
}
