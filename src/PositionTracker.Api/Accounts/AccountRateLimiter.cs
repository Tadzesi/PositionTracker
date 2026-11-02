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
/// <item>sign-up/sign-in requests per IP bucket (<see cref="AccountOptions.RequestsPerIpPerHour"/>, 20) and, for IPv6,
/// per /48 (<see cref="AccountOptions.RequestsPerHourPerPrefix48"/>, 100; controller ruling P11), all or none;</item>
/// <item>account mails per (address, IP bucket) (<see cref="AccountOptions.MailsPerEmailAndIpPerHour"/>, 3), per address
/// (<see cref="AccountOptions.MailsPerEmailPerHour"/>, 10) and in total (<see cref="AccountOptions.GlobalMailsPerHour"/>,
/// 200); a mail takes from all of them or from none;</item>
/// <item>link redemptions per IP bucket (<see cref="AccountOptions.LinkRedeemsPerIpPerHour"/>, 60).</item>
/// </list>
/// An IP bucket is the IPv4 address (IPv4-mapped IPv6 included) or the IPv6 /64 prefix. Every key is a salted
/// HMAC-SHA256, so no address or IP is kept. A suppression is counted on <see cref="SuppressedCounterName"/> (tag
/// <c>limit</c> = <c>ip</c>, <c>prefix</c>, <c>email_ip</c>, <c>email</c> or <c>global</c>, nothing personal), logged at
/// debug level, and kept as hourly counts for 24 hours (<see cref="LimitHitsLast24h"/>, shown to operators by
/// <c>GET /api/ops/email-mode</c>); the global cap also logs a warning once per hour. The built-in ASP.NET Core rate limiter is not used: over the
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
    private readonly HourlyCounter _requestsPerPrefix;
    private readonly LimitHitLog _hits;
    private readonly HourlyCounter _mailsPerEmailAndIp;
    private readonly HourlyCounter _mailsPerEmail;
    private readonly HourlyCounter _mailsGlobal;
    private readonly HourlyCounter _redeemsPerIp;
    private readonly HourlyCounter _codesPerEmail;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccountRateLimiter> _logger;
    private readonly int _globalCap;
    private DateTimeOffset _globalWarnedAt = DateTimeOffset.MinValue;

    public AccountRateLimiter(IOptions<AccountOptions> options, TimeProvider clock, ILogger<AccountRateLimiter> logger)
    {
        var o = options.Value;
        _salt = string.IsNullOrEmpty(o.RateLimitSalt) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(o.RateLimitSalt);
        _requestsPerIp = new HourlyCounter(clock, o.RequestsPerIpPerHour, o.RateLimitMaxKeys);
        _requestsPerPrefix = new HourlyCounter(clock, o.RequestsPerHourPerPrefix48, o.RateLimitMaxKeys);
        _hits = new LimitHitLog(clock);
        _mailsPerEmailAndIp = new HourlyCounter(clock, o.MailsPerEmailAndIpPerHour, o.RateLimitMaxKeys);
        _mailsPerEmail = new HourlyCounter(clock, o.MailsPerEmailPerHour, o.RateLimitMaxKeys);
        _mailsGlobal = new HourlyCounter(clock, o.GlobalMailsPerHour, 1);
        _redeemsPerIp = new HourlyCounter(clock, o.LinkRedeemsPerIpPerHour, o.RateLimitMaxKeys);
        _codesPerEmail = new HourlyCounter(clock, o.CodeAttemptsPerEmailPerHour, o.RateLimitMaxKeys);
        _globalCap = o.GlobalMailsPerHour;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Takes one sign-up/sign-in request from the IP's bucket and, for IPv6, its /48, all or none; false (and counted)
    /// when either is used up.
    /// </summary>
    public bool TryTakeRequest(IPAddress? ip)
    {
        var bucketKey = Key("ip", IpBucket(ip));
        if (IpPrefix48(ip) is not { } prefix)
        {
            if (_requestsPerIp.TryTake(bucketKey)) return true;
            return Refuse("ip");
        }
        var prefixKey = Key("prefix", prefix);
        lock (_requestsPerPrefix)
        {
            if (!_requestsPerIp.CanTake(bucketKey)) return Refuse("ip");
            if (!_requestsPerPrefix.CanTake(prefixKey)) return Refuse("prefix");
            HourlyCounter.TryTakeAll((_requestsPerIp, bucketKey), (_requestsPerPrefix, prefixKey));
            return true;
        }
    }

    /// <summary>Limit hits of the last 24 hours per kind (counts only).</summary>
    public LimitHits LimitHitsLast24h() => _hits.Last24h();

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

    /// <summary>Takes one code attempt for the address (whatever the IP); false (and counted) when the hour's attempts are used up.</summary>
    public bool TryTakeCodeAttempt(string normalizedEmail) =>
        _codesPerEmail.TryTake(Key("code", normalizedEmail)) || Refuse("code");

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

    /// <summary>The IPv6 /48 prefix of <paramref name="ip"/>; null for IPv4 (IPv4-mapped included) or none.</summary>
    internal static string? IpPrefix48(IPAddress? ip)
    {
        if (ip is null || ip.IsIPv4MappedToIPv6 || ip.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 6, 10);
        return new IPAddress(bytes) + "/48";
    }

    private bool Refuse(string limit)
    {
        Report(limit);
        return false;
    }

    private void Report(string limit)
    {
        Suppressed.Add(1, new KeyValuePair<string, object?>("limit", limit));
        _hits.Add(limit);
        _logger.LogDebug("Account request suppressed by the {Limit} rate limit; answered as usual without sending.", limit);
    }

    private string Key(string kind, string value) =>
        Convert.ToBase64String(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(kind + "\n" + value)));
}

/// <summary>Limit hits of the last 24 hours (counts only, no personal data).</summary>
public sealed record LimitHits(int Global, int PerEmail, int PerEmailAndIp, int PerIp, int PerPrefix)
{
    public int Total => Global + PerEmail + PerEmailAndIp + PerIp + PerPrefix;
}

/// <summary>Hourly counts of limit hits per kind in a ring of 24 slots on the injected clock.</summary>
internal sealed class LimitHitLog
{
    private static readonly string[] AccountKinds = ["global", "email", "email_ip", "ip", "prefix"];
    private readonly string[] Kinds;
    private readonly TimeProvider clock;
    private readonly long[] _hours = new long[24];
    private readonly int[,] _counts;

    /// <summary>The account limits' kinds.</summary>
    public LimitHitLog(TimeProvider clock) : this(clock, AccountKinds) { }

    public LimitHitLog(TimeProvider clock, string[] kinds)
    {
        this.clock = clock;
        Kinds = kinds;
        _counts = new int[24, kinds.Length];
    }

    public void Add(string kind)
    {
        var index = Array.IndexOf(Kinds, kind);
        if (index < 0) return;
        var hour = clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerHour;
        var slot = (int)(hour % 24);
        lock (_counts)
        {
            if (_hours[slot] != hour)
            {
                _hours[slot] = hour;
                for (var k = 0; k < Kinds.Length; k++) _counts[slot, k] = 0;
            }
            _counts[slot, index]++;
        }
    }

    public LimitHits Last24h()
    {
        var sums = Sums();
        return new LimitHits(sums[0], sums[1], sums[2], sums[3], sums[4]);
    }

    /// <summary>The sums of the last 24 hours per kind, in the order of the kinds.</summary>
    public int[] Sums()
    {
        var hour = clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerHour;
        var sums = new int[Kinds.Length];
        lock (_counts)
        {
            for (var slot = 0; slot < 24; slot++)
            {
                if (_hours[slot] <= hour - 24 || _hours[slot] > hour) continue;
                for (var k = 0; k < Kinds.Length; k++) sums[k] += _counts[slot, k];
            }
        }
        return sums;
    }
}

/// <summary>
/// A fixed window per key (one hour unless given), starting at the key's first use, on the injected clock. At most
/// <c>maxKeys</c> keys are kept: ended windows are swept at most every few minutes (time-based, so the cost is
/// amortised), and when the dictionary is still full the oldest tenth of the windows is evicted at once (an evicted key
/// starts a fresh window: under a flood of distinct keys a single key's count may be reset early, the price of bounded
/// memory).
/// </summary>
internal sealed class HourlyCounter(TimeProvider clock, int limit, int maxKeys, TimeSpan? window = null)
{
    private readonly TimeSpan Window = window ?? TimeSpan.FromHours(1);
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

    /// <summary>Gives back one taken unit of the key's current window (an action that took it did not happen).</summary>
    public void Give(string key)
    {
        lock (_entries)
            if (_entries.TryGetValue(key, out var entry) && entry.Count > 0 && clock.GetUtcNow() < entry.Start + Window)
                entry.Count--;
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
