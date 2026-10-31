using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Persistence;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// Organizer accounts (JSON, camelCase; every error body <c>{"code", "message": {"sk", "en"}}</c>, see
/// <see cref="AccountErrors"/>). Every POST needs <c>X-Requested-With: PositionTracker</c> and
/// <c>Content-Type: application/json</c> (<see cref="BrowserApiHeaders"/>: 400 <c>csrf_failed</c> / 415
/// <c>unsupported_media_type</c>, checked before anything else).
/// <list type="bullet">
/// <item><c>POST /api/account/signup {name, email, language}</c> → 202 <c>{"status":"accepted"}</c> whatever the
/// state of the address (unknown, unconfirmed, confirmed), also over a rate limit; 400 <c>validation_failed</c>
/// (+ <c>field</c>) for invalid input.</item>
/// <item><c>POST /api/account/signin {email}</c> → the same 202.</item>
/// <item><c>POST /api/account/confirm {token}</c> → 200 <c>{"confirmed":true,"signedIn":bool}</c>; the first redemption
/// also starts the session (<c>signedIn</c> true), a reopened link only answers confirmed; 400
/// <c>link_expired|link_used|link_invalid</c>, 429 <c>rate_limited</c>.</item>
/// <item><c>POST /api/account/signin/redeem {token}</c> → sets the session cookie (and confirms the email), 200 with
/// the profile; the same errors.</item>
/// <item><c>POST /api/account/signout</c> → 204; clears the cookie and ends every session of the account.</item>
/// <item><c>GET /api/account/me</c> → 200 <c>{name, email, language, emailConfirmed, emailMode}</c>, 401
/// <c>unauthorized</c> without a session.</item>
/// <item><c>GET /api/account/session</c> → the same 200 profile, or 204 without a session (for the web app's start-up
/// probe: an anonymous visitor must not produce a 401 in the browser console).</item>
/// </list>
/// The tokens travel in the JSON body (controller ruling P9): the emailed links open front-end pages that ask the
/// user to press a button, which POSTs the token. A GET of an API route is 405, so neither a top-level navigation
/// (login CSRF) nor a link scanner can redeem a token.
/// Sign-up and sign-in take at least <see cref="AccountOptions.MinResponseMilliseconds"/> (wall clock) on every
/// path, a database failure included (503 <c>unavailable</c>), so the timing does not reveal an account either. All
/// answers carry <c>Cache-Control: no-store</c> and <c>Referrer-Policy: no-referrer</c>. Without a database or
/// <c>Email:PublicBaseUrl</c> every endpoint answers 503 <c>unavailable</c>.
/// </summary>
public static class AccountEndpoints
{
    public const int MaxNameLength = 200;
    private const long MaxBodyBytes = 8 * 1024;
    private const string LogCategory = "PositionTracker.Api.Accounts.AccountEndpoints";

    public sealed record SignUpRequest(string? Name, string? Email, string? Language);

    public sealed record SignInRequest(string? Email);

    public sealed record TokenRequest(string? Token);

    public sealed record AcceptedResponse(string Status);

    public sealed record ConfirmResponse(bool Confirmed, bool SignedIn);

    public sealed record MeResponse(string Name, string Email, string Language, bool EmailConfirmed, string EmailMode);

    private static readonly AcceptedResponse Accepted = new("accepted");

    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddOptions<AccountOptions>()
            .BindConfiguration(AccountOptions.Section)
            .Validate(o => o.IsValid(out _), "Invalid Accounts configuration (see AccountOptions).")
            .ValidateOnStart();
        services.AddSingleton<AccountRateLimiter>();
        services.AddScoped<LinkTokenService>();
        services.AddScoped<OrganizerService>();
        services.AddOrganizerAuthentication();
        return services;
    }

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var services = http.RequestServices;
            if (!services.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled
                || !EmailLinks.IsHttpUrl(services.GetRequiredService<IOptions<EmailOptions>>().Value.PublicBaseUrl))
                return AccountErrors.Unavailable();
            return await next(context);
        });
        group.MapPost("/signup", SignUpAsync);
        group.MapPost("/signin", SignInAsync);
        group.MapPost("/confirm", ConfirmAsync);
        group.MapPost("/signin/redeem", RedeemAsync);
        group.MapPost("/signout", SignOutAsync);
        group.MapGet("/me", Me).RequireAuthorization(CookieAuth.OrganizerSessionPolicy);
        group.MapGet("/session", Session);
        return app;
    }

    private static async Task<IResult> SignUpAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IOptions<AccountOptions> options, IOptions<JsonOptions> json, ILoggerFactory loggers)
    {
        var started = Stopwatch.GetTimestamp();
        var (body, error) = await ReadAsync<SignUpRequest>(context, json);
        if (error is not null) return error;
        if (!TryName(body!.Name, out var name)) return AccountErrors.InvalidName();
        if (!EmailAddress.TryParse(body.Email, out var email, out var normalized)) return AccountErrors.InvalidEmail();
        if (!TryLanguage(body.Language, out var language)) return AccountErrors.InvalidLanguage();

        var ip = context.Connection.RemoteIpAddress;
        return await AnswerAsync(context, started, options, loggers, "sign-up", limiter.TryTakeRequest(ip)
            ? () => organizers.SignUpAsync(name, email, normalized, language, ip, context.RequestAborted)
            : null);
    }

    private static async Task<IResult> SignInAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IOptions<AccountOptions> options, IOptions<JsonOptions> json, ILoggerFactory loggers)
    {
        var started = Stopwatch.GetTimestamp();
        var (body, error) = await ReadAsync<SignInRequest>(context, json);
        if (error is not null) return error;
        if (!EmailAddress.TryParse(body!.Email, out _, out var normalized)) return AccountErrors.InvalidEmail();

        var ip = context.Connection.RemoteIpAddress;
        return await AnswerAsync(context, started, options, loggers, "sign-in", limiter.TryTakeRequest(ip)
            ? () => organizers.RequestSignInAsync(normalized, ip, context.RequestAborted)
            : null);
    }

    /// <summary>
    /// Runs <paramref name="work"/> (null when a limit suppressed it) and answers 202, or 503 <c>unavailable</c> when it
    /// failed (logged with the exception type only), never before the minimum response time.
    /// </summary>
    private static async Task<IResult> AnswerAsync(HttpContext context, long started, IOptions<AccountOptions> options,
        ILoggerFactory loggers, string operation, Func<Task>? work)
    {
        IResult result = Results.Json(Accepted, statusCode: StatusCodes.Status202Accepted);
        if (work is not null)
        {
            try
            {
                await work();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                loggers.CreateLogger(LogCategory).LogError("Account {Operation} failed ({ExceptionType}); answered 503.",
                    operation, ex.GetType().Name);
                result = AccountErrors.Unavailable();
            }
        }
        var remaining = TimeSpan.FromMilliseconds(options.Value.MinResponseMilliseconds) - Stopwatch.GetElapsedTime(started);
        // Wall-clock latency, deliberately not the injected TimeProvider (which is business time).
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, context.RequestAborted);
        return result;
    }

    private static async Task<IResult> ConfirmAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IOptions<JsonOptions> json, ILoggerFactory loggers)
    {
        var (body, error) = await ReadAsync<TokenRequest>(context, json);
        if (error is not null) return error;
        if (!limiter.TryTakeRedeem(context.Connection.RemoteIpAddress)) return AccountErrors.RateLimited();
        try
        {
            var (status, organizer) = await organizers.ConfirmEmailAsync(body!.Token ?? "", context.RequestAborted);
            if (status != LinkStatus.Ok) return LinkError(status);
            if (organizer is not null) await CookieAuth.SignInAsync(context, organizer);
            return Results.Ok(new ConfirmResponse(true, organizer is not null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            loggers.CreateLogger(LogCategory).LogError("Account confirmation failed ({ExceptionType}); answered 503.", ex.GetType().Name);
            return AccountErrors.Unavailable();
        }
    }

    private static async Task<IResult> RedeemAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IEmailMode mode, IOptions<JsonOptions> json, ILoggerFactory loggers)
    {
        var (body, error) = await ReadAsync<TokenRequest>(context, json);
        if (error is not null) return error;
        if (!limiter.TryTakeRedeem(context.Connection.RemoteIpAddress)) return AccountErrors.RateLimited();
        LinkStatus status;
        OrganizerEntity? organizer;
        try
        {
            (status, organizer) = await organizers.RedeemSignInAsync(body!.Token ?? "", context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            loggers.CreateLogger(LogCategory).LogError("Sign-in link redemption failed ({ExceptionType}); answered 503.", ex.GetType().Name);
            return AccountErrors.Unavailable();
        }
        if (status != LinkStatus.Ok) return LinkError(status);
        await CookieAuth.SignInAsync(context, organizer!);
        return Results.Ok(Profile(organizer!, mode));
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, OrganizerService organizers)
    {
        if (context.User.Identity?.IsAuthenticated == true && CookieAuth.OrganizerId(context.User) is { } id)
            await organizers.SignOutEverywhereAsync(id, context.RequestAborted);
        await context.SignOutAsync(CookieAuth.Scheme);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext context, IEmailMode mode) =>
        CookieAuth.CurrentOrganizer(context) is { } organizer ? Results.Ok(Profile(organizer, mode)) : AccountErrors.Unauthorized();

    private static IResult Session(HttpContext context, IEmailMode mode) =>
        CookieAuth.CurrentOrganizer(context) is { } organizer ? Results.Ok(Profile(organizer, mode)) : Results.NoContent();

    private static MeResponse Profile(OrganizerEntity o, IEmailMode mode) =>
        new(o.Name, o.Email, o.Language, o.EmailConfirmedAt is not null, mode.Mode);

    private static IResult LinkError(LinkStatus status) => status switch
    {
        LinkStatus.Expired => AccountErrors.LinkExpired(),
        LinkStatus.Used => AccountErrors.LinkUsed(),
        _ => AccountErrors.LinkInvalid(),
    };

    private static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext context, IOptions<JsonOptions> json) where T : class
    {
        var (body, readError) = await JsonBody.ReadAsync<T>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (readError is not null) return (null, MapReadError(readError));
        return body is null ? (null, AccountErrors.InvalidBody()) : (body, null);
    }

    /// <summary>
    /// A body read failure in the account error shape: 413 becomes <c>BodyTooLarge</c>; any other status of the
    /// server's <see cref="BadHttpRequestException"/> keeps its status (as <c>validation_failed</c>, field <c>body</c>).
    /// </summary>
    internal static IResult MapReadError(IResult readError) =>
        (readError as IStatusCodeHttpResult)?.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge => AccountErrors.BodyTooLarge(),
            { } status => AccountErrors.InvalidBody(status),
            null => AccountErrors.InvalidBody(),
        };

    /// <summary>
    /// Trimmed, 1 to 200 characters (Unicode scalar values), no control, line separator or bidirectional formatting
    /// characters (<see cref="Events.TextRules"/>, the rule of event names too).
    /// </summary>
    private static bool TryName(string? input, out string name) => Events.TextRules.TryLine(input, MaxNameLength, out name);

    private static bool TryLanguage(string? input, out string language)
    {
        language = (input ?? "").Trim().ToLowerInvariant();
        return language is "sk" or "en";
    }
}
