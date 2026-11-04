using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Api.Events;
using PositionTracker.Api.OwnTracks;
using PositionTracker.Api.Participants;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// <c>GET /api/device/session</c>: what the Android app needs to know with only its device token (bearer, resolved by
/// <see cref="IngestEndpoints.UseDeviceAuthentication"/>, rate limited with the device policy of the token): the event
/// (name, sport, start, state <c>draft|ready|live|finished</c>), the participant (name, bib, own start = wave start or the
/// event start), the location tuning of the sport and the data-link light. The app polls it while it waits for the start
/// and stops tracking for good once the state is <c>finished</c>. A missing, unknown or revoked token is 401 (the app
/// forgets the token); a simulated event (no database) is 404; a database failure 503 with <c>Retry-After: 1</c>.
/// </summary>
public static class DeviceSessionEndpoints
{
    public const string SessionPath = "/api/device/session";

    public static IEndpointRouteBuilder MapDeviceSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(SessionPath, GetAsync).RequireRateLimiting(IngestEndpoints.RateLimitPolicy);
        return app;
    }

    private static async Task<IResult> GetAsync(HttpContext context, EventRegistry registry)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (IngestEndpoints.DeviceCaller(context) is not { } caller)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        }
        if (!Guid.TryParse(caller.EventId, out var eventId) || !Guid.TryParse(caller.ParticipantId, out var participantId))
            return Results.NotFound();
        try
        {
            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var ct = context.RequestAborted;
            var ev = await db.Events.AsNoTracking().Where(e => e.Id == eventId)
                .Select(e => new { e.Name, e.Location, e.Sport, e.State, e.StartTime }).SingleOrDefaultAsync(ct);
            var p = await db.Participants.AsNoTracking().Where(x => x.Id == participantId && x.EventId == eventId)
                .Select(x => new { x.Name, x.Bib, x.Nickname, x.WaveId }).SingleOrDefaultAsync(ct);
            if (ev is null || p is null) return Results.NotFound();
            var waveStart = p.WaveId is { } wave
                ? await db.Waves.AsNoTracking().Where(w => w.Id == wave).Select(w => (DateTimeOffset?)w.StartTime).SingleOrDefaultAsync(ct)
                : null;
            var state = registry.IsFinished(caller.EventId) ? EventState.Finished : ev.State;
            var tuning = EventTuning.For(ev.Sport);
            var runtime = registry.Get(caller.EventId);
            var now = runtime?.Clock.GetUtcNow() ?? context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            return Results.Json(new DeviceSession(
                caller.EventId,
                new DeviceSessionEvent(ev.Name, ev.Location, ev.Sport.ToString().ToLowerInvariant(),
                    state.ToString().ToLowerInvariant(), UtcIso(ev.StartTime)),
                new DeviceSessionMe(p.Name, p.Bib, p.Nickname, UtcIso(waveStart ?? ev.StartTime)),
                new DeviceTuning(tuning.LocatorIntervalSeconds, tuning.LocatorDisplacementM, tuning.IgnoreInaccurateM),
                state == EventState.Finished ? null : LinkHealth.Of(runtime, caller.ParticipantId),
                UtcIso(now)));
        }
        catch (Exception ex) when (EventService.IsDatabaseFailure(ex) && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = "1";
            return EventErrors.Result(EventErrors.Unavailable);
        }
    }

    private static string UtcIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The answer of <c>GET /api/device/session</c>; times are UTC ISO 8601.</summary>
public sealed record DeviceSession(
    string EventId, DeviceSessionEvent Event, DeviceSessionMe Me, DeviceTuning Tuning, LinkHealth? Link, string ServerTime);

/// <param name="State"><c>draft</c>, <c>ready</c>, <c>live</c> or <c>finished</c>.</param>
public sealed record DeviceSessionEvent(string Name, string Location, string Sport, string State, string StartTime);

/// <param name="StartTime">The participant's own start: the wave start, else the event start.</param>
public sealed record DeviceSessionMe(string Name, string Bib, string? Nickname, string StartTime);

/// <summary>How often the phone takes a fix (s), the minimum distance between fixes (m) and the worst accuracy sent (m).</summary>
public sealed record DeviceTuning(int IntervalSeconds, int DisplacementM, int IgnoreInaccurateM);
