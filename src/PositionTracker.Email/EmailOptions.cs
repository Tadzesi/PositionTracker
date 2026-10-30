using Microsoft.Extensions.Options;

namespace PositionTracker.Email;

/// <summary>
/// Configuration section <c>Email</c>. Credentials and real host names are never committed: supply them through
/// environment variables (<c>Email__Smtp__Password</c>) or <c>dotnet user-secrets</c>. Without
/// <see cref="SmtpOptions.Host"/> nothing is sent: the app logs a simulated send instead (real sending is opt-in).
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>The sender address (<c>From</c>); required when <see cref="SmtpOptions.Host"/> is set.</summary>
    public string From { get; set; } = "";

    /// <summary>The sender display name.</summary>
    public string FromName { get; set; } = "PositionTracker";

    /// <summary>The public base URL of the web app (absolute http/https); emailed links are built from it.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public OutboxOptions Outbox { get; set; } = new();

    /// <summary>
    /// Outside Development the app refuses to start without <see cref="SmtpOptions.Host"/> unless this is true:
    /// simulated email (never delivered, rows <c>Simulated</c>) must be an explicit decision.
    /// </summary>
    public bool AllowSimulated { get; set; }

    public bool SmtpEnabled => !string.IsNullOrWhiteSpace(Smtp.Host);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>
    /// True: STARTTLS is required (the connection fails without it). False: implicit TLS on port 465, otherwise a
    /// plain connection (only for a relay on the same machine or a trusted network).
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>SMTP user; empty for no authentication.</summary>
    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>
    /// Timeout of each SMTP operation (seconds). A message takes up to <see cref="OperationsPerMessage"/> operations;
    /// the outbox lease of a claimed batch is sized from it.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Connect, authenticate, send, quit.</summary>
    public const int OperationsPerMessage = 4;
}

public sealed class OutboxOptions
{
    /// <summary>How often the worker looks for due messages (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Messages claimed per round.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts per message, including the first; after the last failed one the message is <c>Failed</c>.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>Delay before the second attempt (seconds); it doubles with every further attempt.</summary>
    public int RetryBaseSeconds { get; set; } = 60;

    /// <summary>Upper bound of the retry delay (seconds).</summary>
    public int RetryMaxSeconds { get; set; } = 3600;

    /// <summary>Days a finished (sent, simulated, failed) row is kept before it is deleted; 0 deletes it at once.</summary>
    public int RetentionDays { get; set; } = 7;
}

/// <summary>
/// Validates <see cref="EmailOptions"/>; every message names the configuration key. Outside Development
/// (<paramref name="isDevelopment"/> false) an SMTP host is required unless <see cref="EmailOptions.AllowSimulated"/>
/// is set, and <see cref="EmailOptions.PublicBaseUrl"/> must be https. The event time zone must resolve.
/// </summary>
public sealed class EmailOptionsValidator(bool isDevelopment, Func<TimeZoneInfo?>? eventTimeZone = null)
    : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var errors = new List<string>();
        var smtp = options.Smtp;
        var outbox = options.Outbox;
        if (!options.SmtpEnabled && !isDevelopment && !options.AllowSimulated)
            errors.Add("Email:Smtp:Host is not set: outside Development the app does not start without real sending. " +
                "Configure Email:Smtp:* or set Email:AllowSimulated=true to run with simulated (never delivered) email.");
        if (options.SmtpEnabled)
        {
            if (!EmailText.IsPlainAddress(options.From))
                errors.Add("Email:From must be a plain email address when Email:Smtp:Host is set.");
            if (smtp.Port is < 1 or > 65535)
                errors.Add("Email:Smtp:Port must be between 1 and 65535.");
            else if (smtp.Port == 465 && smtp.UseStartTls)
                errors.Add("Email:Smtp:Port 465 uses implicit TLS: set Email:Smtp:UseStartTls=false (or use port 587 with STARTTLS).");
            else if (!smtp.UseStartTls && smtp.Port != 465 && !string.IsNullOrEmpty(smtp.User))
                errors.Add("Email:Smtp:UseStartTls=false on a port other than 465 would send Email:Smtp:User and the password " +
                    "in clear text: use STARTTLS (port 587) or implicit TLS (port 465).");
            if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
                errors.Add("Email:PublicBaseUrl is required when Email:Smtp:Host is set.");
            if (smtp.TimeoutSeconds < 1)
                errors.Add("Email:Smtp:TimeoutSeconds must be positive.");
        }
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri) || !EmailLinks.IsHttpUrl(options.PublicBaseUrl))
                errors.Add("Email:PublicBaseUrl must be an absolute http or https URL.");
            else
            {
                if (!isDevelopment && uri.Scheme != Uri.UriSchemeHttps)
                    errors.Add("Email:PublicBaseUrl must use https outside Development.");
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    errors.Add("Email:PublicBaseUrl must not contain a query or a fragment.");
            }
        }
        if (outbox.PollIntervalSeconds < 1 || outbox.BatchSize < 1 || outbox.MaxAttempts < 1 || outbox.RetryBaseSeconds < 1
            || outbox.RetryMaxSeconds < outbox.RetryBaseSeconds || outbox.RetentionDays < 0)
            errors.Add("Email:Outbox: PollIntervalSeconds, BatchSize, MaxAttempts and RetryBaseSeconds must be positive, " +
                "RetryMaxSeconds at least RetryBaseSeconds, RetentionDays not negative.");
        if ((eventTimeZone ?? (() => EmailTemplates.EventTimeZoneOrNull))() is null)
            errors.Add($"The event time zone {EmailTemplates.EventTimeZoneId} cannot be resolved on this machine " +
                "(install the tzdata/ICU time zone data); email dates would be wrong.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
