using System.Net;
using System.Text.Json;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Tests;

/// <summary>
/// What the public and the participant see once an event has finished: <c>GET /api/events/{eventId}/public-results</c> (names only
/// with consent and never of a minor, DNS only counted), the finish time in <c>/info</c> and the participant's own line on their link.
/// Against PostgreSQL (Testcontainers).
/// </summary>
public sealed class FinishedEventTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Public_results_show_a_name_only_with_consent_and_never_of_a_minor()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Finished);
        var fast = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Anna Rýchla");
        var hidden = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Boris Skrytý", showName: false);
        var minor = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Cyril Mladý", isMinor: true);
        var absent = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Dana Neprišla");
        await SeedResultAsync(host, ev, fast, "1", "finished", 1, TimeSpan.Parse("01:02:03.4"), null, 30000);
        await SeedResultAsync(host, ev, hidden, "2", "finished", 2, TimeSpan.Parse("01:05:00"), TimeSpan.Parse("00:02:56.6"), 30000);
        await SeedResultAsync(host, ev, minor, "3", "dnf", null, null, null, 12345.6);
        await SeedResultAsync(host, ev, absent, "4", "dns", null, null, null, 0);

        var response = await ParticipantLinkTests.Client(host).GetAsync($"/api/events/{ev:D}/public-results");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("finished").GetInt32());
        Assert.Equal(1, root.GetProperty("dnf").GetInt32());
        Assert.Equal(1, root.GetProperty("dns").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["Anna Rýchla", "#2", "#3"], rows.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("01:02:03", rows[0].GetProperty("netTime").GetString());
        Assert.Equal("00:02:57", rows[1].GetProperty("gapToLeader").GetString());
        Assert.Equal("dnf", rows[2].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("rank").ValueKind);
        Assert.Equal(12346, rows[2].GetProperty("distanceM").GetDouble());
        Assert.DoesNotContain("Boris", body);
        Assert.DoesNotContain("Cyril", body);
        Assert.DoesNotContain("Dana", body);
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("gender", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_results_follow_a_consent_withdrawn_after_the_finish()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Finished);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Anna Rýchla");
        await SeedResultAsync(host, ev, pid, "1", "finished", 1, TimeSpan.FromHours(1), null, 30000);
        await using (var db = ParticipantLinkTests.Db(host))
        {
            var p = await db.Participants.FindAsync(pid);
            p!.ShowNameToSpectators = false;
            await db.SaveChangesAsync();
        }

        var body = await ParticipantLinkTests.Client(host).GetStringAsync($"/api/events/{ev:D}/public-results");

        Assert.DoesNotContain("Anna", body);
        Assert.Contains("\"#1\"", body);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("live")]
    [InlineData("unknown")]
    [InlineData("not-a-guid")]
    public async Task Public_results_exist_only_for_a_finished_event(string which)
    {
        await using var host = Host();
        var id = which switch
        {
            "ready" => (await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready)).ToString("D"),
            "live" => (await ParticipantLinkTests.SeedEventAsync(host, EventState.Live)).ToString("D"),
            "unknown" => Guid.NewGuid().ToString("D"),
            _ => which,
        };

        var response = await ParticipantLinkTests.Client(host).GetAsync($"/api/events/{id}/public-results");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_info_of_a_finished_event_says_when_it_ended()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Finished);

        using var json = JsonDocument.Parse(await ParticipantLinkTests.Client(host).GetStringAsync($"/api/events/{ev:D}/info"));

        Assert.Equal("finished", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(host.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), json.RootElement.GetProperty("endedAt").GetString());
    }

    [Fact]
    public async Task The_participant_link_of_a_finished_event_carries_their_own_result()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Finished);
        var winner = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Anna");
        var second = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Boris");
        await SeedResultAsync(host, ev, winner, "1", "finished", 1, TimeSpan.FromHours(1), null, 30000);
        await SeedResultAsync(host, ev, second, "2", "finished", 2, TimeSpan.FromHours(1.5), TimeSpan.FromMinutes(30), 30000);
        var link = await ParticipantLinkTests.IssueLinkAsync(host, second);

        using var json = JsonDocument.Parse(await ParticipantLinkTests.Client(host).GetStringAsync($"/api/p/{link}"));

        var result = json.RootElement.GetProperty("result");
        Assert.Equal("finished", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("rank").GetInt32());
        Assert.Equal("01:30:00", result.GetProperty("netTime").GetString());
        Assert.Equal("00:30:00", result.GetProperty("gapToLeader").GetString());
        Assert.Equal(2, result.GetProperty("finishers").GetInt32());
    }

    [Fact]
    public async Task The_participant_link_of_a_running_event_has_no_result()
    {
        await using var host = Host();
        var ev = await ParticipantLinkTests.SeedEventAsync(host, EventState.Ready);
        var pid = await ParticipantLinkTests.SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Anna");
        var link = await ParticipantLinkTests.IssueLinkAsync(host, pid);

        using var json = JsonDocument.Parse(await ParticipantLinkTests.Client(host).GetStringAsync($"/api/p/{link}"));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    private static async Task SeedResultAsync(ApiFactory host, Guid eventId, Guid participantId, string bib, string status, int? rank,
        TimeSpan? netTime, TimeSpan? gap, double distanceM)
    {
        await using var db = ParticipantLinkTests.Db(host);
        var p = await db.Participants.FindAsync(participantId);
        db.ResultRows.Add(new ResultRowEntity
        {
            Id = Guid.CreateVersion7(), EventId = eventId, ParticipantId = participantId, Bib = bib, Name = p!.Name, IsMinor = p.IsMinor,
            NameConsent = p.ShowNameToSpectators, Status = status, Rank = rank, CategoryRank = rank, Start = ParticipantLinkTests.Start,
            FinishTime = netTime is { } t ? ParticipantLinkTests.Start + t : null, NetTime = netTime, GapToLeader = gap, DistanceM = distanceM,
        });
        await db.SaveChangesAsync();
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
