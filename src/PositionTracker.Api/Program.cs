using System.Text.Json;
using System.Text.Json.Serialization;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Email;
using PositionTracker.Api.Endpoints;
using PositionTracker.Api.Events;
using PositionTracker.Api.Hosting;
using PositionTracker.Api.Live;
using PositionTracker.Api.Operations;
using PositionTracker.Api.OwnTracks;
using PositionTracker.Api.Participants;
using PositionTracker.Api.Persistence;
using PositionTracker.Api.Registration;
using PositionTracker.Api.Simulation;

var builder = WebApplication.CreateBuilder(args);

// The participant link token sits in the request path (/api/p/{token}): keep the path out of the host logs.
builder.Services.AddRequestPathPrivacy();
builder.Services.AddEdgeHardening(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<EventRegistry>();
builder.Services.AddPersistence();
builder.Services.AddEmail();
builder.Services.AddAccounts();
builder.Services.AddEventManagement();
builder.Services.AddRegistration();
builder.Services.AddParticipantLinks();
builder.Services.AddOperations();
builder.Services.AddOptions<OrganizerOptions>()
    .BindConfiguration(OrganizerOptions.Section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Configuration value 'Organizer:ApiKey' is required.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(EnumsAsStrings()));
builder.Services.AddIngestRateLimiting();
builder.Services.AddSingleton<FixRateLimiter>();
builder.Services.AddOptions<LiveOptions>()
    .BindConfiguration(LiveOptions.Section)
    .Validate(o => o.IntervalMs > 0, "Configuration value 'Live:IntervalMs' must be positive.")
    .ValidateOnStart();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(EnumsAsStrings()));
builder.Services.AddSingleton<SpectatorFeed>();
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveBroadcaster>());
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationService>());

var app = builder.Build();

app.UseEdgeHardening(); // forwarded headers (when a proxy is configured) and security headers
// Client-side routes of the web app: a GET that no endpoint handles (and is not API, hub or a file) gets index.html. Done as
// middleware, not as a fallback endpoint, so the API keeps its own 404/405 answers.
app.Use((context, next) =>
{
    var path = context.Request.Path;
    if (HttpMethods.IsGet(context.Request.Method) && context.GetEndpoint() is null && path.HasValue && path.Value != "/"
        && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/hubs") && !path.Value!.Contains('.'))
        context.Request.Path = "/index.html";
    return next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(); // the web app build (wwwroot), see web/
app.UseBrowserApiHeaders(); // first: refused requests never reach a limiter
app.UseDeviceAuthentication(); // before the limiter: it partitions by the resolved token
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => "ok");
app.MapAccountEndpoints();
app.MapEventManagementEndpoints();
app.MapRegistrationEndpoints();
app.MapParticipantLinkEndpoints();
app.MapOperationsEndpoints();
app.MapIngestEndpoints();
app.MapOwnTracksEndpoints();
app.MapLiveEndpoints();
app.MapDemoEndpoints();
app.MapOpsEndpoints();
app.MapHub<LiveHub>(LiveHub.Path);

app.Run();

static JsonStringEnumConverter EnumsAsStrings() => new(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

public partial class Program { }
