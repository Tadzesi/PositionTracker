namespace PositionTracker.Api.Events;

/// <summary>
/// Test seam (not registered in production): <see cref="BeforeSave"/> runs in course, category and wave updates
/// after the event was read and before the write, with the event id, so a test can change the event in between.
/// </summary>
public sealed class EventServiceHooks
{
    public Func<Guid, Task>? BeforeSave { get; set; }

    /// <summary>Runs at the start of every attempt of a state change (attempt 1, 2, ...).</summary>
    public Func<Guid, int, Task>? BeforeStateAttempt { get; set; }
}
