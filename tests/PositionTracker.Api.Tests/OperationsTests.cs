using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Operations;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using static PositionTracker.Api.Tests.ParticipantLinkTests;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Organizer operations: alarm acknowledgement, manual DNF, privacy deletion, the retention worker and the organizer
/// session as an alternative to <c>X-Api-Key</c> for the live read and the hub. Against PostgreSQL (Testcontainers).
/// </summary>
public sealed class OperationsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // ----- alarm acknowledgement -----

    [Fact]
    public async Task An_acknowledgement_marks_the_alarm_survives_a_restart_and_is_cleared_with_the_alarm()
    {
        var cs = postgres.NewDatabase();
        Guid ev, pid;
        string token;
        await using (var host = Host(cs))
        {
            var org = await SessionAsync(host, "org@example.org");
            (ev, pid, token) = await LiveRiderAsync(host, org.OrganizerId);
            using var device = host.CreateClient(token);
            await device.PostAsync($"/api/events/{ev}/actions", Json("""{"action":"sos"}"""));

            Assert.False(await AckedAsync(org, ev, pid, "sos")); // raised, not acknowledged
            await AssertNoContentAsync(await org.PostAsync($"/api/events/{ev}/alarms/ack", new { participantId = pid.ToString("D"), kind = "Sos" }));
            Assert.True(await AckedAsync(org, ev, pid, "sos"));
            await using (var db = Db(host)) Assert.Single(await db.AlarmAcks.ToListAsync());

            await AssertErrorAsync(await org.PostAsync($"/api/events/{ev}/alarms/ack", new { participantId = pid.ToString("D"), kind = "LowBattery" }),
                HttpStatusCode.Conflict, "alarm_not_active");
            await AssertErrorAsync(await org.PostAsync($"/api/events/{ev}/alarms/ack", new { participantId = pid.ToString("D"), kind = "Nope" }),
                HttpStatusCode.BadRequest, "validation_failed", "kind");
            await AssertErrorAsync(await org.PostAsync($"/api/events/{ev}/alarms/ack", new { participantId = Guid.NewGuid().ToString("D"), kind = "Sos" }),
                HttpStatusCode.NotFound, "not_found");
        }

        await using (var restarted = Host(cs))
        {
            _ = restarted.Services;
            restarted.Clock.Advance(TimeSpan.FromMinutes(5)); // a new mail is newer than the first host's
            var org = await SessionAsync(restarted, "org@example.org");
            Assert.True(await AckedAsync(org, ev, pid, "sos")); // loaded from the database

            using var device = restarted.CreateClient(token);
            await device.PostAsync($"/api/events/{ev}/actions", Json("""{"action":"sosCleared"}"""));
            using (var json = JsonDocument.Parse(await (await org.GetAsync($"/api/events/{ev}/live")).Content.ReadAsStringAsync()))
                Assert.Empty(json.RootElement.GetProperty("participants")[0].GetProperty("alarms").EnumerateArray());
            await using (var db = Db(restarted)) Assert.Empty(await db.AlarmAcks.ToListAsync()); // cleared with the alarm

            await device.PostAsync($"/api/events/{ev}/actions", Json("""{"action":"sos"}"""));
            Assert.False(await AckedAsync(org, ev, pid, "sos")); // a new alarm is new
        }
    }

    [Fact]
    public async Task Only_the_owner_may_acknowledge_mark_dnf_or_delete()
    {
        await using var host = Host();
        var owner = await SessionAsync(host, "owner@example.org");
        var other = await SessionAsync(host, "other@example.org");
        var (ev, pid, _) = await LiveRiderAsync(host, owner.OrganizerId);
        var id = pid.ToString("D");

        await AssertErrorAsync(await other.PostAsync($"/api/events/{ev}/alarms/ack", new { participantId = id, kind = "Sos" }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.PostAsync($"/api/events/{ev}/participants/{id}/dnf", new { }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.DeleteAsync($"/api/events/{ev}/participants/{id}/data"), HttpStatusCode.NotFound, "not_found");
        using var anonymous = host.CreateClient();
        var noSession = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{ev}/participants/{id}/dnf") { Content = Json("{}") };
        noSession.Headers.Add(PositionTracker.Api.Accounts.BrowserApiHeaders.HeaderName, PositionTracker.Api.Accounts.BrowserApiHeaders.HeaderValue);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(noSession)).StatusCode);
    }

    // ----- DNF -----

    [Fact]
    public async Task The_organizer_marks_a_rider_DNF_and_it_is_persisted()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var (ev, pid, _) = await LiveRiderAsync(host, org.OrganizerId);

        await AssertNoContentAsync(await org.PostAsync($"/api/events/{ev}/participants/{pid:D}/dnf", new { }));
        await AssertErrorAsync(await org.PostAsync($"/api/events/{ev}/participants/{Guid.NewGuid():D}/dnf", new { }), HttpStatusCode.NotFound, "not_found");

        using var json = JsonDocument.Parse(await (await org.GetAsync($"/api/events/{ev}/live")).Content.ReadAsStringAsync());
        Assert.Equal("dnf", json.RootElement.GetProperty("participants")[0].GetProperty("state").GetString()?.ToLowerInvariant());
        for (var i = 0; i < 200; i++)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(600));
            await using var db = Db(host);
            if (await db.ActionLog.AnyAsync(a => a.EventId == ev && a.Action == ParticipantAction.Dnf)) return;
            await Task.Delay(25);
        }
        Assert.Fail("The DNF was not written to the action log.");
    }

    // ----- privacy deletion -----

    [Fact]
    public async Task Privacy_deletion_removes_the_personal_data_and_keeps_only_the_bib()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var (ev, pid, token) = await LiveRiderAsync(host, org.OrganizerId, showName: true);
        using (var device = host.CreateClient(token))
        {
            var point = PositionTracker.Core.Simulation.DemoCourses.Loop()[0];
            var t = host.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var body = string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{"fixes":[{"t":"{{t}}","lat":{{point.Lat:R}},"lon":{{point.Lon:R}},"acc":5}]}""");
            Assert.Equal(HttpStatusCode.Accepted, (await device.PostAsync($"/api/events/{ev}/positions", Json(body))).StatusCode);
        }
        for (var i = 0; i < 200; i++)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(600));
            await using var db = Db(host);
            if (await db.Fixes.AnyAsync(f => f.EventId == ev)) break;
            await Task.Delay(25);
        }

        await AssertNoContentAsync(await org.DeleteAsync($"/api/events/{ev}/participants/{pid:D}/data"));

        await using (var db = Db(host))
        {
            var p = await db.Participants.SingleAsync();
            Assert.Equal("#1", p.Name);
            Assert.Null(p.Nickname);
            Assert.DoesNotContain("rider1@example.com", p.Email + p.EmailNormalized);
            Assert.False(p.ShowNameToSpectators);
            Assert.Equal("1", p.Bib);
            Assert.Empty(await db.Fixes.ToListAsync());
            Assert.Empty(await db.DeviceStatuses.ToListAsync());
            Assert.Empty(await db.Consents.ToListAsync());
            Assert.Empty(await db.LinkTokens.Where(t => t.SubjectId == pid).ToListAsync());
            Assert.Empty(await db.Outbox.Where(m => m.ParticipantId == pid).ToListAsync());
        }
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<PositionTracker.Api.Auth.ITokenService>().ResolveAsync(token, default));
        var runtime = host.Registry.Get(ev.ToString("D"))!;
        Assert.Equal("#1", runtime.Participants[pid.ToString("D")].Name);
        Assert.False(runtime.Participants[pid.ToString("D")].ShowNameToSpectators);
    }

    // ----- retention -----

    [Fact]
    public async Task Retention_deletes_the_fixes_of_events_that_ended_over_30_days_ago_only()
    {
        await using var host = Host();
        var old = await EndedEventWithFixAsync(host, host.Clock.GetUtcNow().AddDays(-31), "old");
        var recent = await EndedEventWithFixAsync(host, host.Clock.GetUtcNow().AddDays(-29), "recent");
        var running = await ParticipantLinkTests.SeedEventAsync(host, EventState.Live);
        var worker = host.Services.GetRequiredService<RetentionWorker>();

        Assert.Equal(1, await worker.RunOnceAsync(default));

        await using var db = Db(host);
        Assert.Empty(await db.Fixes.Where(f => f.EventId == old).ToListAsync());
        Assert.Single(await db.Fixes.Where(f => f.EventId == recent).ToListAsync());
        Assert.Equal(0, await worker.RunOnceAsync(default));
        _ = running;
    }

    // ----- public course -----

    [Fact]
    public async Task The_public_course_describes_a_running_event_without_a_session_and_is_404_otherwise()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var (ev, _, _) = await LiveRiderAsync(host, org.OrganizerId);
        var notRunning = await SeedEventAsync(host, EventState.Ready, org.OrganizerId);
        using var anonymous = host.CreateClient();

        using var json = JsonDocument.Parse(await anonymous.GetStringAsync($"/api/events/{ev}/course"));
        Assert.Equal("Jarná časovka", json.RootElement.GetProperty("event").GetProperty("name").GetString());
        Assert.Equal("cycling", json.RootElement.GetProperty("event").GetProperty("sport").GetString());
        Assert.Equal(30, json.RootElement.GetProperty("event").GetProperty("spectatorDelaySeconds").GetInt32());
        Assert.True(json.RootElement.GetProperty("points").GetArrayLength() >= 2);
        Assert.Equal(2, json.RootElement.GetProperty("points")[0].GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/events/{notRunning}/course")).StatusCode);
    }

    // ----- organizer session for live read and hub -----

    [Fact]
    public async Task An_organizer_session_reads_its_own_live_view_and_the_operator_key_still_works()
    {
        await using var host = Host();
        var owner = await SessionAsync(host, "owner@example.org");
        var other = await SessionAsync(host, "other@example.org");
        var (ev, _, _) = await LiveRiderAsync(host, owner.OrganizerId);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/events/{ev}/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/events/{ev}/live")).StatusCode);
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/events/{ev}/live")).StatusCode);
        using var operatorClient = host.CreateClient();
        operatorClient.DefaultRequestHeaders.Add("X-Api-Key", host.OrganizerKey);
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync($"/api/events/{ev}/live")).StatusCode);
    }

    [Fact]
    public async Task The_hub_accepts_the_owner_session_and_refuses_another_organizer()
    {
        await using var host = Host();
        var owner = await SessionAsync(host, "owner@example.org");
        var other = await SessionAsync(host, "other@example.org");
        var (ev, _, _) = await LiveRiderAsync(host, owner.OrganizerId);

        await using var ownerHub = Connect(host, owner.Cookie);
        await ownerHub.StartAsync();
        await ownerHub.InvokeAsync("JoinOrganizer", ev.ToString("D"), (string?)null);

        await using var otherHub = Connect(host, other.Cookie);
        await otherHub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => otherHub.InvokeAsync("JoinOrganizer", ev.ToString("D"), (string?)null));
        await using var anonymousHub = Connect(host, null);
        await anonymousHub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => anonymousHub.InvokeAsync("JoinOrganizer", ev.ToString("D"), ""));
    }

    // ----- helpers -----

    private ApiFactory Host(string? connectionString = null)
    {
        var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["ConnectionStrings:Default"] = connectionString ?? postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }

    /// <summary>A live event of the organizer with its runtime and one confirmed rider (bib "1") who has consented.</summary>
    private static async Task<(Guid Event, Guid Participant, string Token)> LiveRiderAsync(ApiFactory host, Guid organizerId, bool showName = false)
    {
        var ev = await SeedEventAsync(host, EventState.Live, organizerId);
        await host.Services.GetRequiredService<PositionTracker.Api.Persistence.RuntimeRehydrator>().RebuildAsync(default);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Rider");
        var link = await IssueLinkAsync(host, pid);
        var web = Client(host);
        await PostAsync(web, link, "/confirm", new { });
        var token = await ConsentAsync(web, link, showName);
        host.Clock.SetUtcNow(Start.AddMinutes(1));
        return (ev, pid, token);
    }

    private static async Task<bool> AckedAsync(Organizer org, Guid ev, Guid pid, string kind)
    {
        using var json = JsonDocument.Parse(await (await org.GetAsync($"/api/events/{ev}/live")).Content.ReadAsStringAsync());
        var participant = json.RootElement.GetProperty("participants").EnumerateArray()
            .Single(p => p.GetProperty("participantId").GetString() == pid.ToString("D"));
        var alarm = participant.GetProperty("alarms").EnumerateArray()
            .Single(a => string.Equals(a.GetProperty("kind").GetString(), kind, StringComparison.OrdinalIgnoreCase));
        return alarm.GetProperty("acknowledged").GetBoolean();
    }

    private static async Task AssertNoContentAsync(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static async Task<Guid> EndedEventWithFixAsync(ApiFactory host, DateTimeOffset endedAt, string email)
    {
        var ev = await SeedEventAsync(host, EventState.Finished);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, email);
        await using var db = Db(host);
        await db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(e => e.EndedAt, endedAt));
        db.Fixes.Add(new FixEntity { EventId = ev, ParticipantId = pid, Time = endedAt.AddHours(-1), Lat = 49, Lon = 18, AccuracyM = 5 });
        await db.SaveChangesAsync();
        return ev;
    }

    private static HubConnection Connect(ApiFactory host, string? cookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                if (cookie is not null) o.Headers["Cookie"] = cookie;
            })
            .Build();
}
