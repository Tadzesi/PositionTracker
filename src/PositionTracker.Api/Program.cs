using System.Text.Json;
using System.Text.Json.Serialization;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Api.Live;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<EventRegistry>();
builder.Services.AddOptions<OrganizerOptions>()
    .BindConfiguration(OrganizerOptions.Section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Configuration value 'Organizer:ApiKey' is required.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(EnumsAsStrings()));
builder.Services.AddIngestRateLimiting();
builder.Services.AddOptions<LiveOptions>()
    .BindConfiguration(LiveOptions.Section)
    .Validate(o => o.IntervalMs > 0, "Configuration value 'Live:IntervalMs' must be positive.")
    .ValidateOnStart();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(EnumsAsStrings()));
builder.Services.AddSingleton<SpectatorFeed>();
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveBroadcaster>());

var app = builder.Build();

app.UseRateLimiter();
app.MapGet("/healthz", () => "ok");
app.MapIngestEndpoints();
app.MapLiveEndpoints();
app.MapHub<LiveHub>(LiveHub.Path);

app.Run();

static JsonStringEnumConverter EnumsAsStrings() => new(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

public partial class Program { }
