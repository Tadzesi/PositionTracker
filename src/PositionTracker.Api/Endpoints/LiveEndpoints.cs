using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.Live;
using PositionTracker.Api.Operations;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Read endpoints for the live state:
/// <c>GET /api/events/{eventId}/live</c> (organizer, header <c>X-Api-Key</c>, or an organizer session for its own events; 401 without either, checked
/// before the event lookup) returns the current full <see cref="LiveView"/>;
/// <c>GET /api/events/{eventId}/public</c> (anonymous) returns the delayed <see cref="SpectatorView"/>, empty
/// until a view is at least the spectator delay old. Unknown events are 404.
/// </summary>
public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/{eventId}");
        group.MapGet("/live", GetLive);
        group.MapGet("/public", GetPublic);
        group.MapGet("/course", GetCourse);
        return app;
    }

    private static async Task<IResult> GetLive(string eventId, HttpContext context, EventRegistry registry,
        IOptions<OrganizerOptions> organizer, IOptions<PersistenceOptions> persistence, IDbContextFactory<AppDbContext> contexts,
        AlarmAckService acks)
    {
        if (!organizer.Value.IsValidKey(context.Request.Headers[OrganizerOptions.ApiKeyHeader].ToString()))
        {
            // No operator key: an organizer session may read its own event only.
            if (CookieAuth.OrganizerId(context.User) is not { } organizerId) return Results.Unauthorized();
            if (!await OperationsEndpoints.OwnsEventAsync(contexts, persistence, organizerId, eventId, context.RequestAborted))
                return Results.NotFound();
        }
        return registry.Get(eventId) is { } runtime
            ? Results.Ok(await acks.ApplyAsync(runtime.BuildLiveView(), context.RequestAborted))
            : Results.NotFound();
    }

    /// <summary>
    /// The course and the event header for the public pages (anonymous; only events whose runtime exists, 404 otherwise):
    /// <c>{event:{name,sport,startTime,spectatorDelaySeconds},laps,lapLengthM,closed,points:[[lat,lon]…],checkpoints:[{name,lapDistanceM}]}</c>.
    /// The track is thinned to at most 1 500 points.
    /// </summary>
    private static IResult GetCourse(string eventId, EventRegistry registry)
    {
        if (registry.Get(eventId) is not { } runtime) return Results.NotFound();
        var course = runtime.Course;
        var step = Math.Max(1, (int)Math.Ceiling(course.Points.Count / 1500.0));
        var points = course.Points.Where((_, i) => i % step == 0 || i == course.Points.Count - 1)
            .Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lon, 6) }).ToList();
        return Results.Ok(new
        {
            @event = new
            {
                name = runtime.Settings.Name, sport = runtime.Settings.Sport.ToString().ToLowerInvariant(),
                startTime = runtime.Settings.StartTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                spectatorDelaySeconds = (int)runtime.Settings.SpectatorDelay.TotalSeconds,
            },
            laps = course.Laps, lapLengthM = course.LapLengthM, closed = course.IsClosed, points, checkpoints = course.Checkpoints,
        });
    }

    private static IResult GetPublic(string eventId, EventRegistry registry, SpectatorFeed feed)
    {
        if (registry.Get(eventId) is not { } runtime) return Results.NotFound();
        return Results.Ok(feed.GetCurrent(runtime)
            ?? new SpectatorView(runtime.Clock.GetUtcNow() - runtime.Settings.SpectatorDelay, eventId, []));
    }
}
