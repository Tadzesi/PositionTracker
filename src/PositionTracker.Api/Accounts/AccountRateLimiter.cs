using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// In-memory hourly limits of the account endpoints, on the injected <see cref="TimeProvider"/> (controller ruling P10):
/// <list type="bullet">
/// <item>sign-up/sign-in requests per IP bucket (<see cref="AccountOptions.RequestsPerIpPerHour"/>, 20);</item>
/// <item>account mails per (address, IP bucket) (<see cref="AccountOptions.MailsPerEmailAndIpPerHour"/>, 3), per address
/// (<see cref="AccountOptions.MailsPerEmailPerHour"/>, 10) and in total (<see cref="AccountOptions.GlobalMailsPerHour"/>,
/// 200); a mail takes from all of them or from none;</item>
/// <item>link redemptions per IP bucket (<see cref="AccountOptions.LinkRedeemsPerIpPerHour"/>, 60).</item>
/// </list>
/// An IP bucket is the IPv4 address (IPv4-mapped IPv6 included) or the IPv6 /64 prefix. Every key is a salted
/// HMAC-SHA256, so no address or IP is kept. A suppression is counted on <see cref="SuppressedCounterName"/> (tag
/// <c>limit</c> = <c>ip</c>, <c>email_ip</c>, <c>email</c> or <c>global</c>, nothing personal) and logged at debug
/// level; the global cap logs a warning once per hour. The built-in ASP.NET Core rate limiter is not used: over the
/// limit these endpoints must still answer the usual 202, the address comes from the body, and its windows cannot
/// run on a <see cref="TimeProvider"/>.
/// </summary>
public sealed class AccountRateLimiter
{
    public const string MeterName = "PositionTracker.Accounts";
    public const string SuppressedCounterName = "positiontracker.accounts.suppressed";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Suppressed =
        Meter.CreateCounter<long>(SuppressedCounterName, description: "Account sign-up/sign-in requests or emails suppressed by a rate limit.");

    private readonly byte[] _salt;
    private readonly HourlyCounter _requestsPerIp;
    private readonly HourlyCounter _mailsPerEmailAndIp;
    private readonly HourlyCounter _mailsPerEmail;
    private readonly HourlyCounter _mailsGlobal;
    private readonly HourlyCounter _redeemsPerIp;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccountRateLimiter> _logger;
    private readonly int _globalCap;
    private DateTimeOffset _globalWarnedAt = DateTimeOffset.MinValue;

    public AccountRateLimiter(IOptions<AccountOptions> options, TimeProvider clock, ILogger<AccountRateLimiter> logger)
    {
        var o = options.Value;
        _salt = string.IsNullOrEmpty(o.RateLimitSalt) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(o.RateLimitSalt);
        _requestsPerIp = new HourlyCounter(clock, o.RequestsPerIpPerHour, o.RateLimitMaxKeys);
        _mailsPerEmailAndIp = new HourlyCounter(clock, o.MailsPerEmailAndIpPerHour, o.RateLimitMaxKeys);
        _mailsPerEmail = new HourlyCounter(clock, o.MailsPerEmailPerHour, o.RateLimitMaxKeys);
        _mailsGlobal = new HourlyCounter(clock, o.GlobalMailsPerHour, 1);
        _redeemsPerIp = new HourlyCounter(clock, o.LinkRedeemsPerIpPerHour, o.RateLimitMaxKeys);
        _globalCap = o.GlobalMailsPerHour;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Takes one sign-up/sign-in request of the IP's bucket; false (and counted) when over the limit.</summary>
    public bool TryTakeRequest(IPAddress? ip)
    {
        if (_requestsPerIp.TryTake(Key("ip", IpBucket(ip)))) return true;
        Report("ip");
        return false;
    }

    /// <summary>
    /// Takes one account email to <paramref name="normalizedEmail"/> requested from <paramref name="ip"/>: from the
    /// (address, IP bucket), address and global budgets, all or none. False (and counted) when one is used up.
    /// </summary>
    public bool TryTakeMail(string normalizedEmail, IPAddress? ip)
    {
        var pairKey = Key("email_ip", normalizedEmail + "\n" + IpBucket(ip));
        var emailKey = Key("email", normalizedEmail);
        lock (_mailsGlobal)
        {
            if (!_mailsPerEmailAndIp.CanTake(pairKey)) return Refuse("email_ip");
            if (!_mailsPerEmail.CanTake(emailKey)) return Refuse("email");
            if (!HourlyCounter.TryTakeAll((_mailsPerEmailAndIp, pairKey), (_mailsPerEmail, emailKey), (_mailsGlobal, "global")))
            {
                var now = _clock.GetUtcNow();
                if (now - _globalWarnedAt >= TimeSpan.FromHours(1))
                {
                    _globalWarnedAt = now;
                    _logger.LogWarning("The global cap of {Cap} account emails per hour is reached; further account emails are suppressed this hour.", _globalCap);
                }
                return Refuse("global");
            }
            return true;
        }
    }

    /// <summary>Takes one link redemption of the IP's bucket.</summary>
    public bool TryTakeRedeem(IPAddress? ip) => _redeemsPerIp.TryTake(Key("redeem", IpBucket(ip)));

    /// <summary>The IPv4 address (also from an IPv4-mapped IPv6 address) or the IPv6 /64 prefix; "unknown" without one.</summary>
    internal static string IpBucket(IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private bool Refuse(string limit)
    {
        Report(limit);
        return false;
    }

    private void Report(string limit)
    {
        Suppressed.Add(1, new KeyValuePair<string, object?>("limit", limit));
        _logger.LogDebug("Account request suppressed by the {Limit} rate limit; answered as usual without sending.", limit);
    }

    private string Key(string kind, string value) =>
        Convert.ToBase64String(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(kind + "\n" + value)));
}

/// <summary>
/// A fixed one-hour window per key, starting at the key's first use, on the injected clock. At most
/// <c>maxKeys</c> keys are kept: ended windows are swept at most every few minutes (time-based, so the cost is
/// amortised), and when the dictionary is still full the oldest tenth of the windows is evicted at once (an evicted key
/// starts a fresh window: under a flood of distinct keys a single key's count may be reset early, the price of bounded
/// memory).
/// </summary>
internal sealed class HourlyCounter(TimeProvider clock, int limit, int maxKeys)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    private DateTimeOffset Now => clock.GetUtcNow();

    public int Count
    {
        get
        {
            lock (_entries) return _entries.Count;
        }
    }

    public bool TryTake(string key)
    {
        lock (_entries)
        {
            if (!CanTakeLocked(key, clock.GetUtcNow(), out var entry)) return false;
            Take(key, entry);
            return true;
        }
    }

    /// <summary>Whether <see cref="TryTake"/> would succeed now (nothing is taken).</summary>
    public bool CanTake(string key)
    {
        lock (_entries) return CanTakeLocked(key, clock.GetUtcNow(), out _);
    }

    /// <summary>Takes one from every (counter, key), or nothing when any of them is used up.</summary>
    public static bool TryTakeAll(params (HourlyCounter Counter, string Key)[] budgets)
    {
        var entries = new Entry?[budgets.Length];
        for (var i = 0; i < budgets.Length; i++)
        {
            var (counter, key) = budgets[i];
            lock (counter._entries)
                if (!counter.CanTakeLocked(key, counter.Now, out entries[i])) return false;
        }
        for (var i = 0; i < budgets.Length; i++)
        {
            var (counter, key) = budgets[i];
            lock (counter._entries)
            {
                // Re-check under the lock: another caller may have taken meanwhile (the limiter serializes mails).
                if (counter.CanTakeLocked(key, counter.Now, out var entry)) counter.Take(key, entry);
            }
        }
        return true;
    }

    private bool CanTakeLocked(string key, DateTimeOffset now, out Entry? entry)
    {
        if (now >= _nextSweep) Sweep(now);
        if (_entries.TryGetValue(key, out entry) && now >= entry.Start + Window)
        {
            entry.Start = now;
            entry.Count = 0;
        }
        return entry is null || entry.Count < limit;
    }

    private void Take(string key, Entry? entry)
    {
        if (entry is null)
        {
            if (_entries.Count >= maxKeys) EvictOldest();
            entry = new Entry { Start = clock.GetUtcNow() };
            _entries[key] = entry;
        }
        entry.Count++;
    }

    private void Sweep(DateTimeOffset now)
    {
        _nextSweep = now + SweepInterval;
        foreach (var (key, entry) in _entries)
            if (now >= entry.Start + Window) _entries.Remove(key);
    }

    private void EvictOldest()
    {
        var count = Math.Max(1, _entries.Count / 10);
        foreach (var key in _entries.OrderBy(e => e.Value.Start).Take(count).Select(e => e.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry
    {
        public DateTimeOffset Start;
        public int Count;
    }
}
