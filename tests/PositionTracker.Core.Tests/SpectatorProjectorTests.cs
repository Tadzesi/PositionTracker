using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Tests.TestData;

namespace PositionTracker.Core.Tests;

public class SpectatorProjectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static ParticipantInfo Info(string id, string bib, string name, string? nickname = null,
        bool showName = true, bool minor = false) =>
        new(id, bib, name, nickname, "Open", showName, minor);

    /// <summary>A fully populated organizer row, including every organizer-only field.</summary>
    private static ParticipantView Row(string id, string bib, string name) => new(
        id, bib, name, "Open", 1, 0,
        1_234, 1_234, 48.1, 17.1,
        8.5, 8.0, 117.6, 120, 80, 3.5,
        ParticipantState.Live, TimeSpan.Zero, null,
        new EtaEstimate(T0.AddHours(1), T0.AddMinutes(50), T0.AddMinutes(70), false),
        [new SegmentResult(0, 0, TimeSpan.FromMinutes(5), SegmentColour.Purple)],
        17, 6.5, 34, TimeSpan.FromSeconds(2),
        [new Alarm(AlarmKind.LowBattery, T0), new Alarm(AlarmKind.Sos, T0)]);

    private static LiveView View(params ParticipantView[] rows) => new(T0, "evt-1", rows, rows.Length, 0, 0, 0);

    private static string DisplayNameOf(ParticipantInfo info)
    {
        var view = View(Row(info.Id, info.Bib, info.Name));
        var participants = new Dictionary<string, ParticipantInfo> { [info.Id] = info };
        return Assert.Single(SpectatorProjector.Project(view, participants).Participants).DisplayName;
    }

    [Fact]
    public void Name_is_hidden_when_participant_did_not_consent()
    {
        Assert.Equal("#21", DisplayNameOf(Info("a", "21", "Jana Novakova", "Jani", showName: false)));
    }

    [Fact]
    public void Name_and_nickname_are_always_hidden_for_minors()
    {
        Assert.Equal("#7", DisplayNameOf(Info("a", "7", "Peter Maly", "Petko", showName: true, minor: true)));
        Assert.Equal("#7", DisplayNameOf(Info("a", "7", "Peter Maly", null, showName: true, minor: true)));
    }

    [Fact]
    public void Nickname_is_preferred_over_full_name()
    {
        Assert.Equal("Speedy", DisplayNameOf(Info("a", "3", "Martin Horny", "Speedy")));
    }

    [Fact]
    public void Full_name_is_shown_when_allowed_and_no_nickname_exists()
    {
        Assert.Equal("Martin Horny", DisplayNameOf(Info("a", "3", "Martin Horny")));
        Assert.Equal("Martin Horny", DisplayNameOf(Info("a", "3", "Martin Horny", " ")));
    }

    [Fact]
    public void Participant_missing_from_the_info_map_is_shown_by_bib_only()
    {
        var projected = SpectatorProjector.Project(View(Row("a", "9", "Secret Name")),
            new Dictionary<string, ParticipantInfo>());
        Assert.Equal("#9", Assert.Single(projected.Participants).DisplayName);
    }

    [Fact]
    public void Projection_keeps_public_race_fields_and_order()
    {
        var first = Row("a", "1", "A");
        var second = Row("b", "2", "B") with { Rank = 2, GapToLeader = TimeSpan.FromSeconds(42) };
        var participants = new Dictionary<string, ParticipantInfo> { ["a"] = Info("a", "1", "A"), ["b"] = Info("b", "2", "B") };

        var projected = SpectatorProjector.Project(View(first, second), participants);

        Assert.Equal(T0, projected.At);
        Assert.Equal("evt-1", projected.EventId);
        Assert.Equal(["a", "b"], projected.Participants.Select(p => p.ParticipantId));
        var p = projected.Participants[1];
        Assert.Equal(new SpectatorParticipantView("b", "2", "B", "Open", 2, 0, 1_234, 48.1, 17.1, 8.5, 117.6,
            ParticipantState.Live, TimeSpan.FromSeconds(42), null, second.Eta, second.Segments), p);
    }

    [Theory]
    [InlineData(ParticipantState.Sos, ParticipantState.Live)]
    [InlineData(ParticipantState.Stopped, ParticipantState.Live)]
    [InlineData(ParticipantState.OffCourse, ParticipantState.Live)]
    [InlineData(ParticipantState.NotStarted, ParticipantState.NotStarted)]
    [InlineData(ParticipantState.Live, ParticipantState.Live)]
    [InlineData(ParticipantState.Delayed, ParticipantState.Delayed)]
    [InlineData(ParticipantState.Offline, ParticipantState.Offline)]
    [InlineData(ParticipantState.Finished, ParticipantState.Finished)]
    [InlineData(ParticipantState.Dnf, ParticipantState.Dnf)]
    public void Alarm_states_are_shown_to_spectators_as_live(ParticipantState organizer, ParticipantState spectator)
    {
        var view = View(Row("a", "1", "A") with { State = organizer });
        var projected = SpectatorProjector.Project(view, new Dictionary<string, ParticipantInfo> { ["a"] = Info("a", "1", "A") });

        Assert.Equal(spectator, Assert.Single(projected.Participants).State);
        Assert.Equal(organizer, view.Participants[0].State);
    }

    [Fact]
    public void Serialized_projection_contains_no_organizer_only_fields()
    {
        var view = View(Row("a", "1", "A"));
        Assert.True(view.Participants[0] is { BatteryPct: 17, QueueLength: 34, Alarms.Count: 2 });

        var projected = SpectatorProjector.Project(view, new Dictionary<string, ParticipantInfo> { ["a"] = Info("a", "1", "A") });
        var json = JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(json);
        var names = PropertyNames(doc.RootElement).ToList();
        Assert.Contains("displayName", names);
        foreach (var forbidden in new[] { "battery", "gpsAccuracy", "queue", "alarms", "ascent", "descent", "grade" })
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runtime_participants_feed_the_projection()
    {
        var clock = new FakeTimeProvider(T0);
        var settings = new EventSettings("evt-1", "Ride", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false,
            TrackerOptions.Defaults(Sport.Cycling));
        var runtime = new EventRuntime(settings, Course.Create(TestCourses.Loop(), 1), clock);
        runtime.AddParticipant(Info("a", "11", "Adult Rider", "Rocket"));
        runtime.AddParticipant(Info("b", "12", "Young Rider", "Kid", minor: true));

        Assert.Equal(["a", "b"], runtime.Participants.Keys.Order());
        var projected = SpectatorProjector.Project(runtime.BuildLiveView(), runtime.Participants);

        Assert.Equal("Rocket", projected.Participants.Single(p => p.ParticipantId == "a").DisplayName);
        Assert.Equal("#12", projected.Participants.Single(p => p.ParticipantId == "b").DisplayName);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };
}
