using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.Live;
using PositionTracker.Api.Operations;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Read endpoints for the live state:
/// <c>GET /api/events/{eventId}/live</c> (organizer, header <c>X-Api-Key</c>, or an organizer session for its own events; 401 without either, checked
/// before the event lookup) returns the current full <see cref="LiveView"/>;
/// <c>GET /api/events/{eventId}/public</c> (anonymous) returns the delayed <see cref="SpectatorView"/>, empty
/// until a view is at least the spectator delay old. Unknown events are 404;
/// <c>GET /api/events/{eventId}/info</c> (anonymous) returns the header of a published event (name, location, start, sport, state)
/// also before it has a runtime; a Draft, unknown or malformed event is 404;
/// <c>GET /api/events/{eventId}/public-results</c> (anonymous) returns the stored results of a finished event with names only
/// where the participant agreed (and is not a minor); 404 for any other event.
/// </summary>
public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/{eventId}");
        group.MapGet("/live", GetLive);
        group.MapGet("/public", GetPublic);
        group.MapGet("/course", GetCourse);
        group.MapGet("/info", GetInfo);
        group.MapGet("/public-results", GetPublicResults);
        return app;
    }

    private static async Task<IResult> GetLive(string eventId, HttpContext context, EventRegistry registry,
        ApiKeyGuard keys, IOptions<PersistenceOptions> persistence, IDbContextFactory<AppDbContext> contexts,
        AlarmAckService acks)
    {
        if (!keys.IsValidRequest(context))
        {
            // No operator key: an organizer session may read its own event only.
            if (CookieAuth.ActingOrganizerId(context) is not { } organizerId) return Results.Unauthorized();
            if (!await OperationsEndpoints.OwnsEventAsync(contexts, persistence, organizerId, eventId, context.RequestAborted))
                return Results.NotFound();
        }
        return registry.Get(eventId) is { } runtime
            ? Results.Ok(await acks.ApplyAsync(runtime.BuildLiveView(registry.FinishedAt(eventId)), context.RequestAborted))
            : Results.NotFound();
    }

    /// <summary>
    /// The course and the event header for the public pages (anonymous; only events whose runtime exists, 404 otherwise):
    /// <c>{event:{name,sport,startTime,spectatorDelaySeconds},laps,lapLengthM,closed,points:[[lat,lon]…],checkpoints:[{name,lapDistanceM}]}</c>.
    /// The track is thinned to at most 1 500 points. <c>profile</c> is the elevation profile of one lap,
    /// <c>[[distanceM, elevationM]…]</c> thinned to at most 400 points, or null for a course without elevation.
    /// </summary>
    private static IResult GetCourse(string eventId, HttpContext context, EventRegistry registry)
    {
        if (registry.Get(eventId) is not { } runtime) return Results.NotFound();
        var course = runtime.Course;
        // the course changes only when the event is registered again (a new runtime): the tag follows that, and a revisit costs a 304
        var etag = $"W/\"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(runtime):x}-{runtime.Settings.StartTime.UtcTicks:x}-{(long)runtime.Settings.SpectatorDelay.TotalSeconds:x}-{runtime.Settings.Name.Length:x}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        if (context.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
        var step = Math.Max(1, (int)Math.Ceiling(course.Points.Count / 1500.0));
        var points = course.Points.Where((_, i) => i % step == 0 || i == course.Points.Count - 1)
            .Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lon, 6) }).ToList();
        var profileStep = Math.Max(1, (int)Math.Ceiling(course.Points.Count / 400.0));
        var profile = course.HasElevation
            ? Enumerable.Range(0, course.Points.Count).Where(i => i % profileStep == 0 || i == course.Points.Count - 1)
                .Select(i => new[] { Math.Round(course.CumulativeM[i]), Math.Round(course.Points[i].Ele!.Value, 1) }).ToList()
            : null;
        return Results.Ok(new
        {
            @event = new
            {
                name = runtime.Settings.Name, sport = runtime.Settings.Sport.ToString().ToLowerInvariant(),
                startTime = runtime.Settings.StartTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                spectatorDelaySeconds = (int)runtime.Settings.SpectatorDelay.TotalSeconds,
            },
            laps = course.Laps, lapLengthM = course.LapLengthM, closed = course.IsClosed, points, profile, checkpoints = course.Checkpoints,
        });
    }

    /// <summary>
    /// The public header of an event that is not a Draft: <c>{name,location,startTime,sport,state,endedAt}</c> with <c>state</c> one of
    /// <c>ready|live|finished</c> and <c>endedAt</c> null until it finished. Nothing about the organizer or the participants.
    /// </summary>
    private static async Task<IResult> GetInfo(string eventId, HttpContext context, IDbContextFactory<AppDbContext> contexts,
        IOptions<PersistenceOptions> persistence)
    {
        if (!persistence.Value.Enabled || !EventService.TryParseId(eventId, out var id)) return Results.NotFound();
        await using var db = await contexts.CreateDbContextAsync(context.RequestAborted);
        var found = await db.Events.AsNoTracking().Where(e => e.Id == id && e.State != EventState.Draft)
            .Select(e => new { e.Name, e.Location, e.StartTime, e.Sport, e.State, e.EndedAt }).FirstOrDefaultAsync(context.RequestAborted);
        if (found is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Ok(new
        {
            name = found.Name, location = found.Location,
            startTime = Utc(found.StartTime),
            sport = found.Sport.ToString().ToLowerInvariant(), state = found.State.ToString().ToLowerInvariant(),
            endedAt = found.State == EventState.Finished && found.EndedAt is { } ended ? Utc(ended) : null,
        });
    }

    private static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The results of a finished event for anyone: <c>{finished,dnf,dns,rows:[{rank,categoryRank,bib,name,category,status,netTime,
    /// gapToLeader,distanceM}]}</c>, finishers in rank order and then those who did not finish (DNS only counted). <c>name</c> follows
    /// the spectator rule on the participant's current consent: the nickname or the name if they agreed and are not a minor, else
    /// <c>#bib</c>. Times are whole seconds. Nothing else about the participants (no gender, wave, start or speeds).
    /// </summary>
    private static async Task<IResult> GetPublicResults(string eventId, HttpContext context, IDbContextFactory<AppDbContext> contexts,
        IOptions<PersistenceOptions> persistence)
    {
        if (!persistence.Value.Enabled || !EventService.TryParseId(eventId, out var id)) return Results.NotFound();
        var ct = context.RequestAborted;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Events.AsNoTracking().AnyAsync(e => e.Id == id && e.State == EventState.Finished, ct)) return Results.NotFound();
        var rows = await db.ResultRows.AsNoTracking().Where(r => r.EventId == id)
            .Select(r => new
            {
                r.Rank, r.CategoryRank, r.Bib, r.Category, r.Status, r.NetTime, r.GapToLeader, r.DistanceM, r.IsMinor,
                Person = db.Participants.Where(p => p.Id == r.ParticipantId)
                    .Select(p => new { p.Name, p.Nickname, p.NicknameHidden, p.ShowNameToSpectators, p.IsMinor }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        static TimeSpan? Seconds(TimeSpan? t) => t is { } v ? TimeSpan.FromSeconds(Math.Round(v.TotalSeconds)) : null;
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Ok(new
        {
            finished = rows.Count(r => r.Status == "finished"), dnf = rows.Count(r => r.Status == "dnf"), dns = rows.Count(r => r.Status == "dns"),
            rows = rows.Where(r => r.Status != "dns")
                .OrderBy(r => r.Rank ?? int.MaxValue).ThenByDescending(r => r.DistanceM).ThenBy(r => r.Bib, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    rank = r.Rank, categoryRank = r.CategoryRank, bib = r.Bib,
                    name = r.Person is { ShowNameToSpectators: true, IsMinor: false } person && !r.IsMinor
                        ? (!person.NicknameHidden && !string.IsNullOrWhiteSpace(person.Nickname) ? person.Nickname : person.Name)
                        : "#" + r.Bib,
                    category = r.Category, status = r.Status, netTime = Seconds(r.NetTime), gapToLeader = Seconds(r.GapToLeader),
                    distanceM = Math.Round(r.DistanceM),
                }),
        });
    }

    private static IResult GetPublic(string eventId, EventRegistry registry, SpectatorFeed feed)
    {
        if (registry.Get(eventId) is not { } runtime) return Results.NotFound();
        return Results.Ok(feed.GetCurrent(runtime)
            ?? new SpectatorView(runtime.Clock.GetUtcNow() - runtime.Settings.SpectatorDelay, eventId, []));
    }
}
