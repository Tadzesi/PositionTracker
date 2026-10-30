using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Endpoints;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Shares one host; every test that changes or reads participant state uses its own participant
/// (<see cref="ApiFactory.AddParticipant"/>) or its own host, so the tests do not depend on order.
/// </summary>
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

    private static object[] Fixes(ApiFactory f, int count) =>
        Enumerable.Range(0, count).Select(i => Fix(f, i, i)).ToArray();

    private static string Utc(int seconds) =>
        ApiFactory.T0.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(int Accepted, int Rejected)> ReadResult(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("accepted").GetInt32(), doc.RootElement.GetProperty("rejected").GetInt32());
    }

    private ParticipantView View(string participantId) =>
        factory.Event.BuildLiveView().Participants.Single(p => p.ParticipantId == participantId);

    [Fact]
    public async Task Missing_authorization_header_returns_401_with_bearer_challenge()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Unknown_token_returns_401_with_bearer_challenge()
    {
        using var client = factory.CreateClient("not-a-real-token");
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Token_of_another_event_returns_403()
    {
        using var client = factory.CreateClient(factory.Tokens.Issue("evt-other", "q1"));
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_event_returns_404()
    {
        using var client = factory.CreateClient(factory.Tokens.Issue("evt-missing", "q1"));
        var response = await client.PostAsJsonAsync("/api/events/evt-missing/positions",
            new { fixes = new[] { Fix(factory, 0, 0) } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Token_of_a_participant_missing_from_the_runtime_returns_404()
    {
        using var client = factory.CreateClient(factory.Tokens.Issue(ApiFactory.EventId, "ghost"));

        var positions = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0) } });
        var action = await client.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.NotFound, positions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, action.StatusCode);
    }

    [Fact]
    public async Task An_internal_exception_is_a_500_not_a_404()
    {
        // The runtime's clock throws KeyNotFoundException, standing in for a Core bug.
        var settings = factory.Event.Settings with { EventId = "evt-broken" };
        var runtime = factory.Registry.Add(new EventRuntime(settings, factory.Event.Course, new ThrowingClock()));
        runtime.AddParticipant(new ParticipantInfo("b1", "1", "Broken", null, "Open", true, false));
        using var client = factory.CreateClient(factory.Tokens.Issue("evt-broken", "b1"));

        var positions = await client.PostAsJsonAsync("/api/events/evt-broken/positions", new
        {
            fixes = new[] { Fix(factory, 0, 0) },
            status = new { battery = 10, queue = 0, foreground = true }, // under 20 % reads the clock
        });
        var action = await client.PostAsync("/api/events/evt-broken/actions", Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, positions.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, action.StatusCode);
    }

    [Fact]
    public async Task Valid_batch_returns_202_with_accepted_count_and_stores_status()
    {
        var (id, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var body = new
        {
            fixes = new[] { Fix(factory, 0, 0), Fix(factory, 50, 5), Fix(factory, 100, 10) },
            status = new { battery = 71, charging = false, queue = 2, foreground = true },
        };

        var response = await client.PostAsJsonAsync(PositionsUrl, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((3, 0), await ReadResult(response));
        Assert.Equal(71, View(id).BatteryPct);
        Assert.Equal(2, View(id).QueueLength);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(100.5)]
    [InlineData(250.0)]
    public async Task Battery_outside_0_to_100_is_stored_as_unknown_not_as_an_error(double battery)
    {
        var (id, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var body = new { fixes = new[] { Fix(factory, 0, 0) }, status = new { battery, queue = 0, foreground = true } };

        var response = await client.PostAsJsonAsync(PositionsUrl, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(View(id).BatteryPct);
        Assert.DoesNotContain(View(id).Alarms, a => a.Kind == AlarmKind.LowBattery);
    }

    [Fact]
    public async Task Inaccurate_fix_is_counted_as_rejected()
    {
        var (_, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var response = await client.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(factory, 0, 0, acc: 80) } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((0, 1), await ReadResult(response));
    }

    [Fact]
    public async Task Out_of_range_latitude_is_counted_as_rejected()
    {
        var (_, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var response = await client.PostAsync(PositionsUrl,
            Json($$"""{"fixes":[{"t":"{{Utc(0)}}","lat":95.0,"lon":18.7,"acc":5}]}"""));

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
        using var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(PositionsUrl, Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Utc_timestamp_with_fraction_is_accepted()
    {
        var (_, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var response = await client.PostAsync(PositionsUrl,
            Json("""{"fixes":[{"t":"2026-11-07T10:00:30.250Z","lat":49.0,"lon":18.7}]}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal((1, 0), await ReadResult(response));
    }

    [Fact]
    public async Task Empty_fixes_returns_400()
    {
        using var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(PositionsUrl, Json("""{"fixes":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Full_batch_of_500_fixes_fits_the_body_cap_and_is_accepted()
    {
        var (_, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixes = Fixes(factory, IngestEndpoints.MaxFixesPerBatch),
            status = new { battery = 100, charging = true, queue = 10_000, foreground = false },
        });

        Assert.True(bytes.Length < IngestEndpoints.MaxBodyBytes, $"{bytes.Length} bytes");
        var response = await client.PostAsync(PositionsUrl, Bytes(bytes));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task More_than_500_fixes_within_the_body_cap_returns_413()
    {
        using var client = factory.CreateClient(factory.Token1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { fixes = Fixes(factory, IngestEndpoints.MaxFixesPerBatch + 1) });

        Assert.True(bytes.Length < IngestEndpoints.MaxBodyBytes, $"{bytes.Length} bytes");
        var response = await client.PostAsync(PositionsUrl, Bytes(bytes));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Positions_body_over_the_cap_returns_413_and_leaves_the_runtime_untouched(bool withContentLength)
    {
        var (id, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        // A legal fix count padded past the cap, so only the body size can cause the 413.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { fixes = Fixes(factory, 3), pad = new string('x', 300 * 1024) });
        Assert.True(bytes.Length > IngestEndpoints.MaxBodyBytes, $"{bytes.Length} bytes");

        var response = await client.PostAsync(PositionsUrl, withContentLength ? Bytes(bytes) : new ChunkedContent(bytes));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(View(id).LastSignalAgo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Action_body_over_the_cap_returns_413_and_leaves_the_runtime_untouched(bool withContentLength)
    {
        var (id, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var bytes = Encoding.UTF8.GetBytes($$"""{"action":"sos","pad":"{{new string('x', 300 * 1024)}}"}""");

        var response = await client.PostAsync(ActionsUrl, withContentLength ? Bytes(bytes) : new ChunkedContent(bytes));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.NotEqual(ParticipantState.Sos, View(id).State);
    }

    [Fact]
    public async Task Sos_action_returns_204_and_runtime_shows_sos()
    {
        var (id, token) = factory.AddParticipant();
        using var client = factory.CreateClient(token);
        var response = await client.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ParticipantState.Sos, View(id).State);

        var cleared = await client.PostAsync(ActionsUrl, Json("""{"action":"sosCleared"}"""));
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.NotEqual(ParticipantState.Sos, View(id).State);
    }

    [Theory]
    [InlineData("""{"action":"explode"}""")]
    [InlineData("""{"action":3}""")]
    [InlineData("""{}""")]
    [InlineData("""{"action":""")]
    public async Task Unknown_or_malformed_action_returns_400(string json)
    {
        using var client = factory.CreateClient(factory.Token1);
        var response = await client.PostAsync(ActionsUrl, Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Actions_require_a_token_of_the_route_event()
    {
        using var anonymousClient = factory.CreateClient();
        using var foreignClient = factory.CreateClient(factory.Tokens.Issue("evt-other", "q2"));

        var anonymous = await anonymousClient.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));
        var foreign = await foreignClient.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bearer", Assert.Single(anonymous.Headers.WwwAuthenticate).Scheme);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_returns_401()
    {
        using var f = new ApiFactory();
        using var client = f.CreateClient(f.Token1);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(ActionsUrl, Json("""{"action":"pause"}"""))).StatusCode);

        f.Tokens.Revoke(ApiFactory.EventId, ApiFactory.Participant1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(ActionsUrl, Json("""{"action":"resume"}"""))).StatusCode);
    }

    /// <summary>
    /// The limiter's window runs on the real clock (System.Threading.RateLimiting has no TimeProvider hook), so
    /// this sends 61 requests in quick succession on a dedicated host.
    /// </summary>
    [Fact]
    public async Task Sixty_first_request_within_a_minute_returns_429_with_retry_after_and_other_tokens_are_unaffected()
    {
        using var f = new ApiFactory();
        using var client1 = f.CreateClient(f.Token1);
        using var client2 = f.CreateClient(f.Token2);
        var body = new { fixes = new[] { Fix(f, 0, 0) } };

        for (var i = 0; i < IngestEndpoints.RequestsPerMinute; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client1.PostAsJsonAsync(PositionsUrl, body)).StatusCode);

        var limited = await client1.PostAsJsonAsync(PositionsUrl, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var retryAfter = limited.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value.TotalSeconds, 1, 60);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client1.PostAsync(ActionsUrl, Json("""{"action":"sos"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client2.PostAsJsonAsync(PositionsUrl, body)).StatusCode);
    }

    [Fact]
    public async Task Fixes_over_600_per_minute_and_token_are_counted_as_rejected_with_202()
    {
        using var f = new ApiFactory();
        f.Clock.SetUtcNow(ApiFactory.T0.AddMinutes(20)); // every fix below lies in the past
        using var client1 = f.CreateClient(f.Token1);
        using var client2 = f.CreateClient(f.Token2);
        object Batch(int from) => new { fixes = Enumerable.Range(from, 500).Select(i => Fix(f, i * 5.0, i)).ToArray() };

        var first = await client1.PostAsJsonAsync(PositionsUrl, Batch(0));
        var second = await client1.PostAsJsonAsync(PositionsUrl, Batch(500));
        var other = await client2.PostAsJsonAsync(PositionsUrl, Batch(0));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal((500, 0), await ReadResult(first));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal((100, 400), await ReadResult(second));
        Assert.Equal((500, 0), await ReadResult(other));

        f.Clock.Advance(TimeSpan.FromMinutes(1)); // a new window
        var third = await client1.PostAsJsonAsync(PositionsUrl, new { fixes = new[] { Fix(f, 5_000, 1_000) } });
        Assert.Equal((1, 0), await ReadResult(third));
    }

    [Fact]
    public void Startup_fails_without_organizer_key()
    {
        using var f = new ApiFactory();
        f.Settings["Organizer:ApiKey"] = "";
        var ex = Assert.Throws<OptionsValidationException>(() => f.Services);
        Assert.Contains("Organizer:ApiKey", ex.Message);
    }

    private static ByteArrayContent Bytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    /// <summary>A body sent without <c>Content-Length</c> (chunked).</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ChunkedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < _bytes.Length; offset += 8 * 1024)
                await stream.WriteAsync(_bytes.AsMemory(offset, Math.Min(8 * 1024, _bytes.Length - offset)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new KeyNotFoundException("simulated internal failure");
    }
}
