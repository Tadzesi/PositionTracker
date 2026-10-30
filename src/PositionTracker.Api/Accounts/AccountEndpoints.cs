using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using FromQueryAttribute = Microsoft.AspNetCore.Mvc.FromQueryAttribute;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Persistence;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// Organizer accounts (JSON, camelCase; every error body <c>{"code", "message": {"sk", "en"}}</c>, see
/// <see cref="AccountErrors"/>):
/// <list type="bullet">
/// <item><c>POST /api/account/signup {name, email, language}</c> → 202 <c>{"status":"accepted"}</c> whatever the
/// state of the address (unknown, unconfirmed, confirmed), also over a rate limit; 400 <c>validation_failed</c>
/// (+ <c>field</c>) for invalid input.</item>
/// <item><c>POST /api/account/signin {email}</c> → the same 202.</item>
/// <item><c>GET /api/account/confirm?token=…</c> → 200 <c>{"confirmed":true}</c> (also when reopened), 400
/// <c>link_expired|link_used|link_invalid</c>, 429 <c>rate_limited</c>.</item>
/// <item><c>GET /api/account/signin/redeem?token=…</c> → sets the session cookie, 200 with the profile; the same
/// errors.</item>
/// <item><c>POST /api/account/signout</c> → 204; clears the cookie and ends every session of the account.</item>
/// <item><c>GET /api/account/me</c> → 200 <c>{name, email, language, emailConfirmed, emailMode}</c>, 401
/// <c>unauthorized</c> without a session.</item>
/// </list>
/// Sign-up and sign-in take at least <see cref="AccountOptions.MinResponseMilliseconds"/> (wall clock) on every
/// path, so the timing does not reveal an account either. All answers carry <c>Cache-Control: no-store</c> and
/// <c>Referrer-Policy: no-referrer</c>. Without a database or <c>Email:PublicBaseUrl</c> every endpoint answers 503
/// <c>unavailable</c>.
/// </summary>
public static class AccountEndpoints
{
    public const int MaxNameLength = 200;
    private const long MaxBodyBytes = 8 * 1024;

    public sealed record SignUpRequest(string? Name, string? Email, string? Language);

    public sealed record SignInRequest(string? Email);

    public sealed record AcceptedResponse(string Status);

    public sealed record ConfirmResponse(bool Confirmed);

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
        group.MapGet("/confirm", ConfirmAsync);
        group.MapGet("/signin/redeem", RedeemAsync);
        group.MapPost("/signout", SignOutAsync);
        group.MapGet("/me", Me).RequireAuthorization(CookieAuth.OrganizerSessionPolicy);
        return app;
    }

    private static async Task<IResult> SignUpAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IOptions<AccountOptions> options, IOptions<JsonOptions> json)
    {
        var started = Stopwatch.GetTimestamp();
        var (body, error) = await ReadAsync<SignUpRequest>(context, json);
        if (error is not null) return error;
        if (!TryName(body!.Name, out var name)) return AccountErrors.InvalidName();
        if (!EmailAddress.TryParse(body.Email, out var email)) return AccountErrors.InvalidEmail();
        if (!TryLanguage(body.Language, out var language)) return AccountErrors.InvalidLanguage();

        if (limiter.TryTakeRequest(context.Connection.RemoteIpAddress))
            await organizers.SignUpAsync(name, email, language, context.RequestAborted);
        return await AcceptedAsync(started, options, context.RequestAborted);
    }

    private static async Task<IResult> SignInAsync(HttpContext context, OrganizerService organizers, AccountRateLimiter limiter,
        IOptions<AccountOptions> options, IOptions<JsonOptions> json)
    {
        var started = Stopwatch.GetTimestamp();
        var (body, error) = await ReadAsync<SignInRequest>(context, json);
        if (error is not null) return error;
        if (!EmailAddress.TryParse(body!.Email, out var email)) return AccountErrors.InvalidEmail();

        if (limiter.TryTakeRequest(context.Connection.RemoteIpAddress))
            await organizers.RequestSignInAsync(email, context.RequestAborted);
        return await AcceptedAsync(started, options, context.RequestAborted);
    }

    private static async Task<IResult> ConfirmAsync([FromQuery] string? token, HttpContext context, OrganizerService organizers,
        AccountRateLimiter limiter)
    {
        if (!limiter.TryTakeRedeem(context.Connection.RemoteIpAddress)) return AccountErrors.RateLimited();
        var status = await organizers.ConfirmEmailAsync(token ?? "", context.RequestAborted);
        return status == LinkStatus.Ok ? Results.Ok(new ConfirmResponse(true)) : LinkError(status);
    }

    private static async Task<IResult> RedeemAsync([FromQuery] string? token, HttpContext context, OrganizerService organizers,
        AccountRateLimiter limiter, IEmailMode mode)
    {
        if (!limiter.TryTakeRedeem(context.Connection.RemoteIpAddress)) return AccountErrors.RateLimited();
        var (status, organizer) = await organizers.RedeemSignInAsync(token ?? "", context.RequestAborted);
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

    private static MeResponse Profile(OrganizerEntity o, IEmailMode mode) =>
        new(o.Name, o.Email, o.Language, o.EmailConfirmedAt is not null, mode.Mode);

    private static IResult LinkError(LinkStatus status) => status switch
    {
        LinkStatus.Expired => AccountErrors.LinkExpired(),
        LinkStatus.Used => AccountErrors.LinkUsed(),
        _ => AccountErrors.LinkInvalid(),
    };

    /// <summary>The 202 of sign-up and sign-in, not before the minimum response time (measured from <paramref name="started"/>).</summary>
    private static async Task<IResult> AcceptedAsync(long started, IOptions<AccountOptions> options, CancellationToken ct)
    {
        var remaining = TimeSpan.FromMilliseconds(options.Value.MinResponseMilliseconds) - Stopwatch.GetElapsedTime(started);
        // Wall-clock latency, deliberately not the injected TimeProvider (which is business time).
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        return Results.Json(Accepted, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext context, IOptions<JsonOptions> json) where T : class
    {
        var (body, tooLarge) = await JsonBody.ReadAsync<T>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (tooLarge is not null) return (null, AccountErrors.BodyTooLarge());
        return body is null ? (null, AccountErrors.InvalidBody()) : (body, null);
    }

    /// <summary>Trimmed, 1 to 200 characters (Unicode scalar values), no control characters or line separators.</summary>
    private static bool TryName(string? input, out string name)
    {
        name = (input ?? "").Trim();
        if (name.Length == 0) return false;
        var count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029) return false;
            if (++count > MaxNameLength) return false;
        }
        return true;
    }

    private static bool TryLanguage(string? input, out string language)
    {
        language = (input ?? "").Trim().ToLowerInvariant();
        return language is "sk" or "en";
    }
}
