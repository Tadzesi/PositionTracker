using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Events;

namespace PositionTracker.Api.Live;

/// <summary>Configuration section <c>Live</c>.</summary>
public sealed class LiveOptions
{
    public const string Section = "Live";

    /// <summary>Broadcast interval in milliseconds; must be positive.</summary>
    public int IntervalMs { get; set; } = 1500;
}

/// <summary>
/// Every <see cref="LiveOptions.IntervalMs"/> (on the injected <see cref="TimeProvider"/>) broadcasts each
/// registered event: builds its organizer view, pushes it to the <see cref="SpectatorFeed"/>, sends it as
/// <see cref="LiveHub.LiveUpdateMethod"/> to the organizer group and, once a view is old enough, the delayed
/// spectator view as <see cref="LiveHub.PublicUpdateMethod"/> to the spectator group. A failing event is logged
/// and skipped; the others are still broadcast.
/// </summary>
public sealed class LiveBroadcaster(
    EventRegistry registry, SpectatorFeed feed, IHubContext<LiveHub> hub, IOptions<LiveOptions> options,
    TimeProvider clock, ILogger<LiveBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.IntervalMs), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await BroadcastAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Broadcasts every registered event once and drops the buffers of events no longer registered.</summary>
    public async Task BroadcastAsync(CancellationToken cancellationToken = default)
    {
        var events = registry.All;
        feed.RetainOnly(events);
        foreach (var runtime in events)
        {
            var eventId = runtime.Settings.EventId;
            try
            {
                var view = runtime.BuildLiveView();
                feed.Push(runtime, view);
                await hub.Clients.Group(LiveHub.OrganizerGroup(eventId))
                    .SendAsync(LiveHub.LiveUpdateMethod, view, cancellationToken);
                if (feed.GetCurrent(runtime) is { } spectatorView)
                    await hub.Clients.Group(LiveHub.SpectatorGroup(eventId))
                        .SendAsync(LiveHub.PublicUpdateMethod, spectatorView, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Live broadcast failed for event {EventId}.", eventId);
            }
        }
    }
}
