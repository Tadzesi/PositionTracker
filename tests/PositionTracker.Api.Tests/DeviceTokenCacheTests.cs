using Microsoft.Extensions.Time.Testing;
using PositionTracker.Api.Auth;

namespace PositionTracker.Api.Tests;

/// <summary>The device token cache in front of the database lookup: hits, expiry and revocations that act at once.</summary>
public sealed class DeviceTokenCacheTests
{
    private const string Event = "0195f3a0-0000-7000-8000-000000000001";
    private const string Rider = "0195f3a0-0000-7000-8000-0000000000aa";
    private const string Other = "0195f3a0-0000-7000-8000-0000000000bb";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_resolved_token_is_served_from_memory_until_its_lifetime_ends()
    {
        var cache = new DeviceTokenCache(_clock);
        cache.Remember("h1", Event, Rider, _clock.GetUtcNow());

        Assert.Equal((Event, Rider), cache.Get("h1"));
        _clock.Advance(DeviceTokenCache.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(cache.Get("h1"));
    }

    [Fact]
    public void A_revocation_drops_the_participants_tokens_at_once_and_only_theirs()
    {
        var cache = new DeviceTokenCache(_clock);
        cache.Remember("h1", Event, Rider, _clock.GetUtcNow());
        cache.Remember("h2", Event, Other, _clock.GetUtcNow());

        cache.Forget(Event, Rider);

        Assert.Null(cache.Get("h1"));
        Assert.Equal((Event, Other), cache.Get("h2"));
    }

    [Fact]
    public void A_lookup_that_read_the_database_around_a_revocation_is_not_trusted()
    {
        // The revocation commits with the caller's transaction; a lookup started before it, or just after it, may have
        // read the old row. Neither result is kept, so the next request asks the database again.
        var cache = new DeviceTokenCache(_clock);
        var before = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMilliseconds(5));
        cache.Forget(Event, Rider);
        cache.Remember("h1", Event, Rider, before);
        Assert.Null(cache.Get("h1"));

        _clock.Advance(TimeSpan.FromSeconds(10));
        cache.Remember("h1", Event, Rider, _clock.GetUtcNow());
        Assert.Null(cache.Get("h1"));

        _clock.Advance(DeviceTokenCache.Grace);
        cache.Remember("h1", Event, Rider, _clock.GetUtcNow());
        Assert.Equal((Event, Rider), cache.Get("h1"));
    }

    [Fact]
    public void Deleting_an_event_forgets_all_its_tokens()
    {
        var cache = new DeviceTokenCache(_clock);
        cache.Remember("h1", Event, Rider, _clock.GetUtcNow());
        cache.Remember("h2", "0195f3a0-0000-7000-8000-000000000002", Rider, _clock.GetUtcNow());

        cache.ForgetEvent(Event);

        Assert.Null(cache.Get("h1"));
        Assert.NotNull(cache.Get("h2"));
    }
}
