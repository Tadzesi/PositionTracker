using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data.Entities;

namespace PositionTracker.Data.Tests;

public class MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migrations_apply_to_an_empty_PostgreSQL_container()
    {
        var options = PostgresFixture.Options(postgres.NewDatabase());
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Initial", StringComparison.Ordinal));
            Assert.False(db.Database.HasPendingModelChanges(), "The model has changes that are not in a migration.");

            // Every table takes a row and keys and relations hold.
            var start = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
            var organizer = new OrganizerEntity
            {
                Id = Guid.NewGuid(), Name = "Žofia Organizátorka", Email = "Org@Example.com",
                EmailNormalized = "org@example.com", Language = "sk", CreatedAt = start,
            };
            var ev = new EventEntity
            {
                Id = Guid.NewGuid(), OrganizerId = organizer.Id, Name = "Jesenná časovka", Location = "Žilina",
                StartTime = start, Sport = Sport.Cycling, State = EventState.Live, Laps = 3, SpectatorDelaySeconds = 30,
                GpxBytes = [1, 2, 3], CheckpointsJson = """[{"name":"CP1","lapDistanceM":3000}]""", CreatedAt = start,
            };
            var category = new CategoryEntity { Id = Guid.NewGuid(), EventId = ev.Id, Name = "Open" };
            var wave = new WaveEntity { Id = Guid.NewGuid(), EventId = ev.Id, Name = "A", StartTime = start };
            var participant = new ParticipantEntity
            {
                Id = Guid.NewGuid(), EventId = ev.Id, Bib = "7", Name = "Ján Novák", Nickname = "Janko",
                Email = "Jan@Example.com", EmailNormalized = "jan@example.com", Gender = "male", Language = "sk",
                CategoryId = category.Id, WaveId = wave.Id, Status = ParticipantStatus.Confirmed, IsMinor = false,
                ShowNameToSpectators = true, CreatedAt = start,
            };
            db.AddRange(organizer, ev, category, wave, participant,
                new CourseEntity { EventId = ev.Id, LapLengthM = 12_500, AscentM = 120, DescentM = 120, PointCount = 501, UploadedAt = start },
                new LinkTokenEntity
                {
                    Id = Guid.NewGuid(), Purpose = "ParticipantInvite", SubjectId = participant.Id, TokenHash = new byte[32],
                    CreatedAt = start, ExpiresAt = start.AddDays(14),
                },
                new DeviceTokenEntity { Id = Guid.NewGuid(), EventId = ev.Id, ParticipantId = participant.Id, TokenHash = new byte[32], IssuedAt = start },
                new FixEntity { EventId = ev.Id, ParticipantId = participant.Id, Time = start.AddTicks(1), Lat = 49.1, Lon = 18.7, AccuracyM = null },
                new DeviceStatusEntity { EventId = ev.Id, ParticipantId = participant.Id, ReceivedAt = start, BatteryPct = 71, QueueLength = 0, InForeground = true },
                new ActionLogEntity { EventId = ev.Id, ParticipantId = participant.Id, Action = ParticipantAction.Sos, Time = start.AddTicks(3) },
                new ConsentEntity
                {
                    ParticipantId = participant.Id, LocationConsent = true, ShowNameToSpectators = true,
                    TextVersion = "2026-10-30", Time = start,
                },
                new AlarmAckEntity { EventId = ev.Id, ParticipantId = participant.Id, Kind = AlarmKind.Stopped, AcknowledgedAt = start },
                new EmailOutboxEntity
                {
                    Id = Guid.NewGuid(), ToAddress = "jan@example.com", ToName = "Ján Novák", Subject = "Pozvánka",
                    HtmlBody = "<p>x</p>", TextBody = "x", Language = "sk", Purpose = "invitation", Status = EmailOutboxStatus.Pending,
                    CreatedAt = start, NextAttemptAt = start, ParticipantId = participant.Id,
                });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var fix = await db.Fixes.SingleAsync();
            Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero).AddTicks(1), fix.Time); // tick-exact
            Assert.Null(fix.AccuracyM);
            Assert.Equal(ParticipantAction.Sos, (await db.ActionLog.SingleAsync()).Action);
            Assert.Equal("Ján Novák", (await db.Participants.SingleAsync()).Name);
            Assert.Equal(1, await db.Consents.CountAsync());
            Assert.Equal(1, await db.Outbox.CountAsync());

            // The same email twice in one event is refused by the database too.
            var ev = await db.Events.SingleAsync();
            db.Participants.Add(new ParticipantEntity
            {
                Id = Guid.NewGuid(), EventId = ev.Id, Bib = "8", Name = "Copy", Email = "JAN@example.com",
                EmailNormalized = "jan@example.com", Gender = "male", Language = "en", Status = ParticipantStatus.Invited,
                CreatedAt = ev.StartTime,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
    [Fact]
    public async Task The_point_to_point_migration_marks_the_existing_one_lap_events_on_an_open_course()
    {
        var options = PostgresFixture.Options(postgres.NewDatabase());
        var start = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        var organizer = new OrganizerEntity
        {
            Id = Guid.NewGuid(), Name = "Org", Email = "org@example.com", EmailNormalized = "org@example.com", Language = "sk", CreatedAt = start,
        };
        EventEntity Event(string name, int laps, bool? closed)
        {
            var ev = new EventEntity
            {
                Id = Guid.NewGuid(), OrganizerId = organizer.Id, Name = name, Location = "Žilina", StartTime = start, Sport = Sport.Cycling,
                State = EventState.Draft, Laps = laps, SpectatorDelaySeconds = 30, GpxBytes = closed is null ? null : [1], CreatedAt = start,
            };
            return ev;
        }
        var open = Event("open", 1, false);
        var loop = Event("loop", 1, true);
        var laps = Event("laps", 3, true);
        var none = Event("none", 1, null);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.AddRange(organizer, open, loop, laps, none);
            foreach (var (ev, closed) in new[] { (open, false), (loop, true), (laps, true) })
                db.Add(new CourseEntity { EventId = ev.Id, LapLengthM = 12_500, PointCount = 501, IsClosed = closed, UploadedAt = start });
            await db.SaveChangesAsync();

            // Back to before the column, then up again: the migration fills it from the stored courses.
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261101194428_ResultsStartObserved");
            await migrator.MigrateAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var flags = await db.Events.ToDictionaryAsync(e => e.Name, e => e.PointToPoint);
            Assert.Equal(new Dictionary<string, bool> { ["open"] = true, ["loop"] = false, ["laps"] = false, ["none"] = false }, flags);
        }
    }
}
