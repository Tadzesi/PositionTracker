using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Api.OwnTracks;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Simulation;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The OwnTracks adapter: the message mapper, the HTTP Basic ingest (<c>/api/ingest/owntracks</c>), the configuration
/// builder and the config link of the participant page. Against PostgreSQL (Testcontainers).
/// </summary>
public sealed class OwnTracksTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ----- mapper -----

    [Fact]
    public void A_location_maps_to_one_fix_and_a_device_status()
    {
        var batch = OwnTracksMapper.ToBatch(Parse("""
            {"_type":"location","lat":49.0,"lon":18.7,"tst":1790000000,"acc":6,"vel":28,"alt":410,"batt":71,"bs":2,"tid":"21","conn":"m"}
            """))!;

        var fix = Assert.Single(batch.Fixes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), fix.Time);
        Assert.Equal((49.0, 18.7, 6.0), (fix.Lat, fix.Lon, fix.AccuracyM!.Value));
        Assert.Equal(new DeviceStatus(71, true, 0, true), batch.Status);
    }

    [Theory]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"bs":1}""", false)]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"bs":3}""", true)]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"bs":0}""", null)]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000}""", null)]
    public void The_battery_state_maps_to_charging(string json, bool? charging) =>
        Assert.Equal(charging, OwnTracksMapper.ToBatch(Parse(json))!.Status!.Charging);

    [Theory]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"batt":150}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"batt":-1}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"batt":"x"}""")]
    public void A_battery_outside_0_to_100_is_unknown(string json) =>
        Assert.Null(OwnTracksMapper.ToBatch(Parse(json))!.Status!.BatteryPct);

    [Fact]
    public void A_missing_accuracy_is_unknown_not_invalid() =>
        Assert.Null(Assert.Single(OwnTracksMapper.ToBatch(Parse("""{"_type":"location","lat":49,"lon":18,"tst":1790000000}"""))!.Fixes).AccuracyM);

    [Theory]
    [InlineData("""{"_type":"transition","tst":1790000000,"event":"enter","acc":5}""")]
    [InlineData("""{"_type":"lwt","tst":1790000000}""")]
    [InlineData("""{"_type":"waypoints","waypoints":[]}""")]
    [InlineData("""{"_type":"status","wifi":1}""")]
    [InlineData("""{"lat":49,"lon":18,"tst":1790000000}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000000}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":4102444800}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":18,"tst":1790000000.5}""")]
    [InlineData("""{"_type":"location","lat":91,"lon":18,"tst":1790000000}""")]
    [InlineData("""{"_type":"location","lat":49,"lon":181,"tst":1790000000}""")]
    [InlineData("""{"_type":"location","lat":"49","lon":18,"tst":1790000000}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""null""")]
    public void Anything_but_a_valid_location_maps_to_null(string json) => Assert.Null(OwnTracksMapper.ToBatch(Parse(json)));

    // ----- configuration -----

    [Fact]
    public void The_config_is_valid_http_mode_json_that_round_trips_and_holds_no_secret_but_the_token()
    {
        var config = OwnTracksConfigBuilder.Build("https://tracker.example.org/", "42", "TOKEN-VALUE", "42", EventTuning.For(Sport.Cycling));

        const string prefix = "owntracks:///config?inline=";
        Assert.StartsWith(prefix, config.DeepLink);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(config.DeepLink[prefix.Length..])));
        var json = Parse(decoded);
        Assert.Equal(config.OtrcJson.ToJsonString(), decoded);
        Assert.Equal("configuration", json.GetProperty("_type").GetString());
        Assert.Equal(3, json.GetProperty("mode").GetInt32());
        Assert.Equal("https://tracker.example.org/api/ingest/owntracks", json.GetProperty("url").GetString());
        Assert.True(json.GetProperty("auth").GetBoolean());
        Assert.Equal("42", json.GetProperty("username").GetString());
        Assert.Equal("TOKEN-VALUE", json.GetProperty("password").GetString());
        Assert.Equal(2, json.GetProperty("monitoring").GetInt32());
        Assert.Equal(5, json.GetProperty("locatorInterval").GetInt32());
        Assert.Equal(10, json.GetProperty("locatorDisplacement").GetInt32());
        Assert.Equal(50, json.GetProperty("ignoreInaccurateLocations").GetInt32());
        Assert.True(json.GetProperty("cmd").GetBoolean());
        Assert.True(json.GetProperty("remoteConfiguration").GetBoolean());
        Assert.Equal(config.DeepLink, config.QrPayload);
        Assert.Equal(["_type", "mode", "url", "auth", "username", "password", "tid", "monitoring", "locatorInterval",
            "locatorDisplacement", "ignoreInaccurateLocations", "cmd", "remoteConfiguration"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(10, EventTuning.For(Sport.Running).LocatorIntervalSeconds);
    }

    [Fact]
    public void The_stop_link_and_command_set_quiet_monitoring_only()
    {
        const string prefix = "owntracks:///config?inline=";
        Assert.Equal("owntracks:///config?inline=eyJfdHlwZSI6ImNvbmZpZ3VyYXRpb24iLCJtb25pdG9yaW5nIjotMX0%3D", OwnTracksConfigBuilder.StopDeepLink);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(OwnTracksConfigBuilder.StopDeepLink[prefix.Length..])));
        Assert.Equal(OwnTracksConfigBuilder.StopConfiguration().ToJsonString(), decoded);
        Assert.Equal("""[{"_type":"cmd","action":"setConfiguration","configuration":{"_type":"configuration","monitoring":-1}}]""",
            OwnTracksConfigBuilder.StopCommand().ToJsonString());
    }

    [Theory]
    [InlineData("7", "07")]
    [InlineData("a-12", "A1")]
    [InlineData("123", "12")]
    public void The_tracker_id_has_two_characters(string bib, string tid) => Assert.Equal(tid, OwnTracksConfigBuilder.TrackerId(bib));

    // ----- ingest -----

    [Fact]
    public async Task A_location_reaches_the_runtime_and_is_persisted_and_other_types_are_ignored_with_200()
    {
        await using var host = Host();
        var (ev, pid, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        foreach (var other in new[] { """{"_type":"transition","tst":1790000000,"event":"enter","acc":5}""", """{"_type":"lwt","tst":1790000000}""",
                     """{"_type":"waypoints","waypoints":[]}""", """{"_type":"status"}""", "not json", "" })
        {
            var ignored = await PostAsync(client, "1", token, other);
            Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
            Assert.Equal("[]", await ignored.Content.ReadAsStringAsync());
        }

        for (var i = 0; i < 200; i++)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(600));
            await using var db = ParticipantLinkTests.Db(host);
            if (await db.Fixes.CountAsync(f => f.EventId == ev) == 1) break;
            await Task.Delay(25);
        }
        await using (var db = ParticipantLinkTests.Db(host)) Assert.Equal(1, await db.Fixes.CountAsync(f => f.EventId == ev));
        using var organizer = host.CreateClient();
        organizer.DefaultRequestHeaders.Add("X-Api-Key", host.OrganizerKey);
        var live = await organizer.GetStringAsync($"/api/events/{ev}/live");
        Assert.Contains(pid.ToString("D"), live);
    }

    [Fact]
    public async Task Bad_credentials_are_one_and_the_same_401()
    {
        await using var host = Host();
        var (_, _, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();
        var body = Location(host);

        var cases = new List<HttpResponseMessage>
        {
            await PostAsync(client, null, null, body),
            await PostAsync(client, "1", "not-a-token", body),
            await PostAsync(client, "2", token, body),          // wrong user name
            await PostAsync(client, "", token, body),
            await PostRawAsync(client, "Basic !!!not-base64", body),
            await PostRawAsync(client, "Bearer " + token, body),
            await PostRawAsync(client, "Basic " + Convert.ToBase64String("no-colon"u8.ToArray()), body),
        };
        var texts = new List<string>();
        foreach (var r in cases)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Contains("Basic", r.Headers.WwwAuthenticate.ToString());
            texts.Add(await r.Content.ReadAsStringAsync());
        }
        Assert.Single(texts.Distinct());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "1", token, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "1", token, body)).StatusCode); // user name is case-insensitive too
    }

    [Fact]
    public async Task A_revoked_token_is_401_also_after_a_restart()
    {
        var cs = postgres.NewDatabase();
        Guid ev;
        string oldToken, newToken;
        await using (var host = Host(cs))
        {
            (ev, var pid, oldToken) = await LiveRiderAsync(host);
            var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
            newToken = await ParticipantLinkTests.ConsentAsync(ParticipantLinkTests.Client(host), link, false);
            using var client = host.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "1", oldToken, Location(host))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "1", newToken, Location(host))).StatusCode);
        }
        await using var restarted = Host(cs);
        _ = restarted.Services;
        using var again = restarted.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(again, "1", oldToken, Location(restarted))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(again, "1", newToken, Location(restarted))).StatusCode);
        _ = ev;
    }

    [Fact]
    public async Task A_body_over_16_KB_is_413()
    {
        await using var host = Host();
        var (_, _, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();

        var big = $$"""{"_type":"location","lat":49,"lon":18,"tst":1790000000,"pad":"{{new string('x', 17 * 1024)}}"}""";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(client, "1", token, big)).StatusCode);
    }

    [Fact]
    public async Task A_backlog_over_the_fix_budget_is_429_with_retry_after_and_the_next_window_accepts_again()
    {
        await using var host = Host();
        var (_, _, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();

        for (var i = 0; i < FixRateLimiter.FixesPerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "1", token, Location(host))).StatusCode);
        var limited = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "1", token, Location(host))).StatusCode);
    }

    private const string StopAnswer =
        """[{"_type":"cmd","action":"setConfiguration","configuration":{"_type":"configuration","monitoring":-1}}]""";

    [Fact]
    public async Task A_live_event_still_answers_an_empty_array()
    {
        await using var host = Host();
        var (_, _, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_finished_event_answers_every_message_with_the_stop_command()
    {
        await using var host = Host();
        var (ev, _, token) = await LiveRiderAsync(host);
        await FinishAsync(host, ev);
        using var client = host.CreateClient();

        foreach (var body in new[] { Location(host), """{"_type":"lwt","tst":1790000000}""" })
        {
            var response = await PostAsync(client, "1", token, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(StopAnswer, await response.Content.ReadAsStringAsync());
        }
        await using var db = ParticipantLinkTests.Db(host);
        Assert.Empty(await db.Fixes.ToListAsync());
    }

    [Fact]
    public async Task A_finished_event_without_a_runtime_still_answers_the_stop_command()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
        var web = ParticipantLinkTests.Client(host);
        await ParticipantLinkTests.PostAsync(web, link, "/confirm", new { });
        var token = await ParticipantLinkTests.ConsentAsync(web, link, false);
        await using (var db = ParticipantLinkTests.Db(host))
            await db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EventState.Finished));
        Assert.Null(host.Registry.Get(ev.ToString("D")));
        using var client = host.CreateClient();

        var response = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StopAnswer, await response.Content.ReadAsStringAsync());
    }

    // ----- "tracking still on" notice -----

    [Fact]
    public async Task A_phone_that_stops_within_the_grace_period_gets_no_email()
    {
        await using var host = Host();
        var (ev, pid, token) = await LiveRiderAsync(host);
        await FinishAsync(host, ev);
        using var client = host.CreateClient();
        var t = host.Clock.GetUtcNow();

        await PostAsync(client, "1", token, Location(host));
        host.Clock.SetUtcNow(t.AddMinutes(9));
        await PostAsync(client, "1", token, Location(host));

        await using var db = ParticipantLinkTests.Db(host);
        var p = await db.Participants.SingleAsync(x => x.Id == pid);
        Assert.Equal(t, p.TrackingAfterFinishAt);
        Assert.Null(p.StopNoticeQueuedAt);
        Assert.Empty(await db.Outbox.Where(o => o.Purpose == EmailPurpose.TrackingStop).ToListAsync());
    }

    [Fact]
    public async Task A_phone_still_sending_after_the_grace_period_gets_one_email()
    {
        await using var host = Host();
        var (ev, pid, token) = await LiveRiderAsync(host);
        await FinishAsync(host, ev);
        using var client = host.CreateClient();
        var t = host.Clock.GetUtcNow();

        foreach (var minutes in new[] { 0, 10, 11, 60 })
        {
            host.Clock.SetUtcNow(t.AddMinutes(minutes));
            var response = await PostAsync(client, "1", token, Location(host));
            Assert.Equal(StopAnswer, await response.Content.ReadAsStringAsync());
        }

        await using var db = ParticipantLinkTests.Db(host);
        var mail = Assert.Single(await db.Outbox.Where(o => o.Purpose == EmailPurpose.TrackingStop).ToListAsync());
        Assert.Equal("rider1@example.com", mail.ToAddress);
        Assert.Contains("https://tracker.example.org/stop-tracking?lang=sk", mail.TextBody);
        Assert.Equal(t.AddMinutes(10), (await db.Participants.SingleAsync(x => x.Id == pid)).StopNoticeQueuedAt);
    }

    [Fact]
    public async Task A_backlog_burst_writes_seen_once_and_sends_no_email()
    {
        await using var host = Host();
        var (ev, pid, token) = await LiveRiderAsync(host);
        await FinishAsync(host, ev);
        using var client = host.CreateClient();
        var t = host.Clock.GetUtcNow();

        for (var i = 0; i < 20; i++)
        {
            host.Clock.SetUtcNow(t.AddSeconds(i));
            await PostAsync(client, "1", token, Location(host));
        }

        await using var db = ParticipantLinkTests.Db(host);
        Assert.Equal(t, (await db.Participants.SingleAsync(x => x.Id == pid)).TrackingAfterFinishAt);
        Assert.Empty(await db.Outbox.Where(o => o.Purpose == EmailPurpose.TrackingStop).ToListAsync());
    }

    [Fact]
    public async Task The_stop_command_is_returned_even_when_the_notice_fails()
    {
        await using var host = Host(services: s => s.AddScoped<ITrackingStopNotifier, FailingNotifier>());
        var (ev, _, token) = await LiveRiderAsync(host);
        await FinishAsync(host, ev);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StopAnswer, await response.Content.ReadAsStringAsync());
    }

    private sealed class FailingNotifier : ITrackingStopNotifier
    {
        public Task NoteAsync(string eventId, string participantId, CancellationToken ct) =>
            throw new Npgsql.NpgsqlException("connection refused");
    }

    /// <summary>Marks a live event finished the way <c>EventService</c> does: the database row and the registry flag.</summary>
    private static async Task FinishAsync(ApiFactory host, Guid ev)
    {
        await using (var db = ParticipantLinkTests.Db(host))
            await db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.State, EventState.Finished).SetProperty(e => e.EndedAt, host.Clock.GetUtcNow()));
        host.Registry.SetFinished(ev.ToString("D"), true);
    }

    [Fact]
    public async Task A_not_live_event_answers_200_with_an_empty_array()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
        var web = ParticipantLinkTests.Client(host);
        await ParticipantLinkTests.PostAsync(web, link, "/confirm", new { });
        var token = await ParticipantLinkTests.ConsentAsync(web, link, false);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "1", token, Location(host));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        await using var db = ParticipantLinkTests.Db(host);
        Assert.Empty(await db.Fixes.ToListAsync());
    }

    // ----- config link of the participant page -----

    [Fact]
    public async Task The_config_link_needs_the_current_device_token_and_hands_out_a_working_config()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
        var web = ParticipantLinkTests.Client(host);
        await ParticipantLinkTests.PostAsync(web, link, "/confirm", new { });

        await ParticipantLinkTests.AssertErrorAsync(await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { }),
            HttpStatusCode.Conflict, "consent_required", "deviceToken");
        await ParticipantLinkTests.AssertErrorAsync(await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { deviceToken = "nope" }),
            HttpStatusCode.Conflict, "consent_required", "deviceToken");
        var token = await ParticipantLinkTests.ConsentAsync(web, link, false);

        var response = await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { deviceToken = token });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        var otrc = json.RootElement.GetProperty("otrcJson");
        Assert.Equal(token, otrc.GetProperty("password").GetString());
        Assert.Equal("1", otrc.GetProperty("username").GetString());
        Assert.Equal("https://tracker.example.org/api/ingest/owntracks", otrc.GetProperty("url").GetString());
        Assert.StartsWith("owntracks:///config?inline=", json.RootElement.GetProperty("deepLink").GetString());
        Assert.Equal(json.RootElement.GetProperty("deepLink").GetString(), json.RootElement.GetProperty("qrPayload").GetString());
        await using (var db = ParticipantLinkTests.Db(host)) Assert.Single(await db.DeviceTokens.ToListAsync()); // nothing new issued

        await ParticipantLinkTests.AssertErrorAsync(await ParticipantLinkTests.PostAsync(web, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "/owntracks-config", new { deviceToken = token }),
            HttpStatusCode.NotFound, "link_invalid");
        host.Clock.Advance(PositionTracker.Api.Accounts.LinkTokenService.InviteLifetime + TimeSpan.FromMinutes(1));
        await ParticipantLinkTests.AssertErrorAsync(await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { deviceToken = token }),
            HttpStatusCode.BadRequest, "link_expired");
    }

    [Fact]
    public async Task The_config_link_is_rate_limited_per_link()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);
        var link = await ParticipantLinkTests.IssueLinkAsync(host, await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider"));
        var web = ParticipantLinkTests.Client(host);

        for (var i = 0; i < 60; i++) await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { });
        await ParticipantLinkTests.AssertErrorAsync(await ParticipantLinkTests.PostAsync(web, link, "/owntracks-config", new { }),
            HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task No_token_credential_or_authorization_header_reaches_the_logs()
    {
        var logs = new LogCapture();
        await using var host = Host(services: s => s.AddSingleton<ILoggerProvider>(logs), settings: s =>
        {
            s["Logging:LogLevel:Default"] = "Trace";
            s["Logging:LogLevel:Microsoft"] = "Trace";
            s["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        });
        var (_, _, token) = await LiveRiderAsync(host);
        using var client = host.CreateClient();
        await PostAsync(client, "1", token, Location(host));
        await PostAsync(client, "1", "wrong-secret-value", Location(host));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("1:" + token));

        var text = logs.Text();
        Assert.Contains("Executed endpoint", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain("wrong-secret-value", text);
        Assert.DoesNotContain(basic, text);
    }

    // ----- helpers -----

    private ApiFactory Host(string? connectionString = null, Action<IServiceCollection>? services = null,
        Action<Dictionary<string, string?>>? settings = null)
    {
        var factory = new ApiFactory { SeedTestEvent = false, ConfigureServices = services };
        factory.Settings["ConnectionStrings:Default"] = connectionString ?? postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        settings?.Invoke(factory.Settings);
        return factory;
    }

    /// <summary>A live event with its runtime and one confirmed rider (bib "1") who has consented: the device token.</summary>
    private static async Task<(Guid Event, Guid Participant, string Token)> LiveRiderAsync(ApiFactory host)
    {
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Live);
        await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);
        var web = ParticipantLinkTests.Client(host);
        await ParticipantLinkTests.PostAsync(web, link, "/confirm", new { });
        var token = await ParticipantLinkTests.ConsentAsync(web, link, false);
        host.Clock.SetUtcNow(ParticipantLinkTests.Start.AddMinutes(1)); // the race is on: fixes are accepted
        return (ev, pid, token);
    }

    private static string Location(ApiFactory host)
    {
        var point = DemoCourses.Loop()[0];
        var tst = host.Clock.GetUtcNow().ToUnixTimeSeconds();
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{"_type":"location","lat":{{point.Lat:R}},"lon":{{point.Lon:R}},"tst":{{tst}},"acc":5,"batt":80,"bs":1,"tid":"1","conn":"m"}""");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? user, string? password, string body) =>
        PostRawAsync(client, user is null || password is null ? null : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")), body);

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string? authorization, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/owntracks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return client.SendAsync(request);
    }

    private sealed class LogCapture : ILoggerProvider, ISupportExternalScope
    {
        private readonly List<string> _lines = [];
        private IExternalScopeProvider? _scopes;

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public string Text()
        {
            lock (_lines) return string.Join('\n', _lines);
        }

        public void Dispose() { }

        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = new StringBuilder(formatter(state, exception)).Append(' ').Append(exception);
                owner._scopes?.ForEachScope((scope, sb) => sb.Append(" | ").Append(scope), line);
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    foreach (var (key, value) in values) line.Append(' ').Append(key).Append('=').Append(value);
                lock (owner._lines) owner._lines.Add(line.ToString());
            }
        }
    }
}
