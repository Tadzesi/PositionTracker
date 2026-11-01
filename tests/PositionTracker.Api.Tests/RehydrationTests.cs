using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PositionTracker.Api.Live;
using PositionTracker.Api.Persistence;
using PositionTracker.Api.Simulation;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Persistence through the HTTP host against a real PostgreSQL (Testcontainers): fixes and actions are written off the
/// request path, and a new host over the same database rebuilds the live events with identical views.
/// </summary>
public class RehydrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = ApiFactory.T0;
    private static readonly IReadOnlyList<Checkpoint> Checkpoints = [new("CP1", 3_000), new("CP2", 8_000)];
    private static readonly Course Course = Course.Create(DemoCourses.Loop(), 1, Checkpoints);

    [Fact]
    public async Task Restart_gives_identical_live_and_public_views()
    {
        var cs = postgres.NewDatabase();
        var seeded = await SeedAsync(cs);
        var snapshotAt = T0.AddSeconds(485);

        string live1, public1;
        var first = Host(cs, T0);
        await using (first)
        {
            var runtime = first.Registry.Get(seeded.EventId);
            Assert.NotNull(runtime); // rebuilt at startup from the database (Live, no history yet)
            Assert.Equal(new[] { seeded.P1, seeded.P2, seeded.P3, seeded.P4 }.Order(), runtime.Participants.Keys.Order());
            Assert.False(runtime.HasParticipant(seeded.Invited)); // only confirmed participants race

            var clients = new Dictionary<string, HttpClient>();
            foreach (var id in new[] { seeded.P1, seeded.P2, seeded.P3, seeded.P4 })
                clients[id] = first.CreateClient(first.Tokens.Issue(seeded.EventId, id));
            var speeds = new Dictionary<string, double> { [seeded.P1] = 9, [seeded.P2] = 8, [seeded.P3] = 7, [seeded.P4] = 6 };

            for (var s = 30; s <= 480; s += 30)
            {
                first.Clock.SetUtcNow(T0.AddSeconds(s + 2));
                foreach (var (id, client) in clients)
                {
                    if (id == seeded.P4 && s > 150) continue; // goes silent, then DNF
                    var seconds = Enumerable.Range(0, 6).Select(i => s - 25 + 5 * i).Where(t => t >= 0).ToList();
                    if (s == 30) seconds.Insert(0, 0);
                    if (id == seeded.P2) seconds.RemoveAll(t => t is >= 120 and <= 200); // held back on the phone
                    if (id == seeded.P2 && s == 300) // the late batch: older fixes arrive after newer ones
                        seconds.InsertRange(0, Enumerable.Range(0, 17).Select(i => 120 + 5 * i));
                    if (seconds.Count == 0) continue;
                    object? status = id == seeded.P3 ? null : new
                    {
                        battery = id == seeded.P1 ? 40 - s / 12.0 : 80,
                        charging = false, queue = seconds.Count - 1, foreground = true,
                    };
                    var fixes = seconds.Select(t => Fix(speeds[id] * t, t)).ToList();
                    if (id == seeded.P1 && s == 60) fixes.Add(new { t = Utc(T0.AddSeconds(57)), lat = 49.0, lon = 18.0, acc = 500.0 });
                    var response = await client.PostAsJsonAsync($"/api/events/{seeded.EventId}/positions", new { fixes, status });
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                }

                if (s == 210) await Act(clients[seeded.P4], seeded.EventId, "dnf");
                if (s == 240) await Act(clients[seeded.P1], seeded.EventId, "pause");
                if (s == 270) await Act(clients[seeded.P1], seeded.EventId, "resume");
                if (s == 390) await Act(clients[seeded.P3], seeded.EventId, "sos");
                if (s == 330) await UpdateParticipantAsync(cs, runtime, seeded.P1, p => p.Nickname = "Flash");
                if (s == 360) await UpdateParticipantAsync(cs, runtime, seeded.P2, p => p.ShowNameToSpectators = false);
            }

            (live1, public1) = await SnapshotAsync(first, seeded.EventId, snapshotAt);
            foreach (var client in clients.Values) client.Dispose();
        } // the host stops here and flushes its pending writes

        await using (var db = new AppDbContext(PostgresFixture.Options(cs)))
        {
            Assert.True(await db.Fixes.CountAsync() > 300);
            Assert.DoesNotContain(await db.Fixes.ToListAsync(), f => f.AccuracyM > 50); // rejected fixes are not stored
            Assert.Equal([ParticipantAction.Dnf, ParticipantAction.Pause, ParticipantAction.Resume, ParticipantAction.Sos],
                await db.ActionLog.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        }

        await using var second = Host(cs, snapshotAt);
        var (live2, public2) = await SnapshotAsync(second, seeded.EventId, snapshotAt);

        Assert.Equal(live1, live2);
        Assert.Equal(public1, public2);

        // The compared views are not trivial.
        using var live = JsonDocument.Parse(live2);
        var rows = live.RootElement.GetProperty("participants").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        string State(string id) => rows.Single(r => r.GetProperty("participantId").GetString() == id).GetProperty("state").GetString()!;
        Assert.Equal("sos", State(seeded.P3));
        Assert.Equal("dnf", State(seeded.P4));
        Assert.Contains("purple", live2);
        Assert.Contains("lowBattery", live2);
        Assert.Contains("Flash", public2);
        Assert.DoesNotContain("Rider Two", public2);
        Assert.DoesNotContain("Young Rider", public2);
        Assert.DoesNotContain("Not Yet", live2);
    }

    [Fact]
    public async Task Fixes_are_written_in_batches_off_the_request_path_and_flushed_on_shutdown()
    {
        var cs = postgres.NewDatabase();
        var seeded = await SeedAsync(cs);
        await using var host = Host(cs, T0.AddSeconds(300), GatedStore);
        var store = (GatedFixStore)host.Services.GetRequiredService<IFixStore>();
        using var p1 = host.CreateClient(host.Tokens.Issue(seeded.EventId, seeded.P1));
        using var p2 = host.CreateClient(host.Tokens.Issue(seeded.EventId, seeded.P2));
        using var p3 = host.CreateClient(host.Tokens.Issue(seeded.EventId, seeded.P3));
        store.Close();

        var inaccurate = new { t = Utc(T0.AddSeconds(22)), lat = 49.0, lon = 18.0, acc = 500.0 };
        await Post(p1, seeded.EventId, [.. Fixes(9, 0, 5), inaccurate]);
        await Post(p2, seeded.EventId, Fixes(8, 0, 5));
        await Post(p1, seeded.EventId, Fixes(9, 25, 5));
        Assert.Empty(store.Calls); // answered without touching the database; the 500 ms batch window is still open

        host.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntil(() => store.Calls.Count == 1); // the writer is now blocked inside the database write
        await Post(p2, seeded.EventId, Fixes(8, 25, 5)); // ingest does not wait for the blocked database
        store.Open();
        await WaitUntil(() => store.Written.Count == 1);
        Assert.Equal(15, store.Calls.Single()); // three requests, one write, the inaccurate fix not among them

        await Post(p3, seeded.EventId, Fixes(7, 0, 200, step: 1)); // 200 fixes do not wait for the window
        await WaitUntil(() => store.Written.Count == 2);
        Assert.Equal([15, 205], store.Calls);

        await Post(p1, seeded.EventId, Fixes(9, 50, 5)); // still pending when the host stops
        await host.DisposeAsync();

        Assert.Equal([15, 205, 5], store.Calls);
        await using var db = new AppDbContext(PostgresFixture.Options(cs));
        Assert.Equal(225, await db.Fixes.CountAsync());
        Assert.Equal(3, await db.DeviceStatuses.CountAsync()); // every request carried a status, but an unchanged one is written once a minute per participant
    }

    [Fact]
    public async Task Ingest_answers_503_without_applying_the_batch_when_the_write_queue_is_full()
    {
        var cs = postgres.NewDatabase();
        var seeded = await SeedAsync(cs);
        var host = Host(cs, T0.AddSeconds(300), GatedStore);
        host.Settings["Persistence:QueueCapacity"] = "1";
        host.Settings["Persistence:EnqueueTimeoutMs"] = "200";
        await using (host)
        {
            var store = (GatedFixStore)host.Services.GetRequiredService<IFixStore>();
            using var p1 = host.CreateClient(host.Tokens.Issue(seeded.EventId, seeded.P1));
            store.Close();

            await Post(p1, seeded.EventId, Fixes(9, 0, 5));
            host.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await WaitUntil(() => store.Calls.Count == 1); // the only slot is held until the database write ends

            var refused = await p1.PostAsJsonAsync($"/api/events/{seeded.EventId}/positions", new { fixes = Fixes(9, 25, 5) });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), refused.Headers.RetryAfter?.Delta);
            var view = host.Registry.Get(seeded.EventId)!.BuildLiveView().Participants.Single(p => p.ParticipantId == seeded.P1);
            Assert.Equal(T0.AddSeconds(20), view.LastRaceFixTime); // nothing of the refused batch was applied

            store.Open();
            await WaitUntil(() => store.Written.Count == 1);
            await Post(p1, seeded.EventId, Fixes(9, 25, 5)); // the resent batch now goes through
        }

        await using var db = new AppDbContext(PostgresFixture.Options(cs));
        Assert.Equal(10, await db.Fixes.CountAsync());
    }

    [Fact]
    public async Task Simulated_events_are_never_persisted()
    {
        var cs = postgres.NewDatabase();
        await SeedAsync(cs);
        await using (var host = Host(cs, T0))
        {
            var simulation = host.Services.GetRequiredService<SimulationService>();
            var demo = simulation.Start(new DemoRequest(Participants: 2));
            simulation.Step(demo.EventId, TimeSpan.FromMinutes(2));
            using var client = host.CreateClient(simulation.TokenOf(demo.EventId, "sim-001"));
            var start = host.Registry.Get(demo.EventId)!.Settings.StartTime;
            var point = host.Registry.Get(demo.EventId)!.Course.PointAt(1_200);

            var positions = await client.PostAsJsonAsync($"/api/events/{demo.EventId}/positions", new
            {
                fixes = new[] { new { t = Utc(start.AddSeconds(150)), lat = point.Lat, lon = point.Lon, acc = 5.0 } },
                status = new { battery = 50, queue = 0, foreground = true },
            });
            var action = await client.PostAsJsonAsync($"/api/events/{demo.EventId}/actions", new { action = "sos" });
            Assert.Equal(HttpStatusCode.Accepted, positions.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, action.StatusCode);
        }

        await using var db = new AppDbContext(PostgresFixture.Options(cs));
        Assert.Equal(0, await db.Fixes.CountAsync());
        Assert.Equal(0, await db.DeviceStatuses.CountAsync());
        Assert.Equal(0, await db.ActionLog.CountAsync());
    }

    private static void GatedStore(IServiceCollection services)
    {
        services.RemoveAll<IFixStore>();
        services.AddSingleton<IFixStore>(sp => new GatedFixStore(ActivatorUtilities.CreateInstance<FixStore>(sp)));
    }

    private static ApiFactory Host(string connectionString, DateTimeOffset now, Action<IServiceCollection>? services = null)
    {
        var factory = new ApiFactory { SeedTestEvent = false, ConfigureServices = services };
        factory.Settings["ConnectionStrings:Default"] = connectionString;
        factory.Settings["Live:IntervalMs"] = "3600000"; // views are pushed by the test only
        factory.Clock.SetUtcNow(now);
        return factory;
    }

    /// <summary>
    /// Pushes a view at <paramref name="at"/> and another one a spectator delay later, then returns the organizer
    /// and spectator JSON (the spectator one is the projection of the first view).
    /// </summary>
    private static async Task<(string Live, string Public)> SnapshotAsync(ApiFactory host, string eventId, DateTimeOffset at)
    {
        host.Clock.SetUtcNow(at);
        var broadcaster = host.Services.GetRequiredService<LiveBroadcaster>();
        await broadcaster.BroadcastAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await broadcaster.BroadcastAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", host.OrganizerKey);
        var live = await client.GetAsync($"/api/events/{eventId}/live");
        var spectators = await client.GetAsync($"/api/events/{eventId}/public");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, spectators.StatusCode);
        return (await live.Content.ReadAsStringAsync(), await spectators.Content.ReadAsStringAsync());
    }

    /// <summary>Stands in for the participant endpoints of a later task: database first, then the runtime.</summary>
    private static async Task UpdateParticipantAsync(
        string cs, EventRuntime runtime, string participantId, Action<ParticipantEntity> change)
    {
        await using var db = new AppDbContext(PostgresFixture.Options(cs));
        var entity = await db.Participants.SingleAsync(p => p.Id == Guid.Parse(participantId));
        change(entity);
        await db.SaveChangesAsync();
        runtime.UpdateParticipant(runtime.Participants[participantId] with
        {
            Nickname = entity.Nickname, ShowNameToSpectators = entity.ShowNameToSpectators,
        });
    }

    private static async Task Act(HttpClient client, string eventId, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/events/{eventId}/actions", new { action });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task Post(HttpClient client, string eventId, object[] fixes)
    {
        var response = await client.PostAsJsonAsync($"/api/events/{eventId}/positions",
            new { fixes, status = new { battery = 60, queue = 0, foreground = true } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static object[] Fixes(double speed, int fromSecond, int count, int step = 5) =>
        Enumerable.Range(0, count).Select(i => Fix(speed * (fromSecond + i * step), fromSecond + i * step)).ToArray();

    private static object Fix(double totalM, int seconds)
    {
        var p = Course.PointAt(totalM);
        return new { t = Utc(T0.AddSeconds(seconds)), lat = p.Lat, lon = p.Lon, acc = 5.0 };
    }

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached within 10 s.");
            await Task.Delay(10);
        }
    }

    private sealed record Seeded(string EventId, string P1, string P2, string P3, string P4, string Invited);

    /// <summary>
    /// A Live cycling event (one lap of the 12.5 km demo loop with two checkpoints, start <see cref="T0"/>, 30 s
    /// spectator delay) with four confirmed participants (the third a minor) and one still invited.
    /// </summary>
    private static async Task<Seeded> SeedAsync(string cs)
    {
        await using var db = new AppDbContext(PostgresFixture.Options(cs));
        await db.Database.MigrateAsync();
        var created = T0.AddDays(-10);
        var organizer = new OrganizerEntity
        {
            Id = Guid.NewGuid(), Name = "Org", Email = "org@example.com", EmailNormalized = "org@example.com",
            Language = "en", CreatedAt = created, EmailConfirmedAt = created,
        };
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(), OrganizerId = organizer.Id, Name = "Test Ride", Location = "Loop", StartTime = T0,
            Sport = Sport.Cycling, State = EventState.Live, Laps = 1, SpectatorDelaySeconds = 30,
            GpxBytes = Gpx(DemoCourses.Loop()),
            CheckpointsJson = """[{"name":"CP1","lapDistanceM":3000},{"name":"CP2","lapDistanceM":8000}]""",
            CreatedAt = created,
        };
        var open = new CategoryEntity { Id = Guid.NewGuid(), EventId = ev.Id, Name = "Open" };
        var n = 0;
        ParticipantEntity Participant(string name, string? nickname, ParticipantStatus status, bool minor = false)
        {
            n++;
            return new ParticipantEntity
            {
                Id = Guid.NewGuid(), EventId = ev.Id, Bib = n.ToString(CultureInfo.InvariantCulture), Name = name,
                Nickname = nickname, Email = $"rider{n}@example.com", EmailNormalized = $"rider{n}@example.com",
                Gender = "female", Language = "en", CategoryId = open.Id, Status = status, IsMinor = minor,
                ShowNameToSpectators = true, CreatedAt = created.AddMinutes(n),
            };
        }
        var p1 = Participant("Rider One", null, ParticipantStatus.Confirmed);
        var p2 = Participant("Rider Two", "Speedy", ParticipantStatus.Confirmed);
        var p3 = Participant("Young Rider", "Kid", ParticipantStatus.Confirmed, minor: true);
        var p4 = Participant("Rider Four", null, ParticipantStatus.Confirmed);
        var invited = Participant("Not Yet", null, ParticipantStatus.Invited);
        db.AddRange(organizer, ev, open, p1, p2, p3, p4, invited);
        await db.SaveChangesAsync();
        return new Seeded(ev.Id.ToString("D"), p1.Id.ToString("D"), p2.Id.ToString("D"), p3.Id.ToString("D"),
            p4.Id.ToString("D"), invited.Id.ToString("D"));
    }

    private static byte[] Gpx(IReadOnlyList<GeoPoint> points)
    {
        var gpx = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>""");
        foreach (var p in points)
            gpx.Append(CultureInfo.InvariantCulture, $"""<trkpt lat="{p.Lat:R}" lon="{p.Lon:R}">""")
                .Append(p.Ele is { } ele ? string.Create(CultureInfo.InvariantCulture, $"<ele>{ele:R}</ele>") : "")
                .Append("</trkpt>");
        return Encoding.UTF8.GetBytes(gpx.Append("</trkseg></trk></gpx>").ToString());
    }

    /// <summary>
    /// The real store behind a gate: <see cref="Calls"/> records the fix count of every write as it starts,
    /// <see cref="Written"/> as it completes; while closed, writes wait.
    /// </summary>
    private sealed class GatedFixStore(IFixStore inner) : IFixStore
    {
        private volatile TaskCompletionSource _gate = Opened();

        public ConcurrentQueue<int> Calls { get; } = new();

        public ConcurrentQueue<int> Written { get; } = new();

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public async Task WriteAsync(IReadOnlyList<HistoryWrite> writes, CancellationToken ct)
        {
            var count = writes.OfType<FixBatchWrite>().Sum(w => w.Fixes.Count);
            Calls.Enqueue(count);
            await _gate.Task.WaitAsync(ct);
            await inner.WriteAsync(writes, ct);
            Written.Enqueue(count);
        }

        public Task AppendAsync(string eventId, string participantId, IReadOnlyList<PositionFix> accepted,
            DeviceStatus? status, DateTimeOffset receivedAt, CancellationToken ct) =>
            WriteAsync([new FixBatchWrite(eventId, participantId, accepted, status, receivedAt)], ct);

        public IAsyncEnumerable<StoredFix> ReadAsync(string eventId, CancellationToken ct) => inner.ReadAsync(eventId, ct);

        public IAsyncEnumerable<StoredStatus> ReadStatusesAsync(string eventId, CancellationToken ct) =>
            inner.ReadStatusesAsync(eventId, ct);

        public IAsyncEnumerable<StoredAction> ReadActionsAsync(string eventId, CancellationToken ct) =>
            inner.ReadActionsAsync(eventId, ct);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset eventEndedBefore, CancellationToken ct) =>
            inner.DeleteOlderThanAsync(eventEndedBefore, ct);

        private static TaskCompletionSource Opened()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }
    }
}
