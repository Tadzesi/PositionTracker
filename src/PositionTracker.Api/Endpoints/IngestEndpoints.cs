using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Device endpoints authenticated by a participant bearer token:
/// <c>POST /api/events/{eventId}/positions</c> (202 with accepted/rejected counts) and
/// <c>POST /api/events/{eventId}/actions</c> (204). A missing, unknown or revoked token is 401, a token of
/// another event 403, an unknown event or participant 404, a finished event 409 <c>event_finished</c>, a malformed body 400, more than
/// <see cref="MaxFixesPerBatch"/> fixes or a body over <see cref="MaxBodyBytes"/> 413. Fixes with invalid coordinates or poor accuracy are not errors;
/// they are counted as rejected. Each token may make <see cref="RequestsPerMinute"/> requests per fixed
/// one-minute window (429 with <c>Retry-After</c> beyond); unauthenticated requests share a window per remote IP.
/// On top of that each token may submit <see cref="FixRateLimiter.FixesPerMinute"/> fixes per minute. The budget is
/// decided per batch, all or nothing: a batch that does not fit into the rest of the current window is refused with
/// 429 and <c>Retry-After</c> (seconds until the window ends, when the whole batch fits) and nothing of it, neither
/// fixes nor status, is applied; the client resends the same batch after that time.
/// <para>Persistence (real events with a database, see <see cref="PersistenceQueue"/>): the request first takes a slot
/// in the write queue, waiting at most <see cref="PersistenceOptions.EnqueueTimeoutMs"/>; without one it is refused
/// with 503 and <c>Retry-After: 1</c> before anything is applied, and the client resends the batch. With a slot it is
/// applied and its stored fixes, status or action are queued for the background writer: the response never waits for
/// the database.</para>
/// </summary>
public static class IngestEndpoints
{
    public const int MaxFixesPerBatch = 500;
    public const int RequestsPerMinute = 60;
    /// <summary>Request body cap; a full batch of <see cref="MaxFixesPerBatch"/> fixes is about 60 KB.</summary>
    public const long MaxBodyBytes = 256 * 1024;

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    private const string RateLimitPolicy = "device";
    private static readonly object CallerKey = new();

    /// <summary>Registers the per-token rate limiter used by <see cref="MapIngestEndpoints"/>.</summary>
    public static IServiceCollection AddIngestRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                SetRetryAfter(context.HttpContext,
                    context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : RateLimitWindow);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(RateLimitPolicy, context =>
            {
                var key = Authenticate(context) is { } caller
                    ? "token:" + caller.TokenHash
                    : "ip:" + context.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RequestsPerMinute,
                    Window = RateLimitWindow,
                    QueueLimit = 0,
                });
            });
        });

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/{eventId}").RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/positions", PostPositions);
        group.MapPost("/actions", PostAction);
        return app;
    }

    private static async Task<IResult> PostPositions(
        string eventId, HttpContext context, EventRegistry registry, FixRateLimiter fixLimiter, PersistenceQueue history,
        IOptions<JsonOptions> json)
    {
        if (Authorize(context, eventId, registry, out var caller, out var runtime) is { } denied) return denied;

        var (request, tooLarge) = await JsonBody.ReadAsync<IngestRequest>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (tooLarge is not null) return tooLarge;
        if (request?.Fixes is not { } fixes || fixes.Any(f => f is null))
            return BadRequest("Expected {\"fixes\":[{\"t\",\"lat\",\"lon\",\"acc\"}],\"status\":{...}}.");
        if (fixes.Count == 0) return BadRequest("'fixes' must not be empty.");
        if (fixes.Count > MaxFixesPerBatch)
            return Results.Problem($"At most {MaxFixesPerBatch} fixes per batch.",
                statusCode: StatusCodes.Status413PayloadTooLarge);

        // A battery level outside 0..100 is a device bug, not a reason to refuse the fixes: it is stored as unknown.
        var status = request.Status is { } s
            ? new DeviceStatus(s.Battery is >= 0 and <= 100 ? s.Battery : null, s.Charging, s.Queue, s.Foreground)
            : null;
        var batch = new PositionBatch(fixes.Select(f => new PositionFix(f.T, f.Lat, f.Lon, f.Acc)).ToList(), status);
        return await ApplyAsync(context, eventId, runtime, caller.ParticipantId, caller.TokenHash, batch, fixLimiter, history,
            ingested => Results.Accepted(value: ingested));
    }

    /// <summary>
    /// Applies a batch for an authenticated participant: the all-or-nothing fix budget (429 + <c>Retry-After</c>), the
    /// write-queue reservation (503 + <c>Retry-After: 1</c>), the runtime and the persistence. Shared by the bearer
    /// endpoint and the OwnTracks adapter; <paramref name="ok"/> builds the success answer.
    /// </summary>
    internal static async Task<IResult> ApplyAsync(HttpContext context, string eventId, EventRuntime runtime, string participantId,
        string tokenHash, PositionBatch batch, FixRateLimiter fixLimiter, PersistenceQueue history, Func<IngestResult, IResult> ok)
    {
        var status = batch.Status;
        var persist = history.ShouldPersist(runtime.Settings);
        if (persist && !await history.TryReserveAsync(context.RequestAborted)) return QueueFull(context);
        var reserved = persist;
        try
        {
            // All or nothing: a batch over the fix budget is refused before anything (fixes or status) is applied.
            if (!fixLimiter.TryTake(tokenHash, batch.Fixes.Count, out var retryAfter))
            {
                SetRetryAfter(context, retryAfter);
                return Results.Problem(
                    $"At most {FixRateLimiter.FixesPerMinute} fixes per minute; resend this batch after Retry-After.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            var ingested = runtime.IngestDetailed(participantId, batch);
            if (persist && (ingested.Stored.Count > 0 || status is not null))
            {
                reserved = false;
                if (!history.Enqueue(new FixBatchWrite(eventId, participantId, ingested.Stored, status, ingested.At)))
                    return QueueFull(context);
            }
            return ok(ingested.Result);
        }
        finally
        {
            if (reserved) history.Release();
        }
    }

    /// <summary>503 with <c>Retry-After: 1</c>: the position history cannot take the write now.</summary>
    private static IResult QueueFull(HttpContext context)
    {
        SetRetryAfter(context, TimeSpan.FromSeconds(1));
        return Results.Problem("The position store is busy; resend this request after Retry-After.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Sets <c>Retry-After</c> in whole seconds, rounded up and at least 1.</summary>
    private static void SetRetryAfter(HttpContext context, TimeSpan wait) =>
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static async Task<IResult> PostAction(
        string eventId, HttpContext context, EventRegistry registry, PersistenceQueue history, IOptions<JsonOptions> json)
    {
        if (Authorize(context, eventId, registry, out var caller, out var runtime) is { } denied) return denied;

        var (request, tooLarge) = await JsonBody.ReadAsync<ActionRequest>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (tooLarge is not null) return tooLarge;
        if (request is null || !Enum.IsDefined(request.Action))
            return BadRequest("Expected {\"action\":\"sos|sosCleared|pause|resume|dnf\"}.");
        var persist = history.ShouldPersist(runtime.Settings);
        if (persist && !await history.TryReserveAsync(context.RequestAborted)) return QueueFull(context);
        var reserved = persist;
        try
        {
            var at = runtime.Act(caller.ParticipantId, request.Action);
            if (persist)
            {
                reserved = false;
                if (!history.Enqueue(new ActionWrite(eventId, caller.ParticipantId, request.Action, at))) return QueueFull(context);
            }
            return Results.NoContent();
        }
        finally
        {
            if (reserved) history.Release();
        }
    }

    /// <summary>
    /// Returns the error result, or <c>null</c> with the caller and its event when access is allowed. An unknown
    /// event or a participant missing from it is 404.
    /// </summary>
    private static IResult? Authorize(HttpContext context, string eventId, EventRegistry registry,
        out Caller caller, out EventRuntime runtime)
    {
        caller = null!;
        runtime = null!;
        if (Authenticate(context) is not { } c)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        }
        if (!string.Equals(c.EventId, eventId, StringComparison.Ordinal)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (registry.Get(eventId) is not { } r || !r.HasParticipant(c.ParticipantId)) return Results.NotFound();
        if (registry.IsFinished(eventId)) return EventErrors.Result(EventErrors.EventFinished);
        caller = c;
        runtime = r;
        return null;
    }

    /// <summary>
    /// The caller resolved by <see cref="UseDeviceAuthentication"/> (null when there was no valid bearer token). The
    /// rate limiter policy and the handler both read it; resolving needs the database, so it happens in the middleware.
    /// </summary>
    private static Caller? Authenticate(HttpContext context) =>
        context.Items.TryGetValue(CallerKey, out var cached) ? (Caller?)cached : null;

    /// <summary>
    /// Resolves the bearer token of the device routes (<c>/api/events/{id}/positions|actions</c>) before the rate
    /// limiter: simulated events from memory, real events from the database (<see cref="ITokenService"/>), so a token
    /// revoked or re-issued is refused at once and database tokens survive a restart. A database failure is 503.
    /// </summary>
    public static IApplicationBuilder UseDeviceAuthentication(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && IsDeviceRoute(context.Request.Path)
                && BearerToken(context.Request) is { } raw)
            {
                try
                {
                    context.Items[CallerKey] = await ResolveAsync(context, raw);
                }
                catch (Exception ex) when (EventService.IsDatabaseFailure(ex) && !context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.Headers.RetryAfter = "1";
                    await EventErrors.Result(EventErrors.Unavailable).ExecuteAsync(context);
                    return;
                }
            }
            await next(context);
        });

    private static async Task<Caller?> ResolveAsync(HttpContext context, string raw)
    {
        var services = context.RequestServices;
        (string EventId, string ParticipantId)? id = services.GetRequiredService<TokenService>().Resolve(raw);
        if (id is null && services.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled)
            id = await services.GetRequiredService<ITokenService>().ResolveAsync(raw, context.RequestAborted);
        return id is { } found ? new Caller(found.EventId, found.ParticipantId, TokenService.Hash(raw)) : null;
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }

    private static bool IsDeviceRoute(PathString path)
    {
        if (!path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase, out var rest)) return false;
        var segments = (rest.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && (segments[1].Equals("positions", StringComparison.OrdinalIgnoreCase)
            || segments[1].Equals("actions", StringComparison.OrdinalIgnoreCase));
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    private sealed record Caller(string EventId, string ParticipantId, string TokenHash);
}

public sealed record IngestRequest
{
    public required List<FixRequest> Fixes { get; init; }
    public StatusRequest? Status { get; init; }
}

public sealed record FixRequest
{
    [JsonConverter(typeof(UtcTimestampConverter))]
    public required DateTimeOffset T { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public double? Acc { get; init; }
}

public sealed record StatusRequest
{
    public double? Battery { get; init; }
    public bool? Charging { get; init; }
    public required int Queue { get; init; }
    public required bool Foreground { get; init; }
}

public sealed record ActionRequest
{
    public required ParticipantAction Action { get; init; }
}
