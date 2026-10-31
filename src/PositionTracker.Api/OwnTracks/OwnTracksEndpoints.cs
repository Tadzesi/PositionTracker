using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;

namespace PositionTracker.Api.OwnTracks;

/// <summary>
/// <c>POST /api/ingest/owntracks</c>: the OwnTracks app in HTTP mode. HTTP Basic auth: user name = the participant's
/// bib (the public code, not a secret), password = the device token. Any failure (no or malformed header, unknown or
/// revoked token, wrong user name) is the same 401 with <c>WWW-Authenticate: Basic</c>. The body is one OwnTracks JSON
/// message (at most 16 KB, else 413). Answers 200 <c>[]</c> (the booklet's expected body) for a location that was
/// handled and for everything that is ignored on purpose: other message types, malformed or invalid messages, and
/// messages for an event that is not live yet or already finished (the phone must not retry those forever). A batch over
/// the fix budget is 429 with <c>Retry-After</c> and nothing of it is applied; 503 + <c>Retry-After: 1</c> when the
/// position store is busy. Nothing of the credentials is logged.
/// </summary>
public static class OwnTracksEndpoints
{
    public const long MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapOwnTracksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(OwnTracksConfigBuilder.IngestPath, PostAsync);
        return app;
    }

    private static async Task<IResult> PostAsync(HttpContext context, EventRegistry registry, FixRateLimiter fixLimiter, PersistenceQueue history)
    {
        (string EventId, string ParticipantId)? id;
        string? username, password;
        try
        {
            (username, password) = ParseBasic(context.Request.Headers.Authorization.ToString());
            id = password is null ? null : await ResolveAsync(context, password);
        }
        catch (Exception ex) when (EventService.IsDatabaseFailure(ex) && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = "1";
            return EventErrors.Result(EventErrors.Unavailable);
        }
        var runtime = id is { } known ? registry.Get(known.EventId) : null;
        if (id is not { } caller || username is null || !UsernameMatches(runtime, caller.ParticipantId, username, registry.Get(caller.EventId) is null))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"PositionTracker\"";
            return AccountErrors.Unauthorized();
        }

        if (context.Request.ContentLength > MaxBodyBytes) return TooLarge();
        JsonDocument? document;
        try
        {
            document = await JsonDocument.ParseAsync(new MaxLengthReadStream(context.Request.Body, MaxBodyBytes), cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            return Ignored();
        }
        catch (BadHttpRequestException)
        {
            return TooLarge();
        }
        using (document)
        {
            if (OwnTracksMapper.ToBatch(document.RootElement) is not { } batch) return Ignored();
            if (runtime is null || registry.IsFinished(caller.EventId) || !runtime.HasParticipant(caller.ParticipantId)) return Ignored();
            return await IngestEndpoints.ApplyAsync(context, caller.EventId, runtime, caller.ParticipantId, TokenService.Hash(password!),
                batch, fixLimiter, history, _ => Ignored());
        }
    }

    /// <summary>The user name must be the participant's bib; for an event without a runtime (not live) the token alone decides.</summary>
    private static bool UsernameMatches(Core.Runtime.EventRuntime? runtime, string participantId, string username, bool noRuntime)
    {
        if (runtime is null) return noRuntime;
        return runtime.Participants.TryGetValue(participantId, out var info)
            && string.Equals(info.Bib, username, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string EventId, string ParticipantId)?> ResolveAsync(HttpContext context, string token)
    {
        var services = context.RequestServices;
        var found = services.GetRequiredService<TokenService>().Resolve(token);
        if (found is not null) return (found.Value.EventId, found.Value.ParticipantId);
        if (!services.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled) return null;
        return await services.GetRequiredService<ITokenService>().ResolveAsync(token, context.RequestAborted);
    }

    private static (string? Username, string? Password) ParseBasic(string header)
    {
        const string scheme = "Basic ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || header.Length > 512) return (null, null);
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[scheme.Length..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? (null, null) : (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    private static IResult Ignored() => Results.Json(Array.Empty<object>());

    private static IResult TooLarge() =>
        Results.Problem($"The request body must not exceed {MaxBodyBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
}
