using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// In-memory hourly limits of the account endpoints, on the injected <see cref="TimeProvider"/>: sign-up/sign-in
/// requests per IP, account emails per address, link redemptions per IP. Every key is a salted HMAC-SHA256 of the
/// IP or the normalized address, so no address or IP is kept. A suppressed sign-up/sign-in is counted on the
/// <see cref="SuppressedCounterName"/> counter (tag <c>limit</c> = <c>ip</c> or <c>email</c>, nothing personal).
/// The built-in ASP.NET Core rate limiter is not used here: over the limit these endpoints must still answer the
/// usual 202, the email key comes from the body, and its windows cannot run on a <see cref="TimeProvider"/>.
/// </summary>
public sealed class AccountRateLimiter
{
    public const string MeterName = "PositionTracker.Accounts";
    public const string SuppressedCounterName = "positiontracker.accounts.suppressed";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Suppressed =
        Meter.CreateCounter<long>(SuppressedCounterName, description: "Account sign-up/sign-in requests or emails suppressed by a rate limit.");

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly byte[] _salt;
    private readonly HourlyCounter _requestsPerIp;
    private readonly HourlyCounter _mailsPerEmail;
    private readonly HourlyCounter _redeemsPerIp;
    private readonly ILogger<AccountRateLimiter> _logger;

    public AccountRateLimiter(IOptions<AccountOptions> options, TimeProvider clock, ILogger<AccountRateLimiter> logger)
    {
        var o = options.Value;
        _salt = string.IsNullOrEmpty(o.RateLimitSalt) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(o.RateLimitSalt);
        _requestsPerIp = new HourlyCounter(clock, o.RequestsPerIpPerHour);
        _mailsPerEmail = new HourlyCounter(clock, o.MailsPerEmailPerHour);
        _redeemsPerIp = new HourlyCounter(clock, o.LinkRedeemsPerIpPerHour);
        _logger = logger;
    }

    /// <summary>Takes one sign-up/sign-in request of <paramref name="ip"/>; false (and counted) when over the limit.</summary>
    public bool TryTakeRequest(IPAddress? ip) => Take(_requestsPerIp, Key("ip", ip), "ip");

    /// <summary>Takes one account email to <paramref name="normalizedEmail"/>; false (and counted) when over the limit.</summary>
    public bool TryTakeMail(string normalizedEmail) => Take(_mailsPerEmail, Key("email", normalizedEmail), "email");

    /// <summary>Takes one link redemption of <paramref name="ip"/>.</summary>
    public bool TryTakeRedeem(IPAddress? ip) => _redeemsPerIp.TryTake(Key("redeem", ip));

    private bool Take(HourlyCounter counter, string key, string limit)
    {
        if (counter.TryTake(key)) return true;
        Suppressed.Add(1, new KeyValuePair<string, object?>("limit", limit));
        _logger.LogDebug("Account request suppressed by the per-{Limit} rate limit; answered as usual without sending.", limit);
        return false;
    }

    private string Key(string kind, IPAddress? ip) => Key(kind, ip?.ToString() ?? "unknown");

    private string Key(string kind, string value) =>
        Convert.ToBase64String(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(kind + "\n" + value)));

    /// <summary>A fixed one-hour window per key, starting at the key's first use; ended windows are pruned.</summary>
    private sealed class HourlyCounter(TimeProvider clock, int limit)
    {
        private const int PruneEvery = 256;
        private readonly ConcurrentDictionary<string, Entry> _entries = new();
        private int _calls;

        public bool TryTake(string key)
        {
            var now = clock.GetUtcNow();
            if (Interlocked.Increment(ref _calls) % PruneEvery == 0) Prune(now);
            var entry = _entries.GetOrAdd(key, _ => new Entry { Start = now });
            lock (entry)
            {
                if (now >= entry.Start + Window)
                {
                    entry.Start = now;
                    entry.Count = 0;
                }
                if (entry.Count >= limit) return false;
                entry.Count++;
                return true;
            }
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (var (key, entry) in _entries)
            {
                bool ended;
                lock (entry) ended = now >= entry.Start + Window;
                if (ended) _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }

        private sealed class Entry
        {
            public DateTimeOffset Start;
            public int Count;
        }
    }
}
