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
public sealed partial class SmtpEmailSender : IEmailSender
{
    private readonly IOptions<EmailOptions> _options;
    private readonly Func<ISmtpClient> _clients;

    public SmtpEmailSender(IOptions<EmailOptions> options) : this(options, () => new SmtpClient()) { }

    /// <summary>With a client factory (tests use a fake <see cref="ISmtpClient"/>).</summary>
    internal SmtpEmailSender(IOptions<EmailOptions> options, Func<ISmtpClient> clients)
    {
        _options = options;
        _clients = clients;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = _options.Value;
        var smtp = settings.Smtp;
        var mime = ToMime(message, settings);
        using var client = _clients();
        client.Timeout = smtp.TimeoutSeconds * 1000;
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

        // The server accepted the message: nothing after this point may turn it into a failure (and a resend), not
        // even shutdown, so QUIT ignores the token and every error.
        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
#pragma warning disable CA1031 // deliberately swallowed, see above
        catch (Exception)
        {
        }
#pragma warning restore CA1031
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
    /// Permanent (retrying cannot help, the participant is marked EmailFailed): a 5xx reply that rejects this
    /// recipient or this message (<see cref="SmtpErrorCode.RecipientNotAccepted"/>,
    /// <see cref="SmtpErrorCode.MessageNotAccepted"/>), or any reply with an enhanced status code 5.1.x (bad
    /// address); a malformed address. Transient (retried; after the last attempt the row fails with this reason):
    /// 4xx replies, 5.7.x policy replies (relay denied, authentication required, blocklisted), a 554 without 5.1.x
    /// (typically a greeting or blocklist rejection of the sending server), a rejected sender, unexpected replies,
    /// authentication, network, TLS and protocol errors, timeouts: all of them are problems of the sending side,
    /// not of the recipient. The reason holds only the status code, the enhanced code and the error kind, never the
    /// server's reply text (which can echo the address).
    /// </summary>
    internal static EmailDeliveryException Classify(Exception ex) => ex switch
    {
        EmailDeliveryException delivery => delivery,
        SmtpCommandException command => ClassifyCommand(command),
        ParseException => new EmailDeliveryException(true, "Malformed address."),
        AuthenticationException => new EmailDeliveryException(false, "SMTP authentication failed."),
        SslHandshakeException => new EmailDeliveryException(false, "TLS handshake failed."),
        SmtpProtocolException => new EmailDeliveryException(false, "SMTP protocol error."),
        SocketException socket => new EmailDeliveryException(false, $"Network error ({socket.SocketErrorCode})."),
        _ => new EmailDeliveryException(false, $"Delivery error ({ex.GetType().Name})."),
    };

    private static EmailDeliveryException ClassifyCommand(SmtpCommandException command)
    {
        var status = (int)command.StatusCode;
        var enhanced = EnhancedStatus().Match(command.Message ?? "") is { Success: true } m ? m.Value : null;
        var permanent = status is >= 500 and < 600 && (
            enhanced is not null && enhanced.StartsWith("5.1.", StringComparison.Ordinal)
            || command.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted
               && status != 554
               && (enhanced is null || !enhanced.StartsWith("5.7.", StringComparison.Ordinal)));
        return new EmailDeliveryException(permanent,
            enhanced is null ? $"SMTP {status} {command.ErrorCode}" : $"SMTP {status} {enhanced} {command.ErrorCode}");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\d.])[245]\.\d{1,3}\.\d{1,3}(?![\d.])")]
    private static partial System.Text.RegularExpressions.Regex EnhancedStatus();
}
