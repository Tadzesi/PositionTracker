using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Email;

public static class EmailOutbox
{
    /// <summary>
    /// A new pending outbox row for <paramref name="message"/>, due at once. Add it to the context in the same
    /// transaction as the change that causes the email (for example the new participant or link token).
    /// <paramref name="participantId"/> is set for invitations, whose permanent failure marks the participant
    /// <see cref="ParticipantStatus.EmailFailed"/>.
    /// </summary>
    public static EmailOutboxEntity Create(EmailMessage message, Guid? participantId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ToAddress = message.ToAddress,
        ToName = message.ToName,
        Subject = message.Subject,
        HtmlBody = message.HtmlBody,
        TextBody = message.TextBody,
        Language = message.Language,
        Purpose = message.Purpose,
        ReplyToAddress = message.ReplyToAddress,
        ReplyToName = message.ReplyToName,
        Status = EmailOutboxStatus.Pending,
        CreatedAt = now,
        NextAttemptAt = now,
        ParticipantId = participantId,
    };
}
