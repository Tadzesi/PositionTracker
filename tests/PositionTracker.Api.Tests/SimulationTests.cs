using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Live;
using PositionTracker.Api.Simulation;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Every test uses its own host: the fake clock never moves, so the background feeding loop never ticks and the
/// simulation advances only through <see cref="SimulationService.Step"/>.
/// </summary>
public class SimulationTests
{
    private const string DemoUrl = "/api/demo/events";
    private static readonly string[] OrganizerOnly = ["battery", "gpsAccuracy", "queue", "alarms", "ascent", "descent", "grade"];

    private static SimulationService Simulation(ApiFactory factory) => factory.Services.GetRequiredService<SimulationService>();

    private static HttpClient OrganizerClient(ApiFactory factory, string? key = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key ?? factory.OrganizerKey);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    private static object FixBody(PositionFix fix) => new
    {
        fixes = new[]
        {
            new
            {
                t = fix.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
                lat = fix.Lat, lon = fix.Lon, acc = fix.AccuracyM,
            },
        },
    };

    /// <summary>Steps from <paramref name="from"/> (exclusive) to <paramref name="to"/> (inclusive) and calls <paramref name="observe"/> after each step.</summary>
    private static void StepRange(SimulationService sim, string eventId, TimeSpan from, TimeSpan to, TimeSpan step,
        Action<TimeSpan>? observe = null)
    {
        for (var t = from + step; t <= to; t += step)
        {
            sim.Step(eventId, t);
            observe?.Invoke(t);
        }
    }

    private static ParticipantView Row(EventRuntime runtime, string participantId) =>
        runtime.BuildLiveView().Participants.Single(p => p.ParticipantId == participantId);

    private static VirtualRiderProfile Profile(string id, IReadOnlyList<(TimeSpan, TimeSpan)> dropouts) =>
        new(id, 8, 0.05, 5, 4242, dropouts, null, null, null);

    [Fact]
    public async Task Start_creates_simulated_event_with_30_participants_and_tokens_that_ingest()
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);

        var created = await ReadJson(await organizer.PostAsync(DemoUrl, Json("{}")), HttpStatusCode.Created);

        var eventId = created.GetProperty("eventId").GetString()!;
        Assert.Equal($"/api/events/{eventId}/live", created.GetProperty("organizerUrl").GetString());
        Assert.Equal($"/api/events/{eventId}/public", created.GetProperty("publicUrl").GetString());
        var runtime = factory.Registry.Get(eventId)!;
        Assert.True(runtime.Settings.IsSimulated);
        Assert.Equal(30, runtime.Participants.Count);
        Assert.Equal(3, runtime.Course.Laps);
        Assert.Equal([3_000, 8_000], runtime.Course.Checkpoints.Select(c => c.LapDistanceM));

        var participantId = runtime.Participants.Keys.Order(StringComparer.Ordinal).First();
        var profile = SimulationService.DefaultProfiles(30, Sport.Cycling).Single(p => p.Id == participantId);
        var fix = VirtualRider.Generate(runtime.Course, Sport.Cycling, profile, runtime.Settings.StartTime,
            TimeSpan.FromSeconds(5)).First().Fix;
        using var phone = factory.CreateClient(Simulation(factory).TokenOf(eventId, participantId));
        var response = await phone.PostAsJsonAsync($"/api/events/{eventId}/positions", FixBody(fix));

        var result = await ReadJson(response, HttpStatusCode.Accepted);
        Assert.Equal(1, result.GetProperty("accepted").GetInt32());
        Assert.Equal(ParticipantState.Live, Row(runtime, participantId).State);
    }

    [Fact]
    public async Task List_returns_only_simulated_events()
    {
        using var factory = new ApiFactory();
        var started = Simulation(factory).Start(new DemoRequest(Participants: 5, TimeScale: 50));
        Simulation(factory).Step(started.EventId, TimeSpan.FromMinutes(2));
        using var organizer = OrganizerClient(factory);

        var list = await ReadJson(await organizer.GetAsync(DemoUrl));

        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(started.EventId, item.GetProperty("eventId").GetString());
        Assert.Equal(5, item.GetProperty("participants").GetInt32());
        Assert.Equal(50, item.GetProperty("timeScale").GetDouble());
        Assert.Equal(ApiFactory.T0, item.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("00:02:00", item.GetProperty("simulatedElapsed").GetString());
        Assert.NotNull(factory.Registry.Get(ApiFactory.EventId)); // the real event exists but is not listed
    }

    [Fact]
    public void After_stepping_60_minutes_ranks_are_1_to_N_without_gaps_or_duplicates()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest()).EventId;

        StepRange(sim, eventId, TimeSpan.Zero, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(1));

        var view = factory.Registry.Get(eventId)!.BuildLiveView();
        Assert.Equal(Enumerable.Range(1, 30), view.Participants.Select(p => p.Rank ?? 0).Order());
        Assert.All(view.Participants, p => Assert.True(p.TotalDistanceM > 20_000, $"{p.ParticipantId} at {p.TotalDistanceM:F0} m"));
    }

    [Fact]
    public void Exactly_one_participant_is_in_Sos_and_has_a_Sos_alarm()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest()).EventId;
        var runtime = factory.Registry.Get(eventId)!;

        StepRange(sim, eventId, TimeSpan.Zero, TimeSpan.FromMinutes(39), TimeSpan.FromMinutes(1));
        Assert.DoesNotContain(runtime.BuildLiveView().Participants, p => p.State == ParticipantState.Sos);
        StepRange(sim, eventId, TimeSpan.FromMinutes(39), TimeSpan.FromMinutes(41), TimeSpan.FromMinutes(1));

        var view = runtime.BuildLiveView();
        var sos = Assert.Single(view.Participants, p => p.State == ParticipantState.Sos);
        Assert.Contains(sos.Alarms, a => a.Kind == AlarmKind.Sos && a.Since == ApiFactory.T0.AddMinutes(40));
        Assert.Single(view.Participants, p => p.Alarms.Any(a => a.Kind == AlarmKind.Sos));
    }

    [Fact]
    public async Task Sos_rider_is_sos_for_the_organizer_and_live_for_spectators()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest()).EventId;
        var sosId = SimulationService.DefaultProfiles(30, Sport.Cycling).Single(p => p.SosAt is not null).Id;
        StepRange(sim, eventId, TimeSpan.Zero, TimeSpan.FromMinutes(41), TimeSpan.FromMinutes(1));
        await factory.Services.GetRequiredService<LiveBroadcaster>().BroadcastAsync();
        sim.Step(eventId, TimeSpan.FromMinutes(42)); // past the 30 s spectator delay in simulated time

        using var organizer = OrganizerClient(factory);
        var live = await ReadJson(await organizer.GetAsync($"/api/events/{eventId}/live"));
        using var anonymous = factory.CreateClient();
        var spectator = await ReadJson(await anonymous.GetAsync($"/api/events/{eventId}/public"));

        static JsonElement RowOf(JsonElement view, string id) =>
            view.GetProperty("participants").EnumerateArray().Single(r => r.GetProperty("participantId").GetString() == id);
        Assert.Equal("sos", RowOf(live, sosId).GetProperty("state").GetString());
        Assert.Equal(30, spectator.GetProperty("participants").GetArrayLength());
        Assert.Equal("live", RowOf(spectator, sosId).GetProperty("state").GetString());
    }

    [Fact]
    public void The_off_course_participant_was_OffCourse_at_some_step_and_is_not_OffCourse_at_minute_40()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest()).EventId;
        var runtime = factory.Registry.Get(eventId)!;
        var offId = Assert.Single(SimulationService.DefaultProfiles(30, Sport.Cycling), p => p.OffCourseAt is not null).Id;

        StepRange(sim, eventId, TimeSpan.Zero, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(1));
        var states = new List<ParticipantState>();
        StepRange(sim, eventId, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(28), TimeSpan.FromSeconds(15),
            _ => states.Add(Row(runtime, offId).State));
        StepRange(sim, eventId, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(1));

        Assert.Contains(ParticipantState.OffCourse, states);
        var final = Row(runtime, offId);
        Assert.NotEqual(ParticipantState.OffCourse, final.State);
        Assert.DoesNotContain(final.Alarms, a => a.Kind == AlarmKind.OffCourse);
    }

    [Fact]
    public void Dropout_participants_were_Offline_during_the_dropout_and_Live_after_delivery_of_queued_fixes_and_their_final_distance_matches_a_participant_without_dropout_with_the_same_profile_within_50_m()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var dropout = (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(34));
        var eventId = sim.StartWithProfiles(new DemoRequest(Participants: 2),
            [Profile("drop", [dropout]), Profile("steady", [])]).EventId;
        var runtime = factory.Registry.Get(eventId)!;

        var during = new List<ParticipantState>();
        var after = new List<ParticipantState>();
        StepRange(sim, eventId, TimeSpan.Zero, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(30), t =>
        {
            if (t > TimeSpan.FromMinutes(32) && t < TimeSpan.FromMinutes(34)) during.Add(Row(runtime, "drop").State);
            else if (t >= TimeSpan.FromMinutes(34) && t <= TimeSpan.FromMinutes(36)) after.Add(Row(runtime, "drop").State);
        });

        Assert.NotEmpty(during);
        Assert.All(during, s => Assert.Equal(ParticipantState.Offline, s));
        Assert.All(after, s => Assert.Equal(ParticipantState.Live, s));
        var drop = Row(runtime, "drop");
        var steady = Row(runtime, "steady");
        Assert.Equal(ParticipantState.Live, drop.State);
        Assert.True(steady.TotalDistanceM > 20_000);
        Assert.InRange(drop.TotalDistanceM - steady.TotalDistanceM, -50, 50);
    }

    [Fact]
    public void Default_profiles_have_one_sos_one_off_course_and_two_dropouts()
    {
        var profiles = SimulationService.DefaultProfiles(30, Sport.Cycling);

        Assert.Equal(30, profiles.Select(p => p.Id).Distinct().Count());
        Assert.Equal(TimeSpan.FromMinutes(40), Assert.Single(profiles, p => p.SosAt is not null).SosAt);
        var off = Assert.Single(profiles, p => p.OffCourseAt is not null);
        Assert.Equal((TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(120)), (off.OffCourseAt!.Value, off.OffCourseFor!.Value));
        var drops = profiles.Where(p => p.Dropouts.Count > 0).ToList();
        Assert.Equal(2, drops.Count);
        Assert.All(drops, p => Assert.Equal([(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(34))], p.Dropouts));
        Assert.Equal(6, profiles.Min(p => p.BaseSpeedMps), 6);
        Assert.Equal(10, profiles.Max(p => p.BaseSpeedMps), 6);
        Assert.All(profiles, p => Assert.Equal((0.05, 5.0), (p.SpeedJitter, p.GpsNoiseM)));
        Assert.Equal(4.5, SimulationService.DefaultProfiles(30, Sport.Running).Max(p => p.BaseSpeedMps), 6);
    }

    [Fact]
    public async Task Stop_removes_event_and_revokes_tokens()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest(Participants: 3)).EventId;
        var runtime = factory.Registry.Get(eventId)!;
        var token = sim.TokenOf(eventId, "sim-001");
        var p = runtime.Course.PointAt(10);
        using var organizer = OrganizerClient(factory);

        var deleted = await organizer.DeleteAsync($"{DemoUrl}/{eventId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(factory.Registry.Get(eventId));
        Assert.Null(factory.Tokens.Resolve(token));
        using var phone = factory.CreateClient(token);
        var ingest = await phone.PostAsJsonAsync($"/api/events/{eventId}/positions",
            FixBody(new PositionFix(runtime.Settings.StartTime, p.Lat, p.Lon, 5)));
        Assert.Equal(HttpStatusCode.Unauthorized, ingest.StatusCode);
        Assert.Equal(0, (await ReadJson(await organizer.GetAsync(DemoUrl))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await organizer.DeleteAsync($"{DemoUrl}/{eventId}")).StatusCode);
        Assert.Throws<KeyNotFoundException>(() => sim.Step(eventId, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Real_event_cannot_be_deleted_through_the_demo_endpoint()
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);

        var response = await organizer.DeleteAsync($"{DemoUrl}/{ApiFactory.EventId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(factory.Registry.Get(ApiFactory.EventId));
        Assert.NotNull(factory.Tokens.Resolve(factory.Token1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Demo_endpoints_require_the_organizer_key(string? key)
    {
        using var factory = new ApiFactory();
        var eventId = Simulation(factory).Start(new DemoRequest(Participants: 2)).EventId;
        using var client = key is null ? factory.CreateClient() : OrganizerClient(factory, key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(DemoUrl, Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(DemoUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{DemoUrl}/{eventId}")).StatusCode);
        Assert.NotNull(factory.Registry.Get(eventId));
        Assert.Single(factory.Registry.All, r => r.Settings.IsSimulated);
    }

    [Fact]
    public async Task Public_view_never_contains_battery_or_alarms_for_simulated_events()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest()).EventId;
        var broadcaster = factory.Services.GetRequiredService<LiveBroadcaster>();
        using var anonymous = factory.CreateClient();
        using var organizer = OrganizerClient(factory);
        var checkedViews = 0;

        // Minutes 26 (off course), 33 (dropout), 41 (SOS) and 59 (low battery late in the race).
        foreach (var minute in new[] { 26, 33, 41, 59 })
        {
            StepRange(sim, eventId, TimeSpan.FromMinutes(minute - 1), TimeSpan.FromMinutes(minute), TimeSpan.FromSeconds(30));
            await broadcaster.BroadcastAsync();
            sim.Step(eventId, TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(31));

            var live = await ReadJson(await organizer.GetAsync($"/api/events/{eventId}/live"));
            Assert.Contains(live.GetProperty("participants").EnumerateArray(), r => r.GetProperty("batteryPct").ValueKind == JsonValueKind.Number);
            Assert.Contains(live.GetProperty("participants").EnumerateArray(), r => r.GetProperty("alarms").GetArrayLength() > 0);

            var response = await anonymous.GetAsync($"/api/events/{eventId}/public");
            var json = await response.Content.ReadAsStringAsync();
            var view = await ReadJson(response);
            Assert.Equal(30, view.GetProperty("participants").GetArrayLength());
            var names = PropertyNames(view).ToList();
            foreach (var forbidden in OrganizerOnly)
                Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("\"sos\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"offCourse\"", json, StringComparison.OrdinalIgnoreCase);
            checkedViews++;
        }
        Assert.Equal(4, checkedViews);
    }

    [Fact]
    public async Task Invalid_gpx_returns_400()
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);
        var notGpx = Convert.ToBase64String(Encoding.UTF8.GetBytes("<gpx><trk>not closed"));

        var invalidXml = await organizer.PostAsJsonAsync(DemoUrl, new { gpxBase64 = notGpx });
        var notBase64 = await organizer.PostAsJsonAsync(DemoUrl, new { gpxBase64 = "***" });

        var problem = await ReadJson(invalidXml, HttpStatusCode.BadRequest);
        Assert.Contains("GPX", problem.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, notBase64.StatusCode);
        Assert.DoesNotContain(factory.Registry.All, r => r.Settings.IsSimulated);
    }

    [Fact]
    public async Task Gpx_course_is_used_and_an_open_course_with_several_laps_returns_400()
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);
        static string Gpx(IEnumerable<GeoPoint> points) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "<gpx><trk><trkseg>"
            + string.Concat(points.Select(p => string.Create(CultureInfo.InvariantCulture, $"<trkpt lat=\"{p.Lat}\" lon=\"{p.Lon}\"/>")))
            + "</trkseg></trk></gpx>"));
        var loop = DemoCourses.Loop(5_000);
        var open = DemoCourses.Loop(5_000).Take(loop.Count / 2);

        var openResponse = await organizer.PostAsJsonAsync(DemoUrl, new { laps = 2, gpxBase64 = Gpx(open) });
        var loopResponse = await organizer.PostAsJsonAsync(DemoUrl, new { participants = 4, laps = 2, gpxBase64 = Gpx(loop) });

        var problem = await ReadJson(openResponse, HttpStatusCode.BadRequest);
        Assert.Contains("closed", problem.GetProperty("detail").GetString());
        var created = await ReadJson(loopResponse, HttpStatusCode.Created);
        var runtime = factory.Registry.Get(created.GetProperty("eventId").GetString()!)!;
        Assert.InRange(runtime.Course.LapLengthM, 4_900, 5_000);
        Assert.Equal(2, runtime.Course.Laps);
        Assert.Equal([3_000], runtime.Course.Checkpoints.Select(c => c.LapDistanceM));
    }

    [Theory]
    [InlineData("""{"participants":0}""")]
    [InlineData("""{"participants":101}""")]
    [InlineData("""{"timeScale":0}""")]
    [InlineData("""{"timeScale":201}""")]
    [InlineData("""{"laps":0}""")]
    [InlineData("""{"laps":11}""")]
    [InlineData("""{"sport":"swimming"}""")]
    [InlineData("""not json""")]
    public async Task Out_of_range_or_malformed_request_returns_400(string body)
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);

        var response = await organizer.PostAsync(DemoUrl, Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Registry.All, r => r.Settings.IsSimulated);
    }

    [Fact]
    public async Task Oversized_demo_body_returns_413()
    {
        using var factory = new ApiFactory();
        using var organizer = OrganizerClient(factory);
        var body = "{\"gpxBase64\":\"" + new string('A', 3 * 1024 * 1024) + "\"}";

        var response = await organizer.PostAsync(DemoUrl, Json(body));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public void Scaled_clock_runs_scale_times_faster_until_set_manually()
    {
        var baseClock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ApiFactory.T0.AddHours(5));
        var clock = new ScaledTimeProvider(baseClock, ApiFactory.T0, 20);

        baseClock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Elapsed);
        Assert.Equal(ApiFactory.T0.AddMinutes(1), clock.GetUtcNow());

        clock.SetSimulatedElapsed(TimeSpan.FromMinutes(7));
        baseClock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ApiFactory.T0.AddMinutes(7), clock.GetUtcNow());
    }

    [Fact]
    public void Background_loop_advances_simulations_from_their_scaled_clock()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest(Participants: 4, TimeScale: 20)).EventId;
        var runtime = factory.Registry.Get(eventId)!;

        factory.Clock.Advance(TimeSpan.FromSeconds(30)); // 10 simulated minutes; also fires the periodic timer
        sim.AdvanceAll(); // deterministic: whatever the timer already did, the state is now at 10 minutes

        var view = runtime.BuildLiveView();
        Assert.Equal(ApiFactory.T0.AddMinutes(10), view.At);
        Assert.All(view.Participants, p => Assert.Equal(ParticipantState.Live, p.State));
        Assert.All(view.Participants, p => Assert.InRange(p.TotalDistanceM, 3_000, 6_500));
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(sim.List()).SimulatedElapsed);
    }

    [Fact]
    public void Background_loop_leaves_a_stepped_simulation_alone()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var eventId = sim.Start(new DemoRequest(Participants: 2, TimeScale: 20)).EventId;

        sim.Step(eventId, TimeSpan.FromMinutes(1));
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        sim.AdvanceAll();

        Assert.Equal(ApiFactory.T0.AddMinutes(1), factory.Registry.Get(eventId)!.BuildLiveView().At);
    }

    [Fact]
    public async Task More_than_3_simulated_events_return_409()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        for (var i = 0; i < 3; i++) sim.Start(new DemoRequest(Participants: 2));
        using var organizer = OrganizerClient(factory);

        var response = await organizer.PostAsync(DemoUrl, Json("""{"participants":2}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(3, factory.Registry.All.Count(r => r.Settings.IsSimulated));
    }

    /// <summary>
    /// 100 riders 65 minutes into the default 3-lap demo (the fastest have finished, the slowest are in lap 2).
    /// "Before" is the same projection without rounding. Rounding alone saves about 16 %: segment results
    /// (about 60 % of the bytes) keep their shape, which Plan 5 (on-demand segments) is to change.
    /// </summary>
    [Fact]
    public void Rounded_spectator_payload_for_100_riders_is_smaller_and_keeps_order_and_states()
    {
        using var factory = new ApiFactory();
        var sim = Simulation(factory);
        var started = sim.Start(new DemoRequest(Participants: 100, TimeScale: 50, Laps: 3));
        StepRange(sim, started.EventId, TimeSpan.Zero, TimeSpan.FromMinutes(65), TimeSpan.FromMinutes(5));
        var runtime = factory.Registry.Get(started.EventId)!;
        var json = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;

        var view = runtime.BuildLiveView();
        var spectator = SpectatorProjector.Project(view, runtime.Participants);
        var unrounded = new SpectatorView(view.At, view.EventId, view.Participants.Zip(spectator.Participants)
            .Select(x => x.Second with
            {
                TotalDistanceM = x.First.TotalDistanceM, SpeedMps = x.First.SpeedMps, PaceSecPerKm = x.First.PaceSecPerKm,
                Lat = x.Second.Lat is null ? null : x.First.State == ParticipantState.Finished ? x.First.FinishLat : x.First.Lat,
                Lon = x.Second.Lon is null ? null : x.First.State == ParticipantState.Finished ? x.First.FinishLon : x.First.Lon,
                GapToLeader = x.First.GapToLeader, GapToAhead = x.First.GapToAhead, Eta = x.First.Eta, Segments = x.First.Segments,
            }).ToList());
        var before = JsonSerializer.SerializeToUtf8Bytes(unrounded, json).Length;
        var after = JsonSerializer.SerializeToUtf8Bytes(spectator, json).Length;

        Assert.True(after <= before * 0.86, $"{before} -> {after} bytes");
        Assert.True(after < 90 * 1024, $"{after} bytes");
        Assert.Contains(view.Participants, p => p.State == ParticipantState.Finished);
        Assert.Contains(view.Participants, p => p.Lap == 2);
        Assert.Equal(view.Participants.Select(p => (p.ParticipantId, p.Rank)), spectator.Participants.Select(p => (p.ParticipantId, p.Rank)));
        foreach (var (o, p) in view.Participants.Zip(spectator.Participants))
        {
            Assert.Equal(o.State is ParticipantState.Sos or ParticipantState.Stopped or ParticipantState.OffCourse ? ParticipantState.Live : o.State, p.State);
            Assert.True(Math.Abs(o.TotalDistanceM - p.TotalDistanceM) < 0.1, $"{o.TotalDistanceM} -> {p.TotalDistanceM}");
            Assert.Equal(o.State == ParticipantState.Finished, p.TotalDistanceM >= Math.Round(runtime.Course.TotalLengthM, 1));
            if (p.Lat is { } lat) Assert.Equal(Math.Round(lat, 5), lat);
            if (p.GapToLeader is { } gap) Assert.Equal(0, gap.Ticks % TimeSpan.TicksPerSecond);
            if (p.Eta is { } eta) Assert.Equal(0, eta.Expected.Ticks % TimeSpan.TicksPerSecond);
            Assert.All(p.Segments, seg => Assert.Equal(0, seg.Time.Ticks % TimeSpan.TicksPerSecond));
        }
        // Rounded distances keep the order of the rows that are still riding.
        var riding = spectator.Participants.Where(p => p.State != ParticipantState.Finished && p.Rank is not null).ToList();
        Assert.True(riding.Zip(riding.Skip(1)).All(x => x.First.TotalDistanceM >= x.Second.TotalDistanceM));
    }
}
