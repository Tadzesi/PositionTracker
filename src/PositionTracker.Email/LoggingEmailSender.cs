using Microsoft.Extensions.Logging;

namespace PositionTracker.Email;

/// <summary>
/// The sender used when <c>Email:Smtp:Host</c> is not configured: nothing leaves the machine. It logs only the
/// outbox id, the message purpose and the recipient's domain, never the address, name, subject, body or link
/// (the link is a credential). The outbox worker marks such rows <c>Simulated</c>, not <c>Sent</c>. The first
/// simulated send is also logged as a warning.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    private int _sends;

    public bool IsSimulated => true;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _sends) == 1)
            logger.LogWarning("The first email was simulated, not delivered: Email:Smtp:Host is not set (simulated email mode).");
        logger.LogInformation("Email simulated, not sent (Email:Smtp:Host is not set): outbox {OutboxId}, {Purpose}, recipient domain {RecipientDomain}.",
            message.OutboxId, message.Purpose, EmailText.Domain(message.ToAddress));
        return Task.CompletedTask;
    }
}
