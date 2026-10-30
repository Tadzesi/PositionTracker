namespace PositionTracker.Api.Simulation;

/// <summary>
/// The clock of a simulated event: <see cref="GetUtcNow"/> returns <c>simulatedStart + Elapsed</c>.
/// <see cref="ScaledElapsed"/> is the real time elapsed on the base clock since construction times
/// <see cref="Scale"/>; <see cref="Elapsed"/> follows it until <see cref="SetSimulatedElapsed"/> pins a value.
/// <see cref="SimulationService"/> pins the clock to the time it last delivered fixes for (each loop tick, or
/// <see cref="SimulationService.Step"/>), so views never see simulated time run ahead of the delivered fixes
/// (which would show every rider as Delayed between two ticks). Only the wall-clock time is scaled; timers and
/// timestamps are the default implementations and are not meant to be used through this provider. Thread-safe.
/// </summary>
public sealed class ScaledTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly TimeProvider _base;
    private readonly DateTimeOffset _realStart;
    private TimeSpan? _pinned;

    public ScaledTimeProvider(TimeProvider baseClock, DateTimeOffset simulatedStart, double scale)
    {
        ArgumentNullException.ThrowIfNull(baseClock);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive.");
        _base = baseClock;
        _realStart = baseClock.GetUtcNow();
        SimulatedStart = simulatedStart;
        Scale = scale;
    }

    public DateTimeOffset SimulatedStart { get; }

    public double Scale { get; }

    /// <summary>Real time elapsed since construction times <see cref="Scale"/>; never negative.</summary>
    public TimeSpan ScaledElapsed
    {
        get
        {
            var real = _base.GetUtcNow() - _realStart;
            return real <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks((long)(real.Ticks * Scale));
        }
    }

    /// <summary>Simulated time since <see cref="SimulatedStart"/>: the pinned value if any, else <see cref="ScaledElapsed"/>.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock) return _pinned ?? ScaledElapsed;
        }
    }

    /// <summary>Pins the simulated elapsed time (not negative) until the next call.</summary>
    public void SetSimulatedElapsed(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        lock (_lock) _pinned = elapsed;
    }

    public override DateTimeOffset GetUtcNow() => SimulatedStart + Elapsed;
}
