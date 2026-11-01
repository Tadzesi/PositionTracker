using PositionTracker.Api.Endpoints;
using PositionTracker.Core;

namespace PositionTracker.Api.Tests;

public class StatusPersistThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static DeviceStatus Status(double battery = 80, bool? charging = false, int queue = 0, bool foreground = true) =>
        new(battery, charging, queue, foreground);

    [Fact]
    public void The_first_status_is_stored_and_an_unchanged_one_within_a_minute_is_not()
    {
        var throttle = new StatusPersistThrottle();
        Assert.True(throttle.ShouldStore("p", Status(), T0));
        Assert.False(throttle.ShouldStore("p", Status(), T0.AddSeconds(2)));
        Assert.False(throttle.ShouldStore("p", Status(battery: 80.4), T0.AddSeconds(30)));
    }

    [Fact]
    public void A_change_of_battery_charging_foreground_or_queue_is_stored_at_once()
    {
        var throttle = new StatusPersistThrottle();
        throttle.ShouldStore("p", Status(), T0);
        Assert.True(throttle.ShouldStore("p", Status(battery: 79), T0.AddSeconds(2)));
        Assert.True(throttle.ShouldStore("p", Status(battery: 79, charging: true), T0.AddSeconds(4)));
        Assert.True(throttle.ShouldStore("p", Status(battery: 79, charging: true, foreground: false), T0.AddSeconds(6)));
        Assert.True(throttle.ShouldStore("p", Status(battery: 79, charging: true, foreground: false, queue: 3), T0.AddSeconds(8)));
    }

    [Fact]
    public void An_unchanged_status_is_stored_again_after_a_minute_and_participants_do_not_share_a_budget()
    {
        var throttle = new StatusPersistThrottle();
        throttle.ShouldStore("a", Status(), T0);
        Assert.True(throttle.ShouldStore("b", Status(), T0.AddSeconds(1)));
        Assert.False(throttle.ShouldStore("a", Status(), T0.AddSeconds(59)));
        Assert.True(throttle.ShouldStore("a", Status(), T0.AddSeconds(61)));
    }
}
