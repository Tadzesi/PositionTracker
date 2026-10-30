using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Email;

/// <summary>
/// Sends the email outbox. Every <see cref="OutboxOptions.PollIntervalSeconds"/> (on the injected clock) it:
/// <list type="number">
/// <item><b>claims</b> due <see cref="EmailOutboxStatus.Pending"/> rows in one transaction with
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, counts the attempt and moves <c>NextAttemptAt</c> to the end of a
/// <b>lease</b> <i>before</i> sending. The row lock only lasts for the claim transaction; afterwards the lease keeps
/// other workers off the row. The lease covers the whole batch in the worst case
/// (<see cref="OutboxOptions.BatchSize"/> × <see cref="SmtpOptions.OperationsPerMessage"/> ×
/// <see cref="SmtpOptions.TimeoutSeconds"/>), and a row whose lease has run out before its turn is not sent in this
/// round (it is claimed again later), so a second instance cannot send a row that is still being sent;</item>
/// <item><b>sends</b> each claimed row through <see cref="IEmailSender"/> and records the outcome: <c>Sent</c>,
/// <c>Simulated</c> (no SMTP configured), <c>Failed</c> (permanent error, or a transient one on the last attempt,
/// with the last reason and the attempt count), or still <c>Pending</c> with <c>NextAttemptAt</c> = now + retry
/// delay (<see cref="OutboxOptions.RetryBaseSeconds"/>, doubling, at most <see cref="OutboxOptions.RetryMaxSeconds"/>);</item>
/// <item><b>deletes</b> finished rows older than <see cref="OutboxOptions.RetentionDays"/>.</item>
/// </list>
/// A failed invitation turns its participant from <see cref="ParticipantStatus.Invited"/> into
/// <see cref="ParticipantStatus.EmailFailed"/> (other statuses are left alone). Finished rows lose their bodies.
/// <para>Delivery is at least once: if the process dies after the SMTP server accepted a message but before the
/// outcome was saved, the row is claimed again after its lease and sent again. This is bounded: a due row whose
/// attempts are used up is marked <c>Failed</c> ("outcome unknown") without another send, so a crash loop sends a
/// message at most <see cref="OutboxOptions.MaxAttempts"/> times.</para>
/// Runs only with a database (<see cref="PersistenceOptions.Enabled"/>).
/// </summary>
public sealed class EmailOutboxWorker(
    IDbContextFactory<AppDbContext> contexts, IEmailSender sender, IOptions<EmailOptions> options,
    IOptions<PersistenceOptions> persistence, TimeProvider clock, ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    internal const string OutcomeUnknown = "Attempts used up; the outcome of the last attempt is unknown (interrupted).";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sender.IsSimulated)
            logger.LogWarning("Simulated email mode: Email:Smtp:Host is not set, no email is delivered; outbox rows are marked Simulated.");
        if (!persistence.Value.Enabled) return;
        var interval = TimeSpan.FromSeconds(options.Value.Outbox.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means more may be due: go on at once.
                while (await ProcessDueAsync(stoppingToken) >= options.Value.Outbox.BatchSize) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processing the email outbox failed; trying again at the next poll.");
            }
            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round: claim, send, record, clean up. Returns the number of rows claimed or given up.</summary>
    internal async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var (toSend, givenUp) = await ClaimCoreAsync(ct);
        foreach (var row in toSend)
        {
            ct.ThrowIfCancellationRequested();
            // Normally unreachable (the lease is sized for the worst case); if it happens, another worker may own the
            // row now: leave it, it is claimed again once due (the counted attempt is lost).
            if (clock.GetUtcNow() >= row.NextAttemptAt) break;
            await DeliverAsync(row, ct);
        }
        await CleanupAsync(ct);
        return toSend.Count + givenUp;
    }

    /// <summary>The claim step alone (tests use it to simulate a crash between claim and outcome).</summary>
    internal async Task<IReadOnlyList<EmailOutboxEntity>> ClaimAsync(CancellationToken ct) => (await ClaimCoreAsync(ct)).ToSend;

    private async Task<(List<EmailOutboxEntity> ToSend, int GivenUp)> ClaimCoreAsync(CancellationToken ct)
    {
        var outbox = options.Value.Outbox;
        var now = clock.GetUtcNow();
        var lease = Lease();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = nameof(EmailOutboxStatus.Pending);
        var due = await db.Outbox
            .FromSql($"""
                SELECT * FROM "Outbox"
                WHERE "Status" = {pending} AND "NextAttemptAt" <= {now}
                ORDER BY "NextAttemptAt", "Id"
                LIMIT {outbox.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var toSend = new List<EmailOutboxEntity>();
        var givenUp = 0;
        foreach (var row in due)
        {
            if (row.Attempts >= outbox.MaxAttempts)
            {
                await FailAsync(db, row, OutcomeUnknown, now, ct);
                givenUp++;
                continue;
            }
            row.Attempts++;
            row.NextAttemptAt = now + lease;
            toSend.Add(row);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (toSend, givenUp);
    }

    private async Task DeliverAsync(EmailOutboxEntity row, CancellationToken ct)
    {
        var message = new EmailMessage(row.ToAddress, row.ToName, row.Subject, row.HtmlBody, row.TextBody, row.Language)
        {
            Purpose = row.Purpose,
            OutboxId = row.Id,
            ReplyToAddress = row.ReplyToAddress,
            ReplyToName = row.ReplyToName,
        };
        EmailDeliveryException? failure = null;
        try
        {
            await sender.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the row stays leased and is retried after NextAttemptAt
        }
        catch (EmailDeliveryException ex)
        {
            failure = ex;
        }
        catch (Exception ex)
        {
            // Unknown sender errors: only the type, the message could carry the address.
            failure = new EmailDeliveryException(false, $"Delivery error ({ex.GetType().Name}).");
        }

        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        db.Outbox.Attach(row);
        if (failure is null)
        {
            Finish(row, sender.IsSimulated ? EmailOutboxStatus.Simulated : EmailOutboxStatus.Sent, now);
            if (!sender.IsSimulated) row.SentAt = now;
            row.LastError = null;
        }
        else if (failure.Permanent)
        {
            await FailAsync(db, row, failure.Message, now, CancellationToken.None);
        }
        else if (row.Attempts >= options.Value.Outbox.MaxAttempts)
        {
            await FailAsync(db, row, $"{failure.Message} Gave up after {row.Attempts} attempts.", now, CancellationToken.None);
        }
        else
        {
            row.NextAttemptAt = now + RetryDelay(row.Attempts);
            row.LastError = Truncate(failure.Message);
            logger.LogWarning("Email outbox {OutboxId} ({Purpose}) attempt {Attempt} failed: {Reason}; retrying at {NextAttemptAt:o}.",
                row.Id, row.Purpose, row.Attempts, row.LastError, row.NextAttemptAt);
        }
        // The outcome is saved even when shutdown began meanwhile: the send already happened.
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>Marks the row Failed and, for an invitation, its still invited participant EmailFailed (same transaction).</summary>
    private async Task FailAsync(AppDbContext db, EmailOutboxEntity row, string reason, DateTimeOffset now, CancellationToken ct)
    {
        Finish(row, EmailOutboxStatus.Failed, now);
        row.LastError = Truncate(reason);
        logger.LogWarning("Email outbox {OutboxId} ({Purpose}) failed after {Attempts} attempt(s): {Reason}",
            row.Id, row.Purpose, row.Attempts, row.LastError);
        if (row.Purpose == EmailPurpose.Invitation && row.ParticipantId is { } participantId)
            await db.Participants
                .Where(p => p.Id == participantId && p.Status == ParticipantStatus.Invited)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ParticipantStatus.EmailFailed), ct);
    }

    private static void Finish(EmailOutboxEntity row, EmailOutboxStatus status, DateTimeOffset now)
    {
        row.Status = status;
        row.FinishedAt = now;
        row.HtmlBody = "";
        row.TextBody = "";
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(options.Value.Outbox.RetentionDays);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Outbox.Where(r => r.FinishedAt != null && r.FinishedAt <= cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>How long a claimed batch may take in the worst case: every message using every SMTP timeout.</summary>
    private TimeSpan Lease()
    {
        var value = options.Value;
        return TimeSpan.FromSeconds((double)value.Outbox.BatchSize * SmtpOptions.OperationsPerMessage * value.Smtp.TimeoutSeconds);
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var outbox = options.Value.Outbox;
        var seconds = Math.Min(outbox.RetryMaxSeconds, outbox.RetryBaseSeconds * Math.Pow(2, Math.Min(attempt - 1, 30)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
