using System.Net;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Live;

/// <summary>
/// Caps the open <see cref="LiveHub"/> connections: <see cref="PerAddress"/> per client address bucket (IPv4 address, IPv6
/// /64, see <see cref="AccountRateLimiter.IpBucket"/>) and <see cref="Total"/> in all. Every connection receives the full
/// view of each event it joined every broadcast, so without a cap a single client could open thousands and use up the
/// uplink and memory of the server. The per-address cap is generous because spectators at a venue often share one address
/// (venue Wi-Fi, a mobile carrier's NAT). Thread-safe.
/// </summary>
public sealed class HubConnectionLimiter
{
    public const int PerAddress = 50;
    public const int Total = 5_000;

    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _total;

    /// <summary>Takes a connection slot of <paramref name="ip"/>; false (nothing taken) when a cap is reached.</summary>
    public bool TryOpen(IPAddress? ip)
    {
        var key = AccountRateLimiter.IpBucket(ip);
        lock (_gate)
        {
            if (_total >= Total) return false;
            _perAddress.TryGetValue(key, out var count);
            if (count >= PerAddress) return false;
            _perAddress[key] = count + 1;
            _total++;
            return true;
        }
    }

    /// <summary>Gives back a slot taken by <see cref="TryOpen"/>.</summary>
    public void Close(IPAddress? ip)
    {
        var key = AccountRateLimiter.IpBucket(ip);
        lock (_gate)
        {
            if (!_perAddress.TryGetValue(key, out var count)) return;
            if (count <= 1) _perAddress.Remove(key);
            else _perAddress[key] = count - 1;
            _total--;
        }
    }
}
