using System.Text.Json;
using System.Text.Json.Serialization;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<EventRegistry>();
builder.Services.AddOptions<OrganizerOptions>()
    .BindConfiguration(OrganizerOptions.Section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Configuration value 'Organizer:ApiKey' is required.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(
    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.AddIngestRateLimiting();

var app = builder.Build();

app.UseRateLimiter();
app.MapGet("/healthz", () => "ok");
app.MapIngestEndpoints();

app.Run();

public partial class Program { }
