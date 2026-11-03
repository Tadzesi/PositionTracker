namespace PositionTracker.Api.Accounts;

/// <summary>Configuration section <c>Accounts</c> (organizer sign-up, sign-in and session).</summary>
public sealed class AccountOptions
{
    public const string Section = "Accounts";

    /// <summary>
    /// Secret salt of the keyed hash under which the limiters count per email address (no address is kept in
    /// memory). Empty: a random salt per process, which is enough because the counters live in memory only.
    /// </summary>
    public string RateLimitSalt { get; set; } = "";

    /// <summary>
    /// Sign-up and sign-in requests per IP bucket (IPv4 address or IPv6 /64) and hour; beyond, the same 202 is
    /// answered and nothing is done.
    /// </summary>
    public int RequestsPerIpPerHour { get; set; } = 20;

    /// <summary>
    /// Sign-up and sign-in requests per IPv6 /48 and hour (a home connection holds 256–65 536 /64 buckets); taken
    /// together with the /64 budget, all or none. IPv4 has no prefix budget.
    /// </summary>
    public int RequestsPerHourPerPrefix48 { get; set; } = 100;

    /// <summary>Account emails (confirmation, sign-in) per (address, IP bucket) and hour; beyond, nothing is sent.</summary>
    public int MailsPerEmailAndIpPerHour { get; set; } = 3;

    /// <summary>Account emails per address and hour, whatever the IPs; beyond, nothing is sent.</summary>
    public int MailsPerEmailPerHour { get; set; } = 10;

    /// <summary>Account emails per hour in total; beyond, nothing is sent and a warning is logged (once per hour).</summary>
    public int GlobalMailsPerHour { get; set; } = 200;

    /// <summary>Confirmation and sign-in link redemptions per IP bucket and hour; beyond, 429 <c>rate_limited</c>.</summary>
    public int LinkRedeemsPerIpPerHour { get; set; } = 60;

    /// <summary>
    /// Code attempts per address and hour (right or wrong); beyond, 429 <c>rate_limited</c>. With six digits and a code living
    /// 15 minutes (an hour for a sign-up) a guess succeeds with a chance of about 1 in 100 000 per hour.
    /// </summary>
    public int CodeAttemptsPerEmailPerHour { get; set; } = 10;

    /// <summary>
    /// Wrong codes of an account (counted in the database, so no flood of other keys can reset the count) after which all its
    /// live codes are revoked; the next attempt needs a new mail. With at most <see cref="MailsPerEmailPerHour"/> mails an hour
    /// this bounds the guesses at about 50 an hour, a chance of 1 in 20 000 per hour.
    /// </summary>
    public int CodeFailuresBeforeRevoke { get; set; } = 5;

    /// <summary>Code redemptions per IPv6 /48 and hour (on top of <see cref="LinkRedeemsPerIpPerHour"/> per /64).</summary>
    public int LinkRedeemsPerHourPerPrefix48 { get; set; } = 300;

    /// <summary>Keys each limiter keeps at most (memory bound); the oldest windows are evicted beyond.</summary>
    public int RateLimitMaxKeys { get; set; } = 100_000;

    /// <summary>
    /// Sign-up and sign-in answer no sooner than this (wall-clock milliseconds), so the response time does not tell
    /// whether the address has an account. Should exceed the slowest normal processing time.
    /// </summary>
    public int MinResponseMilliseconds { get; set; } = 400;

    /// <summary>Front-end page (relative to <c>Email:PublicBaseUrl</c>) that receives the confirmation token.</summary>
    public string ConfirmPath { get; set; } = "account/confirm";

    /// <summary>Front-end page (relative to <c>Email:PublicBaseUrl</c>) that receives the sign-in token.</summary>
    public string SignInPath { get; set; } = "account/signin";

    /// <summary>
    /// Directory of the ASP.NET Core data protection keys that encrypt the session cookies. Empty: the framework
    /// default (user profile); in a container set a persistent volume, or every restart signs everyone out.
    /// </summary>
    public string DataProtectionKeysPath { get; set; } = "";

    internal bool IsValid(out string error)
    {
        error = "";
        if (RequestsPerIpPerHour < 1 || RequestsPerHourPerPrefix48 < 1 || MailsPerEmailAndIpPerHour < 1 || MailsPerEmailPerHour < 1 || GlobalMailsPerHour < 1
            || LinkRedeemsPerIpPerHour < 1 || LinkRedeemsPerHourPerPrefix48 < 1 || CodeFailuresBeforeRevoke < 1 || RateLimitMaxKeys < 1)
            error = "Accounts:RequestsPerIpPerHour, RequestsPerHourPerPrefix48, MailsPerEmailAndIpPerHour, MailsPerEmailPerHour, GlobalMailsPerHour, " +
                "LinkRedeemsPerIpPerHour, LinkRedeemsPerHourPerPrefix48, CodeFailuresBeforeRevoke and RateLimitMaxKeys must be positive.";
        else if (MinResponseMilliseconds is < 0 or > 10_000)
            error = "Accounts:MinResponseMilliseconds must be between 0 and 10000.";
        else if (string.IsNullOrWhiteSpace(ConfirmPath) || string.IsNullOrWhiteSpace(SignInPath))
            error = "Accounts:ConfirmPath and Accounts:SignInPath must not be empty.";
        return error.Length == 0;
    }
}
