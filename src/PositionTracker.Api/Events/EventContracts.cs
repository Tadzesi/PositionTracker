using System.Text.Json.Serialization;
using PositionTracker.Api.Endpoints;
using PositionTracker.Core;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Events;

// Requests: strings for values whose format is validated by hand (so the error names the field).

public sealed record CreateEventRequest(string? Name, string? Location, string? StartTime, string? Sport);

public sealed record CheckpointRequest(string? Name, double? LapDistanceM);

public sealed record CourseRequest(string? GpxBase64, int? Laps, List<CheckpointRequest?>? Checkpoints, int? SpectatorDelaySeconds);

public sealed record CategoryRequest(string? Name, string? Gender, int? MinAge, int? MaxAge);

public sealed record WaveRequest(string? Name, string? StartTime);

public sealed record StateRequest(string? State);

// Responses.

public sealed record CreatedEvent(string EventId);

public sealed record EventSummary(
    string EventId, string Name, string Location,
    [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset StartTime,
    Sport Sport, EventState State);

/// <param name="EmailMode"><c>simulated</c> or <c>smtp</c> (controller ruling P8): whether mails really go out.</param>
public sealed record EventList(string EmailMode, IReadOnlyList<EventSummary> Events);

public sealed record CourseView(
    double LapLengthM, double AscentM, double DescentM, int PointCount, bool Closed,
    [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset UploadedAt,
    IReadOnlyList<Checkpoint> Checkpoints);

public sealed record CategoryView(string Id, string Name, string? Gender, int? MinAge, int? MaxAge);

public sealed record WaveView(string Id, string Name, [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset StartTime);

public sealed record ParticipantCounts(int Invited, int Confirmed, int Declined, int EmailFailed);

/// <param name="Running">Whether the event's runtime is registered in this process (live or finished).</param>
/// <param name="EmailMode">See <see cref="EventList.EmailMode"/>.</param>
public sealed record EventManageView(
    string EventId, string Name, string Location,
    [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset StartTime,
    Sport Sport, EventState State, int Laps, int SpectatorDelaySeconds, CourseView? Course,
    IReadOnlyList<CategoryView> Categories, IReadOnlyList<WaveView> Waves, ParticipantCounts Participants,
    bool Running, string EmailMode,
    [property: JsonConverter(typeof(UtcTimestampConverter))] DateTimeOffset CreatedAt,
    [property: JsonConverter(typeof(NullableUtcTimestampConverter))] DateTimeOffset? EndedAt);

/// <summary>A value or the error that prevented it.</summary>
public readonly record struct EventResult<T>(T? Value, EventError? Error)
{
    public static implicit operator EventResult<T>(EventError error) => new(default, error);
}

/// <summary><see cref="UtcTimestampConverter"/> for nullable values (null stays null).</summary>
public sealed class NullableUtcTimestampConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly UtcTimestampConverter Inner = new();

    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(DateTimeOffset), options);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTimeOffset? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is { } v) Inner.Write(writer, v, options);
        else writer.WriteNullValue();
    }
}
