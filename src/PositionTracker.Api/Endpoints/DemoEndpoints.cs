using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Simulation;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Organizer endpoints for simulated demo events, all requiring a valid <c>X-Api-Key</c> (401 otherwise, checked
/// before anything else): <c>POST /api/demo/events</c> (body <see cref="DemoRequest"/>; 201 with
/// <see cref="DemoResponse"/>, 400 invalid request or GPX, 409 too many running, 413 body over
/// <see cref="MaxBodyBytes"/>), <c>GET /api/demo/events</c> (the simulated events only) and
/// <c>DELETE /api/demo/events/{eventId}</c> (204; 404 for an unknown or real event).
/// </summary>
public static class DemoEndpoints
{
    /// <summary>Request body cap; leaves room for a 1 MB GPX as base64.</summary>
    public const long MaxBodyBytes = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/demo/events").AddEndpointFilter(async (context, next) =>
        {
            var organizer = context.HttpContext.RequestServices.GetRequiredService<IOptions<OrganizerOptions>>().Value;
            return organizer.IsValidKey(context.HttpContext.Request.Headers[OrganizerOptions.ApiKeyHeader].ToString())
                ? await next(context)
                : Results.Unauthorized();
        });
        group.MapPost("", Create);
        group.MapGet("", (SimulationService simulation) => Results.Ok(simulation.List()));
        group.MapDelete("/{eventId}", (string eventId, SimulationService simulation) =>
            simulation.Stop(eventId) ? Results.NoContent() : Results.NotFound());
        return app;
    }

    private static async Task<IResult> Create(HttpContext context, SimulationService simulation, IOptions<JsonOptions> json)
    {
        var (request, error) = await JsonBody.ReadAsync<DemoRequest>(context, json.Value.SerializerOptions, MaxBodyBytes);
        if (error is not null) return error;
        if (request is null)
            return Results.Problem(
                "Expected {\"participants\",\"timeScale\",\"laps\",\"gpxBase64\",\"sport\"} (all optional).",
                statusCode: StatusCodes.Status400BadRequest);
        try
        {
            var created = simulation.Start(request);
            return Results.Created(created.OrganizerUrl, created);
        }
        catch (DemoRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
