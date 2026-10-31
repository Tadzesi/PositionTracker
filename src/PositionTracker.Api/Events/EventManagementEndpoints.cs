using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Persistence;

namespace PositionTracker.Api.Events;

/// <summary>
/// The organizer's events (JSON, camelCase; errors <c>{"code","message":{"sk","en"},"field"?}</c>, see
/// <see cref="EventErrors"/>). Every route needs an organizer session (<c>OrganizerSession</c>, 401
/// <c>unauthorized</c>); the course, categories, waves and state routes also a confirmed email
/// (<c>EmailConfirmedOrganizer</c>, 403 <c>email_not_confirmed</c>). An event of another organizer, an unknown id and a
/// simulated event are all 404 <c>not_found</c>. Unsafe requests need <c>X-Requested-With: PositionTracker</c> and
/// <c>Content-Type: application/json</c> (<see cref="BrowserApiHeaders"/>, before anything else).
/// <list type="bullet">
/// <item><c>POST /api/events {name, location, startTime, sport}</c> → 201 <c>{eventId}</c> (a Draft; allowed before the
/// email is confirmed).</item>
/// <item><c>GET /api/events</c> → <c>{emailMode, events:[{eventId,name,location,startTime,sport,state}]}</c>, own
/// events only.</item>
/// <item><c>GET /api/events/{id}/manage</c> → <see cref="EventManageView"/> (with <c>emailMode</c>).</item>
/// <item><c>PUT /api/events/{id}/course {gpxBase64, laps, checkpoints:[{name,lapDistanceM}], spectatorDelaySeconds}</c>
/// → 200 view; 400 <c>gpx_invalid|course_too_short|course_not_closed|checkpoint_invalid|delay_invalid|laps_invalid</c>,
/// 413 <c>gpx_too_large</c>, 409 <c>event_locked|conflict</c>.</item>
/// <item><c>PUT /api/events/{id}/categories [{name, gender?, minAge?, maxAge?}]</c> and
/// <c>PUT /api/events/{id}/waves [{name, startTime}]</c> replace the whole set → 200 view; 400
/// <c>validation_failed</c> (+ <c>field</c>), 409 <c>event_locked|conflict</c>.</item>
/// <item><c>POST /api/events/{id}/state {state: "ready"|"live"|"finished"}</c> → 200 view (repeating the current state
/// is a success); 409 <c>invalid_transition|course_missing|no_confirmed_participants|conflict</c>, 500
/// <c>runtime_failed</c>.</item>
/// </list>
/// Without a database every route answers 503 <c>unavailable</c>; a database failure too. Answers carry
/// <c>Cache-Control: no-store</c>. Nothing personal is logged (ids and exception types only).
/// </summary>
public static class EventManagementEndpoints
{
    private const long SmallBodyBytes = 8 * 1024;
    private const long ListBodyBytes = 64 * 1024;
    /// <summary>Leaves room for a 1 MB GPX as base64 plus the rest of the course.</summary>
    private const long CourseBodyBytes = 2 * 1024 * 1024;
    private const string LogCategory = "PositionTracker.Api.Events.EventManagementEndpoints";

    public static IServiceCollection AddEventManagement(this IServiceCollection services) =>
        services.AddScoped<EventService>();

    public static IEndpointRouteBuilder MapEventManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events")
            .RequireAuthorization(CookieAuth.OrganizerSessionPolicy)
            .WithDatabaseGuard(LogCategory);

        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{eventId}/manage", ManageAsync);
        group.MapPut("/{eventId}/course", PutCourseAsync).RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy);
        group.MapPut("/{eventId}/categories", PutCategoriesAsync).RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy);
        group.MapPut("/{eventId}/waves", PutWavesAsync).RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy);
        group.MapPost("/{eventId}/state", SetStateAsync).RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext context, EventService events) =>
        Organizer(context) is { } organizer
            ? Results.Ok(await events.ListAsync(organizer, context.RequestAborted))
            : AccountErrors.Unauthorized();

    private static async Task<IResult> CreateAsync(HttpContext context, EventService events, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await ReadAsync<CreateEventRequest>(context, json, SmallBodyBytes);
        if (error is not null) return error;
        var result = await events.CreateAsync(organizer, body!, context.RequestAborted);
        return result.Error is { } e
            ? EventErrors.Result(e)
            : Results.Created($"/api/events/{result.Value!.EventId}/manage", result.Value);
    }

    private static async Task<IResult> ManageAsync(string eventId, HttpContext context, EventService events) =>
        Organizer(context) is { } organizer
            ? Answer(await events.GetAsync(organizer, eventId, context.RequestAborted))
            : AccountErrors.Unauthorized();

    private static async Task<IResult> PutCourseAsync(string eventId, HttpContext context, EventService events, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await ReadAsync<CourseRequest>(context, json, CourseBodyBytes);
        if (error is not null) return error;
        return Answer(await events.PutCourseAsync(organizer, eventId, body!, context.RequestAborted));
    }

    private static async Task<IResult> PutCategoriesAsync(string eventId, HttpContext context, EventService events, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await ReadAsync<List<CategoryRequest?>>(context, json, ListBodyBytes);
        if (error is not null) return error;
        return Answer(await events.PutCategoriesAsync(organizer, eventId, body!, context.RequestAborted));
    }

    private static async Task<IResult> PutWavesAsync(string eventId, HttpContext context, EventService events, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await ReadAsync<List<WaveRequest?>>(context, json, ListBodyBytes);
        if (error is not null) return error;
        return Answer(await events.PutWavesAsync(organizer, eventId, body!, context.RequestAborted));
    }

    private static async Task<IResult> SetStateAsync(string eventId, HttpContext context, EventService events, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await ReadAsync<StateRequest>(context, json, SmallBodyBytes);
        if (error is not null) return error;
        return Answer(await events.SetStateAsync(organizer, eventId, body!.State, context.RequestAborted));
    }

    /// <summary>
    /// The organizer API guard: <c>Cache-Control: no-store</c>, 503 <c>unavailable</c> without a database, and a database
    /// failure answered 503 <c>unavailable</c> (logged with the route and exception type only).
    /// </summary>
    internal static RouteGroupBuilder WithDatabaseGuard(this RouteGroupBuilder group, string logCategory)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!http.RequestServices.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled)
                return EventErrors.Result(EventErrors.Unavailable);
            try
            {
                return await next(context);
            }
            catch (Exception ex) when (IsDatabaseFailure(ex) && !http.RequestAborted.IsCancellationRequested)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(logCategory)
                    .LogError("Event request {Method} {Route} failed ({ExceptionType}); answered 503.",
                        http.Request.Method, (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText, ex.GetType().Name);
                return EventErrors.Result(EventErrors.Unavailable);
            }
        });
        return group;
    }

    private static IResult Answer(EventResult<EventManageView> result) =>
        result.Error is { } e ? EventErrors.Result(e) : Results.Ok(result.Value);

    private static Guid? Organizer(HttpContext context) => CookieAuth.OrganizerId(context.User);

    internal static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext context, IOptions<JsonOptions> json, long maxBytes)
        where T : class
    {
        var (body, readError) = await JsonBody.ReadAsync<T>(context, json.Value.SerializerOptions, maxBytes);
        if (readError is not null)
            return (null, EventErrors.Result((readError as IStatusCodeHttpResult)?.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? EventErrors.BodyTooLarge
                : EventErrors.InvalidBody));
        return body is null ? (null, EventErrors.Result(EventErrors.InvalidBody)) : (body, null);
    }

    private static bool IsDatabaseFailure(Exception ex) => EventService.IsDatabaseFailure(ex);
}
