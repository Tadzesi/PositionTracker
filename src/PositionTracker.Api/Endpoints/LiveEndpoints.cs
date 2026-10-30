using Microsoft.Extensions.Options;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.Live;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Read endpoints for the live state:
/// <c>GET /api/events/{eventId}/live</c> (organizer, header <c>X-Api-Key</c>; 401 without a valid key, checked
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
        return app;
    }

    private static IResult GetLive(string eventId, HttpContext context, EventRegistry registry,
        IOptions<OrganizerOptions> organizer)
    {
        if (!organizer.Value.IsValidKey(context.Request.Headers[OrganizerOptions.ApiKeyHeader].ToString()))
            return Results.Unauthorized();
        return registry.Get(eventId) is { } runtime ? Results.Ok(runtime.BuildLiveView()) : Results.NotFound();
    }

    private static IResult GetPublic(string eventId, EventRegistry registry, SpectatorFeed feed)
    {
        if (registry.Get(eventId) is not { } runtime) return Results.NotFound();
        return Results.Ok(feed.GetCurrent(runtime)
            ?? new SpectatorView(runtime.Clock.GetUtcNow() - runtime.Settings.SpectatorDelay, eventId, []));
    }
}
