using System.Net;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Bounds the database lookups of unknown device tokens per client address: a token that is neither simulated nor in the
/// <see cref="DeviceTokenCache"/> costs a query, and the cache keeps only tokens that resolved, so random tokens would
/// reach the database on every request, before any per-token limit. Each address bucket (IPv4 address or IPv6 /64, see
/// <see cref="AccountRateLimiter.IpBucket"/>) may have <see cref="MissesPerMinute"/> lookups that found nothing per
/// one-minute window; beyond that further cache misses are refused (429) without a query until the window ends. Phones
/// with a valid token are not affected: their tokens are cached (and warmed for live events), so they never miss.
/// </summary>
public sealed class DeviceLookupLimiter(TimeProvider clock)
{
    public const int MissesPerMinute = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxKeys = 50_000;

    private readonly HourlyCounter _misses = new(clock, MissesPerMinute, MaxKeys, Window);

    /// <summary>Whether a cache miss of <paramref name="ip"/> may still query the database in the current window.</summary>
    public bool MayLookUp(IPAddress? ip) => _misses.CanTake(AccountRateLimiter.IpBucket(ip));

    /// <summary>Counts a lookup of <paramref name="ip"/> that found no token.</summary>
    public void NoteMiss(IPAddress? ip) => _misses.TryTake(AccountRateLimiter.IpBucket(ip));
}

/// <summary>The outcome of resolving a device token for a request.</summary>
public enum DeviceLookupStatus { Found, NotFound, Throttled }
