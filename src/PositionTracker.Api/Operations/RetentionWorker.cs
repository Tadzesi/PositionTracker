using Microsoft.Extensions.Options;
using PositionTracker.Api.Persistence;

namespace PositionTracker.Api.Operations;

/// <summary>Configuration section <c>Retention</c>.</summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Position fixes and device statuses of an event are deleted this many days after it ended.</summary>
    public int FixDays { get; set; } = 30;

    /// <summary>How often the worker checks (hours).</summary>
    public int CheckIntervalHours { get; set; } = 24;
}

/// <summary>
/// Daily retention: deletes the fixes and device statuses of events that ended more than
/// <see cref="RetentionOptions.FixDays"/> days ago (<see cref="IFixStore.DeleteOlderThanAsync"/>); events that have not
/// ended and newer ones keep everything, the action log is kept. Runs on the injected <see cref="TimeProvider"/>; the
/// first check is one interval after start (not at start-up).
/// </summary>
public sealed class RetentionWorker(IFixStore store, IOptions<PersistenceOptions> persistence, IOptions<RetentionOptions> options,
    TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.CheckIntervalHours), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Retention run failed ({ExceptionType}).", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One run; returns the number of fixes deleted (0 without a database).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!persistence.Value.Enabled) return 0;
        var deleted = await store.DeleteOlderThanAsync(clock.GetUtcNow() - TimeSpan.FromDays(options.Value.FixDays), ct);
        if (deleted > 0) logger.LogInformation("Retention deleted {Count} fixes.", deleted);
        return deleted;
    }
}
