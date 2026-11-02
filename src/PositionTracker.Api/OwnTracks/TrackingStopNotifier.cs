using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Email;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Email;

namespace PositionTracker.Api.OwnTracks;

/// <summary>
/// Notes an OwnTracks request that arrives after the event was finished. The first one is stored on the participant
/// (<c>TrackingAfterFinishAt</c>); the phone got the stop command with it. A request that still arrives
/// <see cref="TrackingStopNotifier.NoticeGrace"/> or later after that means the command did not work: one
/// "tracking still on" email is queued (<c>StopNoticeQueuedAt</c>), never a second one.
/// </summary>
public interface ITrackingStopNotifier
{
    Task NoteAsync(string eventId, string participantId, CancellationToken ct);
}

/// <inheritdoc cref="ITrackingStopNotifier"/>
public sealed class TrackingStopNotifier(AppDbContext db, TrackingStopMemo memo, TimeProvider clock,
    IOptions<PersistenceOptions> persistence, IOptions<EmailOptions> email) : ITrackingStopNotifier
{
    /// <summary>The public page with the stop link (the web route <c>/stop-tracking</c>).</summary>
    public const string StopTrackingPath = "/stop-tracking";

    /// <summary>How long a phone may keep sending after the first stop command before the participant is emailed.</summary>
    public static readonly TimeSpan NoticeGrace = TimeSpan.FromMinutes(10);

    public async Task NoteAsync(string eventId, string participantId, CancellationToken ct)
    {
        if (!persistence.Value.Enabled || !Guid.TryParse(participantId, out var pid)) return;
        var key = eventId + "/" + participantId;
        var now = clock.GetUtcNow();
        if (memo.Get(key) is { } known && (known.Done || now - known.SeenAt < NoticeGrace)) return;

        await db.Participants.Where(p => p.Id == pid && p.TrackingAfterFinishAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TrackingAfterFinishAt, now), ct);
        var state = await db.Participants.AsNoTracking().Where(p => p.Id == pid)
            .Select(p => new { p.TrackingAfterFinishAt, p.StopNoticeQueuedAt }).SingleOrDefaultAsync(ct);
        if (state?.TrackingAfterFinishAt is not { } seenAt) return;
        if (state.StopNoticeQueuedAt is not null)
        {
            memo.Set(key, new TrackingStopMemo.Entry(seenAt, true));
            return;
        }
        if (now - seenAt < NoticeGrace)
        {
            memo.Set(key, new TrackingStopMemo.Entry(seenAt, false));
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Participants.Where(p => p.Id == pid && p.StopNoticeQueuedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StopNoticeQueuedAt, now), ct);
        if (claimed == 1 && await MessageAsync(pid, ct) is { } message)
        {
            db.Outbox.Add(EmailOutbox.Create(message, null, now));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        memo.Set(key, new TrackingStopMemo.Entry(seenAt, true));
    }

    private async Task<EmailMessage?> MessageAsync(Guid participantId, CancellationToken ct)
    {
        var p = await db.Participants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == participantId, ct);
        var ev = p is null ? null : await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == p.EventId, ct);
        var organizer = ev is null ? null : await db.Organizers.AsNoTracking().SingleOrDefaultAsync(o => o.Id == ev.OrganizerId, ct);
        var baseUrl = email.Value.PublicBaseUrl;
        if (p is null || ev is null || organizer is null || !EmailLinks.IsHttpUrl(baseUrl)) return null;
        var language = string.Equals(p.Language, "sk", StringComparison.OrdinalIgnoreCase) ? "sk" : "en";
        var stopUrl = baseUrl!.TrimEnd('/') + StopTrackingPath + "?lang=" + language;
        var message = EmailTemplates.TrackingStillOn(p, ev, organizer, stopUrl, language);
        return message with
        {
            ToAddress = EmailAddress.ToDeliveryAddress(p.Email),
            ReplyToAddress = message.ReplyToAddress is null ? null : DeliveryOrNull(organizer.Email),
        };
    }

    private static string? DeliveryOrNull(string address)
    {
        try
        {
            return EmailAddress.ToDeliveryAddress(address);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// What <see cref="TrackingStopNotifier"/> already knows per event and participant, so a phone that keeps sending every
/// few seconds costs no database round trip: the first request time and whether the notice is settled. Process memory
/// only; after a restart the database answers again.
/// </summary>
public sealed class TrackingStopMemo
{
    public sealed record Entry(DateTimeOffset SeenAt, bool Done);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Entry? Get(string key) => _entries.GetValueOrDefault(key);

    public void Set(string key, Entry entry) => _entries[key] = entry;
}
