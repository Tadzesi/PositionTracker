using System.Text.Json.Serialization;

namespace PositionTracker.Api.Accounts;

/// <summary>Slovak and English texts of an error.</summary>
public sealed record LocalizedText(string Sk, string En);

/// <summary>
/// The error body of the account (and later organizer) API: <c>{"code": "...", "message": {"sk": "...", "en": "..."}}</c>,
/// plus <c>field</c> for <c>validation_failed</c>.
/// </summary>
public sealed record ApiError(
    string Code,
    LocalizedText Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

public static class AccountErrors
{
    public static IResult LinkExpired() => Error(StatusCodes.Status400BadRequest, "link_expired",
        "Platnosť odkazu vypršala. Vyžiadajte si, prosím, nový odkaz.",
        "This link has expired. Please request a new one.");

    public static IResult LinkUsed() => Error(StatusCodes.Status400BadRequest, "link_used",
        "Tento odkaz už bol použitý. Vyžiadajte si, prosím, nový odkaz.",
        "This link has already been used. Please request a new one.");

    public static IResult LinkInvalid() => Error(StatusCodes.Status400BadRequest, "link_invalid",
        "Odkaz je neplatný alebo ho nahradil novší odkaz. Použite odkaz z najnovšieho e-mailu.",
        "This link is invalid or has been replaced by a newer one. Please use the link from the most recent email.");

    public static IResult Unauthorized() => Error(StatusCodes.Status401Unauthorized, "unauthorized",
        "Nie ste prihlásený. Prihláste sa, prosím.",
        "You are not signed in. Please sign in.");

    public static IResult EmailNotConfirmed() => Error(StatusCodes.Status403Forbidden, "email_not_confirmed",
        "Najprv potvrďte svoju e-mailovú adresu odkazom z e-mailu.",
        "Please confirm your email address with the link from the email first.");

    public static IResult Forbidden() => Error(StatusCodes.Status403Forbidden, "forbidden",
        "Na túto akciu nemáte oprávnenie.",
        "You are not allowed to do this.");

    public static IResult CsrfFailed() => Error(StatusCodes.Status400BadRequest, "csrf_failed",
        $"Požiadavka bola z bezpečnostných dôvodov odmietnutá (chýba hlavička {BrowserApiHeaders.HeaderName}: {BrowserApiHeaders.HeaderValue}).",
        $"The request was refused for security reasons (the header {BrowserApiHeaders.HeaderName}: {BrowserApiHeaders.HeaderValue} is missing).");

    public static IResult UnsupportedMediaType() => Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type",
        "Požiadavka musí mať obsah typu application/json.",
        "The request body must be of type application/json.");

    public static IResult RateLimited() => Error(StatusCodes.Status429TooManyRequests, "rate_limited",
        "Príliš veľa pokusov. Skúste to, prosím, neskôr.",
        "Too many attempts. Please try again later.");

    public static IResult Unavailable() => Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
        "Účty organizátorov sú momentálne nedostupné.",
        "Organizer accounts are not available at the moment.");

    public static IResult InvalidBody(int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new ApiError("validation_failed", new LocalizedText(
            "Požiadavka nemá platný formát.",
            "The request is not valid JSON of the expected shape."), "body"), statusCode: status);

    public static IResult BodyTooLarge() =>
        Results.Json(new ApiError("validation_failed", new LocalizedText(
            "Požiadavka je príliš veľká.", "The request is too large."), "body"), statusCode: StatusCodes.Status413PayloadTooLarge);

    public static IResult InvalidName() => Validation("name",
        "Zadajte meno (najviac 200 znakov, bez riadiacich znakov).",
        "Please enter a name (at most 200 characters, no control characters).");

    public static IResult InvalidEmail() => Validation("email",
        "Zadajte jednu platnú e-mailovú adresu (najviac 254 znakov).",
        "Please enter a single valid email address (at most 254 characters).");

    public static IResult InvalidLanguage() => Validation("language",
        "Jazyk musí byť „sk“ alebo „en“.",
        "The language must be \"sk\" or \"en\".");

    private static IResult Validation(string field, string sk, string en) =>
        Results.Json(new ApiError("validation_failed", new LocalizedText(sk, en), field), statusCode: StatusCodes.Status400BadRequest);

    private static IResult Error(int status, string code, string sk, string en) =>
        Results.Json(new ApiError(code, new LocalizedText(sk, en)), statusCode: status);
}
