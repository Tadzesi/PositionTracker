using Microsoft.EntityFrameworkCore;
using Npgsql;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Operations;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Events;

/// <summary>
/// The organizer's events (scoped, on the request's <see cref="AppDbContext"/>). Every operation is limited to the
/// events of <c>organizerId</c>: another organizer's event and an unknown id are the same 404 <c>not_found</c>.
/// Simulated events exist only in memory and are never seen here.
/// <para><b>Validation before any write:</b> every input is validated (the GPX parsed by the hardened
/// <see cref="GpxParser"/> and checked by <see cref="Course.Create"/>) before the event is even read, so an invalid
/// request writes nothing and reveals nothing about the event.</para>
/// <para><b>Concurrency:</b> every write sets a new <see cref="EventEntity.Version"/> (an optimistic concurrency
/// token); a write based on a read that another request has overtaken fails as 409 <c>conflict</c> and changes nothing
/// (also a unique-index race of two category or wave replacements). State changes re-read and retry instead, so
/// repeating a transition that another request just made is answered as the idempotent success it is.</para>
/// <para><b>live</b> builds the runtime with <see cref="RuntimeFactory"/> (the code path of the startup rebuild,
/// <see cref="EventSettings.Create"/> included) inside the transaction that sets <see cref="EventState.Live"/>; the
/// runtime is registered before the commit and removed again if the commit fails, and if the runtime cannot be built the
/// transaction is rolled back (500 <c>runtime_failed</c>), so the database never says Live without a runtime being
/// registered by that request. <c>live</c> on a Live event whose runtime is missing (its startup rebuild failed)
/// rebuilds and registers it. <b>finished</b> keeps the runtime registered, read-only: the device endpoints answer 409
/// <c>event_finished</c>, so nothing more is persisted.</para>
/// </summary>
public sealed class EventService(
    AppDbContext db, EventRegistry registry, RuntimeFactory factory, IEmailMode emailMode, TimeProvider clock,
    ILogger<EventService> logger, EventServiceHooks? hooks = null)
{
    public const int MaxNameLength = 120;
    public const int MaxGroupNameLength = 60;
    public const int MaxLaps = 10;
    public const int MaxCheckpoints = 50;
    public const int MaxCategories = 50;
    public const int MaxWaves = 50;
    public const int MaxGpxBytes = 1024 * 1024;
    public const int DefaultSpectatorDelaySeconds = 30;
    public const int MaxAge = 120;
    public static readonly TimeSpan MaxWaveAfterStart = TimeSpan.FromHours(24);
    /// <summary>A start further ahead is a typo, not a plan.</summary>
    public static readonly TimeSpan MaxStartAhead = TimeSpan.FromDays(5 * 366);
    private const int StateAttempts = 3;
    /// <summary>The lap line of <see cref="GetCoursePreviewAsync"/> is thinned to about this many points.</summary>
    public const int MaxPreviewPoints = 1200;

    // ----- read -----

    public async Task<EventList> ListAsync(Guid organizerId, CancellationToken ct)
    {
        var events = await db.Events.AsNoTracking().Where(e => e.OrganizerId == organizerId)
            .OrderByDescending(e => e.StartTime).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Name, e.Location, e.StartTime, e.Sport, e.State, e.Laps, e.PointToPoint, HasGpx = e.GpxBytes != null })
            .ToListAsync(ct);
        var ids = events.Select(e => e.Id).ToList();
        var lapLengths = await db.Courses.AsNoTracking().Where(c => ids.Contains(c.EventId))
            .Select(c => new { c.EventId, c.LapLengthM }).ToDictionaryAsync(c => c.EventId, c => c.LapLengthM, ct);
        var counts = (await db.Participants.AsNoTracking().Where(p => ids.Contains(p.EventId))
            .GroupBy(p => new { p.EventId, p.Status }).Select(g => new { g.Key.EventId, g.Key.Status, Count = g.Count() }).ToListAsync(ct))
            .ToLookup(c => c.EventId);
        ParticipantCounts CountsOf(Guid id)
        {
            int Of(ParticipantStatus s) => counts[id].SingleOrDefault(c => c.Status == s)?.Count ?? 0;
            return new ParticipantCounts(Of(ParticipantStatus.Invited), Of(ParticipantStatus.Confirmed),
                Of(ParticipantStatus.Declined), Of(ParticipantStatus.EmailFailed));
        }
        return new EventList(emailMode.Mode, events
            .Select(e => new EventSummary(RuntimeFactory.RuntimeId(e.Id), e.Name, e.Location, e.StartTime, e.Sport, e.State, e.Laps, e.PointToPoint,
                e.HasGpx && lapLengths.TryGetValue(e.Id, out var lap) ? lap : null, CountsOf(e.Id)))
            .ToList());
    }

    public async Task<EventResult<EventManageView>> GetAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (await FindAsync(organizerId, eventId, tracked: false, ct) is not { } ev) return EventErrors.NotFound;
        return new EventResult<EventManageView>(await ViewAsync(ev, ct), null);
    }

    // ----- create -----

    public async Task<EventResult<CreatedEvent>> CreateAsync(Guid organizerId, CreateEventRequest request, CancellationToken ct)
    {
        if (!TextRules.TryLine(request.Name, MaxNameLength, out var name)) return EventErrors.InvalidName("name");
        if (!TextRules.TryLine(request.Location, MaxNameLength, out var location)) return EventErrors.InvalidLocation("location");
        var now = clock.GetUtcNow();
        if (!UtcTimestampConverter.TryParse(request.StartTime, out var start) || start <= now || start > now + MaxStartAhead)
            return EventErrors.StartNotInFuture("startTime");
        if (ParseSport(request.Sport) is not { } sport) return EventErrors.InvalidSport("sport");

        var ev = new EventEntity
        {
            Id = Guid.CreateVersion7(now), OrganizerId = organizerId, Name = name, Location = location, StartTime = start,
            Sport = sport, State = EventState.Draft, Laps = 1, SpectatorDelaySeconds = DefaultSpectatorDelaySeconds,
            CreatedAt = now, Version = Guid.NewGuid(),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);
        return new EventResult<CreatedEvent>(new CreatedEvent(RuntimeFactory.RuntimeId(ev.Id)), null);
    }

    // ----- update -----

    /// <summary>
    /// Replaces name, location, start and sport. A finished event is locked; a live one takes only a new name and
    /// location (start and sport must stay as they are). A changed start must be in the future and must keep every
    /// start wave inside [start, start + 24 h] and the confirmation deadline not after the start (409
    /// <c>start_conflict</c>: the waves and the deadline are never moved silently).
    /// </summary>
    public async Task<EventResult<EventManageView>> UpdateAsync(Guid organizerId, string eventId, UpdateEventRequest request, CancellationToken ct)
    {
        if (!TextRules.TryLine(request.Name, MaxNameLength, out var name)) return EventErrors.InvalidName("name");
        if (!TextRules.TryLine(request.Location, MaxNameLength, out var location)) return EventErrors.InvalidLocation("location");
        if (!UtcTimestampConverter.TryParse(request.StartTime, out var start)) return EventErrors.StartNotInFuture("startTime");
        if (ParseSport(request.Sport) is not { } sport) return EventErrors.InvalidSport("sport");

        if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
        if (ev.State == EventState.Finished) return EventErrors.EventLocked;
        var startChanged = start != ev.StartTime;
        if (ev.State == EventState.Live && (startChanged || sport != ev.Sport)) return EventErrors.StartLocked;
        if (startChanged)
        {
            var now = clock.GetUtcNow();
            if (start <= now || start > now + MaxStartAhead) return EventErrors.StartNotInFuture("startTime");
            var waves = await db.Waves.AsNoTracking().Where(w => w.EventId == ev.Id).Select(w => w.StartTime).ToListAsync(ct);
            if (waves.Any(w => w < start || w > start + MaxWaveAfterStart) || ev.ConfirmationDeadline > start)
                return EventErrors.StartConflict;
        }
        ev.Name = name;
        ev.Location = location;
        ev.StartTime = start;
        ev.Sport = sport;
        ev.Version = Guid.NewGuid();
        // A changed start extends the live invitation links (never shortens them) in the same transaction as the save.
        await using var tx = startChanged ? await db.Database.BeginTransactionAsync(ct) : null;
        var saved = await SaveAndViewAsync(ev, ct);
        if (tx is not null && saved.Error is null)
        {
            var until = start + LinkTokenService.InviteAfterStart;
            var purpose = nameof(LinkPurpose.ParticipantInvite);
            var participantIds = db.Participants.Where(x => x.EventId == ev.Id).Select(x => x.Id);
            await db.LinkTokens
                .Where(t => t.Purpose == purpose && t.RevokedAt == null && t.ExpiresAt < until && participantIds.Contains(t.SubjectId))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, until), ct);
            await tx.CommitAsync(ct);
        }
        // A running (or finished) event shows the new name to spectators at once.
        if (saved.Error is null) registry.Get(RuntimeFactory.RuntimeId(ev.Id))?.Rename(name);
        return saved;
    }

    // ----- course -----

    /// <summary>The uploaded course for the organizer's map and elevation profile (404 without a course).</summary>
    public async Task<EventResult<CoursePreview>> GetCoursePreviewAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (await FindAsync(organizerId, eventId, tracked: false, ct) is not { GpxBytes: { Length: > 0 } } ev) return EventErrors.NotFound;
        Course course;
        try
        {
            course = RuntimeFactory.CourseOf(ev);
        }
        catch (Exception ex) when (ex is GpxFormatException or ArgumentException)
        {
            logger.LogError("Stored course of event {EventId} could not be read ({ExceptionType}).", RuntimeFactory.RuntimeId(ev.Id), ex.GetType().Name);
            return EventErrors.NotFound;
        }
        var n = course.Points.Count;
        var step = Math.Max(1, (int)Math.Ceiling(n / (double)MaxPreviewPoints));
        double?[] Row(int i)
        {
            var p = course.Points[i];
            return [Math.Round(p.Lat, 6), Math.Round(p.Lon, 6), p.Ele is { } e ? Math.Round(e, 1) : null, Math.Round(course.CumulativeM[i], 1)];
        }
        var points = new List<double?[]>();
        for (var i = 0; i < n; i += step) points.Add(Row(i));
        if ((n - 1) % step != 0) points.Add(Row(n - 1));
        var elevations = course.HasElevation ? course.Points.Select(p => p.Ele!.Value).ToList() : [];
        var places = course.Checkpoints.Select(c =>
        {
            var at = course.PointAt(c.LapDistanceM);
            return new CheckpointPlace(c.Name, c.LapDistanceM, Math.Round(at.Lat, 6), Math.Round(at.Lon, 6));
        }).ToList();
        return new EventResult<CoursePreview>(new CoursePreview(
            course.LapLengthM, ev.Laps, ev.PointToPoint, course.IsClosed, course.HasElevation,
            elevations.Count > 0 ? Math.Round(elevations.Min(), 1) : null, elevations.Count > 0 ? Math.Round(elevations.Max(), 1) : null,
            course.AscentPerLapM, course.DescentPerLapM, points, places), null);
    }

    public async Task<EventResult<EventManageView>> PutCourseAsync(Guid organizerId, string eventId, CourseRequest request, CancellationToken ct)
    {
        var pointToPoint = request.PointToPoint ?? false;
        var laps = pointToPoint ? 1 : request.Laps ?? 1;
        if (laps is < 1 or > MaxLaps) return EventErrors.LapsInvalid;
        var delay = request.SpectatorDelaySeconds ?? DefaultSpectatorDelaySeconds;
        if (TimeSpan.FromSeconds(delay) < EventSettings.MinSpectatorDelay || TimeSpan.FromSeconds(delay) > EventSettings.MaxSpectatorDelay)
            return EventErrors.DelayInvalid;
        if (ParseCheckpoints(request.Checkpoints) is not { } checkpoints) return EventErrors.CheckpointInvalid;
        // No GPX in the request: laps, delay and checkpoints are changed on the course already stored.
        var reuse = string.IsNullOrWhiteSpace(request.GpxBase64);
        byte[]? gpx = null;
        if (!reuse)
        {
            var (decoded, gpxError) = DecodeGpx(request.GpxBase64);
            if (gpxError is not null) return gpxError;
            gpx = decoded;
            var checkedCourse = BuildCourse(gpx!, laps, checkpoints);
            if (checkedCourse.Error is { } buildError) return buildError;
        }

        // Valid: now (and only now) the database.
        if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
        if (IsFrozen(ev)) return EventErrors.EventLocked;
        if (reuse)
        {
            if (ev.GpxBytes is not { Length: > 0 } storedGpx) return EventErrors.GpxInvalid;
            gpx = storedGpx;
        }
        var built = BuildCourse(gpx!, laps, checkpoints);
        if (built.Error is { } storedError) return storedError;
        var course = built.Value!;
        var now = clock.GetUtcNow();
        ev.GpxBytes = gpx;
        ev.Laps = laps;
        ev.PointToPoint = pointToPoint;
        ev.SpectatorDelaySeconds = delay;
        ev.CheckpointsJson = RuntimeFactory.CheckpointsJsonOf(course.Checkpoints);
        ev.Version = Guid.NewGuid();
        var stored = await db.Courses.SingleOrDefaultAsync(c => c.EventId == ev.Id, ct);
        if (stored is null) db.Courses.Add(stored = new CourseEntity { EventId = ev.Id });
        stored.LapLengthM = course.LapLengthM;
        stored.AscentM = course.AscentPerLapM;
        stored.DescentM = course.DescentPerLapM;
        stored.PointCount = course.Points.Count;
        stored.IsClosed = course.IsClosed;
        if (!reuse) stored.UploadedAt = now;
        return await SaveAndViewAsync(ev, ct);
    }

    private static EventResult<Course> BuildCourse(byte[] gpx, int laps, IReadOnlyList<Checkpoint> checkpoints)
    {
        IReadOnlyList<GeoPoint> points;
        try
        {
            using var stream = new MemoryStream(gpx, writable: false);
            points = GpxParser.Parse(stream);
        }
        catch (GpxFormatException)
        {
            return EventErrors.GpxInvalid;
        }
        try
        {
            return new EventResult<Course>(Course.Create(points, laps, checkpoints), null);
        }
        catch (ArgumentException ex)
        {
            return ex.ParamName switch
            {
                "laps" => EventErrors.CourseNotClosed,
                "checkpoints" => EventErrors.CheckpointInvalid,
                _ => EventErrors.CourseTooShort,
            };
        }
    }

    // ----- categories and waves -----

    public async Task<EventResult<EventManageView>> PutCategoriesAsync(Guid organizerId, string eventId,
        IReadOnlyList<CategoryRequest?> request, CancellationToken ct)
    {
        if (request.Count > MaxCategories) return EventErrors.TooMany("categories", MaxCategories);
        var wanted = new List<CategoryRequest>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.Count; i++)
        {
            var c = request[i];
            if (c is null) return EventErrors.InvalidBody;
            if (!TextRules.TryLine(c.Name, MaxGroupNameLength, out var name) || !names.Add(name))
                return EventErrors.InvalidGroupName($"[{i}].name");
            string? gender = null;
            if (!string.IsNullOrWhiteSpace(c.Gender))
            {
                gender = c.Gender.Trim().ToLowerInvariant();
                if (gender is not ("female" or "male")) return EventErrors.InvalidGender($"[{i}].gender");
            }
            if (c.MinAge is < 0 or > MaxAge) return EventErrors.InvalidAge($"[{i}].minAge");
            if (c.MaxAge is < 0 or > MaxAge) return EventErrors.InvalidAge($"[{i}].maxAge");
            if (c.MinAge > c.MaxAge) return EventErrors.InvalidAge($"[{i}].minAge");
            wanted.Add(new CategoryRequest(name, gender, c.MinAge, c.MaxAge));
        }

        if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
        if (IsFrozen(ev)) return EventErrors.EventLocked;
        // Kept names (ignoring case) keep their row, so participants keep their category.
        var existing = await db.Categories.Where(c => c.EventId == ev.Id).ToListAsync(ct);
        var byName = existing.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var w in wanted)
        {
            if (byName.Remove(w.Name!, out var row))
            {
                row.Name = w.Name!;
                row.Gender = w.Gender;
                row.MinAge = w.MinAge;
                row.MaxAge = w.MaxAge;
            }
            else
            {
                db.Categories.Add(new CategoryEntity
                {
                    Id = Guid.NewGuid(), EventId = ev.Id, Name = w.Name!, Gender = w.Gender, MinAge = w.MinAge, MaxAge = w.MaxAge,
                });
            }
        }
        db.Categories.RemoveRange(byName.Values);
        ev.Version = Guid.NewGuid();
        return await SaveAndViewAsync(ev, ct);
    }

    public async Task<EventResult<EventManageView>> PutWavesAsync(Guid organizerId, string eventId,
        IReadOnlyList<WaveRequest?> request, CancellationToken ct)
    {
        if (request.Count > MaxWaves) return EventErrors.TooMany("waves", MaxWaves);
        var wanted = new List<(string Name, DateTimeOffset Start)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.Count; i++)
        {
            var w = request[i];
            if (w is null) return EventErrors.InvalidBody;
            if (!TextRules.TryLine(w.Name, MaxGroupNameLength, out var name) || !names.Add(name))
                return EventErrors.InvalidGroupName($"[{i}].name");
            if (!UtcTimestampConverter.TryParse(w.StartTime, out var start)) return EventErrors.InvalidWaveStart($"[{i}].startTime");
            wanted.Add((name, start));
        }

        if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
        if (IsFrozen(ev)) return EventErrors.EventLocked;
        for (var i = 0; i < wanted.Count; i++)
            if (wanted[i].Start < ev.StartTime || wanted[i].Start > ev.StartTime + MaxWaveAfterStart)
                return EventErrors.InvalidWaveStart($"[{i}].startTime");
        var existing = await db.Waves.Where(w => w.EventId == ev.Id).ToListAsync(ct);
        var byName = existing.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, start) in wanted)
        {
            if (byName.Remove(name, out var row))
            {
                row.Name = name;
                row.StartTime = start;
            }
            else
            {
                db.Waves.Add(new WaveEntity { Id = Guid.NewGuid(), EventId = ev.Id, Name = name, StartTime = start });
            }
        }
        db.Waves.RemoveRange(byName.Values);
        ev.Version = Guid.NewGuid();
        return await SaveAndViewAsync(ev, ct);
    }

    // ----- confirmation deadline -----

    /// <summary>
    /// Sets (UTC ISO 8601 with <c>Z</c>, in the future, not after the event start) or clears (null) the confirmation
    /// deadline. Draft and Ready only (409 <c>event_locked</c> once live).
    /// </summary>
    public async Task<EventResult<EventManageView>> PutConfirmationDeadlineAsync(Guid organizerId, string eventId, string? deadline,
        CancellationToken ct)
    {
        DateTimeOffset? value = null;
        if (deadline is not null)
        {
            if (!UtcTimestampConverter.TryParse(deadline, out var parsed) || parsed <= clock.GetUtcNow())
                return EventErrors.InvalidDeadline("confirmationDeadline");
            value = parsed;
        }
        if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
        if (IsFrozen(ev)) return EventErrors.EventLocked;
        if (value > ev.StartTime) return EventErrors.InvalidDeadline("confirmationDeadline");
        ev.ConfirmationDeadline = value;
        ev.Version = Guid.NewGuid();
        return await SaveAndViewAsync(ev, ct);
    }

    // ----- state -----

    /// <summary>
    /// Deletes the event with everything that belongs to it: participants (with their consents, device tokens and positions),
    /// course, categories, waves, results, the participants' invitation links and their emails in the outbox (also those not sent
    /// yet, so no invitation to a deleted event goes out). Nobody is notified. A live event is refused (409 <c>event_live</c>);
    /// the row is locked first, so a concurrent go-live either happens before (and the delete is refused) or fails on the gone row.
    /// A finished event's read-only runtime leaves the memory.
    /// </summary>
    public async Task<EventError?> DeleteAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (!TryParseId(eventId, out var id)) return EventErrors.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ev = await db.Events
            .FromSql($"""SELECT * FROM "Events" WHERE "Id" = {id} AND "OrganizerId" = {organizerId} FOR UPDATE""")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (ev is null) return EventErrors.NotFound;
        if (ev.State == EventState.Live) return EventErrors.EventLive;

        var participants = db.Participants.Where(p => p.EventId == id).Select(p => p.Id);
        await db.Outbox.Where(m => m.ParticipantId != null && participants.Contains(m.ParticipantId.Value)).ExecuteDeleteAsync(ct);
        await db.LinkTokens.Where(t => t.Purpose == nameof(LinkPurpose.ParticipantInvite) && participants.Contains(t.SubjectId)).ExecuteDeleteAsync(ct);
        await db.Events.Where(e => e.Id == id).ExecuteDeleteAsync(ct); // the rest goes with the event (cascades)
        await tx.CommitAsync(ct);

        registry.Remove(RuntimeFactory.RuntimeId(id));
        logger.LogInformation("Event {EventId} ({State}) was deleted by its organizer.", RuntimeFactory.RuntimeId(id), ev.State);
        return null;
    }

    public async Task<EventResult<EventManageView>> SetStateAsync(Guid organizerId, string eventId, string? state, CancellationToken ct)
    {
        EventState target;
        switch ((state ?? "").Trim())
        {
            case "ready": target = EventState.Ready; break;
            case "live": target = EventState.Live; break;
            case "finished": target = EventState.Finished; break;
            default: return EventErrors.InvalidState("state");
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            if (await FindAsync(organizerId, eventId, tracked: true, ct) is not { } ev) return EventErrors.NotFound;
            if (hooks?.BeforeStateAttempt is { } attemptHook) await attemptHook(ev.Id, attempt);
            try
            {
                var error = ev.State == target
                    ? await RepeatAsync(ev, ct)
                    : (ev.State, target) switch
                    {
                        (EventState.Draft, EventState.Ready) => await ReadyAsync(ev, ct),
                        (EventState.Ready, EventState.Live) => await GoLiveAsync(ev, ct),
                        (EventState.Live, EventState.Finished) => await FinishAsync(ev, ct),
                        _ => EventErrors.InvalidTransition,
                    };
                if (error is not null) return error;
                db.ChangeTracker.Clear();
                var current = await FindAsync(organizerId, eventId, tracked: false, ct);
                return current is null ? EventErrors.NotFound : new EventResult<EventManageView>(await ViewAsync(current, ct), null);
            }
            catch (DbUpdateConcurrencyException) when (attempt < StateAttempts)
            {
                // Another request changed the event first: decide again on what it did.
            }
            catch (DbUpdateConcurrencyException)
            {
                return EventErrors.Conflict;
            }
        }
    }

    /// <summary>The event is already in the target state: success, after making sure a live event is running.</summary>
    private async Task<EventError?> RepeatAsync(EventEntity ev, CancellationToken ct)
    {
        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        if (ev.State == EventState.Finished) registry.SetFinished(runtimeId, true, ev.EndedAt);
        if (ev.State != EventState.Live || registry.Get(runtimeId) is not null) return null;
        EventRuntime runtime;
        try
        {
            runtime = await factory.BuildAsync(db, ev, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsDatabaseFailure(ex))
        {
            logger.LogError("Runtime of live event {EventId} could not be rebuilt ({ExceptionType}).", runtimeId, ex.GetType().Name);
            return EventErrors.RuntimeFailed;
        }
        try
        {
            registry.Add(runtime);
        }
        catch (ArgumentException)
        {
            // A concurrent request registered it first: running, which is all that was asked.
        }
        return null;
    }

    private async Task<EventError?> ReadyAsync(EventEntity ev, CancellationToken ct)
    {
        if (ev.GpxBytes is not { Length: > 0 }) return EventErrors.CourseMissing;
        ev.State = EventState.Ready;
        ev.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return null;
    }

    private async Task<EventError?> GoLiveAsync(EventEntity ev, CancellationToken ct)
    {
        if (ev.GpxBytes is not { Length: > 0 }) return EventErrors.CourseMissing;
        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The event row lock serializes going live with registration writes (they lock the same row first), so the
        // participants read here are the committed set, and a registration change after this commit sees Live.
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Events" WHERE "Id" = {ev.Id} FOR UPDATE""", ct);
        if (hooks?.BeforeGoLiveRead is { } readHook) await readHook(ev.Id);
        if (!await db.Participants.AnyAsync(p => p.EventId == ev.Id && p.Status == ParticipantStatus.Confirmed, ct))
            return EventErrors.NoConfirmedParticipants;
        EventRuntime runtime;
        try
        {
            runtime = await factory.BuildAsync(db, ev, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsDatabaseFailure(ex))
        {
            // Rolled back by disposing the transaction: the event stays Ready.
            logger.LogError("Runtime of event {EventId} could not be built ({ExceptionType}); it stays ready.", runtimeId, ex.GetType().Name);
            return EventErrors.RuntimeFailed;
        }
        ev.State = EventState.Live;
        ev.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct); // a concurrent change of the event fails here (the caller re-reads)
        try
        {
            registry.Add(runtime);
        }
        catch (ArgumentException)
        {
            // Some runtime with this id is registered although the event was not live: refuse rather than run two.
            logger.LogError("Event {EventId} already had a registered runtime before going live; not changed.", runtimeId);
            return EventErrors.Conflict;
        }
        try
        {
            await tx.CommitAsync(ct);
        }
        catch
        {
            registry.Remove(runtimeId);
            throw;
        }
        return null;
    }

    private async Task<EventError?> FinishAsync(EventEntity ev, CancellationToken ct)
    {
        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        ev.State = EventState.Finished;
        ev.EndedAt = clock.GetUtcNow();
        ev.Version = Guid.NewGuid();
        // Refuse ingest from now on; if the change does not commit, the event is still live and takes ingest again.
        registry.SetFinished(runtimeId, true, ev.EndedAt);
        // the results are kept in the database from now on: the runtime leaves the memory some days after the end
        if (registry.Get(runtimeId) is { } runtime && !await db.ResultRows.AnyAsync(r => r.EventId == ev.Id, ct))
            db.ResultRows.AddRange(await ResultsService.SnapshotAsync(db, ev, runtime, ct));
        if (hooks?.BeforeSave is { } hook) await hook(ev.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request changed the event first, most likely finishing it: keep ingest closed; the caller
            // re-reads and, should the event still be live after all, finishes it on the next attempt (with its own results).
            DropPendingResults();
            throw;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "ResultRows" })
        {
            // A concurrent finish stored the results first: the same as losing the race for the event row.
            DropPendingResults();
            throw new DbUpdateConcurrencyException("The results were stored by a concurrent finish.", ex);
        }
        catch
        {
            DropPendingResults();
            registry.SetFinished(runtimeId, false);
            throw;
        }
        return null;
    }

    // ----- helpers -----

    /// <summary>Forgets the result lines of a failed finish, so a retry on the same context does not insert them twice.</summary>
    private void DropPendingResults()
    {
        foreach (var entry in db.ChangeTracker.Entries<ResultRowEntity>().Where(e => e.State == EntityState.Added).ToList())
            entry.State = EntityState.Detached;
    }

    private async Task<EventEntity?> FindAsync(Guid organizerId, string eventId, bool tracked, CancellationToken ct)
    {
        if (!TryParseId(eventId, out var id)) return null;
        var query = tracked ? db.Events : db.Events.AsNoTracking();
        return await query.SingleOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId, ct);
    }

    /// <summary>Only the canonical runtime id (Guid <c>"D"</c>, lower case; ruling P5) names an event or participant.</summary>
    public static bool TryParseId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// A failure of the database (connection, timeout, provider error, also when EF wraps it in an
    /// <see cref="InvalidOperationException"/>): answered 503 <c>unavailable</c>, never as a broken event.
    /// </summary>
    public static bool IsDatabaseFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Data.Common.DbException or DbUpdateException or TimeoutException) return true;
        return false;
    }

    private static bool IsFrozen(EventEntity ev) => ev.State is EventState.Live or EventState.Finished;

    /// <summary>Saves (409 <c>conflict</c> when another request overtook this one) and returns the fresh view.</summary>
    private async Task<EventResult<EventManageView>> SaveAndViewAsync(EventEntity ev, CancellationToken ct)
    {
        if (hooks?.BeforeSave is { } hook) await hook(ev.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return EventErrors.Conflict;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return EventErrors.Conflict;
        }
        db.ChangeTracker.Clear();
        return new EventResult<EventManageView>(await ViewAsync(ev, ct), null);
    }

    private async Task<EventManageView> ViewAsync(EventEntity ev, CancellationToken ct)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.EventId == ev.Id, ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.EventId == ev.Id).OrderBy(c => c.Name).ToListAsync(ct);
        var waves = await db.Waves.AsNoTracking().Where(w => w.EventId == ev.Id).OrderBy(w => w.StartTime).ThenBy(w => w.Name).ToListAsync(ct);
        var counts = await db.Participants.AsNoTracking().Where(p => p.EventId == ev.Id)
            .GroupBy(p => p.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(ParticipantStatus s) => counts.SingleOrDefault(c => c.Status == s)?.Count ?? 0;
        var runtimeId = RuntimeFactory.RuntimeId(ev.Id);
        return new EventManageView(
            runtimeId, ev.Name, ev.Location, ev.StartTime, ev.Sport, ev.State, ev.Laps, ev.PointToPoint, ev.SpectatorDelaySeconds,
            course is null || ev.GpxBytes is null
                ? null
                : new CourseView(course.LapLengthM, course.AscentM, course.DescentM, course.PointCount, course.IsClosed,
                    course.UploadedAt, RuntimeFactory.CheckpointsOf(ev.CheckpointsJson)),
            categories.Select(c => new CategoryView(c.Id.ToString("D"), c.Name, c.Gender, c.MinAge, c.MaxAge)).ToList(),
            waves.Select(w => new WaveView(w.Id.ToString("D"), w.Name, w.StartTime)).ToList(),
            new ParticipantCounts(Count(ParticipantStatus.Invited), Count(ParticipantStatus.Confirmed),
                Count(ParticipantStatus.Declined), Count(ParticipantStatus.EmailFailed)),
            registry.Get(runtimeId) is not null, emailMode.Mode, ev.CreatedAt, ev.EndedAt, ev.ConfirmationDeadline);
    }

    private static Sport? ParseSport(string? sport) => sport switch
    {
        "running" => Sport.Running,
        "cycling" => Sport.Cycling,
        _ => null,
    };

    /// <summary>Names valid and unique (ignoring case), distances finite and distinct; null when not.</summary>
    private static List<Checkpoint>? ParseCheckpoints(List<CheckpointRequest?>? input)
    {
        if (input is null) return [];
        if (input.Count > MaxCheckpoints) return null;
        var result = new List<Checkpoint>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distances = new HashSet<double>();
        foreach (var c in input)
        {
            if (c is null || !TextRules.TryLine(c.Name, MaxGroupNameLength, out var name) || !names.Add(name)) return null;
            if (c.LapDistanceM is not { } d || !double.IsFinite(d) || !distances.Add(d)) return null;
            result.Add(new Checkpoint(name, d));
        }
        return result;
    }

    /// <summary>The decoded GPX, or 400 <c>gpx_invalid</c> (missing, not base64) / 413 <c>gpx_too_large</c> (over 1 MB).</summary>
    private static (byte[]? Gpx, EventError? Error) DecodeGpx(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return (null, EventErrors.GpxInvalid);
        // Count only the base64 characters: line-wrapped base64 (CR/LF every 76 characters) is fine.
        long chars = 0;
        foreach (var c in base64)
            if (c is not (' ' or (char)9 or (char)10 or (char)13)) chars++;
        if (chars / 4 * 3 > MaxGpxBytes + 2) return (null, EventErrors.GpxTooLarge);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return (null, EventErrors.GpxInvalid);
        }
        return bytes.Length > MaxGpxBytes ? (null, EventErrors.GpxTooLarge) : (bytes, null);
    }
}
