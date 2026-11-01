using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Events;
using PositionTracker.Email;

namespace PositionTracker.Api.Participants;

/// <summary>
/// Per-link request budget: <see cref="PerMinute"/> requests per fixed one-minute window and link token, on the injected
/// <see cref="TimeProvider"/>. Keys are hashes of the token (the token itself is not kept). Beyond
/// <see cref="MaxKeys"/> live windows new keys are not tracked (a flood of random tokens cannot grow memory; such
/// tokens do not exist anyway).
/// </summary>
public sealed class ParticipantLinkLimiter(TimeProvider clock)
{
    public const int PerMinute = 60;
    public const int MaxKeys = 50_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (long Window, int Count)> _counts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Takes one request of the link's budget; false (with the wait until the next window) when it is used up.</summary>
    public bool TryTake(string linkToken, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow().UtcTicks;
        var window = now / Window.Ticks;
        retryAfter = TimeSpan.FromTicks((window + 1) * Window.Ticks - now);
        var key = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(linkToken.Length > 128 ? linkToken[..128] : linkToken)));
        lock (_gate)
        {
            if (_counts.Count >= MaxKeys) Prune(window);
            if (!_counts.TryGetValue(key, out var entry) || entry.Window != window)
            {
                if (_counts.Count >= MaxKeys) return true;
                entry = (window, 0);
            }
            if (entry.Count >= PerMinute) return false;
            _counts[key] = (window, entry.Count + 1);
            return true;
        }
    }

    private void Prune(long window)
    {
        foreach (var (key, entry) in _counts)
            if (entry.Window != window) _counts.TryRemove(key, out _);
    }
}

/// <summary>
/// The participant's invitation link under <c>/api/p/{linkToken}</c> (JSON, camelCase; errors
/// <c>{"code","message":{"sk","en"},"field"?}</c>). The link token is the only credential and sits in the path, so
/// these routes answer <c>Cache-Control: no-store</c> and <c>Referrer-Policy: no-referrer</c>, are limited to
/// <see cref="ParticipantLinkLimiter.PerMinute"/> requests a minute per link (429 <c>rate_limited</c> + <c>Retry-After</c>),
/// and the host does not log request paths (see <c>Program.cs</c>). Unsafe requests need the browser headers
/// (<see cref="Accounts.BrowserApiHeaders"/>).
/// <list type="bullet">
/// <item><c>GET</c> → 200 <c>{event:{name,location,startTime,sport},me:{name,bib,category,gender,nickname,status,locked:{gender}},language}</c>;
/// the current state also for confirmed and declined participants; <c>link</c> is the data-link light (level, ages in seconds, problems) once the event has a runtime.</item>
/// <item><c>POST /confirm {nickname?,gender?}</c> → 200 <c>{status:"Confirmed"}</c> (idempotent);
/// <c>POST /decline</c> → 200 <c>{status:"Declined"}</c> (409 <c>decline_locked</c> from the start);
/// <c>PUT /profile {nickname?,gender?}</c> → 200 (409 <c>gender_locked</c> once the event started);
/// <c>POST /consent {locationConsent:true,showNameToSpectators}</c> → 200 <c>{deviceToken,eventId}</c> (the token only here).</item>
/// </list>
/// 409 <c>event_finished</c> for changes after the end. Without a database 503 <c>unavailable</c>.
/// </summary>
public static class ParticipantLinkEndpoints
{
    private const long BodyBytes = 4 * 1024;
    private const string LogCategory = "PositionTracker.Api.Participants.ParticipantLinkEndpoints";

    public static IServiceCollection AddParticipantLinks(this IServiceCollection services)
    {
        services.AddSingleton<ParticipantLinkLimiter>();
        services.AddScoped<ParticipantService>();
        return services;
    }

    public static IEndpointRouteBuilder MapParticipantLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/p/{linkToken}").WithDatabaseGuard(LogCategory);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var limiter = http.RequestServices.GetRequiredService<ParticipantLinkLimiter>();
            if (http.GetRouteValue("linkToken") is string token && !limiter.TryTake(token, out var wait))
            {
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return EventErrors.Result(ParticipantErrors.RateLimited);
            }
            return await next(context);
        });

        group.MapGet("", async (string linkToken, HttpContext context, ParticipantService service) =>
            Answer(await service.GetAsync(linkToken, context.RequestAborted)));
        group.MapPost("/confirm", async (string linkToken, HttpContext context, ParticipantService service, IOptions<JsonOptions> json) =>
            await WithBody(context, json, (ProfileRequest body) => service.ConfirmAsync(linkToken, body, context.RequestAborted)));
        group.MapPost("/decline", async (string linkToken, HttpContext context, ParticipantService service) =>
            Answer(await service.DeclineAsync(linkToken, context.RequestAborted)));
        group.MapPut("/profile", async (string linkToken, HttpContext context, ParticipantService service, IOptions<JsonOptions> json) =>
            await WithBody(context, json, (ProfileRequest body) => service.UpdateProfileAsync(linkToken, body, context.RequestAborted)));
        group.MapPost("/consent", async (string linkToken, HttpContext context, ParticipantService service, IOptions<JsonOptions> json) =>
            await WithBody(context, json, (ConsentRequest body) => service.ConsentAsync(linkToken, body, context.RequestAborted)));
        group.MapPost("/owntracks-config", async (string linkToken, HttpContext context, ParticipantService service, IOptions<JsonOptions> json,
            IOptions<EmailOptions> email) =>
        {
            var baseUrl = email.Value.PublicBaseUrl;
            if (!EmailLinks.IsHttpUrl(baseUrl)) return EventErrors.Result(EventErrors.Unavailable);
            return await WithBody(context, json, (OwnTracksConfigRequest body) => service.OwnTracksConfigAsync(linkToken, body, baseUrl, context.RequestAborted));
        });
        return app;
    }

    private static async Task<IResult> WithBody<TBody, TResult>(HttpContext context, IOptions<JsonOptions> json, Func<TBody, Task<EventResult<TResult>>> run)
        where TBody : class
    {
        var (body, error) = await EventManagementEndpoints.ReadAsync<TBody>(context, json, BodyBytes);
        return error ?? Answer(await run(body!));
    }

    private static IResult Answer<T>(EventResult<T> result) =>
        result.Error is { } e ? EventErrors.Result(e) : Results.Json(result.Value, statusCode: StatusCodes.Status200OK);
}
