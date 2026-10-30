using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Persistence;

/// <summary>
/// The clock of a runtime rebuilt from the database. <see cref="EventRuntime"/> takes its clock once and reads
/// <c>GetUtcNow()</c> whenever it checks or stamps something (the fix-ahead check, the low-battery start, the time
/// of an action), so a replay must set that time instead of reading the wall clock: while replaying, the rehydrator
/// sets it (<see cref="SetReplayTime"/>) to the stored time of each fix batch, status and action, which is the value
/// the runtime used originally. <see cref="GoLive"/> then switches it, once and for good, to the live clock. Timers
/// and timestamps always come from the live clock. Thread-safe.
/// </summary>
public sealed class ReplayTimeProvider(TimeProvider live, DateTimeOffset replayStart) : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _replayNow = replayStart;
    private bool _isLive;

    public bool IsLive
    {
        get
        {
            lock (_lock) return _isLive;
        }
    }

    /// <summary>Sets the replayed time; throws <see cref="InvalidOperationException"/> once live.</summary>
    public void SetReplayTime(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_isLive) throw new InvalidOperationException("The clock is live; replay time can no longer be set.");
            _replayNow = now;
        }
    }

    /// <summary>From now on <see cref="GetUtcNow"/> is the live clock.</summary>
    public void GoLive()
    {
        lock (_lock) _isLive = true;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            if (!_isLive) return _replayNow;
        return live.GetUtcNow();
    }

    public override long GetTimestamp() => live.GetTimestamp();

    public override long TimestampFrequency => live.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => live.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        live.CreateTimer(callback, state, dueTime, period);
}
