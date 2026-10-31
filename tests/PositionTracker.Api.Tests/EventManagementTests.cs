using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Events;
using PositionTracker.Api.Simulation;
using PositionTracker.Core;
using PositionTracker.Core.Simulation;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Organizer event management (<c>/api/events</c>): creation, course upload, categories, waves, the state machine and
/// the runtime registration of <c>live</c>, through the HTTP host against PostgreSQL (Testcontainers). Confirmed
/// participants are seeded through <see cref="AppDbContext"/> (controller ruling P2).
/// </summary>
public sealed class EventManagementTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BaseUrl = "https://tracker.example.org";
    private const string IpHeader = "X-Test-Remote-Ip";
    private static readonly Regex SignInLink = new(@"https://tracker\.example\.org/account/signin\?token=([A-Za-z0-9%_\-]+)");
    private static readonly DateTimeOffset Start = ApiFactory.T0.AddDays(1);
    private static readonly byte[] LoopGpx = Gpx(DemoCourses.Loop());
    private int _ip;

    // ----- creation -----

    [Fact]
    public async Task Creating_an_event_stores_a_draft_and_is_allowed_before_email_confirmation()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org", confirmed: false);

        var response = await org.PostAsync("/api/events", new
        {
            name = "  Jarná časovka 🚴 ", location = "Žilina – Vodné dielo", startTime = Utc(Start), sport = "cycling",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = await EventIdAsync(response);
        Assert.Equal($"/api/events/{id}/manage", response.Headers.Location?.OriginalString);
        Assert.Equal(id.ToString("D"), id.ToString("D").ToLowerInvariant());
        var stored = await EventAsync(host, id);
        Assert.Equal("Jarná časovka 🚴", stored.Name);
        Assert.Equal("Žilina – Vodné dielo", stored.Location);
        Assert.Equal(Start, stored.StartTime);
        Assert.Equal(Sport.Cycling, stored.Sport);
        Assert.Equal(EventState.Draft, stored.State);
        Assert.Equal(org.OrganizerId, stored.OrganizerId);
        Assert.Equal(1, stored.Laps);
        Assert.Equal(30, stored.SpectatorDelaySeconds);
        Assert.Null(stored.GpxBytes);

        using var manage = await JsonAsync(await org.GetAsync($"/api/events/{id}/manage"));
        var root = manage.RootElement;
        Assert.Equal(id.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal("draft", root.GetProperty("state").GetString());
        Assert.Equal("cycling", root.GetProperty("sport").GetString());
        Assert.Equal(Utc(Start), root.GetProperty("startTime").GetString());
        Assert.Equal(30, root.GetProperty("spectatorDelaySeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("course").ValueKind);
        Assert.Equal(EmailModes.Simulated, root.GetProperty("emailMode").GetString());
        Assert.False(root.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Names_and_locations_of_120_characters_are_accepted_and_longer_ones_rejected()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var name120 = new string('ž', 118) + "🚴🚴"; // characters outside the BMP count once

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(org, name: name120, location: name120)).StatusCode);
        await AssertErrorAsync(await CreateAsync(org, name: name120 + "a"), HttpStatusCode.BadRequest, "validation_failed", "name");
        await AssertErrorAsync(await CreateAsync(org, location: name120 + "a"), HttpStatusCode.BadRequest, "validation_failed", "location");
    }

    public static TheoryData<string, string?, string?, string?, string?> InvalidCreations => new()
    {
        { "name", null, "Loc", "future", "running" },
        { "name", "   ", "Loc", "future", "running" },
        { "name", "Line\nBreak", "Loc", "future", "running" },
        { "name", "Tab\there", "Loc", "future", "running" },
        { "name", "Bidi \u202Eevil", "Loc", "future", "running" },
        { "name", "Isolate \u2066x\u2069", "Loc", "future", "running" },
        { "name", "Separator \u2028x", "Loc", "future", "running" },
        { "location", "Name", null, "future", "running" },
        { "location", "Name", "Bad\r\nBcc: x@y.z", "future", "running" },
        { "location", "Name", "Mark \u200F", "future", "running" },
        { "startTime", "Name", "Loc", null, "running" },
        { "startTime", "Name", "Loc", "past", "running" },
        { "startTime", "Name", "Loc", "now", "running" },
        { "startTime", "Name", "Loc", "2026-11-08T10:00:00+01:00", "running" },
        { "startTime", "Name", "Loc", "2026-11-08T10:00:00", "running" },
        { "startTime", "Name", "Loc", "tomorrow", "running" },
        { "sport", "Name", "Loc", "future", null },
        { "sport", "Name", "Loc", "future", "swimming" },
        { "sport", "Name", "Loc", "future", "0" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreations))]
    public async Task Invalid_creation_input_is_rejected_with_the_field_and_nothing_is_stored(
        string field, string? name, string? location, string? startTime, string? sport)
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var start = startTime switch
        {
            "future" => Utc(Start),
            "past" => Utc(host.Clock.GetUtcNow().AddMinutes(-1)),
            "now" => Utc(host.Clock.GetUtcNow()),
            _ => startTime,
        };

        var response = await org.PostRawAsync("/api/events", JsonSerializer.Serialize(new { name, location, startTime = start, sport }));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        await using var db = Db(host);
        Assert.Empty(await db.Events.ToListAsync());
    }

    [Fact]
    public async Task Malformed_and_oversized_bodies_are_rejected_and_requests_need_a_session()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");

        await AssertErrorAsync(await org.PostRawAsync("/api/events", "{not json"), HttpStatusCode.BadRequest, "validation_failed", "body");
        await AssertErrorAsync(await org.PostRawAsync("/api/events", "null"), HttpStatusCode.BadRequest, "validation_failed", "body");
        await AssertErrorAsync(await org.PostRawAsync("/api/events", JsonSerializer.Serialize(new { name = new string('x', 20_000) })),
            HttpStatusCode.RequestEntityTooLarge, "validation_failed", "body");

        var anonymous = Client(host);
        await AssertErrorAsync(await anonymous.GetAsync("/api/events"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await Send(anonymous, HttpMethod.Post, "/api/events", Body(new { name = "x" })),
            HttpStatusCode.Unauthorized, "unauthorized");
    }

    // ----- course -----

    [Fact]
    public async Task An_unconfirmed_organizer_gets_403_email_not_confirmed_for_course_categories_waves_and_state()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org", confirmed: false);
        var id = await CreatedAsync(org);

        await AssertErrorAsync(await PutCourseAsync(org, id), HttpStatusCode.Forbidden, "email_not_confirmed");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/categories", Array.Empty<object>()), HttpStatusCode.Forbidden, "email_not_confirmed");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/waves", Array.Empty<object>()), HttpStatusCode.Forbidden, "email_not_confirmed");
        await AssertErrorAsync(await StateAsync(org, id, "ready"), HttpStatusCode.Forbidden, "email_not_confirmed");
        Assert.Null((await EventAsync(host, id)).GpxBytes);
    }

    [Fact]
    public async Task A_valid_course_is_stored_with_its_parameters_and_shown_in_the_manage_view()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);
        var gpx = Gpx(DemoCourses.Loop(elevationAtM: d => 300 + 20 * Math.Sin(d / 1000)));

        var response = await PutCourseAsync(org, id, gpx, laps: 3, delay: 45,
            checkpoints: [new { name = "Bufet", lapDistanceM = 8_000.0 }, new { name = "Kopec", lapDistanceM = 3_000.5 }]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await EventAsync(host, id);
        Assert.Equal(gpx, stored.GpxBytes);
        Assert.Equal(3, stored.Laps);
        Assert.Equal(45, stored.SpectatorDelaySeconds);
        using (var cps = JsonDocument.Parse(stored.CheckpointsJson!))
        {
            Assert.Equal(["Kopec", "Bufet"], cps.RootElement.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
            Assert.Equal(3_000.5, cps.RootElement[0].GetProperty("lapDistanceM").GetDouble());
        }
        var expected = Course.Create(DemoCourses.Loop(elevationAtM: d => 300 + 20 * Math.Sin(d / 1000)), 3);
        await using (var db = Db(host))
        {
            var course = await db.Courses.SingleAsync(c => c.EventId == id);
            Assert.Equal(expected.LapLengthM, course.LapLengthM, 6);
            Assert.Equal(expected.AscentPerLapM, course.AscentM, 6);
            Assert.Equal(expected.DescentPerLapM, course.DescentM, 6);
            Assert.Equal(expected.Points.Count, course.PointCount);
            Assert.Equal(host.Clock.GetUtcNow(), course.UploadedAt);
        }

        using var manage = await JsonAsync(response);
        var c = manage.RootElement.GetProperty("course");
        Assert.Equal(expected.LapLengthM, c.GetProperty("lapLengthM").GetDouble(), 6);
        Assert.True(c.GetProperty("closed").GetBoolean());
        Assert.Equal(2, c.GetProperty("checkpoints").GetArrayLength());
        Assert.Equal(3, manage.RootElement.GetProperty("laps").GetInt32());

        // A second upload replaces the first; the delay defaults to 30 s and laps to 1.
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"/api/events/{id}/course", new { gpxBase64 = Convert.ToBase64String(LoopGpx) })).StatusCode);
        stored = await EventAsync(host, id);
        Assert.Equal(LoopGpx, stored.GpxBytes);
        Assert.Equal(1, stored.Laps);
        Assert.Equal(30, stored.SpectatorDelaySeconds);
        await using (var db = Db(host)) Assert.Single(await db.Courses.Where(x => x.EventId == id).ToListAsync());
    }

    public static TheoryData<string, string> InvalidCourses => new()
    {
        { "gpx_invalid", "not-base64" },
        { "gpx_invalid", "not-xml" },
        { "gpx_invalid", "dtd" },
        { "gpx_invalid", "one-point" },
        { "gpx_invalid", "missing" },
        { "course_too_short", "short" },
        { "course_not_closed", "open-two-laps" },
        { "checkpoint_invalid", "checkpoint-zero" },
        { "checkpoint_invalid", "checkpoint-beyond" },
        { "checkpoint_invalid", "checkpoint-duplicate-distance" },
        { "checkpoint_invalid", "checkpoint-duplicate-name" },
        { "checkpoint_invalid", "checkpoint-no-name" },
        { "checkpoint_invalid", "checkpoint-control-name" },
        { "delay_invalid", "delay-4" },
        { "delay_invalid", "delay-301" },
        { "laps_invalid", "laps-0" },
        { "laps_invalid", "laps-11" },
    };

    [Theory]
    [MemberData(nameof(InvalidCourses))]
    public async Task Invalid_courses_are_rejected_with_distinct_codes_and_nothing_is_stored(string code, string variant)
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);
        var loop = Convert.ToBase64String(LoopGpx);
        object Cp(string name, double d) => new { name, lapDistanceM = d };
        object body = variant switch
        {
            "not-base64" => new { gpxBase64 = "%%%" },
            "not-xml" => new { gpxBase64 = B64("this is not a gpx") },
            "dtd" => new { gpxBase64 = B64("""<?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY x "y">]><gpx><trk><trkseg><trkpt lat="49" lon="18"/><trkpt lat="49.1" lon="18"/></trkseg></trk></gpx>""") },
            "one-point" => new { gpxBase64 = B64("""<gpx><trk><trkseg><trkpt lat="49" lon="18"/></trkseg></trk></gpx>""") },
            "missing" => new { laps = 1 },
            "short" => new { gpxBase64 = Convert.ToBase64String(Gpx([new GeoPoint(49, 18, null), new GeoPoint(49.0001, 18, null)])) },
            "open-two-laps" => new { gpxBase64 = Convert.ToBase64String(Gpx([new GeoPoint(49, 18, null), new GeoPoint(49.01, 18, null)])), laps = 2 },
            "checkpoint-zero" => new { gpxBase64 = loop, checkpoints = new[] { Cp("A", 0) } },
            "checkpoint-beyond" => new { gpxBase64 = loop, checkpoints = new[] { Cp("A", 12_500) } },
            "checkpoint-duplicate-distance" => new { gpxBase64 = loop, checkpoints = new[] { Cp("A", 3000), Cp("B", 3000) } },
            "checkpoint-duplicate-name" => new { gpxBase64 = loop, checkpoints = new[] { Cp("Bufet", 3000), Cp("BUFET", 6000) } },
            "checkpoint-no-name" => new { gpxBase64 = loop, checkpoints = new[] { Cp(" ", 3000) } },
            "checkpoint-control-name" => new { gpxBase64 = loop, checkpoints = new[] { Cp("A\u202E", 3000) } },
            "delay-4" => new { gpxBase64 = loop, spectatorDelaySeconds = 4 },
            "delay-301" => new { gpxBase64 = loop, spectatorDelaySeconds = 301 },
            "laps-0" => new { gpxBase64 = loop, laps = 0 },
            "laps-11" => new { gpxBase64 = loop, laps = 11 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/course", body), HttpStatusCode.BadRequest, code);

        var stored = await EventAsync(host, id);
        Assert.Null(stored.GpxBytes);
        Assert.Null(stored.CheckpointsJson);
        await using var db = Db(host);
        Assert.Empty(await db.Courses.ToListAsync());
    }

    [Fact]
    public async Task Course_limits_delay_and_laps_bounds_are_inclusive_and_a_gpx_over_one_megabyte_is_413()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);

        Assert.Equal(HttpStatusCode.OK, (await PutCourseAsync(org, id, delay: 5, laps: 10)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutCourseAsync(org, id, delay: 300, laps: 1)).StatusCode);

        var big = new byte[1024 * 1024 + 1];
        Array.Fill(big, (byte)' ');
        LoopGpx.CopyTo(big, 0);
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/course", new { gpxBase64 = Convert.ToBase64String(big) }),
            HttpStatusCode.RequestEntityTooLarge, "gpx_too_large");
        Assert.Equal(300, (await EventAsync(host, id)).SpectatorDelaySeconds);
    }

    // ----- categories and waves -----

    [Fact]
    public async Task Categories_replace_the_whole_set_keep_the_ids_of_kept_names_and_reject_invalid_sets_atomically()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);

        var first = await org.PutAsync($"/api/events/{id}/categories", new object[]
        {
            new { name = "Muži 18–39", gender = "male", minAge = 18, maxAge = 39 },
            new { name = "Ženy", gender = "female" },
            new { name = "Open" },
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = await CategoriesAsync(host, id);
        Assert.Equal(["Muži 18–39", "Open", "Ženy"], before.Select(c => c.Name).Order(StringComparer.Ordinal));
        var open = before.Single(c => c.Name == "Open");
        var participant = await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed, open.Id);

        var second = await org.PutAsync($"/api/events/{id}/categories", new object[]
        {
            new { name = "OPEN" }, // same name, other case: the same category
            new { name = "Juniori", minAge = 0, maxAge = 17 },
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var after = await CategoriesAsync(host, id);
        Assert.Equal(["Juniori", "OPEN"], after.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal(open.Id, after.Single(c => c.Name == "OPEN").Id);
        await using (var db = Db(host))
            Assert.Equal(open.Id, (await db.Participants.SingleAsync(p => p.Id == participant)).CategoryId);
        using (var manage = await JsonAsync(second))
            Assert.Equal(2, manage.RootElement.GetProperty("categories").GetArrayLength());

        foreach (var invalid in new object[][]
                 {
                     [new { name = "A" }, new { name = "a" }],                 // duplicate, case-insensitive
                     [new { name = "" }],
                     [new { name = new string('x', 61) }],
                     [new { name = "Bidi\u202E" }],
                     [new { name = "A", minAge = -1 }],
                     [new { name = "A", maxAge = 121 }],
                     [new { name = "A", minAge = 40, maxAge = 39 }],
                     [new { name = "A", gender = "other" }],
                 })
            await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/categories", invalid), HttpStatusCode.BadRequest, "validation_failed");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/categories", Enumerable.Range(0, 51).Select(i => new { name = $"C{i}" })),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(["Juniori", "OPEN"], (await CategoriesAsync(host, id)).Select(c => c.Name).Order(StringComparer.Ordinal));

        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"/api/events/{id}/categories", Array.Empty<object>())).StatusCode);
        Assert.Empty(await CategoriesAsync(host, id));
    }

    [Fact]
    public async Task Waves_start_between_the_event_start_and_24_hours_later_and_replace_the_whole_set()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);

        var ok = await org.PutAsync($"/api/events/{id}/waves", new object[]
        {
            new { name = "Elita", startTime = Utc(Start) },
            new { name = "Hobby", startTime = Utc(Start.AddHours(24)) },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        foreach (var invalid in new object[][]
                 {
                     [new { name = "Early", startTime = Utc(Start.AddSeconds(-1)) }],
                     [new { name = "Late", startTime = Utc(Start.AddHours(24).AddSeconds(1)) }],
                     [new { name = "Offset", startTime = "2026-11-08T11:00:00+01:00" }],
                     [new { name = "None", startTime = (string?)null }],
                     [new { name = "A", startTime = Utc(Start) }, new { name = " a ", startTime = Utc(Start) }],
                     [new { name = "Line\nbreak", startTime = Utc(Start) }],
                 })
            await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/waves", invalid), HttpStatusCode.BadRequest, "validation_failed");

        await using var db = Db(host);
        var waves = await db.Waves.Where(w => w.EventId == id).OrderBy(w => w.StartTime).ToListAsync();
        Assert.Equal(["Elita", "Hobby"], waves.Select(w => w.Name));
        Assert.Equal(Start.AddHours(24), waves[1].StartTime);
    }

    // ----- state machine and runtime -----

    [Fact]
    public async Task The_state_machine_allows_draft_ready_live_finished_only_and_live_registers_the_runtime()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);
        var runtimeId = id.ToString("D");

        await AssertErrorAsync(await StateAsync(org, id, "live"), HttpStatusCode.Conflict, "invalid_transition");
        await AssertErrorAsync(await StateAsync(org, id, "finished"), HttpStatusCode.Conflict, "invalid_transition");
        await AssertErrorAsync(await StateAsync(org, id, "ready"), HttpStatusCode.Conflict, "course_missing");
        await AssertErrorAsync(await StateAsync(org, id, "draft"), HttpStatusCode.BadRequest, "validation_failed", "state");
        await AssertErrorAsync(await StateAsync(org, id, "LIVE!"), HttpStatusCode.BadRequest, "validation_failed", "state");

        Assert.Equal(HttpStatusCode.OK, (await PutCourseAsync(org, id, checkpoints: [new { name = "CP1", lapDistanceM = 4_000.0 }])).StatusCode);
        var ready = await StateAsync(org, id, "ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using (var view = await JsonAsync(ready)) Assert.Equal("ready", view.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "ready")).StatusCode); // idempotent
        await AssertErrorAsync(await StateAsync(org, id, "finished"), HttpStatusCode.Conflict, "invalid_transition");

        // live needs at least one confirmed participant
        var invited = await SeedParticipantAsync(host, id, ParticipantStatus.Invited);
        await AssertErrorAsync(await StateAsync(org, id, "live"), HttpStatusCode.Conflict, "no_confirmed_participants");
        Assert.Null(host.Registry.Get(runtimeId));
        Assert.Equal(EventState.Ready, (await EventAsync(host, id)).State);

        var p1 = await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        var p2 = await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        var live = await StateAsync(org, id, "live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using (var view = await JsonAsync(live))
        {
            Assert.Equal("live", view.RootElement.GetProperty("state").GetString());
            Assert.True(view.RootElement.GetProperty("running").GetBoolean());
        }
        Assert.Equal(EventState.Live, (await EventAsync(host, id)).State);

        var runtime = host.Registry.Get(runtimeId);
        Assert.NotNull(runtime);
        Assert.False(runtime.Settings.IsSimulated);
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.Settings.SpectatorDelay);
        Assert.Equal(Start, runtime.Settings.StartTime);
        Assert.Equal("Jarná časovka", runtime.Settings.Name);
        Assert.Equal(new[] { p1, p2 }.Select(p => p.ToString("D")).Order(), runtime.Participants.Keys.Order());
        Assert.False(runtime.HasParticipant(invited.ToString("D")));
        Assert.Equal(["CP1"], runtime.Course.Checkpoints.Select(c => c.Name));
        Assert.Equal(HttpStatusCode.OK, (await Client(host).GetAsync($"/api/events/{runtimeId}/public")).StatusCode);

        // idempotent, and a live event is frozen
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
        Assert.Same(runtime, host.Registry.Get(runtimeId));
        await AssertErrorAsync(await PutCourseAsync(org, id), HttpStatusCode.Conflict, "event_locked");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/categories", new[] { new { name = "X" } }), HttpStatusCode.Conflict, "event_locked");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{id}/waves", Array.Empty<object>()), HttpStatusCode.Conflict, "event_locked");
        await AssertErrorAsync(await StateAsync(org, id, "ready"), HttpStatusCode.Conflict, "invalid_transition");

        // a device can send while live
        var device = host.CreateClient(host.Tokens.Issue(runtimeId, p1.ToString("D")));
        Assert.Equal(HttpStatusCode.Accepted, (await PostFixAsync(device, runtimeId, Start.AddMinutes(-5))).StatusCode);

        // finished: the runtime stays registered for the results, ingest is refused
        var finished = await StateAsync(org, id, "finished");
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var ended = await EventAsync(host, id);
        Assert.Equal(EventState.Finished, ended.State);
        Assert.Equal(host.Clock.GetUtcNow(), ended.EndedAt);
        Assert.Same(runtime, host.Registry.Get(runtimeId));
        Assert.True(host.Registry.IsFinished(runtimeId));
        await AssertErrorAsync(await PostFixAsync(device, runtimeId, Start.AddMinutes(-4)), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await device.PostAsJsonAsync($"/api/events/{runtimeId}/actions", new { action = "sos" }),
            HttpStatusCode.Conflict, "event_finished");
        Assert.Equal(HttpStatusCode.OK, (await Client(host).GetAsync($"/api/events/{runtimeId}/public")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "finished")).StatusCode); // idempotent
        Assert.Equal(ended.EndedAt, (await EventAsync(host, id)).EndedAt);
        await AssertErrorAsync(await StateAsync(org, id, "live"), HttpStatusCode.Conflict, "invalid_transition");
        await AssertErrorAsync(await PutCourseAsync(org, id), HttpStatusCode.Conflict, "event_locked");
    }

    [Fact]
    public async Task Live_without_a_course_is_409_course_missing()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, gpx: null);
        await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);

        await AssertErrorAsync(await StateAsync(org, id, "live"), HttpStatusCode.Conflict, "course_missing");
        Assert.Equal(EventState.Ready, (await EventAsync(host, id)).State);
        Assert.Null(host.Registry.Get(id.ToString("D")));
    }

    [Fact]
    public async Task A_runtime_that_cannot_be_built_leaves_the_event_ready_and_answers_500_json()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        // Stored data that bypassed validation: EventSettings.Create refuses a 3 s spectator delay.
        var id = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, LoopGpx, delaySeconds: 3);
        await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);

        await AssertErrorAsync(await StateAsync(org, id, "live"), HttpStatusCode.InternalServerError, "runtime_failed");

        Assert.Equal(EventState.Ready, (await EventAsync(host, id)).State);
        Assert.Null(host.Registry.Get(id.ToString("D")));
    }

    [Fact]
    public async Task Live_on_a_live_event_whose_runtime_is_missing_rebuilds_it_through_the_shared_factory()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, LoopGpx);
        var p = await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
        var runtimeId = id.ToString("D");
        var device = host.CreateClient(host.Tokens.Issue(runtimeId, p.ToString("D")));
        host.Clock.SetUtcNow(Start.AddMinutes(2));
        Assert.Equal(HttpStatusCode.Accepted, (await PostFixAsync(device, runtimeId, Start.AddMinutes(1))).StatusCode);
        await WaitForFixesAsync(host, id, 1);

        Assert.True(host.Registry.Remove(runtimeId)); // as if the startup rebuild had failed
        using var view = await JsonAsync(await org.GetAsync($"/api/events/{id}/manage"));
        Assert.False(view.RootElement.GetProperty("running").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
        var rebuilt = host.Registry.Get(runtimeId);
        Assert.NotNull(rebuilt);
        Assert.NotNull(rebuilt.BuildLiveView().Participants.Single().LastRaceFixTime); // the stored history was replayed
    }

    [Fact]
    public async Task A_restart_rebuilds_live_and_recently_finished_events_and_a_finished_one_still_refuses_ingest()
    {
        var cs = postgres.NewDatabase();
        Guid liveId, finishedId;
        await using (var first = Host(cs))
        {
            var org = await SessionAsync(first, "anna@example.org");
            liveId = await SeedEventAsync(first, org.OrganizerId, EventState.Ready, LoopGpx);
            finishedId = await SeedEventAsync(first, org.OrganizerId, EventState.Ready, LoopGpx);
            foreach (var id in new[] { liveId, finishedId })
            {
                await SeedParticipantAsync(first, id, ParticipantStatus.Confirmed);
                Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
            }
            Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, finishedId, "finished")).StatusCode);
        }

        await using var second = Host(cs);
        _ = second.Services;
        Assert.NotNull(second.Registry.Get(liveId.ToString("D")));
        Assert.False(second.Registry.IsFinished(liveId.ToString("D")));
        Assert.NotNull(second.Registry.Get(finishedId.ToString("D")));
        Assert.True(second.Registry.IsFinished(finishedId.ToString("D")));

        await using var db = Db(second);
        var p = await db.Participants.SingleAsync(x => x.EventId == finishedId);
        var device = second.CreateClient(second.Tokens.Issue(finishedId.ToString("D"), p.Id.ToString("D")));
        await AssertErrorAsync(await PostFixAsync(device, finishedId.ToString("D"), Start.AddMinutes(-5)), HttpStatusCode.Conflict, "event_finished");
    }

    // ----- ownership, lists, simulated events -----

    [Fact]
    public async Task Another_organizer_gets_404_for_every_route_and_lists_show_only_own_events()
    {
        await using var host = Host();
        var owner = await SessionAsync(host, "owner@example.org");
        var other = await SessionAsync(host, "other@example.org");
        var id = await CreatedAsync(owner, name: "Owner's race");
        Assert.Equal(HttpStatusCode.OK, (await PutCourseAsync(owner, id)).StatusCode);
        var otherId = await CreatedAsync(other, name: "Other race");

        await AssertErrorAsync(await other.GetAsync($"/api/events/{id}/manage"), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await PutCourseAsync(other, id), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.PutAsync($"/api/events/{id}/categories", Array.Empty<object>()), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.PutAsync($"/api/events/{id}/waves", Array.Empty<object>()), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await StateAsync(other, id, "ready"), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.GetAsync("/api/events/not-a-guid/manage"), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await other.GetAsync($"/api/events/{Guid.NewGuid()}/manage"), HttpStatusCode.NotFound, "not_found");
        var stored = await EventAsync(host, id);
        Assert.Equal(EventState.Draft, stored.State);
        Assert.Equal(LoopGpx, stored.GpxBytes);

        using var ownList = await JsonAsync(await owner.GetAsync("/api/events"));
        Assert.Equal(EmailModes.Simulated, ownList.RootElement.GetProperty("emailMode").GetString());
        var own = Assert.Single(ownList.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(id.ToString("D"), own.GetProperty("eventId").GetString());
        Assert.Equal("Owner's race", own.GetProperty("name").GetString());
        Assert.Equal("draft", own.GetProperty("state").GetString());
        using var otherList = await JsonAsync(await other.GetAsync("/api/events"));
        Assert.Equal(otherId.ToString("D"), Assert.Single(otherList.RootElement.GetProperty("events").EnumerateArray()).GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task Simulated_events_are_never_listed_or_managed()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);
        var demo = host.Services.GetRequiredService<SimulationService>().Start(new DemoRequest(Participants: 2, Laps: 1));
        Assert.NotNull(host.Registry.Get(demo.EventId));

        using var list = await JsonAsync(await org.GetAsync("/api/events"));
        Assert.Equal([id.ToString("D")], list.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()));
        await AssertErrorAsync(await org.GetAsync($"/api/events/{demo.EventId}/manage"), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await StateAsync(org, demo.EventId, "finished"), HttpStatusCode.NotFound, "not_found");
        Assert.False(host.Registry.IsFinished(demo.EventId));
    }

    // ----- browser headers (P9/P10) -----

    [Fact]
    public async Task Organizer_event_routes_need_the_browser_headers_and_device_routes_stay_exempt()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/events"), (HttpMethod.Put, $"/api/events/{id}/course"),
                     (HttpMethod.Put, $"/api/events/{id}/categories"), (HttpMethod.Put, $"/api/events/{id}/waves"),
                     (HttpMethod.Post, $"/api/events/{id}/state"),
                 })
        {
            await AssertErrorAsync(await org.SendAsync(method, path, Body(new { }), header: false), HttpStatusCode.BadRequest, "csrf_failed");
            await AssertErrorAsync(await org.SendAsync(method, path, new StringContent("{}", Encoding.UTF8, "text/plain")),
                HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }
        Assert.Single(await EventsAsync(host));

        // Device routes: bearer token, no browser header, still accepted.
        Assert.Equal(HttpStatusCode.OK, (await PutCourseAsync(org, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "ready")).StatusCode);
        var p = await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
        var device = host.CreateClient(host.Tokens.Issue(id.ToString("D"), p.ToString("D")));
        Assert.False(device.DefaultRequestHeaders.Contains(BrowserApiHeaders.HeaderName));
        Assert.Equal(HttpStatusCode.Accepted, (await PostFixAsync(device, id.ToString("D"), Start.AddMinutes(-5))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PostAsJsonAsync($"/api/events/{id:D}/actions", new { action = "pause" })).StatusCode);
    }

    // ----- concurrency -----

    [Fact]
    public async Task Concurrent_live_requests_register_exactly_one_runtime_and_all_succeed()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, LoopGpx);
        await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StateAsync(org, id, "live")));

        foreach (var r in responses) Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Assert.NotNull(host.Registry.Get(id.ToString("D")));
        Assert.Single(host.Registry.All);
        Assert.Equal(EventState.Live, (await EventAsync(host, id)).State);
    }

    [Fact]
    public async Task Concurrent_category_replacements_leave_exactly_one_complete_set()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "anna@example.org");
        var id = await CreatedAsync(org);
        var sets = Enumerable.Range(0, 6)
            .Select(i => Enumerable.Range(0, 5).Select(j => $"S{i}-C{j}").ToArray())
            .ToList();

        var responses = await Task.WhenAll(sets.Select(set =>
            org.PutAsync($"/api/events/{id}/categories", set.Select(n => new { name = n }))));

        foreach (var r in responses)
        {
            if (r.StatusCode == HttpStatusCode.Conflict) await AssertErrorAsync(r, HttpStatusCode.Conflict, "conflict");
            else Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        var names = (await CategoriesAsync(host, id)).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Contains(sets, set => set.Order(StringComparer.Ordinal).SequenceEqual(names));
    }

    [Fact]
    public async Task A_course_upload_that_loses_a_race_with_a_state_change_is_409_conflict_and_stores_nothing()
    {
        var hooks = new EventServiceHooks();
        await using var host = Host(services: s => s.AddSingleton(hooks));
        var org = await SessionAsync(host, "anna@example.org");
        var id = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, LoopGpx);
        await SeedParticipantAsync(host, id, ParticipantStatus.Confirmed);
        var raced = false;
        hooks.BeforeSave = async eventId =>
        {
            if (raced || eventId != id) return;
            raced = true;
            // Another request takes the event live between this request's read and its write.
            Assert.Equal(HttpStatusCode.OK, (await StateAsync(org, id, "live")).StatusCode);
        };
        var other = Gpx(DemoCourses.Loop(lapLengthM: 5_000));

        await AssertErrorAsync(await PutCourseAsync(org, id, other), HttpStatusCode.Conflict, "conflict");

        Assert.True(raced);
        var stored = await EventAsync(host, id);
        Assert.Equal(EventState.Live, stored.State);
        Assert.Equal(LoopGpx, stored.GpxBytes);
        Assert.Equal(Course.Create(DemoCourses.Loop(), 1).LapLengthM, host.Registry.Get(id.ToString("D"))!.Course.LapLengthM, 6);
    }

    // ----- helpers -----

    private ApiFactory Host(string? connectionString = null, Action<IServiceCollection>? services = null)
    {
        var factory = new ApiFactory
        {
            SeedTestEvent = false,
            ConfigureServices = s =>
            {
                s.AddSingleton<IStartupFilter, TestRemoteIp>();
                services?.Invoke(s);
            },
        };
        factory.Settings["ConnectionStrings:Default"] = connectionString ?? postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = BaseUrl;
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Email:Outbox:RetentionDays"] = "3650";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }

    private HttpClient Client(ApiFactory host)
    {
        var client = host.CreateDefaultClient(new Uri("https://localhost"));
        client.DefaultRequestHeaders.Add(IpHeader, $"192.0.2.{Interlocked.Increment(ref _ip)}");
        return client;
    }

    /// <summary>An organizer with a session: the account is seeded, then a sign-in link is requested and redeemed.</summary>
    private async Task<Organizer> SessionAsync(ApiFactory host, string email, bool confirmed = true)
    {
        _ = host.Services;
        var id = Guid.NewGuid();
        await using (var db = Db(host))
        {
            db.Organizers.Add(new OrganizerEntity
            {
                Id = id, Name = "Org " + email, Email = email, EmailNormalized = email, Language = "sk",
                CreatedAt = host.Clock.GetUtcNow(), EmailConfirmedAt = host.Clock.GetUtcNow(), SecurityStamp = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        var client = Client(host);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signin", Body(new { email }))).StatusCode);
        string token;
        await using (var db = Db(host))
        {
            var mail = await db.Outbox.Where(m => m.Purpose == EmailPurpose.SignIn && m.ToAddress == email).OrderBy(m => m.Id).LastAsync();
            token = Uri.UnescapeDataString(SignInLink.Match(mail.TextBody).Groups[1].Value);
        }
        var redeemed = await Send(client, HttpMethod.Post, "/api/account/signin/redeem", Body(new { token }));
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        if (!confirmed)
        {
            await using var db = Db(host);
            await db.Organizers.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.EmailConfirmedAt, (DateTimeOffset?)null));
        }
        return new Organizer(client, cookie, id);
    }

    private sealed class Organizer(HttpClient client, string cookie, Guid organizerId)
    {
        public Guid OrganizerId { get; } = organizerId;

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

        public Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, Body(body));

        public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, Body(body));

        public Task<HttpResponseMessage> PostRawAsync(string path, string json) =>
            SendAsync(HttpMethod.Post, path, new StringContent(json, Encoding.UTF8, "application/json"));

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, bool header = true)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            if (header) request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        return client.SendAsync(request);
    }

    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> CreateAsync(Organizer org, string name = "Jarná časovka", string location = "Žilina") =>
        org.PostAsync("/api/events", new { name, location, startTime = Utc(Start), sport = "running" });

    private static async Task<Guid> CreatedAsync(Organizer org, string name = "Jarná časovka")
    {
        var response = await CreateAsync(org, name);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await EventIdAsync(response);
    }

    private static async Task<Guid> EventIdAsync(HttpResponseMessage response)
    {
        using var json = await JsonAsync(response);
        return Guid.ParseExact(json.RootElement.GetProperty("eventId").GetString()!, "D");
    }

    private static Task<HttpResponseMessage> PutCourseAsync(Organizer org, Guid id, byte[]? gpx = null, int laps = 1, int delay = 30,
        object[]? checkpoints = null) =>
        org.PutAsync($"/api/events/{id}/course", new
        {
            gpxBase64 = Convert.ToBase64String(gpx ?? LoopGpx), laps, checkpoints = checkpoints ?? [], spectatorDelaySeconds = delay,
        });

    private static Task<HttpResponseMessage> StateAsync(Organizer org, Guid id, string state) => StateAsync(org, id.ToString("D"), state);

    private static Task<HttpResponseMessage> StateAsync(Organizer org, string id, string state) =>
        org.PostAsync($"/api/events/{id}/state", new { state });

    private static Task<HttpResponseMessage> PostFixAsync(HttpClient device, string eventId, DateTimeOffset t) =>
        device.PostAsJsonAsync($"/api/events/{eventId}/positions", new
        {
            fixes = new[] { new { t = Utc(t), lat = 49.0, lon = 18.7, acc = 5.0 } },
        });

    private static async Task<Guid> SeedEventAsync(ApiFactory host, Guid organizerId, EventState state, byte[]? gpx, int delaySeconds = 30)
    {
        await using var db = Db(host);
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(), OrganizerId = organizerId, Name = "Seeded", Location = "Loop", StartTime = Start,
            Sport = Sport.Cycling, State = state, Laps = 1, SpectatorDelaySeconds = delaySeconds, GpxBytes = gpx,
            CreatedAt = host.Clock.GetUtcNow(),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<Guid> SeedParticipantAsync(ApiFactory host, Guid eventId, ParticipantStatus status, Guid? categoryId = null)
    {
        await using var db = Db(host);
        var n = await db.Participants.CountAsync(p => p.EventId == eventId) + 1;
        var p = new ParticipantEntity
        {
            Id = Guid.NewGuid(), EventId = eventId, Bib = n.ToString(CultureInfo.InvariantCulture), Name = $"Rider {n}",
            Email = $"rider{n}@example.com", EmailNormalized = $"rider{n}@example.com", Gender = "female", Language = "sk",
            CategoryId = categoryId, Status = status, ShowNameToSpectators = true, CreatedAt = host.Clock.GetUtcNow().AddTicks(n),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task WaitForFixesAsync(ApiFactory host, Guid eventId, int count)
    {
        for (var i = 0; i < 200; i++)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(600));
            await using var db = Db(host);
            if (await db.Fixes.CountAsync(f => f.EventId == eventId) >= count) return;
            await Task.Delay(25);
        }
        Assert.Fail("The fixes were not written.");
    }

    private static async Task<EventEntity> EventAsync(ApiFactory host, Guid id)
    {
        await using var db = Db(host);
        return await db.Events.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private static async Task<List<EventEntity>> EventsAsync(ApiFactory host)
    {
        await using var db = Db(host);
        return await db.Events.AsNoTracking().ToListAsync();
    }

    private static async Task<List<CategoryEntity>> CategoriesAsync(ApiFactory host, Guid id)
    {
        await using var db = Db(host);
        return await db.Categories.AsNoTracking().Where(c => c.EventId == id).ToListAsync();
    }

    private static AppDbContext Db(ApiFactory host)
    {
        _ = host.Services;
        return new AppDbContext(PostgresFixture.Options(host.Settings["ConnectionStrings:Default"]!));
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("message");
        var sk = message.GetProperty("sk").GetString();
        var en = message.GetProperty("en").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sk));
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(sk, en);
        if (field is not null) Assert.Equal(field, json.RootElement.GetProperty("field").GetString());
        var expectedSk = code switch
        {
            "not_found" => "neexistuje",
            "gpx_invalid" => "GPX",
            "course_too_short" => "krátka",
            "course_not_closed" => "uzavretá",
            "checkpoint_invalid" => "Kontrolný bod",
            "delay_invalid" => "Oneskorenie",
            "laps_invalid" => "Počet kôl",
            "event_locked" => "už nemožno meniť",
            "event_finished" => "skončilo",
            "conflict" => "súčasne",
            _ => null,
        };
        if (expectedSk is not null) Assert.Contains(expectedSk, sk);
    }

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static byte[] Gpx(IReadOnlyList<GeoPoint> points)
    {
        var gpx = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>""");
        foreach (var p in points)
            gpx.Append(CultureInfo.InvariantCulture, $"""<trkpt lat="{p.Lat:R}" lon="{p.Lon:R}">""")
                .Append(p.Ele is { } ele ? string.Create(CultureInfo.InvariantCulture, $"<ele>{ele:R}</ele>") : "")
                .Append("</trkpt>");
        return Encoding.UTF8.GetBytes(gpx.Append("</trkseg></trk></gpx>").ToString());
    }

    /// <summary>Sets the remote IP from a test header (the test server has none).</summary>
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
