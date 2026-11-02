using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.OwnTracks;

/// <summary>
/// <c>POST /api/ingest/owntracks</c>: the OwnTracks app in HTTP mode. HTTP Basic auth: user name = the participant's
/// bib (the public code, not a secret), password = the device token. Any failure (no or malformed header, unknown or
/// revoked token, wrong user name) is the same 401 with <c>WWW-Authenticate: Basic</c>. The body is one OwnTracks JSON
/// message (at most 16 KB, else 413). Answers 200 <c>[]</c> (the booklet's expected body) for a location that was
/// handled and for everything that is ignored on purpose: other message types, malformed or invalid messages, and
/// messages for an event that is not live yet (the phone must not retry those forever). For a finished event every
/// authenticated message is answered 200 with <see cref="OwnTracksConfigBuilder.StopCommand"/> (switch to quiet mode)
/// and nothing of it is applied. A batch over
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
        try
        {
            if (await IsFinishedAsync(context, registry, caller.EventId))
            {
                await NoteAfterFinishAsync(context, caller.EventId, caller.ParticipantId);
                return Stop();
            }
        }
        catch (Exception ex) when (EventService.IsDatabaseFailure(ex) && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = "1";
            return EventErrors.Result(EventErrors.Unavailable);
        }
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

    /// <summary>
    /// Whether the event is finished: the registry flag, or, when the runtime already left the memory, the stored event state.
    /// An event that is not live yet is not finished (its phones must keep sending test positions). The stored state is
    /// cached (<see cref="EventFinishedCache"/>).
    /// </summary>
    private static async Task<bool> IsFinishedAsync(HttpContext context, EventRegistry registry, string eventId)
    {
        if (registry.IsFinished(eventId)) return true;
        if (registry.Get(eventId) is not null || !Guid.TryParse(eventId, out var id)) return false;
        var services = context.RequestServices;
        if (!services.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled) return false;
        return await services.GetRequiredService<EventFinishedCache>().GetAsync(eventId, () => services.GetRequiredService<AppDbContext>()
            .Events.AsNoTracking().AnyAsync(e => e.Id == id && e.State == EventState.Finished, context.RequestAborted));
    }

    /// <summary>
    /// Records the request after the finish (and emails the participant if the phone keeps sending, see
    /// <see cref="ITrackingStopNotifier"/>). Best effort: any failure (database, an undeliverable address, ...) is logged and the
    /// stop command is answered anyway, so a phone that keeps sending is never left without it.
    /// </summary>
    private static async Task NoteAfterFinishAsync(HttpContext context, string eventId, string participantId)
    {
        try
        {
            await context.RequestServices.GetRequiredService<ITrackingStopNotifier>().NoteAsync(eventId, participantId, context.RequestAborted);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PositionTracker.OwnTracks").LogWarning(
                "Event {EventId}, participant {ParticipantId}: the request after the finish could not be recorded ({ExceptionType}).",
                eventId, participantId, ex.GetType().Name);
        }
    }

    /// <summary>The answer for a finished event: the command that switches the phone to quiet mode.</summary>
    private static IResult Stop() => Results.Json(OwnTracksConfigBuilder.StopCommand());

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
