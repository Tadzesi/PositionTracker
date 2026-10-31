using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Operations;

/// <summary>
/// Organizer operations during and after an event, under <c>/api/events/{eventId}</c> (organizer session, confirmed email;
/// another organizer's or an unknown event is 404 <c>not_found</c>; unsafe requests need the browser headers):
/// <list type="bullet">
/// <item><c>POST /alarms/ack {participantId, kind}</c> → 204; the acknowledgement shows as <c>acknowledged:true</c> on the alarm in
/// the live view until the alarm clears. 409 <c>event_not_live</c>, 409 <c>alarm_not_active</c>.</item>
/// <item><c>POST /participants/{pid}/dnf</c> → 204 (the organizer marks the rider as withdrawn; persisted in the action log).</item>
/// <item><c>DELETE /participants/{pid}/data</c> → 204: privacy deletion on request. Fixes, device statuses, consents, tokens, links and
/// queued mails of the participant are deleted; name, email and nickname are anonymised (the name becomes <c>#bib</c>); the event
/// results keep the bib only. A running event keeps the rider's in-memory track until the next restart.</item>
/// </list>
/// </summary>
public static class OperationsEndpoints
{
    private const string LogCategory = "PositionTracker.Api.Operations.OperationsEndpoints";

    public static IServiceCollection AddOperations(this IServiceCollection services)
    {
        services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.Section)
            .Validate(o => o.FixDays > 0 && o.CheckIntervalHours > 0, "Retention:FixDays and CheckIntervalHours must be positive.")
            .ValidateOnStart();
        services.AddSingleton<AlarmAckService>();
        services.AddScoped<OperationsService>();
        services.AddSingleton<RetentionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionWorker>());
        return services;
    }

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/{eventId}")
            .RequireAuthorization(CookieAuth.OrganizerSessionPolicy)
            .RequireAuthorization(CookieAuth.EmailConfirmedOrganizerPolicy)
            .WithDatabaseGuard(LogCategory);
        group.MapPost("/alarms/ack", async (string eventId, HttpContext context, OperationsService ops, IOptions<JsonOptions> json) =>
        {
            if (CookieAuth.OrganizerId(context.User) is not { } organizer) return AccountErrors.Unauthorized();
            var (body, error) = await EventManagementEndpoints.ReadAsync<AckRequest>(context, json, 2048);
            return error ?? Answer(await ops.AckAsync(organizer, eventId, body!, context.RequestAborted));
        });
        group.MapPost("/participants/{participantId}/dnf", async (string eventId, string participantId, HttpContext context, OperationsService ops) =>
            CookieAuth.OrganizerId(context.User) is { } organizer
                ? Answer(await ops.DnfAsync(organizer, eventId, participantId, context.RequestAborted))
                : AccountErrors.Unauthorized());
        group.MapDelete("/participants/{participantId}/data", async (string eventId, string participantId, HttpContext context, OperationsService ops) =>
            CookieAuth.OrganizerId(context.User) is { } organizer
                ? Answer(await ops.DeleteDataAsync(organizer, eventId, participantId, context.RequestAborted))
                : AccountErrors.Unauthorized());
        return app;
    }

    /// <summary>Whether the organizer owns the (database) event; used by the live read and the hub.</summary>
    public static async Task<bool> OwnsEventAsync(IDbContextFactory<AppDbContext> contexts, IOptions<PersistenceOptions> persistence,
        Guid organizerId, string eventId, CancellationToken ct)
    {
        if (!persistence.Value.Enabled || !EventService.TryParseId(eventId, out var id)) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Events.AnyAsync(e => e.Id == id && e.OrganizerId == organizerId, ct);
    }

    private static IResult Answer(EventError? error) => error is { } e ? EventErrors.Result(e) : Results.NoContent();
}

public sealed record AckRequest(string? ParticipantId, string? Kind);

public static class OperationsErrors
{
    public static readonly EventError NotLive = new(StatusCodes.Status409Conflict, "event_not_live",
        "Podujatie nebeží.", "The event is not live.");

    public static readonly EventError AlarmNotActive = new(StatusCodes.Status409Conflict, "alarm_not_active",
        "Účastník tento alarm momentálne nemá.", "The participant does not have this alarm right now.");

    public static readonly EventError UnknownParticipant = new(StatusCodes.Status404NotFound, "not_found",
        "Účastník v podujatí neexistuje.", "The participant is not in the event.");

    public static readonly EventError InvalidKind = new(StatusCodes.Status400BadRequest, "validation_failed",
        "Druh alarmu musí byť Sos, Stopped, OffCourse, Offline, LowBattery alebo LapsInferred.",
        "The alarm kind must be Sos, Stopped, OffCourse, Offline, LowBattery or LapsInferred.", "kind");

    public static readonly EventError Busy = new(StatusCodes.Status503ServiceUnavailable, "busy",
        "Záznam polôh je práve zaneprázdnený. Skúste to, prosím, o chvíľu.", "The position store is busy. Please try again in a moment.");
}

/// <summary>Organizer operations (scoped, on the request's <see cref="AppDbContext"/>); limited to the organizer's own events.</summary>
public sealed class OperationsService(AppDbContext db, EventRegistry registry, AlarmAckService acks, PersistenceQueue history, ITokenService deviceTokens)
{
    public async Task<EventError?> AckAsync(Guid organizerId, string eventId, AckRequest request, CancellationToken ct)
    {
        if (await FindEventAsync(organizerId, eventId, ct) is not { } ev) return EventErrors.NotFound;
        if (!Enum.TryParse<AlarmKind>(request.Kind?.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return OperationsErrors.InvalidKind;
        if (Runtime(ev) is not { } runtime) return OperationsErrors.NotLive;
        if (request.ParticipantId is not { } participantId || !runtime.HasParticipant(participantId)) return OperationsErrors.UnknownParticipant;
        return await acks.AckAsync(runtime, participantId, kind, ct) ? null : OperationsErrors.AlarmNotActive;
    }

    public async Task<EventError?> DnfAsync(Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        if (await FindEventAsync(organizerId, eventId, ct) is not { } ev) return EventErrors.NotFound;
        if (registry.IsFinished(RuntimeFactory.RuntimeId(ev.Id))) return EventErrors.EventFinished;
        if (Runtime(ev) is not { } runtime) return OperationsErrors.NotLive;
        if (!runtime.HasParticipant(participantId)) return OperationsErrors.UnknownParticipant;

        var persist = history.ShouldPersist(runtime.Settings);
        if (persist && !await history.TryReserveAsync(ct)) return OperationsErrors.Busy;
        var reserved = persist;
        try
        {
            var at = runtime.Act(participantId, ParticipantAction.Dnf);
            if (persist)
            {
                reserved = false;
                if (!history.Enqueue(new ActionWrite(runtime.Settings.EventId, participantId, ParticipantAction.Dnf, at))) return OperationsErrors.Busy;
            }
            return null;
        }
        finally
        {
            if (reserved) history.Release();
        }
    }

    public async Task<EventError?> DeleteDataAsync(Guid organizerId, string eventId, string participantId, CancellationToken ct)
    {
        if (await FindEventAsync(organizerId, eventId, ct) is not { } ev) return EventErrors.NotFound;
        if (!EventService.TryParseId(participantId, out var pid)) return OperationsErrors.UnknownParticipant;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.Participants.SingleOrDefaultAsync(x => x.Id == pid && x.EventId == ev.Id, ct);
        if (p is null) return OperationsErrors.UnknownParticipant;

        await db.Fixes.Where(f => f.EventId == ev.Id && f.ParticipantId == pid).ExecuteDeleteAsync(ct);
        await db.DeviceStatuses.Where(s => s.EventId == ev.Id && s.ParticipantId == pid).ExecuteDeleteAsync(ct);
        await db.Consents.Where(c => c.ParticipantId == pid).ExecuteDeleteAsync(ct);
        await db.LinkTokens.Where(t => t.Purpose == nameof(LinkPurpose.ParticipantInvite) && t.SubjectId == pid).ExecuteDeleteAsync(ct);
        await db.Outbox.Where(m => m.ParticipantId == pid).ExecuteDeleteAsync(ct);
        await db.AlarmAcks.Where(a => a.EventId == ev.Id && a.ParticipantId == pid).ExecuteDeleteAsync(ct);
        await deviceTokens.RevokeAsync(RuntimeFactory.RuntimeId(ev.Id), pid.ToString("D"), ct);

        var anonymous = $"deleted-{pid:N}@deleted.invalid";
        p.Name = "#" + p.Bib;
        p.Nickname = null;
        p.NicknameHidden = true;
        p.Email = anonymous;
        p.EmailNormalized = anonymous;
        p.ShowNameToSpectators = false;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        if (registry.Get(runtimeId) is { } runtime && !registry.IsFinished(runtimeId) && runtime.HasParticipant(pid.ToString("D")))
        {
            await db.Entry(p).Reference(x => x.Category).LoadAsync(ct);
            runtime.UpdateParticipant(RuntimeFactory.ParticipantInfoOf(p));
        }
        return null;
    }

    private Task<EventEntity?> FindEventAsync(Guid organizerId, string eventId, CancellationToken ct) =>
        EventService.TryParseId(eventId, out var id)
            ? db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId, ct)
            : Task.FromResult<EventEntity?>(null);

    private EventRuntime? Runtime(EventEntity ev) => registry.Get(RuntimeFactory.RuntimeId(ev.Id));
}
