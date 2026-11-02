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
    EventRegistry registry, SpectatorFeed feed, Operations.AlarmAckService acks, IHubContext<LiveHub> hub, IOptions<LiveOptions> options,
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
    /// <summary>When the broadcaster first saw each finished event (and which runtime); only the broadcast loop touches it.</summary>
    private readonly Dictionary<string, (PositionTracker.Core.Runtime.EventRuntime Runtime, DateTimeOffset Since)> _finished = new(StringComparer.Ordinal);

    /// <summary>A finished event is still pushed this long after the spectator delay, so the final positions reach the spectators.</summary>
    private static readonly TimeSpan FinishedGrace = TimeSpan.FromSeconds(15);

    /// <summary>The simulated demo events have their own lifetime (see <see cref="Simulation.SimulationService"/>).</summary>
    private static bool IsSimulated(string eventId) => eventId.StartsWith("demo-", StringComparison.Ordinal);

    public async Task BroadcastAsync(CancellationToken cancellationToken = default)
    {
        var events = registry.All;
        feed.RetainOnly(events);
        var now = clock.GetUtcNow();
        foreach (var gone in _finished.Keys.Where(k => registry.Get(k) is null).ToList()) _finished.Remove(gone);
        foreach (var runtime in events)
        {
            var eventId = runtime.Settings.EventId;
            if (registry.IsFinished(eventId) && !IsSimulated(eventId))
            {
                if (!_finished.TryGetValue(eventId, out var seen) || !ReferenceEquals(seen.Runtime, runtime))
                    _finished[eventId] = seen = (runtime, now);
                var age = now - seen.Since;
                // nothing changes in a finished event: after the delay the last pushed view is the final one, and after the days the
                // results are kept for (the same as after a restart) the runtime leaves the memory
                if (age > TimeSpan.FromDays(Persistence.RuntimeRehydrator.FinishedRuntimeDays)) { registry.Remove(eventId); continue; }
                if (age > runtime.Settings.SpectatorDelay + FinishedGrace) continue;
            }
            try
            {
                // a finished event is shown as it was at its finish: its views do not age
                var view = runtime.BuildLiveView(registry.FinishedAt(eventId));
                feed.Push(runtime, view);
                await hub.Clients.Group(LiveHub.OrganizerGroup(eventId))
                    .SendAsync(LiveHub.LiveUpdateMethod, await acks.ApplyAsync(view, cancellationToken), cancellationToken);
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
