using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Core.Simulation;
using PositionTracker.Data.Entities;
using PositionTracker.Email;
using static PositionTracker.Api.Tests.ParticipantLinkTests;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The whole registration path through the HTTP host against PostgreSQL: organizer sign-up and confirmation, sign-in,
/// event with a 3-lap course, two registered participants (SK and EN) who confirm through their emailed links, consent,
/// go-live, positions with the device tokens, the live and spectator views, a restart on the same database and the
/// re-issue of a device token. Emails are read from the outbox (no SMTP host is configured).
/// </summary>
public sealed class EndToEndRegistrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly Regex Link = new(@"https://tracker\.example\.org/(?:account/(?:confirm|signin)|p)\?token=([A-Za-z0-9%_\-]+)");

    [Fact]
    public async Task From_sign_up_to_live_views_a_restart_and_a_reissued_device_token()
    {
        var cs = postgres.NewDatabase();
        Guid ev;
        string tokenA, tokenB, nameB;
        string idA, idB;
        await using (var host = Host(cs))
        {
            var client = Client(host);
            var start = host.Clock.GetUtcNow().AddDays(1);

            // Organizer: sign-up, confirmation, sign-in.
            Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signup",
                new { name = "Anna Organizer", email = "anna@example.org", language = "en" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Post, "/api/account/confirm", new { token = await MailTokenAsync(host, EmailPurpose.OrganizerEmailConfirmation, "anna@example.org") })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signin", new { email = "anna@example.org" })).StatusCode);
            var redeemed = await Send(client, HttpMethod.Post, "/api/account/signin/redeem", new { token = await MailTokenAsync(host, EmailPurpose.SignIn, "anna@example.org") });
            Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
            var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
            var org = new Session(client, cookie);

            // Event with a 3-lap loop.
            using (var created = await JsonAsync(await org.SendAsync(HttpMethod.Post, "/api/events", new
            {
                name = "Spring time trial", location = "Žilina", startTime = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), sport = "cycling",
            })))
                ev = Guid.Parse(created.RootElement.GetProperty("eventId").GetString()!);
            Assert.Equal(HttpStatusCode.OK, (await org.SendAsync(HttpMethod.Put, $"/api/events/{ev}/course",
                new { gpxBase64 = Convert.ToBase64String(Gpx(DemoCourses.Loop())), laps = 3, spectatorDelaySeconds = 5 })).StatusCode);

            // Two participants (SK and EN).
            idA = await RegisterAsync(org, ev, "Jana Nováková", "jana@example.com", "sk");
            idB = await RegisterAsync(org, ev, "Tom Smith", "tom@example.com", "en");
            nameB = "Tom Smith";
            var linkA = await InviteTokenAsync(host, "jana@example.com");
            var linkB = await InviteTokenAsync(host, "tom@example.com");

            using (var view = await JsonAsync(await GetAsync(client, linkA))) Assert.Equal("sk", view.RootElement.GetProperty("language").GetString());
            using (var view = await JsonAsync(await GetAsync(client, linkB))) Assert.Equal("en", view.RootElement.GetProperty("language").GetString());
            await PostAsync(client, linkA, "/confirm", new { nickname = "Rocket" });
            await PostAsync(client, linkB, "/confirm", new { });
            tokenA = await ConsentAsync(client, linkA, true);   // consents to show the name
            tokenB = await ConsentAsync(client, linkB, false);  // does not

            // Ready, then live.
            Assert.Equal(HttpStatusCode.OK, (await org.SendAsync(HttpMethod.Post, $"/api/events/{ev}/state", new { state = "ready" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await org.SendAsync(HttpMethod.Post, $"/api/events/{ev}/state", new { state = "live" })).StatusCode);
            host.Clock.SetUtcNow(start.AddMinutes(1));
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(host, ev, tokenA)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(host, ev, tokenB)).StatusCode);
            await WaitForFixesAsync(host, ev, 2);

            var (live, publicView) = await ViewsAsync(host, ev, org);
            AssertViews(live, publicView, idA, idB, "Rocket");

            // Restart on the same database: identical views.
            await host.DisposeAsync();
        }

        await using (var restarted = Host(cs))
        {
            _ = restarted.Services;
            restarted.Clock.Advance(TimeSpan.FromMinutes(5)); // new mails are newer than the first host's
            var org = await SignedInAsync(restarted);
            var (live, publicView) = await ViewsAsync(restarted, ev, org);
            AssertViews(live, publicView, idA, idB, "Rocket");

            // The organizer re-issues Tom's device token; the old one stops working at once. Tom gets a new link and consents again.
            Assert.Equal(HttpStatusCode.OK, (await org.SendAsync(HttpMethod.Post, $"/api/events/{ev}/participants/{idB}/reissue-token", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await IngestAsync(restarted, ev, tokenB)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(restarted, ev, tokenA)).StatusCode);
            var client = Client(restarted);
            var newToken = await ConsentAsync(client, await InviteTokenAsync(restarted, "tom@example.com"), false);
            Assert.NotEqual(tokenB, newToken);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(restarted, ev, newToken)).StatusCode);
        }
        _ = nameB;
    }

    private static void AssertViews(JsonElement live, JsonElement publicView, string idA, string idB, string nicknameA)
    {
        var liveIds = live.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("participantId").GetString()).ToList();
        Assert.Contains(idA, liveIds);
        Assert.Contains(idB, liveIds);
        var spectators = publicView.GetProperty("participants").EnumerateArray().ToDictionary(
            p => p.GetProperty("participantId").GetString()!, p => p.GetProperty("displayName").GetString());
        Assert.Equal(nicknameA, spectators[idA]);   // consented to show the name: the nickname
        Assert.Equal("#2", spectators[idB]);        // did not: the bib
    }

    private async Task<(JsonElement Live, JsonElement Public)> ViewsAsync(ApiFactory host, Guid ev, Session org)
    {
        var broadcaster = host.Services.GetRequiredService<PositionTracker.Api.Live.LiveBroadcaster>();
        await broadcaster.BroadcastAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await broadcaster.BroadcastAsync();
        var live = await org.GetAsync($"/api/events/{ev}/live");
        using var anonymous = host.CreateClient();
        var spectators = await anonymous.GetAsync($"/api/events/{ev}/public");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, spectators.StatusCode);
        return (JsonDocument.Parse(await live.Content.ReadAsStringAsync()).RootElement.Clone(),
            JsonDocument.Parse(await spectators.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static async Task<string> RegisterAsync(Session org, Guid ev, string name, string email, string language)
    {
        using var json = await JsonAsync(await org.SendAsync(HttpMethod.Post, $"/api/events/{ev}/participants", new { name, email, gender = "male", language }));
        return json.RootElement.GetProperty("participantId").GetString()!;
    }

    private static async Task<string> MailTokenAsync(ApiFactory host, string purpose, string to)
    {
        await using var db = Db(host);
        var mail = await db.Outbox.Where(m => m.Purpose == purpose && m.ToAddress == to).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).LastAsync();
        return Uri.UnescapeDataString(Link.Match(mail.TextBody).Groups[1].Value);
    }

    private static Task<string> InviteTokenAsync(ApiFactory host, string to) => MailTokenAsync(host, EmailPurpose.Invitation, to);

    /// <summary>After a restart: a fresh sign-in of the same organizer.</summary>
    private static async Task<Session> SignedInAsync(ApiFactory host)
    {
        var client = Client(host);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signin", new { email = "anna@example.org" })).StatusCode);
        var redeemed = await Send(client, HttpMethod.Post, "/api/account/signin/redeem", new { token = await MailTokenAsync(host, EmailPurpose.SignIn, "anna@example.org") });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return new Session(client, redeemed.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Split(';')[0]);
    }

    private static async Task<HttpResponseMessage> IngestAsync(ApiFactory host, Guid ev, string token)
    {
        using var client = host.CreateClient(token);
        var point = DemoCourses.Loop()[0];
        var t = host.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var body = string.Create(CultureInfo.InvariantCulture, $$"""{"fixes":[{"t":"{{t}}","lat":{{point.Lat:R}},"lon":{{point.Lon:R}},"acc":5}]}""");
        return await client.PostAsync($"/api/events/{ev}/positions", Json(body));
    }

    private static async Task WaitForFixesAsync(ApiFactory host, Guid ev, int count)
    {
        for (var i = 0; i < 200; i++)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(600));
            await using var db = Db(host);
            if (await db.Fixes.CountAsync(f => f.EventId == ev) >= count) return;
            await Task.Delay(25);
        }
        Assert.Fail("The fixes were not written.");
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string link) => client.GetAsync($"/api/p/{link}");

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = Json(JsonSerializer.Serialize(body)) };
        request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        return client.SendAsync(request);
    }

    private sealed class Session(HttpClient client, string cookie)
    {
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body)
        {
            var request = new HttpRequestMessage(method, path) { Content = Json(JsonSerializer.Serialize(body)) };
            request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }

        public Task<HttpResponseMessage> GetAsync(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }
    }

    private ApiFactory Host(string connectionString)
    {
        var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["ConnectionStrings:Default"] = connectionString;
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }
}
