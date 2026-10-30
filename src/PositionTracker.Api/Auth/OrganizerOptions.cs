namespace PositionTracker.Api.Auth;

/// <summary>Configuration section <c>Organizer</c>; <see cref="ApiKey"/> is required (validated at startup).</summary>
public sealed class OrganizerOptions
{
    public const string Section = "Organizer";

    public string ApiKey { get; set; } = "";
}
