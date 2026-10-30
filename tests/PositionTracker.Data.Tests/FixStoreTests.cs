using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data.Entities;

namespace PositionTracker.Data.Tests;

public class FixStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private static PositionFix Fix(int seconds, double lat, double? acc = 5) => new(T0.AddSeconds(seconds), lat, 18.7, acc);

    [Fact]
    public async Task FixStore_appends_and_reads_in_device_time_order_and_deletes_old_events_only()
    {
        var options = PostgresFixture.Options(postgres.NewDatabase());
        await using (var db = new AppDbContext(options)) await db.Database.MigrateAsync();
        var store = new FixStore(new PooledDbContextFactory<AppDbContext>(options));
        var now = T0.AddDays(45);
        var (oldEvent, oldRider) = await SeedAsync(options, endedAt: now.AddDays(-31));
        var (recentEvent, recentRider) = await SeedAsync(options, endedAt: now.AddDays(-29));
        var (liveEvent, liveRider) = await SeedAsync(options, endedAt: null);

        // Out of device-time order across two appends; the two fixes at +10 s keep their insertion order.
        var late = Fix(10, 49.2).Time.AddTicks(1234567); // sub-microsecond ticks survive the round trip
        await store.AppendAsync(oldEvent, oldRider, [Fix(30, 49.3), Fix(10, 49.1, acc: null)],
            new DeviceStatus(15, false, 2, true), T0.AddSeconds(31), CancellationToken.None);
        await store.AppendAsync(oldEvent, oldRider, [Fix(20, 49.2), new PositionFix(late, 49.25, 18.7, 4), Fix(10, 49.15)],
            null, T0.AddSeconds(41), CancellationToken.None);
        await store.WriteAsync(
        [
            new FixBatchWrite(recentEvent, recentRider, [Fix(5, 48.0)], null, T0.AddSeconds(6)),
            new ActionWrite(recentEvent, recentRider, ParticipantAction.Sos, T0.AddSeconds(7).AddTicks(3)),
            new FixBatchWrite(liveEvent, liveRider, [Fix(5, 47.0)], new DeviceStatus(null, null, 0, false), T0.AddSeconds(8)),
        ], CancellationToken.None);

        var read = await ReadAll(store.ReadAsync(oldEvent, CancellationToken.None));
        Assert.Equal([49.1, 49.15, 49.25, 49.2, 49.3], read.Select(f => f.Fix.Lat));
        Assert.All(read, f => Assert.Equal(oldRider, f.ParticipantId));
        Assert.Equal(late, read[2].Fix.Time);
        Assert.Null(read[0].Fix.AccuracyM);
        Assert.Equal(T0.AddSeconds(30), read[^1].Fix.Time);
        var status = Assert.Single(await ReadAll(store.ReadStatusesAsync(oldEvent, CancellationToken.None)));
        Assert.Equal(new StoredStatus(oldRider, new DeviceStatus(15, false, 2, true), T0.AddSeconds(31)), status);
        Assert.Equal(new StoredAction(recentRider, ParticipantAction.Sos, T0.AddSeconds(7).AddTicks(3)),
            Assert.Single(await ReadAll(store.ReadActionsAsync(recentEvent, CancellationToken.None))));

        // Retention: only events that ended before the cut-off lose their fixes (and device statuses).
        var deleted = await store.DeleteOlderThanAsync(now.AddDays(-30), CancellationToken.None);

        Assert.Equal(5, deleted);
        Assert.Empty(await ReadAll(store.ReadAsync(oldEvent, CancellationToken.None)));
        Assert.Empty(await ReadAll(store.ReadStatusesAsync(oldEvent, CancellationToken.None)));
        Assert.Single(await ReadAll(store.ReadAsync(recentEvent, CancellationToken.None)));
        Assert.Single(await ReadAll(store.ReadAsync(liveEvent, CancellationToken.None)));
        Assert.Single(await ReadAll(store.ReadStatusesAsync(liveEvent, CancellationToken.None)));
        Assert.Single(await ReadAll(store.ReadActionsAsync(recentEvent, CancellationToken.None)));
    }

    private static async Task<(string EventId, string ParticipantId)> SeedAsync(
        DbContextOptions<AppDbContext> options, DateTimeOffset? endedAt)
    {
        await using var db = new AppDbContext(options);
        var organizer = new OrganizerEntity
        {
            Id = Guid.NewGuid(), Name = "Org", Email = $"org-{Guid.NewGuid():N}@example.com",
            EmailNormalized = $"org-{Guid.NewGuid():N}@example.com", Language = "en", CreatedAt = T0,
        };
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(), OrganizerId = organizer.Id, Name = "Ride", Location = "Here", StartTime = T0,
            Sport = Sport.Cycling, State = endedAt is null ? EventState.Live : EventState.Finished, Laps = 1,
            SpectatorDelaySeconds = 30, CreatedAt = T0, EndedAt = endedAt,
        };
        var participant = new ParticipantEntity
        {
            Id = Guid.NewGuid(), EventId = ev.Id, Bib = "1", Name = "Rider", Email = "r@example.com",
            EmailNormalized = "r@example.com", Gender = "female", Language = "en", Status = ParticipantStatus.Confirmed,
            ShowNameToSpectators = true, CreatedAt = T0,
        };
        db.AddRange(organizer, ev, participant);
        await db.SaveChangesAsync();
        return (ev.Id.ToString("D"), participant.Id.ToString("D"));
    }

    private static async Task<List<T>> ReadAll<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
