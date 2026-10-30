namespace PositionTracker.Email;

/// <summary>
/// A rendered email. <see cref="ToName"/> and <see cref="Subject"/> are single-line, length-limited text (see
/// <see cref="EmailText"/>); <see cref="HtmlBody"/> has every user value HTML-escaped.
/// </summary>
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string HtmlBody, string TextBody, string Language)
{
    /// <summary>What the message is for (one of <see cref="EmailPurpose"/>); safe to log.</summary>
    public string Purpose { get; init; } = EmailPurpose.Other;

    /// <summary>The outbox row the message was sent from (set by the outbox worker); safe to log.</summary>
    public Guid? OutboxId { get; init; }

    /// <summary>Where replies go (the organizer, for invitations); null for none.</summary>
    public string? ReplyToAddress { get; init; }

    public string? ReplyToName { get; init; }
}

/// <summary>Message kinds; they are stored in the outbox and appear in logs instead of any message content.</summary>
public static class EmailPurpose
{
    public const string Invitation = "invitation";
    public const string OrganizerEmailConfirmation = "organizerEmailConfirmation";
    public const string SignIn = "signIn";
    public const string DeliverabilityTest = "deliverabilityTest";
    public const string Other = "other";
}

/// <summary>Sends one email. Throws <see cref="EmailDeliveryException"/> when the message was not accepted.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);

    /// <summary>True for a sender that only pretends to send (no SMTP server configured).</summary>
    bool IsSimulated => false;
}

/// <summary>
/// A failed delivery. <see cref="Permanent"/>: retrying cannot help (the recipient or message was rejected, the
/// address is malformed). The message never contains personal data (no address, name or server reply text).
/// </summary>
public sealed class EmailDeliveryException(bool permanent, string reason, Exception? inner = null)
    : Exception(reason, inner)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// Whether email is really delivered (<c>"smtp"</c>) or only simulated (<c>"simulated"</c>: no SMTP host
/// configured). Organizer-facing endpoints surface it as <c>emailMode</c>.
/// </summary>
public interface IEmailMode
{
    bool IsSimulated { get; }

    string Mode => IsSimulated ? EmailModes.Simulated : EmailModes.Smtp;
}

public static class EmailModes
{
    public const string Smtp = "smtp";
    public const string Simulated = "simulated";
}

/// <summary>The mode of the registered <see cref="IEmailSender"/>.</summary>
public sealed class EmailMode(IEmailSender sender) : IEmailMode
{
    public bool IsSimulated => sender.IsSimulated;
}
