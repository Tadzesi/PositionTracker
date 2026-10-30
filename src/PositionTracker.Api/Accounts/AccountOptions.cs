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

    /// <summary>Sign-up and sign-in requests per remote IP and hour; beyond, the same 202 is answered and nothing is done.</summary>
    public int RequestsPerIpPerHour { get; set; } = 20;

    /// <summary>Account emails (confirmation, sign-in) per email address and hour; beyond, nothing is sent.</summary>
    public int MailsPerEmailPerHour { get; set; } = 3;

    /// <summary>Confirmation and sign-in link redemptions per remote IP and hour; beyond, 429 <c>rate_limited</c>.</summary>
    public int LinkRedeemsPerIpPerHour { get; set; } = 60;

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
        if (RequestsPerIpPerHour < 1 || MailsPerEmailPerHour < 1 || LinkRedeemsPerIpPerHour < 1)
            error = "Accounts:RequestsPerIpPerHour, MailsPerEmailPerHour and LinkRedeemsPerIpPerHour must be positive.";
        else if (MinResponseMilliseconds is < 0 or > 10_000)
            error = "Accounts:MinResponseMilliseconds must be between 0 and 10000.";
        else if (string.IsNullOrWhiteSpace(ConfirmPath) || string.IsNullOrWhiteSpace(SignInPath))
            error = "Accounts:ConfirmPath and Accounts:SignInPath must not be empty.";
        return error.Length == 0;
    }
}
