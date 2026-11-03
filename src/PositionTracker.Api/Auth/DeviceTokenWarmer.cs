using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Keeps the <see cref="DeviceTokenCache"/> warm for every live database event: the active tokens of an event are loaded when
/// its runtime appears (at start-up, at go-live) and again every <see cref="Refresh"/>, half the cache lifetime, so a token
/// of a running event never expires out of the cache. Without it a mass start of phones silent for over the lifetime (or
/// right after a restart) sent every request to the database at once (load test: 1 000 phones, p99 ~3 s).
/// </summary>
/// <remarks>
/// Each load is one query per event and is taken as of the time before it (<see cref="DeviceTokenCache.Remember"/>), so a
/// revocation committed meanwhile is still honoured: the cache distrusts entries read within its grace period. Finished and
/// simulated events are not loaded.
/// </remarks>
public sealed class DeviceTokenWarmer(
    EventRegistry registry, DeviceTokenCache cache, IDbContextFactory<AppDbContext> databases, IOptions<PersistenceOptions> persistence,
    TimeProvider clock, ILogger<DeviceTokenWarmer> logger) : BackgroundService
{
    /// <summary>How often new live events are looked for.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>How often the tokens of a live event are reloaded.</summary>
    public static readonly TimeSpan Refresh = DeviceTokenCache.Lifetime / 2;

    private readonly Dictionary<string, DateTimeOffset> _loadedAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Device token warm-up failed ({ExceptionType}).", ex.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Loads the events that are new or due; returns the number of tokens loaded (0 without a database).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!persistence.Value.Enabled) return 0;
        await _gate.WaitAsync(ct);
        try
        {
            var live = registry.All.Select(r => r.Settings.EventId)
                .Where(id => DbTokenService.IsDatabaseId(id) && !registry.IsFinished(id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _loadedAt.Keys.Where(id => !live.Contains(id)).ToList()) _loadedAt.Remove(gone);

            var loaded = 0;
            foreach (var eventId in live)
            {
                var readAt = clock.GetUtcNow();
                if (_loadedAt.TryGetValue(eventId, out var at) && readAt - at < Refresh) continue;
                var id = Guid.ParseExact(eventId, "D");
                await using var db = await databases.CreateDbContextAsync(ct);
                var tokens = await db.DeviceTokens.AsNoTracking()
                    .Where(t => t.EventId == id && t.RevokedAt == null)
                    .Select(t => new { t.ParticipantId, t.TokenHash })
                    .ToListAsync(ct);
                foreach (var t in tokens)
                    cache.Remember(Convert.ToHexString(t.TokenHash), eventId, t.ParticipantId.ToString("D"), readAt);
                _loadedAt[eventId] = readAt;
                loaded += tokens.Count;
            }
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }
}
