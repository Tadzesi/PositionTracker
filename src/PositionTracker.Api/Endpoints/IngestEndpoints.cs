using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Device endpoints authenticated by a participant bearer token:
/// <c>POST /api/events/{eventId}/positions</c> (202 with accepted/rejected counts) and
/// <c>POST /api/events/{eventId}/actions</c> (204). A missing, unknown or revoked token is 401, a token of
/// another event 403, an unknown event or participant 404, a malformed body 400, more than
/// <see cref="MaxFixesPerBatch"/> fixes or a body over <see cref="MaxBodyBytes"/> 413. Fixes with invalid coordinates or poor accuracy are not errors;
/// they are counted as rejected. Each token may make <see cref="RequestsPerMinute"/> requests per fixed
/// one-minute window (429 with <c>Retry-After</c> beyond); unauthenticated requests share a window per remote IP.
/// On top of that each token may submit <see cref="FixRateLimiter.FixesPerMinute"/> fixes per minute. The budget is
/// decided per batch, all or nothing: a batch that does not fit into the rest of the current window is refused with
/// 429 and <c>Retry-After</c> (seconds until the window ends, when the whole batch fits) and nothing of it, neither
/// fixes nor status, is applied; the client resends the same batch after that time.
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
        string eventId, HttpContext context, EventRegistry registry, FixRateLimiter fixLimiter, IOptions<JsonOptions> json)
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
        // All or nothing: a batch over the fix budget is refused before anything (fixes or status) is applied.
        if (!fixLimiter.TryTake(caller.TokenHash, fixes.Count, out var retryAfter))
        {
            SetRetryAfter(context, retryAfter);
            return Results.Problem(
                $"At most {FixRateLimiter.FixesPerMinute} fixes per minute; resend this batch after Retry-After.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        var batch = new PositionBatch(fixes.Select(f => new PositionFix(f.T, f.Lat, f.Lon, f.Acc)).ToList(), status);
        return Results.Accepted(value: runtime.Ingest(caller.ParticipantId, batch));
    }

    /// <summary>Sets <c>Retry-After</c> in whole seconds, rounded up and at least 1.</summary>
    private static void SetRetryAfter(HttpContext context, TimeSpan wait) =>
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static async Task<IResult> PostAction(
        string eventId, HttpContext context, EventRegistry registry, IOptions<JsonOptions> json)
    {
        if (Authorize(context, eventId, registry, out var caller, out var runtime) is { } denied) return denied;

        var (request, tooLarge) = await JsonBody.ReadAsync<ActionRequest>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (tooLarge is not null) return tooLarge;
        if (request is null || !Enum.IsDefined(request.Action))
            return BadRequest("Expected {\"action\":\"sos|sosCleared|pause|resume|dnf\"}.");
        runtime.Act(caller.ParticipantId, request.Action);
        return Results.NoContent();
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
        caller = c;
        runtime = r;
        return null;
    }

    /// <summary>Resolves the bearer token once per request (the rate limiter and the handler both need it).</summary>
    private static Caller? Authenticate(HttpContext context)
    {
        if (context.Items.TryGetValue(CallerKey, out var cached)) return (Caller?)cached;

        Caller? caller = null;
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            var raw = header[scheme.Length..].Trim();
            if (context.RequestServices.GetRequiredService<TokenService>().Resolve(raw) is { } id)
                caller = new Caller(id.EventId, id.ParticipantId, TokenService.Hash(raw));
        }
        context.Items[CallerKey] = caller;
        return caller;
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
