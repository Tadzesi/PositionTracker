using Microsoft.Extensions.Logging;

namespace PositionTracker.Email;

/// <summary>
/// The sender used when <c>Email:Smtp:Host</c> is not configured: nothing leaves the machine. It logs only the
/// outbox id, the message purpose and the recipient's domain, never the address, name, subject, body or link
/// (the link is a credential). The outbox worker marks such rows <c>Simulated</c>, not <c>Sent</c>.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public bool IsSimulated => true;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation("Email simulated, not sent (Email:Smtp:Host is not set): outbox {OutboxId}, {Purpose}, recipient domain {RecipientDomain}.",
            message.OutboxId, message.Purpose, EmailText.Domain(message.ToAddress));
        return Task.CompletedTask;
    }
}
