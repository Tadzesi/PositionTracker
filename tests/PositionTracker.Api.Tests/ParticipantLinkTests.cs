using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Email;
using PositionTracker.Api.Live;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Simulation;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The participant's invitation link (<c>/api/p/{linkToken}</c>): viewing, confirming, declining, the profile (nickname,
/// gender), consent with the device token, the runtime of a live event, device tokens surviving a restart, link states,
/// the per-link rate limit and that no link token reaches the logs. Through the HTTP host against PostgreSQL
/// (Testcontainers); fixtures are seeded through <see cref="AppDbContext"/> (ruling P2).
/// </summary>
public sealed class ParticipantLinkTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BaseUrl = "https://tracker.example.org";
    private static readonly Regex SignInLink = new(@"https://tracker\.example\.org/account/signin\?token=([A-Za-z0-9%_\-]+)");
    internal static readonly DateTimeOffset Start = ApiFactory.T0.AddDays(1);

    // ----- viewing -----

    [Fact]
    public async Task Get_shows_the_event_and_the_participant_without_email_or_tokens()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var category = await SeedCategoryAsync(host, ev, "Elite");
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Jana Nováková", categoryId: category, language: "en");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        var response = await GetAsync(client, link);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        var e = json.RootElement.GetProperty("event");
        Assert.Equal("Jarná časovka", e.GetProperty("name").GetString());
        Assert.Equal("Žilina", e.GetProperty("location").GetString());
        Assert.Equal("2026-11-08T10:00:00Z", e.GetProperty("startTime").GetString());
        Assert.Equal("cycling", e.GetProperty("sport").GetString());
        var me = json.RootElement.GetProperty("me");
        Assert.Equal("Jana Nováková", me.GetProperty("name").GetString());
        Assert.Equal("1", me.GetProperty("bib").GetString());
        Assert.Equal("Elite", me.GetProperty("category").GetString());
        Assert.Equal("female", me.GetProperty("gender").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("nickname").ValueKind);
        Assert.Equal("Invited", me.GetProperty("status").GetString());
        Assert.False(me.GetProperty("locked").GetProperty("gender").GetBoolean());
        Assert.Equal("en", json.RootElement.GetProperty("language").GetString());
        Assert.DoesNotContain("@example.com", body);
        Assert.DoesNotContain(link, body);
        Assert.DoesNotContain("deviceToken", body);
    }

    [Fact]
    public async Task Get_shows_the_current_state_of_confirmed_and_declined_participants_instead_of_an_error()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var confirmed = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Confirmed Rider");
        var declined = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Declined Rider");
        var confirmedLink = await IssueLinkAsync(host, confirmed);
        var declinedLink = await IssueLinkAsync(host, declined);
        var client = Client(host);

        Assert.Equal("Confirmed", await StatusOfAsync(await PostAsync(client, confirmedLink, "/confirm", new { nickname = "Rocket" })));
        Assert.Equal("Declined", await StatusOfAsync(await PostAsync(client, declinedLink, "/decline", new { })));

        using (var json = await JsonAsync(await GetAsync(client, confirmedLink)))
        {
            Assert.Equal("Confirmed", json.RootElement.GetProperty("me").GetProperty("status").GetString());
            Assert.Equal("Rocket", json.RootElement.GetProperty("me").GetProperty("nickname").GetString());
        }
        using (var json = await JsonAsync(await GetAsync(client, declinedLink)))
            Assert.Equal("Declined", json.RootElement.GetProperty("me").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(EventState.Live, false)]
    [InlineData(EventState.Finished, true)]
    public async Task The_view_says_whether_the_event_is_finished(EventState state, bool finished)
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, state);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Rider");
        var link = await IssueLinkAsync(host, pid);

        using var json = await JsonAsync(await GetAsync(Client(host), link));
        Assert.Equal(finished, json.RootElement.GetProperty("event").GetProperty("finished").GetBoolean());
    }

    [Fact]
    public async Task Unknown_expired_and_superseded_links_have_distinct_answers()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var old = await IssueLinkAsync(host, pid);
        var current = await IssueLinkAsync(host, pid); // supersedes the first
        var client = Client(host);

        await AssertErrorAsync(await GetAsync(client, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), HttpStatusCode.NotFound, "link_invalid");
        await AssertErrorAsync(await GetAsync(client, old), HttpStatusCode.Gone, "link_superseded");
        await AssertErrorAsync(await PostAsync(client, old, "/confirm", new { }), HttpStatusCode.Gone, "link_superseded");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, current)).StatusCode);

        host.Clock.Advance(LinkTokenService.InviteLifetime + TimeSpan.FromMinutes(1));
        await AssertErrorAsync(await GetAsync(client, current), HttpStatusCode.BadRequest, "link_expired");
        await AssertErrorAsync(await PostAsync(client, current, "/confirm", new { }), HttpStatusCode.BadRequest, "link_expired");
        await AssertErrorAsync(await PostAsync(client, current, "/consent", new { locationConsent = true, showNameToSpectators = false }),
            HttpStatusCode.BadRequest, "link_expired");
        await using var db = Db(host);
        Assert.Equal(ParticipantStatus.Invited, (await db.Participants.SingleAsync()).Status);
        Assert.Empty(await db.DeviceTokens.ToListAsync());
    }

    // ----- confirm, decline, profile -----

    [Fact]
    public async Task Confirm_is_idempotent_and_sets_nickname_and_gender()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        Assert.Equal("Confirmed", await StatusOfAsync(await PostAsync(client, link, "/confirm", new { nickname = "  Rýchly 🚀 ", gender = "male" })));
        Assert.Equal("Confirmed", await StatusOfAsync(await PostAsync(client, link, "/confirm", new { })));

        await using var db = Db(host);
        var p = await db.Participants.SingleAsync();
        Assert.Equal(ParticipantStatus.Confirmed, p.Status);
        Assert.Equal("Rýchly 🚀", p.Nickname);
        Assert.Equal("male", p.Gender);
    }

    public static TheoryData<string, string> InvalidProfiles => new()
    {
        { "nickname", "nickname-31" },
        { "nickname", "nickname-newline" },
        { "nickname", "nickname-bidi" },
        { "nickname", "nickname-control" },
        { "gender", "gender-other" },
    };

    [Theory]
    [MemberData(nameof(InvalidProfiles))]
    public async Task Invalid_nickname_or_gender_is_400_with_the_field_and_nothing_changes(string field, string variant)
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);
        object body = variant switch
        {
            "nickname-31" => new { nickname = new string('n', 31) },
            "nickname-newline" => new { nickname = "a\nb" },
            "nickname-bidi" => new { nickname = "abc‮evil" },
            "nickname-control" => new { nickname = "a\u0007b" },
            _ => new { gender = "x" },
        };

        await AssertErrorAsync(await PostAsync(client, link, "/confirm", body), HttpStatusCode.BadRequest, "validation_failed", field);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, link, "/profile", body), HttpStatusCode.BadRequest, "validation_failed", field);

        await using var db = Db(host);
        var p = await db.Participants.SingleAsync();
        Assert.Equal(ParticipantStatus.Invited, p.Status);
        Assert.Null(p.Nickname);
        Assert.Equal("female", p.Gender);
    }

    [Fact]
    public async Task A_30_character_nickname_is_accepted_and_an_empty_one_clears_it()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);
        var nickname = new string('ž', 29) + "🏁";

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname })).StatusCode);
        await using (var db = Db(host)) Assert.Equal(nickname, (await db.Participants.SingleAsync()).Nickname);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname = "" })).StatusCode);
        await using (var db = Db(host)) Assert.Null((await db.Participants.SingleAsync()).Nickname);
    }

    [Fact]
    public async Task Gender_changes_are_409_gender_locked_after_the_start_while_the_nickname_stays_editable()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { gender = "male" })).StatusCode);

        host.Clock.SetUtcNow(Start.AddMinutes(1));

        using (var json = await JsonAsync(await GetAsync(client, link)))
            Assert.True(json.RootElement.GetProperty("me").GetProperty("locked").GetProperty("gender").GetBoolean());
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, link, "/profile", new { gender = "female" }), HttpStatusCode.Conflict, "gender_locked", "gender");
        await AssertErrorAsync(await PostAsync(client, link, "/confirm", new { gender = "female" }), HttpStatusCode.Conflict, "gender_locked", "gender");
        // The same value is no change; the nickname can change at any time.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { gender = "male", nickname = "Late" })).StatusCode);
        await using var db = Db(host);
        var p = await db.Participants.SingleAsync();
        Assert.Equal("male", p.Gender);
        Assert.Equal("Late", p.Nickname);
    }

    [Fact]
    public async Task Decline_after_confirm_is_allowed_until_the_start_and_revokes_the_device_token()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var early = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Early");
        var late = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Late");
        var earlyLink = await IssueLinkAsync(host, early);
        var lateLink = await IssueLinkAsync(host, late);
        var client = Client(host);
        await PostAsync(client, earlyLink, "/confirm", new { });
        await PostAsync(client, lateLink, "/confirm", new { });
        var token = await ConsentAsync(client, earlyLink, false);

        Assert.Equal("Declined", await StatusOfAsync(await PostAsync(client, earlyLink, "/decline", new { })));
        Assert.Equal("Declined", await StatusOfAsync(await PostAsync(client, earlyLink, "/decline", new { }))); // idempotent
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ITokenService>().ResolveAsync(token, default));
        await AssertErrorAsync(await PostAsync(client, earlyLink, "/consent", new { locationConsent = true, showNameToSpectators = false }),
            HttpStatusCode.Conflict, "not_confirmed");

        host.Clock.SetUtcNow(Start);
        await AssertErrorAsync(await PostAsync(client, lateLink, "/decline", new { }), HttpStatusCode.Conflict, "decline_locked");
        await using var db = Db(host);
        Assert.Equal(ParticipantStatus.Declined, (await db.Participants.SingleAsync(p => p.Id == early)).Status);
        Assert.Equal(ParticipantStatus.Confirmed, (await db.Participants.SingleAsync(p => p.Id == late)).Status);
    }

    [Fact]
    public async Task A_finished_event_refuses_changes_but_still_shows_the_state()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Finished);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, link)).StatusCode);
        await AssertErrorAsync(await PostAsync(client, link, "/confirm", new { }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await PostAsync(client, link, "/decline", new { }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname = "x" }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = true }),
            HttpStatusCode.Conflict, "event_finished");
    }

    // ----- consent and device token -----

    [Fact]
    public async Task The_link_answer_carries_the_data_link_light_that_is_good_once_the_device_reaches_the_server()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Live);
        await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);
        await PostAsync(client, link, "/confirm", new { });
        var token = await ConsentAsync(client, link, false);

        using (var before = await JsonAsync(await GetAsync(client, link)))
            Assert.Equal("waiting", before.RootElement.GetProperty("link").GetProperty("level").GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(host, ev, token)).StatusCode);
        using var after = await JsonAsync(await GetAsync(client, link));
        var light = after.RootElement.GetProperty("link");
        Assert.True(light.GetProperty("level").GetString() == "good", light.ToString());
        Assert.Equal(0, light.GetProperty("problems").GetArrayLength());
        Assert.True(light.GetProperty("lastBatchAgoSeconds").GetDouble() < 5);
        using (var only = await JsonAsync(await GetAsync(client, link + "/link"))) // the cheap polling endpoint answers the same light
            Assert.Equal("good", only.RootElement.GetProperty("link").GetProperty("level").GetString());
        Assert.True(light.GetProperty("earlyOnly").GetBoolean()); // the seeded start is far away: connected, stored from an hour before it
    }

    [Fact]
    public async Task Consent_issues_the_device_token_once_stores_the_consent_and_a_second_consent_reissues()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider", showName: false);
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = true }),
            HttpStatusCode.Conflict, "not_confirmed");
        await PostAsync(client, link, "/confirm", new { });
        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { locationConsent = false, showNameToSpectators = true }),
            HttpStatusCode.BadRequest, "validation_failed", "locationConsent");
        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { showNameToSpectators = true }),
            HttpStatusCode.BadRequest, "validation_failed", "locationConsent");
        await using (var db = Db(host)) Assert.Empty(await db.DeviceTokens.ToListAsync());

        var consent = await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = true });
        var body = await consent.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Equal("no-store", consent.Headers.CacheControl?.ToString());
        string first;
        using (var json = JsonDocument.Parse(body))
        {
            first = json.RootElement.GetProperty("deviceToken").GetString()!;
            Assert.Equal(ev.ToString("D"), json.RootElement.GetProperty("eventId").GetString());
        }
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.DoesNotContain(first, await (await GetAsync(client, link)).Content.ReadAsStringAsync()); // returned once

        var second = await ConsentAsync(client, link, false);
        Assert.NotEqual(first, second);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            Assert.Null(await tokens.ResolveAsync(first, default));
            Assert.Equal((ev.ToString("D"), pid.ToString("D")), await tokens.ResolveAsync(second, default));
        }
        await using (var db = Db(host))
        {
            var consents = await db.Consents.OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(2, consents.Count);
            Assert.All(consents, c =>
            {
                Assert.Equal(pid, c.ParticipantId);
                Assert.True(c.LocationConsent);
                Assert.False(string.IsNullOrWhiteSpace(c.TextVersion));
                Assert.Equal(host.Clock.GetUtcNow(), c.Time);
            });
            Assert.True(consents[0].ShowNameToSpectators);
            Assert.False(consents[1].ShowNameToSpectators);
            Assert.False((await db.Participants.SingleAsync()).ShowNameToSpectators);
            var rows = await db.DeviceTokens.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, r => r.RevokedAt is null);
            Assert.All(rows, r => Assert.Equal(32, r.TokenHash.Length)); // only the hash is stored
        }

        // A re-consent needs a valid link: once it is superseded, the device token stays as it is.
        var newLink = await IssueLinkAsync(host, pid);
        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = false }),
            HttpStatusCode.Gone, "link_superseded");
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ITokenService>().ResolveAsync(second, default));
        Assert.NotEqual(second, await ConsentAsync(client, newLink, false));
    }

    [Fact]
    public async Task A_minor_cannot_show_the_name_to_spectators()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Young Rider", isMinor: true, showName: false);
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        await AssertErrorAsync(await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = true }),
            HttpStatusCode.Conflict, "minor_name_hidden", "showNameToSpectators");
        await using (var db = Db(host))
        {
            Assert.Empty(await db.Consents.ToListAsync());
            Assert.Empty(await db.DeviceTokens.ToListAsync());
        }
        await ConsentAsync(client, link, false);
        await using (var db = Db(host)) Assert.False((await db.Participants.SingleAsync()).ShowNameToSpectators);
    }

    // ----- live events -----

    [Fact]
    public async Task Confirming_on_a_live_event_adds_the_participant_to_the_runtime_and_profile_changes_reach_the_spectators()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, EventState.Ready, org.OrganizerId);
        await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "First Rider");
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Late Rider", showName: false);
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, (await org.PostAsync($"/api/events/{ev}/state", new { state = "live" })).StatusCode);
        var runtime = host.Registry.Get(ev.ToString("D"))!;
        var id = pid.ToString("D");
        Assert.False(runtime.HasParticipant(id));

        await PostAsync(client, link, "/confirm", new { nickname = "Rocket" });
        Assert.True(runtime.HasParticipant(id));
        Assert.Equal("Rocket", runtime.Participants[id].Nickname);
        Assert.False(runtime.Participants[id].ShowNameToSpectators);

        var version = runtime.ParticipantsVersion;
        await ConsentAsync(client, link, true);
        Assert.True(runtime.Participants[id].ShowNameToSpectators);
        Assert.True(runtime.ParticipantsVersion > version);
        Assert.Equal("Rocket", await SpectatorNameAsync(host, ev, id));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname = "Comet" })).StatusCode);
        Assert.Equal("Comet", runtime.Participants[id].Nickname);
        Assert.Equal("Comet", await SpectatorNameAsync(host, ev, id));

        // The organizer's moderation wins: a hidden nickname is not passed to the runtime, also after a participant edit.
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"/api/events/{ev}/participants/{id}", new { hideNickname = true })).StatusCode);
        Assert.Equal("Late Rider", await SpectatorNameAsync(host, ev, id));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname = "Again" })).StatusCode);
        Assert.Null(runtime.Participants[id].Nickname);
        Assert.Equal("Late Rider", await SpectatorNameAsync(host, ev, id));
    }

    [Fact]
    public async Task Database_device_tokens_reach_the_ingest_and_a_revoked_one_is_401_also_after_a_restart()
    {
        var cs = postgres.NewDatabase();
        Guid ev;
        string oldToken, newToken;
        await using (var host = Host(cs))
        {
            ev = await SeedEventAsync(host, EventState.Live);
            await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
            var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
            var link = await IssueLinkAsync(host, pid);
            var client = Client(host);
            await PostAsync(client, link, "/confirm", new { });
            oldToken = await ConsentAsync(client, link, true);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(host, ev, oldToken)).StatusCode);

            newToken = await ConsentAsync(client, link, true);
            Assert.Equal(HttpStatusCode.Unauthorized, (await IngestAsync(host, ev, oldToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(host, ev, newToken)).StatusCode);
        }

        await using (var restarted = Host(cs))
        {
            _ = restarted.Services; // rebuilds the live event from the database
            Assert.Equal(HttpStatusCode.Unauthorized, (await IngestAsync(restarted, ev, oldToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await IngestAsync(restarted, ev, newToken)).StatusCode);
            using var action = restarted.CreateClient(newToken);
            var response = await action.PostAsync($"/api/events/{ev}/actions", Json("""{"action":"pause"}"""));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    // ----- limits, headers, logs -----

    [Fact]
    public async Task Each_link_may_make_60_requests_per_minute()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var a = await IssueLinkAsync(host, await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "A"));
        var b = await IssueLinkAsync(host, await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "B"));
        var client = Client(host);

        for (var i = 0; i < 60; i++) Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, a)).StatusCode);
        var limited = await GetAsync(client, a);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.NotNull(limited.Headers.RetryAfter);
        await AssertErrorAsync(await PostAsync(client, a, "/confirm", new { }), HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, b)).StatusCode);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, a)).StatusCode);
    }

    [Fact]
    public async Task Unsafe_link_requests_need_the_browser_headers()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Ready);
        var link = await IssueLinkAsync(host, await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider"));
        var client = Client(host);

        var noHeader = new HttpRequestMessage(HttpMethod.Post, $"/api/p/{link}/confirm") { Content = Json("{}") };
        await AssertErrorAsync(await client.SendAsync(noHeader), HttpStatusCode.BadRequest, "csrf_failed");
        var form = new HttpRequestMessage(HttpMethod.Post, $"/api/p/{link}/confirm") { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        form.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(form)).StatusCode);
        await using var db = Db(host);
        Assert.Equal(ParticipantStatus.Invited, (await db.Participants.SingleAsync()).Status);
    }

    [Fact]
    public async Task No_link_token_or_device_token_reaches_the_logs_even_at_trace_level()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = Host(services: s => s.AddSingleton<ILoggerProvider>(logs), settings: s =>
        {
            s["Logging:LogLevel:Default"] = "Trace";
            s["Logging:LogLevel:Microsoft"] = "Trace";
            s["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
            s["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
            s["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Trace";
        });
        var ev = await SeedEventAsync(host, EventState.Live);
        await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var old = await IssueLinkAsync(host, pid);
        var link = await IssueLinkAsync(host, pid);
        var client = Client(host);

        await GetAsync(client, link);
        await GetAsync(client, old);
        await PostAsync(client, link, "/confirm", new { nickname = "a\nb" });
        await PostAsync(client, link, "/confirm", new { });
        await SendAsync(client, HttpMethod.Put, link, "/profile", new { nickname = "Rocket" });
        var token = await ConsentAsync(client, link, true);
        await IngestAsync(host, ev, token);
        for (var i = 0; i < 60; i++) await GetAsync(client, link); // into the rate limit

        var text = logs.Text();
        Assert.Contains("Executed endpoint", text); // the capture sees the request pipeline at trace level
        Assert.DoesNotContain(link, text);
        Assert.DoesNotContain(old, text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(Uri.EscapeDataString(link), text);
    }

    [Fact]
    public async Task Without_a_database_the_link_routes_are_503_unavailable()
    {
        await using var host = new ApiFactory { SeedTestEvent = false };
        using var client = host.CreateClient();
        await AssertErrorAsync(await client.GetAsync("/api/p/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), HttpStatusCode.ServiceUnavailable, "unavailable");
    }

    // ----- helpers -----

    private static async Task<string> SpectatorNameAsync(ApiFactory host, Guid ev, string participantId)
    {
        var broadcaster = host.Services.GetRequiredService<LiveBroadcaster>();
        await broadcaster.BroadcastAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await broadcaster.BroadcastAsync();
        using var client = host.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/events/{ev}/public"));
        return json.RootElement.GetProperty("participants").EnumerateArray()
            .Single(r => r.GetProperty("participantId").GetString() == participantId)
            .GetProperty("displayName").GetString()!;
    }

    private static async Task<HttpResponseMessage> IngestAsync(ApiFactory host, Guid ev, string token)
    {
        using var client = host.CreateClient(token);
        var point = DemoCourses.Loop()[0];
        var t = host.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var body = string.Create(CultureInfo.InvariantCulture, $$"""{"fixes":[{"t":"{{t}}","lat":{{point.Lat:R}},"lon":{{point.Lon:R}},"acc":5}]}""");
        return await client.PostAsync($"/api/events/{ev}/positions", Json(body));
    }

    internal static async Task<string> ConsentAsync(HttpClient client, string link, bool showName)
    {
        using var json = await JsonAsync(await PostAsync(client, link, "/consent", new { locationConsent = true, showNameToSpectators = showName }));
        return json.RootElement.GetProperty("deviceToken").GetString()!;
    }

    private static async Task<string> StatusOfAsync(HttpResponseMessage response)
    {
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("status").GetString()!;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string link) => client.GetAsync($"/api/p/{link}");

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, string link, string suffix, object body) =>
        SendAsync(client, HttpMethod.Post, link, suffix, body);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string link, string suffix, object body)
    {
        var request = new HttpRequestMessage(method, $"/api/p/{link}{suffix}") { Content = Json(JsonSerializer.Serialize(body)) };
        request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        return client.SendAsync(request);
    }

    internal static async Task<string> IssueLinkAsync(ApiFactory host, Guid participantId)
    {
        await using var db = Db(host);
        return await new LinkTokenService(db, host.Clock).IssueAsync(LinkPurpose.ParticipantInvite, participantId, LinkTokenService.InviteLifetime, default);
    }

    private ApiFactory Host(string? connectionString = null, Action<IServiceCollection>? services = null,
        Action<Dictionary<string, string?>>? settings = null)
    {
        var factory = new ApiFactory { SeedTestEvent = false, ConfigureServices = services };
        factory.Settings["ConnectionStrings:Default"] = connectionString ?? postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = BaseUrl;
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        settings?.Invoke(factory.Settings);
        return factory;
    }

    internal static HttpClient Client(ApiFactory host) => host.CreateDefaultClient(new Uri("https://localhost"));

    /// <summary>An organizer with a session: the account is seeded, then a sign-in link is requested and redeemed.</summary>
    internal static async Task<Organizer> SessionAsync(ApiFactory host, string email)
    {
        var id = await OrganizerAsync(host, email);
        var client = Client(host);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signin", JsonSerializer.Serialize(new { email }))).StatusCode);
        string token;
        await using (var db = Db(host))
        {
            var mail = await db.Outbox.Where(m => m.Purpose == EmailPurpose.SignIn && m.ToAddress == email).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).LastAsync();
            token = Uri.UnescapeDataString(SignInLink.Match(mail.TextBody).Groups[1].Value);
        }
        var redeemed = await Send(client, HttpMethod.Post, "/api/account/signin/redeem", JsonSerializer.Serialize(new { token }));
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        return new Organizer(client, cookie, id);
    }

    private static async Task<Guid> OrganizerAsync(ApiFactory host, string email)
    {
        await using var db = Db(host);
        var existing = await db.Organizers.SingleOrDefaultAsync(o => o.EmailNormalized == email);
        if (existing is not null) return existing.Id;
        var id = Guid.NewGuid();
        db.Organizers.Add(new OrganizerEntity
        {
            Id = id, Name = "Org " + email, Email = email, EmailNormalized = email, Language = "sk",
            CreatedAt = host.Clock.GetUtcNow(), EmailConfirmedAt = host.Clock.GetUtcNow(), SecurityStamp = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return id;
    }

    internal sealed class Organizer(HttpClient client, string cookie, Guid organizerId)
    {
        public Guid OrganizerId { get; } = organizerId;

        public Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

        public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

        public string Cookie => cookie;

        public Task<HttpResponseMessage> GetAsync(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }

        public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
        {
            var request = new HttpRequestMessage(method, path) { Content = Json(JsonSerializer.Serialize(body ?? new { })) };
            request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string json)
    {
        var request = new HttpRequestMessage(method, path) { Content = Json(json) };
        request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        return client.SendAsync(request);
    }

    internal static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    internal static async Task<Guid> SeedEventAsync(ApiFactory host, EventState state, Guid? organizerId = null)
    {
        var organizer = organizerId ?? await OrganizerAsync(host, "seed-organizer@example.org");
        await using var db = Db(host);
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(), OrganizerId = organizer, Name = "Jarná časovka", Location = "Žilina", StartTime = Start,
            Sport = Sport.Cycling, State = state, Laps = 1, SpectatorDelaySeconds = 30, GpxBytes = Gpx(DemoCourses.Loop()),
            CreatedAt = host.Clock.GetUtcNow(), Version = Guid.NewGuid(),
            EndedAt = state == EventState.Finished ? host.Clock.GetUtcNow() : null,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<Guid> SeedCategoryAsync(ApiFactory host, Guid eventId, string name)
    {
        await using var db = Db(host);
        var c = new CategoryEntity { Id = Guid.NewGuid(), EventId = eventId, Name = name };
        db.Categories.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    internal static async Task<Guid> SeedParticipantAsync(ApiFactory host, Guid eventId, ParticipantStatus status, string name,
        Guid? categoryId = null, string language = "sk", bool isMinor = false, bool showName = true)
    {
        await using var db = Db(host);
        var n = await db.Participants.CountAsync(p => p.EventId == eventId) + 1;
        var p = new ParticipantEntity
        {
            Id = Guid.NewGuid(), EventId = eventId, Bib = n.ToString(CultureInfo.InvariantCulture), Name = name,
            Email = $"rider{n}@example.com", EmailNormalized = $"rider{n}@example.com", Gender = "female", Language = language,
            CategoryId = categoryId, Status = status, IsMinor = isMinor, ShowNameToSpectators = showName,
            CreatedAt = host.Clock.GetUtcNow().AddSeconds(n),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    internal static AppDbContext Db(ApiFactory host)
    {
        _ = host.Services;
        return new AppDbContext(PostgresFixture.Options(host.Settings["ConnectionStrings:Default"]!));
    }

    internal static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("message");
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("sk").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("en").GetString()));
        if (field is not null) Assert.Equal(field, json.RootElement.GetProperty("field").GetString());
    }

    internal static byte[] Gpx(IReadOnlyList<GeoPoint> points)
    {
        var gpx = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>""");
        foreach (var p in points)
            gpx.Append(CultureInfo.InvariantCulture, $"""<trkpt lat="{p.Lat:R}" lon="{p.Lon:R}"></trkpt>""");
        return Encoding.UTF8.GetBytes(gpx.Append("</trkseg></trk></gpx>").ToString());
    }

    /// <summary>Captures every log line with its scopes (so a request path in a scope is seen too).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly List<string> _lines = [];
        private IExternalScopeProvider? _scopes;

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public string Text()
        {
            lock (_lines) return string.Join('\n', _lines);
        }

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = new StringBuilder($"{logLevel} {category}: {formatter(state, exception)} {exception}");
                owner._scopes?.ForEachScope((scope, sb) =>
                {
                    sb.Append(" | scope: ").Append(scope);
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (key, value) in pairs) sb.Append(' ').Append(key).Append('=').Append(value);
                }, line);
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    foreach (var (key, value) in values) line.Append(' ').Append(key).Append('=').Append(value);
                lock (owner._lines) owner._lines.Add(line.ToString());
            }
        }
    }
}
