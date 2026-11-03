using System.Net;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Auth;

/// <summary>
/// Checks the operator key (<see cref="OrganizerOptions.ApiKey"/>) with a bound on guessing: each client address bucket
/// (IPv4 address, IPv6 /64, see <see cref="AccountRateLimiter.IpBucket"/>) may present <see cref="FailuresPerHour"/> wrong keys
/// per hour; beyond that every key from it is refused without comparing until the window ends. An empty key is not a guess
/// (the hub's <c>JoinOrganizer</c> uses it for the session path) and is never counted.
/// </summary>
public sealed class ApiKeyGuard(IOptions<OrganizerOptions> options, TimeProvider clock)
{
    public const int FailuresPerHour = 20;

    /// <summary>Shortest key accepted outside Development (validated at startup).</summary>
    public const int MinKeyLength = 32;

    private readonly HourlyCounter _failures = new(clock, FailuresPerHour, 50_000);

    /// <summary>Whether <paramref name="candidate"/> is the operator key and the address may still try one.</summary>
    public bool IsValidKey(string? candidate, IPAddress? ip)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var bucket = AccountRateLimiter.IpBucket(ip);
        if (!_failures.CanTake(bucket)) return false;
        if (options.Value.IsValidKey(candidate)) return true;
        _failures.TryTake(bucket);
        return false;
    }

    /// <summary><see cref="IsValidKey(string?, IPAddress?)"/> for the <see cref="OrganizerOptions.ApiKeyHeader"/> of a request.</summary>
    public bool IsValidRequest(HttpContext context) =>
        IsValidKey(context.Request.Headers[OrganizerOptions.ApiKeyHeader].ToString(), context.Connection.RemoteIpAddress);
}
