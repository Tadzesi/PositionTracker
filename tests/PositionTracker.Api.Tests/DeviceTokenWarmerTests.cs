using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Persistence;
using PositionTracker.Data.Entities;
using static PositionTracker.Api.Tests.ParticipantLinkTests;

namespace PositionTracker.Api.Tests;

/// <summary>
/// The device token warm-up: a live event's tokens are in the cache before its phones send anything (a cold mass start used to
/// send every request to the database at once). Against PostgreSQL (Testcontainers).
/// </summary>
public sealed class DeviceTokenWarmerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_live_events_tokens_are_cached_before_use_and_a_revoked_one_is_not()
    {
        await using var host = Host();
        var ev = await SeedEventAsync(host, EventState.Live);
        var keep = await RiderTokenAsync(host, ev, "Keep");
        var drop = await RiderTokenAsync(host, ev, "Drop");
        await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ITokenService>().RevokeAsync(ev.ToString("D"), drop.Participant.ToString("D"), default);
        host.Clock.Advance(DeviceTokenCache.Grace + TimeSpan.FromSeconds(1)); // the issue and the revocation are past their grace
        var cache = host.Services.GetRequiredService<DeviceTokenCache>();
        Assert.Null(cache.Get(TokenService.Hash(keep.Token)));

        await host.Services.GetRequiredService<DeviceTokenWarmer>().RunOnceAsync(default); // (the hosted loop may have run already)

        Assert.Equal((ev.ToString("D"), keep.Participant.ToString("D")), cache.Get(TokenService.Hash(keep.Token)));
        Assert.Null(cache.Get(TokenService.Hash(drop.Token)));
    }

    [Fact]
    public async Task Tokens_stay_cached_while_the_event_runs_and_finished_events_are_not_loaded()
    {
        await using var host = Host();
        var live = await SeedEventAsync(host, EventState.Live);
        var rider = await RiderTokenAsync(host, live, "Live");
        var finished = await SeedEventAsync(host, EventState.Live);
        var done = await RiderTokenAsync(host, finished, "Done"); // tokens are issued while it runs, then it ends
        await using (var db = Db(host))
            await db.Events.Where(e => e.Id == finished)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.State, EventState.Finished).SetProperty(e => e.EndedAt, host.Clock.GetUtcNow()));
        await host.Services.GetRequiredService<RuntimeRehydrator>().RebuildAsync(default);
        host.Clock.Advance(DeviceTokenCache.Grace + TimeSpan.FromSeconds(1));
        var warmer = host.Services.GetRequiredService<DeviceTokenWarmer>();
        var cache = host.Services.GetRequiredService<DeviceTokenCache>();
        await warmer.RunOnceAsync(default);

        // an hour of silence: each refresh reloads the event before its entries reach the cache lifetime
        for (var minute = 0; minute < 60; minute += 5)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(5));
            await warmer.RunOnceAsync(default);
        }

        Assert.NotNull(cache.Get(TokenService.Hash(rider.Token)));
        Assert.Null(cache.Get(TokenService.Hash(done.Token)));
    }

    /// <summary>A rider who confirmed and consented: the device token is issued (database event, so a database token).</summary>
    private static async Task<(Guid Participant, string Token)> RiderTokenAsync(ApiFactory host, Guid ev, string name)
    {
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, name);
        var link = await IssueLinkAsync(host, pid);
        var web = Client(host);
        await PostAsync(web, link, "/confirm", new { });
        return (pid, await ConsentAsync(web, link, showName: false));
    }

    private ApiFactory Host()
    {
        var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["ConnectionStrings:Default"] = postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        return factory;
    }
}
