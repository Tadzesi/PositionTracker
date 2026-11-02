using System.Net;
using System.Text.Json;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Tests;

/// <summary>
/// <c>GET /api/events/{eventId}/info</c>: the anonymous header of a published event (name, place, start, sport, state),
/// available before the event has a runtime; a Draft, unknown or malformed event is 404. Against PostgreSQL (Testcontainers).
/// </summary>
public sealed class PublicEventInfoTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_ready_event_shows_its_name_place_and_start_to_anyone()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);

        var response = await ParticipantLinkTests.Client(host).GetAsync($"/api/events/{ev:D}/info");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Jarná časovka", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("Žilina", json.RootElement.GetProperty("location").GetString());
        Assert.Equal("2026-11-08T10:00:00Z", json.RootElement.GetProperty("startTime").GetString());
        Assert.Equal("cycling", json.RootElement.GetProperty("sport").GetString());
        Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("endedAt").ValueKind);
        Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("organizer", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("unknown")]
    [InlineData("not-a-guid")]
    public async Task Draft_unknown_and_malformed_events_are_not_found(string which)
    {
        await using var host = Host();
        var id = which switch
        {
            "draft" => (await ParticipantLinkTests.SeedEventAsync(host, EventState.Draft)).ToString("D"),
            "unknown" => Guid.NewGuid().ToString("D"),
            _ => which,
        };

        var response = await ParticipantLinkTests.Client(host).GetAsync($"/api/events/{id}/info");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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
}
