using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Operations;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;

namespace PositionTracker.Api.Live;

/// <summary>
/// SignalR hub at <see cref="Path"/>. Clients join an event's organizer group (with the organizer key) or its
/// spectator group; <see cref="LiveBroadcaster"/> pushes <see cref="LiveUpdateMethod"/> (the full organizer
/// view) to <see cref="OrganizerGroup"/> and <see cref="PublicUpdateMethod"/> (the delayed spectator view) to
/// <see cref="SpectatorGroup"/>. Groups are only joined through these methods.
/// </summary>
public sealed class LiveHub(EventRegistry registry, IOptions<OrganizerOptions> organizer, IDbContextFactory<AppDbContext> contexts,
    IOptions<PersistenceOptions> persistence, IOptions<PositionTracker.Api.Admin.AdminOptions> admins) : Hub
{
    public const string Path = "/hubs/live";
    public const string LiveUpdateMethod = "LiveUpdate";
    public const string PublicUpdateMethod = "PublicUpdate";

    public static string OrganizerGroup(string eventId) => "org:" + eventId;

    public static string SpectatorGroup(string eventId) => "spec:" + eventId;

    /// <summary>
    /// Joins the organizer group with the operator key, or (empty key) with an organizer session that owns the event; a
    /// wrong key, a foreign event or an unknown event is a <see cref="HubException"/>.
    /// </summary>
    public async Task JoinOrganizer(string eventId, string? apiKey)
    {
        if (!organizer.Value.IsValidKey(apiKey ?? ""))
        {
            var isAdmin = Context.GetHttpContext() is { } http && CookieAuth.CurrentOrganizer(http) is { } who && admins.Value.IsAdmin(who);
            var owner = Context.User is { } user ? CookieAuth.OrganizerId(user) : null;
            if (!isAdmin && (owner is not { } organizerId
                || !await OperationsEndpoints.OwnsEventAsync(contexts, persistence, organizerId, eventId, Context.ConnectionAborted)))
                throw new HubException("Invalid key");
        }
        EnsureEvent(eventId);
        await Groups.AddToGroupAsync(Context.ConnectionId, OrganizerGroup(eventId), Context.ConnectionAborted);
    }

    /// <summary>Joins the spectator group; an unknown event is a <see cref="HubException"/>.</summary>
    public Task JoinSpectator(string eventId)
    {
        EnsureEvent(eventId);
        return Groups.AddToGroupAsync(Context.ConnectionId, SpectatorGroup(eventId), Context.ConnectionAborted);
    }

    private void EnsureEvent(string eventId)
    {
        if (string.IsNullOrEmpty(eventId) || registry.Get(eventId) is null)
            throw new HubException("Unknown event");
    }
}
