namespace PositionTracker.Core.Tests.TestData;

/// <summary>A settable clock that, unlike <c>FakeTimeProvider</c>, may also be set back in time.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public void SetUtcNow(DateTimeOffset value) => Now = value;

    public override DateTimeOffset GetUtcNow() => Now;
}
