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
/// <see cref="SpectatorGroup"/>. Groups are only joined through these methods. Connections are capped per client address and
/// in total (<see cref="HubConnectionLimiter"/>), a connection may join at most <see cref="MaxGroupsPerConnection"/> groups,
/// and wrong operator keys are counted per address (<see cref="ApiKeyGuard"/>).
/// </summary>
public sealed class LiveHub(EventRegistry registry, ApiKeyGuard keys, IDbContextFactory<AppDbContext> contexts,
    IOptions<PersistenceOptions> persistence, IOptions<PositionTracker.Api.Admin.AdminOptions> admins, HubConnectionLimiter connections) : Hub
{
    public const string Path = "/hubs/live";

    public const string LiveUpdateMethod = "LiveUpdate";
    public const string PublicUpdateMethod = "PublicUpdate";

    public static string OrganizerGroup(string eventId) => "org:" + eventId;

    public static string SpectatorGroup(string eventId) => "spec:" + eventId;

    /// <summary>Groups one connection may join (a dashboard watches one event; a few is plenty).</summary>
    public const int MaxGroupsPerConnection = 4;

    private static readonly object SlotKey = new();
    private static readonly object GroupsKey = new();

    public override Task OnConnectedAsync()
    {
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        if (!connections.TryOpen(ip))
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        Context.Items[SlotKey] = ip;
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(SlotKey, out var ip)) connections.Close(ip as System.Net.IPAddress);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Joins the organizer group with the operator key, or (empty key) with an organizer session that owns the event; a
    /// wrong key, a foreign event or an unknown event is a <see cref="HubException"/>.
    /// </summary>
    public async Task JoinOrganizer(string eventId, string? apiKey)
    {
        if (!keys.IsValidKey(apiKey, Context.GetHttpContext()?.Connection.RemoteIpAddress))
        {
            var isAdmin = Context.GetHttpContext() is { } http && CookieAuth.CurrentOrganizer(http) is { } who && admins.Value.IsAdmin(who);
            var owner = Context.User is { } user ? CookieAuth.OrganizerId(user) : null;
            if (!isAdmin && (owner is not { } organizerId
                || !await OperationsEndpoints.OwnsEventAsync(contexts, persistence, organizerId, eventId, Context.ConnectionAborted)))
                throw new HubException("Invalid key");
        }
        EnsureEvent(eventId);
        TakeGroup(OrganizerGroup(eventId));
        await Groups.AddToGroupAsync(Context.ConnectionId, OrganizerGroup(eventId), Context.ConnectionAborted);
    }

    /// <summary>Joins the spectator group; an unknown event is a <see cref="HubException"/>.</summary>
    public Task JoinSpectator(string eventId)
    {
        EnsureEvent(eventId);
        TakeGroup(SpectatorGroup(eventId));
        return Groups.AddToGroupAsync(Context.ConnectionId, SpectatorGroup(eventId), Context.ConnectionAborted);
    }

    /// <summary>Records a joined group; joining the same group again is free, a group beyond the cap is a <see cref="HubException"/>.</summary>
    private void TakeGroup(string group)
    {
        if (Context.Items[GroupsKey] is not HashSet<string> joined) Context.Items[GroupsKey] = joined = new HashSet<string>(StringComparer.Ordinal);
        if (!joined.Contains(group) && joined.Count >= MaxGroupsPerConnection) throw new HubException("Too many groups");
        joined.Add(group);
    }

    private void EnsureEvent(string eventId)
    {
        if (string.IsNullOrEmpty(eventId) || registry.Get(eventId) is null)
            throw new HubException("Unknown event");
    }
}
