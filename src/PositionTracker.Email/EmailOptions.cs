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

    /// <summary>Timeout of each SMTP operation (seconds). Must be below <see cref="OutboxOptions.RetryBaseSeconds"/>.</summary>
    public int TimeoutSeconds { get; set; } = 30;
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

/// <summary>Validates <see cref="EmailOptions"/>; every message names the configuration key.</summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var errors = new List<string>();
        var smtp = options.Smtp;
        var outbox = options.Outbox;
        if (options.SmtpEnabled)
        {
            if (!EmailText.IsPlainAddress(options.From))
                errors.Add("Email:From must be a plain email address when Email:Smtp:Host is set.");
            if (smtp.Port is < 1 or > 65535)
                errors.Add("Email:Smtp:Port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
                errors.Add("Email:PublicBaseUrl is required when Email:Smtp:Host is set.");
            if (smtp.TimeoutSeconds < 1 || smtp.TimeoutSeconds >= outbox.RetryBaseSeconds)
                errors.Add("Email:Smtp:TimeoutSeconds must be positive and below Email:Outbox:RetryBaseSeconds.");
        }
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) && !EmailLinks.IsHttpUrl(options.PublicBaseUrl))
            errors.Add("Email:PublicBaseUrl must be an absolute http or https URL.");
        if (outbox.PollIntervalSeconds < 1 || outbox.BatchSize < 1 || outbox.MaxAttempts < 1 || outbox.RetryBaseSeconds < 1
            || outbox.RetryMaxSeconds < outbox.RetryBaseSeconds || outbox.RetentionDays < 0)
            errors.Add("Email:Outbox: PollIntervalSeconds, BatchSize, MaxAttempts and RetryBaseSeconds must be positive, " +
                "RetryMaxSeconds at least RetryBaseSeconds, RetentionDays not negative.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
