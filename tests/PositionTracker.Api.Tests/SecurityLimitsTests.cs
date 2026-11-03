using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Live;
using PositionTracker.Data.Entities;
using static PositionTracker.Api.Tests.ParticipantLinkTests;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The limits that keep anonymous floods away from the database and the hub: unknown device tokens and unknown participant
/// links per client address, wrong operator keys per address, and hub connections per address and in total. Against
/// PostgreSQL (Testcontainers) where the database is the point.
/// </summary>
public sealed class SecurityLimitsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string IpHeader = "X-Test-Remote-Ip";

    [Fact]
    public async Task Unknown_device_tokens_of_one_address_stop_reaching_the_database_but_cached_tokens_still_work()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Live);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var web = Client(host);
        await PostAsync(web, link, "/confirm", new { });
        var token = await ConsentAsync(web, link, showName: false);
        var path = $"/api/events/{ev:D}/positions";

        // the valid token is used once (so it is cached; entries read right after the issue are not trusted) before the flood
        host.Clock.Advance(DeviceTokenCache.Grace + TimeSpan.FromSeconds(1));
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PositionsAsync(host, path, token, "198.51.100.1")).StatusCode);

        for (var i = 0; i < DeviceLookupLimiter.MissesPerMinute; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PositionsAsync(host, path, "random-" + i, "198.51.100.1")).StatusCode);
        var refused = await PositionsAsync(host, path, "random-x", "198.51.100.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);

        var cached = await PositionsAsync(host, path, token, "198.51.100.1");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, cached.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, cached.StatusCode);
        // another address has its own budget, and the window ends
        Assert.Equal(HttpStatusCode.Unauthorized, (await PositionsAsync(host, path, "random-y", "198.51.100.2")).StatusCode);
        host.Clock.Advance(DeviceLookupLimiter.Window);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PositionsAsync(host, path, "random-z", "198.51.100.1")).StatusCode);
    }

    [Fact]
    public async Task OwnTracks_with_unknown_tokens_is_limited_per_address_too()
    {
        await using var host = Host();
        for (var i = 0; i < DeviceLookupLimiter.MissesPerMinute; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await OwnTracksAsync(host, "random-" + i, "198.51.100.5")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await OwnTracksAsync(host, "random-x", "198.51.100.5")).StatusCode);
    }

    [Fact]
    public async Task Unknown_participant_links_of_one_address_are_limited()
    {
        await using var host = Host();
        for (var i = 0; i < Participants.ParticipantLinkLimiter.UnknownLinksPerIpPerMinute; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await GetLinkAsync(host, "no-such-link-" + i, "198.51.100.7")).StatusCode);
        await AssertErrorAsync(await GetLinkAsync(host, "no-such-link-x", "198.51.100.7"), HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal(HttpStatusCode.NotFound, (await GetLinkAsync(host, "no-such-link-y", "198.51.100.8")).StatusCode);
    }

    [Fact]
    public void Wrong_operator_keys_are_limited_per_address()
    {
        var clock = new FakeTimeProvider(ApiFactory.T0);
        var guard = new ApiKeyGuard(Options.Create(new OrganizerOptions { ApiKey = "the-right-key-0123456789abcdefghij" }), clock);
        var attacker = IPAddress.Parse("198.51.100.20");
        for (var i = 0; i < ApiKeyGuard.FailuresPerHour; i++) Assert.False(guard.IsValidKey("guess-" + i, attacker));

        Assert.False(guard.IsValidKey("the-right-key-0123456789abcdefghij", attacker)); // refused without comparing
        Assert.True(guard.IsValidKey("the-right-key-0123456789abcdefghij", IPAddress.Parse("198.51.100.21")));
        Assert.False(guard.IsValidKey("", attacker)); // an empty key never matches and is not a guess
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(guard.IsValidKey("the-right-key-0123456789abcdefghij", attacker));
    }

    [Fact]
    public void Hub_connections_are_capped_per_address_and_given_back()
    {
        var limiter = new HubConnectionLimiter();
        var ip = IPAddress.Parse("198.51.100.30");
        for (var i = 0; i < HubConnectionLimiter.PerAddress; i++) Assert.True(limiter.TryOpen(ip));
        Assert.False(limiter.TryOpen(ip));
        Assert.True(limiter.TryOpen(IPAddress.Parse("198.51.100.31")));

        limiter.Close(ip);
        Assert.True(limiter.TryOpen(ip));
    }

    [Fact]
    public void A_short_operator_key_is_refused_at_startup_outside_Development()
    {
        using var factory = new ApiFactory { SeedTestEvent = false, EnvironmentName = "Production" };
        factory.Settings["Email:Smtp:Host"] = "smtp.invalid";
        factory.Settings["Email:From"] = "noreply@example.org";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Organizer:ApiKey"] = "short";
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains($"at least {ApiKeyGuard.MinKeyLength} characters", ex.ToString());
    }

    // ----- helpers -----

    private static async Task<HttpResponseMessage> PositionsAsync(ApiFactory host, string path, string token, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = Json($$"""{"fixes":[{"t":"{{host.Clock.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}","lat":48.1,"lon":17.1,"acc":5}]}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IpHeader, ip);
        return await Client(host).SendAsync(request);
    }

    private static async Task<HttpResponseMessage> OwnTracksAsync(ApiFactory host, string token, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/owntracks") { Content = Json("""{"_type":"location"}""") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("1:" + token)));
        request.Headers.Add(IpHeader, ip);
        return await Client(host).SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetLinkAsync(ApiFactory host, string link, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/p/" + link);
        request.Headers.Add(IpHeader, ip);
        return await Client(host).SendAsync(request);
    }

    private ApiFactory Host()
    {
        var factory = new ApiFactory
        {
            SeedTestEvent = false,
            ConfigureServices = s => s.AddSingleton<IStartupFilter, TestRemoteIp>(),
        };
        factory.Settings["ConnectionStrings:Default"] = postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }

    private sealed class TestRemoteIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[IpHeader].ToString(), out var ip))
                    context.Connection.RemoteIpAddress = ip;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
