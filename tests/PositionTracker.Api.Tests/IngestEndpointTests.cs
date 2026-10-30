using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Core;

namespace PositionTracker.Api.Tests;

public class IngestEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string PositionsUrl = $"/api/events/{ApiFactory.EventId}/positions";
    private const string ActionsUrl = $"/api/events/{ApiFactory.EventId}/actions";

    /// <summary>A fix on the course at <paramref name="totalM"/> metres, <paramref name="seconds"/> after the start.</summary>
    private static object Fix(ApiFactory f, double totalM, int seconds, double acc = 5)
    {
        var p = f.Event.Course.PointAt(totalM);
        return new { t = Utc(seconds), lat = p.Lat, lon = p.Lon, acc };
    }

    private static string Utc(int seconds) =>
        ApiFactory.T0.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(int Accepted, int Rejected)> ReadResult(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accepted").GetInt32(), doc.RootElement.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Missing_authorization_header_returns_401()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_token_returns_401()
    {
        var client = factory.CreateClient("not-a-real-token");
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_of_another_event_returns_403()
    {
        var foreign = factory.Tokens.Issue("evt-other", "q1");
        var client = factory.CreateClient(foreign);
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_event_returns_404()
    {
        var token = factory.Tokens.Issue("evt-missing", "q1");
        var client = factory.CreateClient(token);
        var response = await client.PostAsJsonAsync("/api/events/evt-missing/positions",
            new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Valid_batch_returns_202_with_accepted_count_and_stores_status()
    {
        var client = factory.CreateClient(factory.Token1);
        var body = new
        {
            fixes = new[] { Fix(factory, 0, 0), Fix(factory, 50, 5), Fix(factory, 100, 10) },
            status = new { battery = 71, charging = false, queue = 2, foreground = true },
        };

        var response = await client.PostAsJsonAsync(PositionsUrl, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((3, 0), await ReadResult(response));
        var view = factory.Event.BuildLiveView().Participants.Single(p => p.ParticipantId == ApiFactory.Participant1);
        Assert.Equal(71, view.BatteryPct);
        Assert.Equal(2, view.QueueLength);
    }

    [Fact]
    public async Task Inaccurate_fix_is_counted_as_rejected()
    {
        var client = factory.CreateClient(factory.Token2);
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 100, acc: 80) } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((0, 1), await ReadResult(response));
    }

    [Fact]
    public async Task Out_of_range_latitude_is_counted_as_rejected()
    {
        var client = factory.CreateClient(factory.Token2);
        var response = await client.PostAsync(PositionsUrl,
            Json($$"""{"fixes":[{"t":"{{Utc(200)}}","lat":95.0,"lon":18.7,"acc":5}]}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((0, 1), await ReadResult(response));
    }

    [Theory]
    [InlineData("""{"fixes":[{"t":"2026-11-07T10:00:05Z","lat":49.0,""")]
    [InlineData("not json")]
    [InlineData("""{"fixes":[{"lat":49.0,"lon":18.7}]}""")]
    [InlineData("""{"fixes":[{"t":"2026-11-07T10:00:05","lat":49.0,"lon":18.7}]}""")]
    [InlineData("""{"fixes":[{"t":"2026-11-07T10:00:05+02:00","lat":49.0,"lon":18.7}]}""")]
    [InlineData("""{"fixes":[{"t":"07/11/2026 10:00:05","lat":49.0,"lon":18.7}]}""")]
    [InlineData("""{"fixes":[null]}""")]
    [InlineData("""{"fixes":null}""")]
    [InlineData("""{"fixes":[{"t":"2026-11-07T10:00:05Z","lat":49.0,"lon":18.7}],"status":{"battery":50}}""")]
    public async Task Malformed_body_returns_400(string json)
    {
        var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(PositionsUrl, Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Utc_timestamp_with_fraction_is_accepted()
    {
        var client = factory.CreateClient(factory.Token2);
        var response = await client.PostAsync(PositionsUrl,
            Json("""{"fixes":[{"t":"2026-11-07T10:05:00.250Z","lat":49.0,"lon":18.7}]}"""));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Empty_fixes_returns_400()
    {
        var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(PositionsUrl, Json("""{"fixes":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_than_500_fixes_returns_413()
    {
        var client = factory.CreateClient(factory.Token1);
        var fixes = Enumerable.Range(0, 501).Select(i => Fix(factory, i, 1_000 + i)).ToArray();
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Sos_action_returns_204_and_runtime_shows_sos()
    {
        var client = factory.CreateClient(factory.Token2);
        var response = await client.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = factory.Event.BuildLiveView().Participants.Single(p => p.ParticipantId == ApiFactory.Participant2);
        Assert.Equal(ParticipantState.Sos, view.State);

        var cleared = await client.PostAsync(ActionsUrl, Json("""{"action":"sosCleared"}"""));
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        view = factory.Event.BuildLiveView().Participants.Single(p => p.ParticipantId == ApiFactory.Participant2);
        Assert.NotEqual(ParticipantState.Sos, view.State);
    }

    [Theory]
    [InlineData("""{"action":"explode"}""")]
    [InlineData("""{"action":3}""")]
    [InlineData("""{}""")]
    [InlineData("""{"action":""")]
    public async Task Unknown_or_malformed_action_returns_400(string json)
    {
        var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(ActionsUrl, Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Actions_require_a_token_of_the_route_event()
    {
        var anonymous = await factory.CreateClient().PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));
        var foreign = await factory.CreateClient(factory.Tokens.Issue("evt-other", "q2"))
            .PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_returns_401()
    {
        using var f = new ApiFactory();
        var client = f.CreateClient(f.Token1);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(ActionsUrl, Json("""{"action":"pause"}"""))).StatusCode);

        f.Tokens.Revoke(ApiFactory.EventId, ApiFactory.Participant1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(ActionsUrl, Json("""{"action":"resume"}"""))).StatusCode);
    }

    /// <summary>
    /// The limiter's window runs on the real clock (System.Threading.RateLimiting has no TimeProvider hook), so
    /// this sends 61 requests in quick succession on a dedicated host.
    /// </summary>
    [Fact]
    public async Task Sixty_first_request_within_a_minute_returns_429_and_other_tokens_are_unaffected()
    {
        using var f = new ApiFactory();
        var client1 = f.CreateClient(f.Token1);
        var body = new { fixes = new[] { Fix(f, 0, 0) } };

        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client1.PostAsJsonAsync(PositionsUrl, body)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client1.PostAsJsonAsync(PositionsUrl, body)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client1.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await f.CreateClient(f.Token2).PostAsJsonAsync(PositionsUrl, body)).StatusCode);
    }

    [Fact]
    public void Startup_fails_without_organizer_key()
    {
        using var f = new ApiFactory();
        f.Settings["Organizer:ApiKey"] = "";
        var ex = Assert.Throws<OptionsValidationException>(() => f.Services);
        Assert.Contains("Organizer:ApiKey", ex.Message);
    }
}
