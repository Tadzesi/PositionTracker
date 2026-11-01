using System.Collections.Concurrent;
using PositionTracker.Core;

namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Decides whether the device status of a batch is written to the history: when it changed (battery by a point, charging,
/// foreground, queue length) or the last written one is older than <see cref="MaxAge"/>. A phone reports a status with every
/// position, which would make the status table as large as the position table; the runtime keeps the newest status in memory
/// either way. Thread-safe; the memory is bounded (stale entries are dropped when it grows).
/// </summary>
public sealed class StatusPersistThrottle
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, (DeviceStatus Status, DateTimeOffset At)> _last = new(StringComparer.Ordinal);

    public bool ShouldStore(string participantId, DeviceStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(participantId);
        ArgumentNullException.ThrowIfNull(status);
        if (_last.TryGetValue(participantId, out var last) && now - last.At < MaxAge && !Changed(last.Status, status)) return false;
        if (_last.Count >= PruneAbove)
            foreach (var (key, value) in _last)
                if (now - value.At > TimeSpan.FromHours(1)) _last.TryRemove(key, out _);
        _last[participantId] = (status, now);
        return true;
    }

    private static bool Changed(DeviceStatus a, DeviceStatus b) =>
        a.Charging != b.Charging || a.InForeground != b.InForeground || a.QueueLength != b.QueueLength
        || a.BatteryPct.HasValue != b.BatteryPct.HasValue
        || (a.BatteryPct.HasValue && Math.Abs(a.BatteryPct.Value - b.BatteryPct!.Value) >= 1);
}
