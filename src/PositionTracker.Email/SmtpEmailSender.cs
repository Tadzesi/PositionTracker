using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace PositionTracker.Email;

/// <summary>
/// Sends through the configured SMTP server with MailKit, one connection per message. Failures become
/// <see cref="EmailDeliveryException"/> (see <see cref="Classify"/>); the SMTP conversation is never logged.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        var smtp = settings.Smtp;
        var mime = ToMime(message, settings);
        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        try
        {
            var security = smtp.UseStartTls ? SecureSocketOptions.StartTls
                : smtp.Port == 465 ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.None;
            await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
            if (!string.IsNullOrEmpty(smtp.User)) await client.AuthenticateAsync(smtp.User, smtp.Password, ct);
            await client.SendAsync(mime, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not EmailDeliveryException)
        {
            throw Classify(ex);
        }

        // The server accepted the message: a failing QUIT must not turn it into a failure (and a resend).
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The MIME message: sender from options, recipient and Reply-To as bare addresses (a malformed one is a permanent
    /// failure), display names and subject sanitised again, text and HTML alternatives, <c>Content-Language</c>, and
    /// <c>Auto-Submitted: auto-generated</c> (no auto-replies to transactional mail).
    /// </summary>
    internal static MimeMessage ToMime(EmailMessage message, EmailOptions options)
    {
        if (!EmailText.IsPlainAddress(message.ToAddress))
            throw new EmailDeliveryException(true, "Invalid recipient address.");
        if (!EmailText.IsPlainAddress(options.From))
            throw new EmailDeliveryException(false, "Email:From is not a valid address.");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(EmailText.SingleLine(options.FromName, EmailText.MaxNameLength), options.From));
        mime.To.Add(new MailboxAddress(EmailText.SingleLine(message.ToName, EmailText.MaxNameLength), message.ToAddress));
        if (message.ReplyToAddress is { } replyTo && EmailText.IsPlainAddress(replyTo))
            mime.ReplyTo.Add(new MailboxAddress(EmailText.SingleLine(message.ReplyToName, EmailText.MaxNameLength), replyTo));
        mime.Subject = EmailText.SingleLine(message.Subject, EmailText.MaxSubjectLength);
        mime.MessageId = MimeUtils.GenerateMessageId(EmailText.Domain(options.From));
        mime.Headers.Add(HeaderId.ContentLanguage, message.Language == "sk" ? "sk" : "en");
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    /// <summary>
    /// Permanent: a 5xx reply to the recipient or the message, a malformed address. Transient: 4xx replies, a rejected
    /// sender or failed authentication (a configuration problem the operator can fix), network, TLS and protocol
    /// errors, timeouts. The reason holds only the status code and error kind, never the server's reply text (which
    /// can echo the address).
    /// </summary>
    internal static EmailDeliveryException Classify(Exception ex) => ex switch
    {
        EmailDeliveryException delivery => delivery,
        SmtpCommandException command => new EmailDeliveryException(
            (int)command.StatusCode is >= 500 and < 600 && command.ErrorCode != SmtpErrorCode.SenderNotAccepted,
            $"SMTP {(int)command.StatusCode} {command.ErrorCode}"),
        ParseException => new EmailDeliveryException(true, "Malformed address."),
        AuthenticationException => new EmailDeliveryException(false, "SMTP authentication failed."),
        SslHandshakeException => new EmailDeliveryException(false, "TLS handshake failed."),
        SmtpProtocolException => new EmailDeliveryException(false, "SMTP protocol error."),
        SocketException socket => new EmailDeliveryException(false, $"Network error ({socket.SocketErrorCode})."),
        _ => new EmailDeliveryException(false, $"Delivery error ({ex.GetType().Name})."),
    };
}
