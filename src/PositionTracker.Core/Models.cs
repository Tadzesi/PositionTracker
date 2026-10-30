namespace PositionTracker.Core;

public enum Sport { Running, Cycling }

public sealed record PositionFix(DateTimeOffset Time, double Lat, double Lon, double? AccuracyM);

public sealed record DeviceStatus(double? BatteryPct, bool? Charging, int QueueLength, bool InForeground);

public sealed record PositionBatch(IReadOnlyList<PositionFix> Fixes, DeviceStatus? Status);
