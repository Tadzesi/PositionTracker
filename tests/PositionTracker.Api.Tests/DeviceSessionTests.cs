using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Persistence;
using PositionTracker.Core.Simulation;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The native app's view of the device API: <c>GET /api/device/session</c> and how the bearer ingest tells a device to
/// wait (<c>event_not_live</c>) or to stop (<c>event_finished</c>, also once the runtime left the memory). PostgreSQL.
/// </summary>
public sealed class DeviceSessionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_ready_event_tells_the_app_to_wait_and_positions_are_409_event_not_live()
    {
        await using var host = Host();
        var (ev, token) = await RiderAsync(host, EventState.Ready);
        using var client = host.CreateClient(token);

        var session = await ParticipantLinkTests.JsonAsync(await client.GetAsync("/api/device/session"));
        var root = session.RootElement;
        Assert.Equal(ev.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal("ready", root.GetProperty("event").GetProperty("state").GetString());
        Assert.Equal("cycling", root.GetProperty("event").GetProperty("sport").GetString());
        Assert.Equal("Jarná časovka", root.GetProperty("event").GetProperty("name").GetString());
        Assert.Equal("1", root.GetProperty("me").GetProperty("bib").GetString());
        Assert.Equal("Rider", root.GetProperty("me").GetProperty("name").GetString());
        Assert.Equal(Iso(ParticipantLinkTests.Start), root.GetProperty("me").GetProperty("startTime").GetString());
        Assert.Equal(5, root.GetProperty("tuning").GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(10, root.GetProperty("tuning").GetProperty("displacementM").GetInt32());
        Assert.Equal(50, root.GetProperty("tuning").GetProperty("ignoreInaccurateM").GetInt32());
        Assert.Equal(JsonValueKindNull, root.GetProperty("link").ValueKind);

        var positions = await client.PostAsJsonAsync($"/api/events/{ev:D}/positions", Batch(host));
        await ParticipantLinkTests.AssertErrorAsync(positions, HttpStatusCode.Conflict, "event_not_live");
        var action = await client.PostAsync($"/api/events/{ev:D}/actions", ParticipantLinkTests.Json("""{"action":"sos"}"""));
        await ParticipantLinkTests.AssertErrorAsync(action, HttpStatusCode.Conflict, "event_not_live");
        await using var db = ParticipantLinkTests.Db(host);
        Assert.Empty(await db.Fixes.ToListAsync());
    }

    [Fact]
    public async Task The_own_start_is_the_wave_start()
    {
        await using var host = Host();
        var (ev, token) = await RiderAsync(host, EventState.Ready);
        var waveStart = ParticipantLinkTests.Start.AddMinutes(20);
        await using (var db = ParticipantLinkTests.Db(host))
        {
            var wave = new WaveEntity { Id = Guid.NewGuid(), EventId = ev, Name = "B", StartTime = waveStart };
            db.Waves.Add(wave);
            await db.SaveChangesAsync();
            await db.Participants.Where(p => p.EventId == ev).ExecuteUpdateAsync(s => s.SetProperty(p => p.WaveId, wave.Id));
        }
        using var client = host.CreateClient(token);

        var session = await ParticipantLinkTests.JsonAsync(await client.GetAsync("/api/device/session"));
        Assert.Equal(Iso(waveStart), session.RootElement.GetProperty("me").GetProperty("startTime").GetString());
        Assert.Equal(Iso(ParticipantLinkTests.Start), session.RootElement.GetProperty("event").GetProperty("startTime").GetString());
    }

    [Fact]
    public async Task A_live_event_has_the_link_light_and_accepts_positions()
    {
        await using var host = Host();
        var (ev, token) = await RiderAsync(host, EventState.Live);
        host.Clock.SetUtcNow(ParticipantLinkTests.Start.AddMinutes(1));
        using var client = host.CreateClient(token);

        var positions = await client.PostAsJsonAsync($"/api/events/{ev:D}/positions", Batch(host));
        Assert.Equal(HttpStatusCode.Accepted, positions.StatusCode);
        var session = await ParticipantLinkTests.JsonAsync(await client.GetAsync("/api/device/session"));
        Assert.Equal("live", session.RootElement.GetProperty("event").GetProperty("state").GetString());
        Assert.Equal("good", session.RootElement.GetProperty("link").GetProperty("level").GetString());
    }

    [Fact]
    public async Task A_finished_event_without_a_runtime_is_409_event_finished_and_the_session_says_finished()
    {
        await using var host = Host();
        var (ev, token) = await RiderAsync(host, EventState.Ready);
        await using (var db = ParticipantLinkTests.Db(host))
            await db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EventState.Finished));
        Assert.Null(host.Registry.Get(ev.ToString("D")));
        using var client = host.CreateClient(token);

        var positions = await client.PostAsJsonAsync($"/api/events/{ev:D}/positions", Batch(host));
        await ParticipantLinkTests.AssertErrorAsync(positions, HttpStatusCode.Conflict, "event_finished");
        var session = await ParticipantLinkTests.JsonAsync(await client.GetAsync("/api/device/session"));
        Assert.Equal("finished", session.RootElement.GetProperty("event").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_finished_live_event_is_finished_in_the_session()
    {
        await using var host = Host();
        var (ev, token) = await RiderAsync(host, EventState.Live);
        host.Registry.SetFinished(ev.ToString("D"), true);
        using var client = host.CreateClient(token);

        var session = await ParticipantLinkTests.JsonAsync(await client.GetAsync("/api/device/session"));
        Assert.Equal("finished", session.RootElement.GetProperty("event").GetProperty("state").GetString());
        Assert.Equal(JsonValueKindNull, session.RootElement.GetProperty("link").ValueKind);
    }

    [Fact]
    public async Task No_unknown_or_revoked_token_is_401()
    {
        await using var host = Host();
        var (_, token) = await RiderAsync(host, EventState.Ready);

        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/device/session")).StatusCode);
        using var unknown = host.CreateClient("not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.GetAsync("/api/device/session")).StatusCode);

        // consenting again (another phone) issues a new token and revokes this one
        await using (var db = ParticipantLinkTests.Db(host))
        {
            var pid = await db.Participants.Select(p => p.Id).SingleAsync();
            var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
            await ParticipantLinkTests.ConsentAsync(ParticipantLinkTests.Client(host), link, false);
        }
        using var revoked = host.CreateClient(token);
        var response = await revoked.GetAsync("/api/device/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private ApiFactory Host()
    {
        var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["ConnectionStrings:Default"] = postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }

    /// <summary>An event in <paramref name="state"/> (live: with its runtime) and one consented rider (bib "1"): the device token.</summary>
    private static async Task<(Guid Event, string Token)> RiderAsync(ApiFactory host, EventState state)
    {
        var ev = await ParticipantLinkTests.SeedEventAsync(host, state);
        if (state == EventState.Live) await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
        var web = ParticipantLinkTests.Client(host);
        await ParticipantLinkTests.PostAsync(web, link, "/confirm", new { });
        return (ev, await ParticipantLinkTests.ConsentAsync(web, link, false));
    }

    private static object Batch(ApiFactory host)
    {
        var point = DemoCourses.Loop()[0];
        return new
        {
            fixes = new[] { new { t = Iso(host.Clock.GetUtcNow()), lat = point.Lat, lon = point.Lon, acc = 5.0 } },
            status = new { battery = 80, charging = false, queue = 0, foreground = true },
        };
    }
}
