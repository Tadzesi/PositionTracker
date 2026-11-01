using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Admin;

/// <summary>Configuration section <c>Admin</c>: who administers the application.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Addresses of the administrators, separated by comma, semicolon or space. Only a confirmed account counts.</summary>
    public string Emails { get; set; } = "";

    public bool IsAdmin(OrganizerEntity organizer)
    {
        if (organizer.EmailConfirmedAt is null) return false;
        foreach (var part in Emails.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (EmailAddress.TryParse(part, out _, out var normalized) && normalized == organizer.EmailNormalized) return true;
        return false;
    }
}

internal sealed class AdminRequirement : IAuthorizationRequirement;

internal sealed class AdminHandler(IHttpContextAccessor accessor, IOptions<AdminOptions> options) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (accessor.HttpContext is { } http && CookieAuth.CurrentOrganizer(http) is { } organizer
            && CookieAuth.OrganizerId(context.User) == organizer.Id && options.Value.IsAdmin(organizer))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The administrator's side. <c>GET /api/admin/events</c> (every event with its organizer and participant counts),
/// <c>GET /api/admin/organizers</c> (accounts with the time they spent on the site) and <c>GET /api/admin/audit</c> (what
/// administrators changed) need an administrator session (403 <c>forbidden</c> otherwise). An administrator edits an event
/// through the organizer API by sending <c>X-Act-As-Organizer: {organizerId}</c>: the request then runs as that organizer, and
/// every change (anything but GET) is recorded in the audit table. <c>POST /api/account/activity</c> is the heartbeat of an open
/// page (see <see cref="OrganizerActivityEntity"/>); only organizers are measured, never spectators or participants.
/// </summary>
public static class AdminEndpoints
{
    public const string ActAsHeader = "X-Act-As-Organizer";
    /// <summary>A heartbeat credits at most this long since the previous one (the page sends one every 30 s).</summary>
    public const int MaxCreditSeconds = 45;
    private const string LogCategory = "PositionTracker.Api.Admin.AdminEndpoints";

    public static IServiceCollection AddAdmin(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.Section);
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthorizationHandler, AdminHandler>();
        return services;
    }

    /// <summary>After authentication: honours <see cref="ActAsHeader"/> for administrators, refuses it for anybody else.</summary>
    public static IApplicationBuilder UseAdminActAs(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Headers.TryGetValue(ActAsHeader, out var header))
        {
            await next();
            return;
        }
        var admins = context.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (CookieAuth.CurrentOrganizer(context) is not { } admin || !admins.IsAdmin(admin))
        {
            await AccountErrors.Forbidden().ExecuteAsync(context);
            return;
        }
        if (!Guid.TryParse(header.ToString(), out var target))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        context.Items[CookieAuth.ActAsItemKey] = target;
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? "";
            await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>()
                .CreateDbContextAsync(context.RequestAborted);
            db.AdminAudit.Add(new AdminAuditEntity
            {
                At = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), AdminId = admin.Id, OrganizerId = target,
                Method = context.Request.Method, Path = path.Length > 300 ? path[..300] : path,
            });
            await db.SaveChangesAsync(context.RequestAborted);
        }
        await next();
    });

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account/activity", HeartbeatAsync).RequireAuthorization(CookieAuth.OrganizerSessionPolicy);
        var group = app.MapGroup("/api/admin")
            .RequireAuthorization(CookieAuth.AdminPolicy)
            .WithDatabaseGuard(LogCategory);
        group.MapGet("/events", EventsAsync);
        group.MapGet("/organizers", OrganizersAsync);
        group.MapGet("/audit", AuditAsync);
        return app;
    }

    public sealed record AdminEvent(string EventId, string Name, string Location, DateTimeOffset StartTime, Sport Sport, EventState State,
        int Laps, double? LapLengthM, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, string OrganizerId, string OrganizerName,
        string OrganizerEmail, ParticipantCounts Participants);

    public sealed record AdminOrganizer(string OrganizerId, string Name, string Email, DateTimeOffset CreatedAt, bool EmailConfirmed,
        int Events, long ActiveSeconds, long ActiveSecondsLast30Days, DateTimeOffset? LastSeenAt);

    public sealed record AuditRow(DateTimeOffset At, string AdminEmail, string OrganizerEmail, string Method, string Path);

    private static async Task<IResult> HeartbeatAsync(HttpContext context, IDbContextFactory<AppDbContext> contexts, TimeProvider clock,
        IOptions<PersistenceOptions> persistence)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!persistence.Value.Enabled || CookieAuth.OrganizerId(context.User) is not { } id) return Results.NoContent();
        var now = clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await using var db = await contexts.CreateDbContextAsync(context.RequestAborted);
        try
        {
            var row = await db.OrganizerActivity.SingleOrDefaultAsync(a => a.OrganizerId == id && a.Day == day, context.RequestAborted);
            if (row is null)
                db.OrganizerActivity.Add(new OrganizerActivityEntity { OrganizerId = id, Day = day, Seconds = 0, LastBeatAt = now });
            else
            {
                row.Seconds += (int)Math.Clamp((now - row.LastBeatAt).TotalSeconds, 0, MaxCreditSeconds);
                row.LastBeatAt = now;
            }
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Two first beats of the day at once: the other one won, this one is not worth a retry.
        }
        return Results.NoContent();
    }

    private static async Task<IResult> EventsAsync(IDbContextFactory<AppDbContext> contexts, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var events = await db.Events.AsNoTracking().OrderByDescending(e => e.StartTime).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.OrganizerId, e.Name, e.Location, e.StartTime, e.Sport, e.State, e.Laps, e.CreatedAt, e.EndedAt, HasGpx = e.GpxBytes != null })
            .ToListAsync(ct);
        var lapLengths = await db.Courses.AsNoTracking().ToDictionaryAsync(c => c.EventId, c => c.LapLengthM, ct);
        var organizers = await db.Organizers.AsNoTracking().ToDictionaryAsync(o => o.Id, o => new { o.Name, o.Email }, ct);
        var counts = (await db.Participants.AsNoTracking().GroupBy(p => new { p.EventId, p.Status })
            .Select(g => new { g.Key.EventId, g.Key.Status, Count = g.Count() }).ToListAsync(ct)).ToLookup(c => c.EventId);
        int Of(Guid id, ParticipantStatus s) => counts[id].SingleOrDefault(c => c.Status == s)?.Count ?? 0;
        return Results.Ok(events.Select(e =>
        {
            organizers.TryGetValue(e.OrganizerId, out var o);
            return new AdminEvent(RuntimeFactory.RuntimeId(e.Id), e.Name, e.Location, e.StartTime, e.Sport, e.State, e.Laps,
                e.HasGpx && lapLengths.TryGetValue(e.Id, out var lap) ? lap : null, e.CreatedAt, e.EndedAt,
                e.OrganizerId.ToString("D"), o?.Name ?? "?", o?.Email ?? "?",
                new ParticipantCounts(Of(e.Id, ParticipantStatus.Invited), Of(e.Id, ParticipantStatus.Confirmed),
                    Of(e.Id, ParticipantStatus.Declined), Of(e.Id, ParticipantStatus.EmailFailed)));
        }).ToList());
    }

    private static async Task<IResult> OrganizersAsync(IDbContextFactory<AppDbContext> contexts, TimeProvider clock, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-30);
        var organizers = await db.Organizers.AsNoTracking().OrderBy(o => o.CreatedAt).ToListAsync(ct);
        var eventCounts = await db.Events.AsNoTracking().GroupBy(e => e.OrganizerId).Select(g => new { Id = g.Key, N = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.N, ct);
        var activity = (await db.OrganizerActivity.AsNoTracking().GroupBy(a => a.OrganizerId)
            .Select(g => new { Id = g.Key, Total = g.Sum(a => (long)a.Seconds), Recent = g.Where(a => a.Day >= since).Sum(a => (long)a.Seconds), Last = g.Max(a => a.LastBeatAt) })
            .ToListAsync(ct)).ToDictionary(a => a.Id);
        return Results.Ok(organizers.Select(o =>
        {
            activity.TryGetValue(o.Id, out var a);
            return new AdminOrganizer(o.Id.ToString("D"), o.Name, o.Email, o.CreatedAt, o.EmailConfirmedAt is not null,
                eventCounts.GetValueOrDefault(o.Id), a?.Total ?? 0, a?.Recent ?? 0, a?.Last);
        }).OrderByDescending(o => o.ActiveSeconds).ToList());
    }

    private static async Task<IResult> AuditAsync(IDbContextFactory<AppDbContext> contexts, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.AdminAudit.AsNoTracking().OrderByDescending(a => a.At).Take(200).ToListAsync(ct);
        var emails = await db.Organizers.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Email, ct);
        return Results.Ok(rows.Select(r => new AuditRow(r.At, emails.GetValueOrDefault(r.AdminId, "?"), emails.GetValueOrDefault(r.OrganizerId, "?"),
            r.Method, r.Path)).ToList());
    }
}
