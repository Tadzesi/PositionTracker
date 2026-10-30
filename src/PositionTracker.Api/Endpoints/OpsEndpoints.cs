using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Operator endpoints, all requiring a valid <c>X-Api-Key</c> (401 otherwise).
/// <c>GET /api/ops/email-mode</c>: <c>{"mode": "smtp"|"simulated", "simulatedLast24h": n}</c>, the number of outbox
/// rows finished as <c>Simulated</c> in the last 24 hours (null without a database; rows already removed by
/// <c>Email:Outbox:RetentionDays</c> are not counted), plus <c>limitHitsLast24h</c> and <c>limitHits</c>
/// (<c>global, perEmail, perEmailAndIp, perIp, perPrefix</c>): account sign-up/sign-in requests and mails suppressed
/// by a rate limit in the last 24 hours (counts only), for alerting (controller ruling P11).
/// </summary>
public static class OpsEndpoints
{
    public sealed record EmailModeResponse(string Mode, int? SimulatedLast24h, int LimitHitsLast24h, LimitHits LimitHits);

    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ops").AddEndpointFilter(async (context, next) =>
        {
            var organizer = context.HttpContext.RequestServices.GetRequiredService<IOptions<OrganizerOptions>>().Value;
            return organizer.IsValidKey(context.HttpContext.Request.Headers[OrganizerOptions.ApiKeyHeader].ToString())
                ? await next(context)
                : Results.Unauthorized();
        });
        group.MapGet("/email-mode", EmailModeAsync);
        return app;
    }

    private static async Task<IResult> EmailModeAsync(IEmailMode mode, IOptions<PersistenceOptions> persistence,
        IDbContextFactory<AppDbContext> contexts, TimeProvider clock, AccountRateLimiter limiter, CancellationToken ct)
    {
        int? simulated = null;
        if (persistence.Value.Enabled)
        {
            var since = clock.GetUtcNow() - TimeSpan.FromHours(24);
            await using var db = await contexts.CreateDbContextAsync(ct);
            simulated = await db.Outbox.CountAsync(r => r.Status == EmailOutboxStatus.Simulated && r.FinishedAt >= since, ct);
        }
        var hits = limiter.LimitHitsLast24h();
        return Results.Ok(new EmailModeResponse(mode.Mode, simulated, hits.Total, hits));
    }
}
