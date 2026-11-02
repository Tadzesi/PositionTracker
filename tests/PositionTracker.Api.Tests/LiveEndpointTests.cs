using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Live;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Tests that move the fake clock or depend on the broadcaster's state create their own <see cref="ApiFactory"/>;
/// only stateless authorization checks share the class fixture.
/// </summary>
public class LiveEndpointTests(ApiFactory shared) : IClassFixture<ApiFactory>
{
    private const string LiveUrl = $"/api/events/{ApiFactory.EventId}/live";
    private const string PublicUrl = $"/api/events/{ApiFactory.EventId}/public";
    private static readonly string[] OrganizerOnly = ["battery", "gpsAccuracy", "queue", "alarms", "ascent", "descent", "grade"];

    private static HttpClient OrganizerClient(ApiFactory factory, string? key = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key ?? factory.OrganizerKey);
        return client;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static DateTimeOffset At(JsonElement view) =>
        DateTimeOffset.Parse(view.GetProperty("at").GetString()!, CultureInfo.InvariantCulture);

    private static HubConnection Connect(ApiFactory factory) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

    /// <summary>Advances the fake clock in 1 s steps (driving the broadcaster) until <paramref name="done"/> completes or 3 s pass.</summary>
    private static async Task<T> AdvanceUntil<T>(ApiFactory factory, Task<T> done)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(3));
        while (!done.IsCompleted && !deadline.IsCompleted)
        {
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(done, Task.Delay(10));
        }
        Assert.True(done.IsCompleted, "No update received within 3 s.");
        return await done;
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    [Fact]
    public async Task Live_without_key_returns_401()
    {
        using var client = shared.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(LiveUrl)).StatusCode);
    }

    [Fact]
    public async Task Live_with_wrong_key_returns_401()
    {
        using var client = OrganizerClient(shared, "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(LiveUrl)).StatusCode);
    }

    [Fact]
    public async Task Public_course_carries_the_elevation_profile_of_one_lap_or_null_without_elevation()
    {
        using var client = shared.CreateClient();
        var course = await ReadJson(await client.GetAsync($"/api/events/{ApiFactory.EventId}/course"));
        Assert.True(course.TryGetProperty("profile", out var profile));
        if (profile.ValueKind == JsonValueKind.Null) return;
        var rows = profile.EnumerateArray().Select(r => (Dist: r[0].GetDouble(), Ele: r[1].GetDouble())).ToList();
        Assert.InRange(rows.Count, 2, 400);
        Assert.Equal(0, rows[0].Dist);
        Assert.Equal(course.GetProperty("lapLengthM").GetDouble(), rows[^1].Dist, 0);
        Assert.Equal(rows.OrderBy(r => r.Dist).Select(r => r.Dist), rows.Select(r => r.Dist));
    }

    [Fact]
    public async Task A_finished_event_stops_being_rebuilt_and_leaves_the_memory_after_the_results_days()
    {
        using var factory = new ApiFactory();
        var broadcaster = factory.Services.GetRequiredService<LiveBroadcaster>();
        await broadcaster.BroadcastAsync();
        factory.Registry.SetFinished(ApiFactory.EventId, true);

        await broadcaster.BroadcastAsync(); // first seen finished
        factory.Clock.Advance(TimeSpan.FromSeconds(31 + 20)); // delay + grace passed: the last pushed view is final
        await broadcaster.BroadcastAsync();
        var feed = factory.Services.GetRequiredService<SpectatorFeed>();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PublicUrl)).StatusCode); // still viewable

        factory.Clock.Advance(TimeSpan.FromDays(8));
        await broadcaster.BroadcastAsync();
        Assert.Null(factory.Registry.Get(ApiFactory.EventId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PublicUrl)).StatusCode);
        Assert.NotNull(feed);
    }

    [Fact]
    public async Task The_organizer_view_of_a_finished_event_stays_at_its_finish_time()
    {
        using var factory = new ApiFactory();
        using var client = OrganizerClient(factory);
        factory.Clock.Advance(TimeSpan.FromMinutes(30)); // the riders have positions by now
        var finishedAt = factory.Clock.GetUtcNow();
        factory.Registry.SetFinished(ApiFactory.EventId, true, finishedAt);
        var atFinish = await ReadJson(await client.GetAsync(LiveUrl));

        factory.Clock.Advance(TimeSpan.FromHours(28));
        var later = await ReadJson(await client.GetAsync(LiveUrl));

        Assert.Equal(finishedAt, At(atFinish));
        Assert.Equal(finishedAt, At(later));
        Assert.Equal(atFinish.GetProperty("participants").ToString(), later.GetProperty("participants").ToString()); // no growing last signal, no late alarms
        Assert.Equal(atFinish.GetProperty("countOffline").GetInt32(), later.GetProperty("countOffline").GetInt32());
    }

    [Fact]
    public async Task Public_course_answers_304_to_a_matching_ETag()
    {
        using var client = shared.CreateClient();
        var first = await client.GetAsync($"/api/events/{ApiFactory.EventId}/course");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!.ToString();
        using var again = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{ApiFactory.EventId}/course");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Live_with_key_returns_200_with_the_full_view()
    {
        using var client = OrganizerClient(shared);
        var view = await ReadJson(await client.GetAsync(LiveUrl));

        Assert.Equal(ApiFactory.EventId, view.GetProperty("eventId").GetString());
        var row = view.GetProperty("participants").EnumerateArray()
            .Single(p => p.GetProperty("participantId").GetString() == ApiFactory.Participant1);
        Assert.Equal("Rider One", row.GetProperty("displayName").GetString());
        Assert.Equal("notStarted", row.GetProperty("state").GetString());
        Assert.True(row.TryGetProperty("queueLength", out _));
    }

    [Fact]
    public async Task Live_and_public_for_unknown_event_return_404()
    {
        using var organizer = OrganizerClient(shared);
        Assert.Equal(HttpStatusCode.NotFound, (await organizer.GetAsync("/api/events/evt-missing/live")).StatusCode);
        using var anonymous = shared.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/events/evt-missing/public")).StatusCode);
    }

    [Fact]
    public async Task Public_is_empty_until_a_view_is_old_enough()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<LiveBroadcaster>().BroadcastAsync();

        var view = await ReadJson(await client.GetAsync(PublicUrl));

        Assert.Equal(ApiFactory.EventId, view.GetProperty("eventId").GetString());
        Assert.Equal(0, view.GetProperty("participants").GetArrayLength());
    }

    [Fact]
    public async Task Public_returns_the_view_from_the_delay_earlier()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<LiveBroadcaster>().BroadcastAsync();

        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        var view = await ReadJson(await client.GetAsync(PublicUrl));

        Assert.Equal(ApiFactory.T0, At(view));
        var ids = view.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("participantId").GetString());
        Assert.Equal([ApiFactory.Participant1, ApiFactory.Participant2], ids.Order());

        using var organizer = OrganizerClient(factory);
        Assert.Equal(ApiFactory.T0.AddSeconds(31), At(await ReadJson(await organizer.GetAsync(LiveUrl))));
    }

    [Fact]
    public async Task Organizer_data_is_not_in_the_public_view()
    {
        using var factory = new ApiFactory();
        var minor = new ParticipantInfo("kid", "21", "Young Rider", "Kiddo", "Junior", true, true);
        var adult = new ParticipantInfo("adult", "22", "Grown Rider", null, "Open", true, false);
        var p = factory.Event.Course.PointAt(10);
        foreach (var info in new[] { minor, adult })
        {
            factory.Event.AddParticipant(info);
            factory.Event.Ingest(info.Id, new PositionBatch([new PositionFix(ApiFactory.T0, p.Lat, p.Lon, 5)],
                new DeviceStatus(17, false, 34, true)));
            factory.Event.Act(info.Id, ParticipantAction.Sos);
        }
        await factory.Services.GetRequiredService<LiveBroadcaster>().BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(31));

        using var organizer = OrganizerClient(factory);
        var live = await ReadJson(await organizer.GetAsync(LiveUrl));
        foreach (var id in new[] { minor.Id, adult.Id })
        {
            var liveRow = live.GetProperty("participants").EnumerateArray().Single(r => r.GetProperty("participantId").GetString() == id);
            Assert.Equal("sos", liveRow.GetProperty("state").GetString());
            Assert.Equal(17, liveRow.GetProperty("batteryPct").GetDouble());
            Assert.Equal(34, liveRow.GetProperty("queueLength").GetInt32());
            Assert.Contains(liveRow.GetProperty("alarms").EnumerateArray(), a => a.GetProperty("kind").GetString() == "sos");
        }

        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(PublicUrl);
        var json = await response.Content.ReadAsStringAsync();
        var view = await ReadJson(response);
        JsonElement Row(string id) =>
            view.GetProperty("participants").EnumerateArray().Single(r => r.GetProperty("participantId").GetString() == id);
        Assert.Equal("#21", Row(minor.Id).GetProperty("displayName").GetString());
        Assert.Equal("Grown Rider", Row(adult.Id).GetProperty("displayName").GetString());
        Assert.Equal("live", Row(minor.Id).GetProperty("state").GetString());
        Assert.Equal("live", Row(adult.Id).GetProperty("state").GetString());
        Assert.DoesNotContain("sos", json, StringComparison.OrdinalIgnoreCase);
        var names = PropertyNames(view).ToList();
        foreach (var forbidden in OrganizerOnly)
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Young Rider", json);
        Assert.DoesNotContain("Kiddo", json);
    }

    [Fact]
    public async Task Public_view_is_projected_once_and_refreshed_when_view_or_participants_change()
    {
        using var factory = new ApiFactory();
        factory.Settings["Live:IntervalMs"] = "3600000"; // keep the background loop out of the count
        var feed = factory.Services.GetRequiredService<SpectatorFeed>();
        var broadcaster = factory.Services.GetRequiredService<LiveBroadcaster>();
        using var client = factory.CreateClient();
        await broadcaster.BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(31));

        var first = await (await client.GetAsync(PublicUrl)).Content.ReadAsStringAsync();
        var second = await (await client.GetAsync(PublicUrl)).Content.ReadAsStringAsync();
        Assert.Equal(first, second);
        Assert.Equal(1, feed.Projections);
        Assert.Same(feed.GetCurrent(factory.Event), feed.GetCurrent(factory.Event));
        Assert.Equal(1, feed.Projections);

        var (added, _) = factory.AddParticipant();
        await client.GetAsync(PublicUrl);
        Assert.Equal(2, feed.Projections);

        await broadcaster.BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        var view = await ReadJson(await client.GetAsync(PublicUrl));
        Assert.Equal(3, feed.Projections);
        Assert.Contains(view.GetProperty("participants").EnumerateArray(), r => r.GetProperty("participantId").GetString() == added);
    }

    [Fact]
    public async Task UpdateParticipant_nickname_is_visible_on_public_at_the_next_tick()
    {
        using var factory = new ApiFactory();
        factory.Settings["Live:IntervalMs"] = "3600000"; // ticks are driven by the test
        var broadcaster = factory.Services.GetRequiredService<LiveBroadcaster>();
        using var client = factory.CreateClient();
        string? NameOf(JsonElement view) => view.GetProperty("participants").EnumerateArray()
            .Single(r => r.GetProperty("participantId").GetString() == ApiFactory.Participant1)
            .GetProperty("displayName").GetString();

        await broadcaster.BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("Rider One", NameOf(await ReadJson(await client.GetAsync(PublicUrl))));

        factory.Event.UpdateParticipant(new ParticipantInfo(ApiFactory.Participant1, "1", "Rider One", "Rocket", "Open", true, false));
        await broadcaster.BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Rocket", NameOf(await ReadJson(await client.GetAsync(PublicUrl))));
    }

    [Fact]
    public async Task Failing_event_does_not_stop_the_broadcast_for_other_events()
    {
        using var factory = new ApiFactory();
        factory.Settings["Logging:LogLevel:PositionTracker.Api.Live.LiveBroadcaster"] = "None";
        var broken = new EventSettings("evt-broken", "Broken", Sport.Cycling, ApiFactory.T0, TimeSpan.FromSeconds(-1),
            false, TrackerOptions.Defaults(Sport.Cycling));
        factory.Registry.Add(new EventRuntime(broken, Course.Create(DemoCourses.Loop(), 1), factory.Clock));

        await factory.Services.GetRequiredService<LiveBroadcaster>().BroadcastAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(31));

        using var client = factory.CreateClient();
        var view = await ReadJson(await client.GetAsync(PublicUrl));
        Assert.Equal(ApiFactory.T0, At(view));
        Assert.Equal(2, view.GetProperty("participants").GetArrayLength());
    }

    [Fact]
    public async Task Re_registered_event_does_not_serve_the_removed_event_views()
    {
        using var factory = new ApiFactory();
        var broadcaster = factory.Services.GetRequiredService<LiveBroadcaster>();
        await broadcaster.BroadcastAsync();
        var old = factory.Event;
        factory.Registry.Remove(ApiFactory.EventId);
        factory.Registry.Add(new EventRuntime(old.Settings, old.Course, factory.Clock));
        factory.Clock.Advance(TimeSpan.FromSeconds(31));

        using var client = factory.CreateClient();
        var view = await ReadJson(await client.GetAsync(PublicUrl));
        Assert.Equal(0, view.GetProperty("participants").GetArrayLength());
    }

    [Fact]
    public async Task Spectator_receives_PublicUpdate_but_no_LiveUpdate()
    {
        using var factory = new ApiFactory();
        factory.Settings["Live:IntervalMs"] = "100";
        await using var connection = Connect(factory);
        var publicUpdate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveUpdates = 0;
        connection.On<JsonElement>("PublicUpdate", v => publicUpdate.TrySetResult(v));
        connection.On<JsonElement>("LiveUpdate", _ => Interlocked.Increment(ref liveUpdates));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinSpectator", ApiFactory.EventId);

        var view = await AdvanceUntil(factory, publicUpdate.Task);

        Assert.Equal(ApiFactory.EventId, view.GetProperty("eventId").GetString());
        Assert.Equal(2, view.GetProperty("participants").GetArrayLength());
        Assert.DoesNotContain(PropertyNames(view), n => OrganizerOnly.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, Volatile.Read(ref liveUpdates));
    }

    [Fact]
    public async Task Organizer_with_key_receives_LiveUpdate()
    {
        using var factory = new ApiFactory();
        factory.Settings["Live:IntervalMs"] = "100";
        await using var connection = Connect(factory);
        var liveUpdate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("LiveUpdate", v => liveUpdate.TrySetResult(v));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinOrganizer", ApiFactory.EventId, factory.OrganizerKey);

        var view = await AdvanceUntil(factory, liveUpdate.Task);

        Assert.Equal(ApiFactory.EventId, view.GetProperty("eventId").GetString());
        Assert.True(view.GetProperty("participants")[0].TryGetProperty("queueLength", out _));
    }

    [Fact]
    public async Task JoinOrganizer_with_wrong_key_throws_HubException()
    {
        await using var connection = Connect(shared);
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinOrganizer", ApiFactory.EventId, "wrong-key"));
        Assert.EndsWith("Invalid key", ex.Message);
        Assert.DoesNotContain(ApiFactory.EventId, ex.Message);
        Assert.DoesNotContain("wrong-key", ex.Message);
    }

    [Fact]
    public async Task JoinSpectator_for_unknown_event_throws_HubException()
    {
        await using var connection = Connect(shared);
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSpectator", "evt-<b>missing</b>"));
        Assert.EndsWith("Unknown event", ex.Message);
        Assert.DoesNotContain("missing", ex.Message);
    }

    [Fact]
    public async Task JoinOrganizer_with_key_for_unknown_event_throws_HubException_without_echoing_the_id()
    {
        await using var connection = Connect(shared);
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("JoinOrganizer", "evt-<b>missing</b>", shared.OrganizerKey));
        Assert.EndsWith("Unknown event", ex.Message);
        Assert.DoesNotContain("missing", ex.Message);
    }
}
