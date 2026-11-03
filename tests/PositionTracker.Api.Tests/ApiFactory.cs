using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PositionTracker.Email;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Events;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Core.Simulation;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Test host with a <see cref="FakeTimeProvider"/>, the organizer key <c>test-key-0123456789abcdefghijklmnop</c> and a seeded cycling event
/// (<see cref="EventId"/>, one lap of the 12.5 km demo loop starting at <see cref="T0"/>) with participants
/// <see cref="Participant1"/> and <see cref="Participant2"/> and their tokens. Change <see cref="Settings"/>
/// before the first use of the factory to override configuration. Set <see cref="SeedTestEvent"/> to false for a
/// host without the seeded in-memory event (for example one that rebuilds its events from a database).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    public const string EventId = "evt-test";
    public const string Participant1 = "p1";
    public const string Participant2 = "p2";

    private string? _token1;
    private string? _token2;

    public FakeTimeProvider Clock { get; } = new(T0);

    /// <summary>Whether the host gets the in-memory test event <see cref="EventId"/> (default true).</summary>
    public bool SeedTestEvent { get; init; } = true;

    /// <summary>Host environment name (default: the factory's, Development).</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Extra service registrations applied after the app's own (to replace a service in a test).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    public string OrganizerKey => "test-key-0123456789abcdefghijklmnop";

    /// <summary>Configuration overrides applied on top of the app's settings when the host is built.</summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Organizer:ApiKey"] = "test-key-0123456789abcdefghijklmnop",
        ["ConnectionStrings:Default"] = "", // in memory, unless a test sets a database
        ["Persistence:MigrateOnStartup"] = "false",
        // Hermetic: the in-memory source is added last, so these override environment variables (and user secrets
        // are removed): no developer or CI mailbox reaches a test host. Tests that need SMTP set a "*.invalid" host.
        ["Email:Smtp:Host"] = "",
        ["Email:Smtp:User"] = "",
        ["Email:Smtp:Password"] = "",
        ["Email:Smtp:Port"] = "587",
        ["Email:Smtp:UseStartTls"] = "true",
        ["Email:From"] = "",
    };

    /// <summary>
    /// Opt-in for a host whose SMTP server is not a reserved <c>*.invalid</c> name. Without it, building such a host
    /// fails the test, so the test project can never send real mail.
    /// </summary>
    public bool AllowRealSmtp { get; init; }

    public EventRegistry Registry => Services.GetRequiredService<EventRegistry>();

    public TokenService Tokens => Services.GetRequiredService<TokenService>();

    public EventRuntime Event => Registry.Get(EventId)!;

    public string Token1 => EnsureStarted(ref _token1);

    public string Token2 => EnsureStarted(ref _token2);

    /// <summary>Adds a fresh participant to the seeded event and returns its id and token (for state-sensitive tests).</summary>
    public (string ParticipantId, string Token) AddParticipant()
    {
        var id = "p-" + Guid.NewGuid().ToString("N");
        Event.AddParticipant(new ParticipantInfo(id, id, "Rider " + id, null, "Open", true, false));
        return (id, Tokens.Issue(EventId, id));
    }

    public HttpClient CreateClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (EnvironmentName is not null) builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Hermetic: the developer's user secrets (for example a real SMTP server) never reach a test host.
            foreach (var secrets in config.Sources.OfType<JsonConfigurationSource>()
                         .Where(j => string.Equals(j.Path, "secrets.json", StringComparison.OrdinalIgnoreCase)).ToList())
                config.Sources.Remove(secrets);
            config.AddInMemoryCollection(Settings);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            ConfigureServices?.Invoke(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (!AllowRealSmtp && host.Services.GetRequiredService<IEmailSender>() is SmtpEmailSender
            && host.Services.GetRequiredService<IOptions<EmailOptions>>().Value.Smtp.Host is var smtpHost
            && !smtpHost.TrimEnd('.').EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            host.Dispose();
            throw new InvalidOperationException(
                "This test host would send through a real SMTP server; use a '*.invalid' host or set AllowRealSmtp.");
        }
        if (!SeedTestEvent) return host;
        var registry = host.Services.GetRequiredService<EventRegistry>();
        var tokens = host.Services.GetRequiredService<TokenService>();
        var settings = new EventSettings(EventId, "Test Ride", Sport.Cycling, T0, TimeSpan.FromSeconds(30), false,
            TrackerOptions.Defaults(Sport.Cycling));
        var runtime = registry.Add(new EventRuntime(settings, Course.Create(DemoCourses.Loop(), 1), Clock));
        runtime.AddParticipant(new ParticipantInfo(Participant1, "1", "Rider One", null, "Open", true, false));
        runtime.AddParticipant(new ParticipantInfo(Participant2, "2", "Rider Two", null, "Open", true, false));
        _token1 = tokens.Issue(EventId, Participant1);
        _token2 = tokens.Issue(EventId, Participant2);
        return host;
    }

    private string EnsureStarted(ref string? token)
    {
        _ = Services;
        return token!;
    }
}
