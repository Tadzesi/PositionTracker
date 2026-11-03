using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;

namespace PositionTracker.Api.Registration;

/// <summary>
/// Participant registration by the organizer, under <c>/api/events/{eventId}/participants</c> (JSON, camelCase; errors
/// <c>{"code","message":{"sk","en"},"field"?}</c>). Every route needs an organizer session (401 <c>unauthorized</c>)
/// with a confirmed email (403 <c>email_not_confirmed</c>); an event or participant of another organizer, unknown or
/// non-canonical ids and simulated events are 404 <c>not_found</c>. Unsafe requests need
/// <c>X-Requested-With: PositionTracker</c> and <c>Content-Type: application/json</c> (<see cref="BrowserApiHeaders"/>).
/// <list type="bullet">
/// <item><c>POST {name,email,gender,language,category?,wave?,bib?}</c> → 201 <c>{participantId,bib,status:"Invited",emailMode}</c>;
/// 400 <c>validation_failed</c> (+ field), 409 <c>bib_taken|email_duplicate|participant_limit|event_finished</c>,
/// 429 <c>rate_limited</c>.</item>
/// <item><c>GET ?status=&amp;category=&amp;q=</c> → <c>{emailMode,eventState,counts:{all,confirmed,invited,declined,emailFailed},participants:[…]}</c>.</item>
/// <item><c>PUT /{pid} {name?,email?,gender?,language?,category?,wave?,bib?,nickname?,hideNickname?}</c> → 200 the
/// participant (+ <c>emailMode</c>); a new address mails a new invitation.</item>
/// <item><c>DELETE /{pid}</c> → 204 (Draft and Ready only; 409 <c>event_live|event_finished</c>).</item>
/// <item><c>POST /{pid}/resend</c> → 202 <c>{emailMode}</c>; <c>POST /{pid}/reissue-token</c> → 200
/// <c>{deviceTokenIssued:true,emailMode}</c>; <c>POST /remind</c> → 202 <c>{sent,limited,emailMode}</c>.</item>
/// </list>
/// Without a database every route answers 503 <c>unavailable</c>; a database failure too.
/// </summary>
public static class RegistrationEndpoints
{
    private const long BodyBytes = 8 * 1024;
    private const string LogCategory = "PositionTracker.Api.Registration.RegistrationEndpoints";

    public static IServiceCollection AddRegistration(this IServiceCollection services)
    {
        services.AddOptions<RegistrationOptions>().BindConfiguration(RegistrationOptions.Section).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RegistrationOptions>, RegistrationOptionsValidator>();
        services.AddSingleton<InvitationLimiter>();
        services.AddScoped<DbTokenService>();
        services.AddSingleton<DeviceTokenCache>();
        services.AddSingleton<DeviceTokenWarmer>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceTokenWarmer>());
        services.AddScoped<ITokenService, DeviceTokens>();
        services.AddScoped<RegistrationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/{eventId}/participants")
            .RequireAuthorization(CookieAuth.OrganizerSessionPolicy)
            .RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy)
            .WithDatabaseGuard(LogCategory);

        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapPost("/remind", RemindAsync);
        group.MapPut("/{participantId}", UpdateAsync);
        group.MapDelete("/{participantId}", DeleteAsync);
        group.MapPost("/{participantId}/resend", ResendAsync);
        group.MapPost("/{participantId}/reissue-token", ReissueAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(string eventId, HttpContext context, RegistrationService registration, IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await EventManagementEndpoints.ReadAsync<CreateParticipantRequest>(context, json, BodyBytes);
        if (error is not null) return error;
        var result = await registration.CreateAsync(organizer, eventId, body!, context.RequestAborted);
        return result.Error is { } e
            ? EventErrors.Result(e)
            : Results.Created($"/api/events/{eventId}/participants/{result.Value!.ParticipantId}", result.Value);
    }

    private static async Task<IResult> ListAsync(string eventId, string? status, string? category, string? q, HttpContext context,
        RegistrationService registration) =>
        Organizer(context) is { } organizer
            ? Answer(await registration.ListAsync(organizer, eventId, status, category, q, context.RequestAborted), StatusCodes.Status200OK)
            : AccountErrors.Unauthorized();

    private static async Task<IResult> UpdateAsync(string eventId, string participantId, HttpContext context, RegistrationService registration,
        IOptions<JsonOptions> json)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        var (body, error) = await EventManagementEndpoints.ReadAsync<UpdateParticipantRequest>(context, json, BodyBytes);
        if (error is not null) return error;
        return Answer(await registration.UpdateAsync(organizer, eventId, participantId, body!, context.RequestAborted), StatusCodes.Status200OK);
    }

    private static async Task<IResult> DeleteAsync(string eventId, string participantId, HttpContext context, RegistrationService registration)
    {
        if (Organizer(context) is not { } organizer) return AccountErrors.Unauthorized();
        return await registration.DeleteAsync(organizer, eventId, participantId, context.RequestAborted) is { } e
            ? EventErrors.Result(e)
            : Results.NoContent();
    }

    private static async Task<IResult> ResendAsync(string eventId, string participantId, HttpContext context, RegistrationService registration) =>
        Organizer(context) is { } organizer
            ? Answer(await registration.ResendAsync(organizer, eventId, participantId, context.RequestAborted), StatusCodes.Status202Accepted)
            : AccountErrors.Unauthorized();

    private static async Task<IResult> ReissueAsync(string eventId, string participantId, HttpContext context, RegistrationService registration) =>
        Organizer(context) is { } organizer
            ? Answer(await registration.ReissueTokenAsync(organizer, eventId, participantId, context.RequestAborted), StatusCodes.Status200OK)
            : AccountErrors.Unauthorized();

    private static async Task<IResult> RemindAsync(string eventId, HttpContext context, RegistrationService registration) =>
        Organizer(context) is { } organizer
            ? Answer(await registration.RemindAsync(organizer, eventId, context.RequestAborted), StatusCodes.Status202Accepted)
            : AccountErrors.Unauthorized();

    private static IResult Answer<T>(EventResult<T> result, int status) =>
        result.Error is { } e ? EventErrors.Result(e) : Results.Json(result.Value, statusCode: status);

    private static Guid? Organizer(HttpContext context) => CookieAuth.ActingOrganizerId(context);
}
