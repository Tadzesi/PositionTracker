using Microsoft.Extensions.Logging.Abstractions;

namespace PositionTracker.Api.Participants;

/// <summary>
/// The participant link token is the only credential of <c>/api/p/{linkToken}</c> and sits in the request path. The
/// ASP.NET Core host writes that path into the log scope of every request (<c>RequestPath</c>) and into the
/// "Request starting/finished" lines, and no log filter can switch the scope off. The host takes its logger for the
/// category <see cref="HostingCategory"/> from the <see cref="ILoggerFactory"/>, so this factory gives it a
/// <see cref="NullLogger"/>: no scope and no start/finish lines. Everything else is logged as usual; routing's debug
/// line that names the path is raised to warning (<see cref="RequestPathPrivacy.AddRequestPathPrivacy"/>). Errors are still logged by their own
/// categories (without the path).
/// </summary>
public sealed class RequestPathPrivacyLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public ILogger CreateLogger(string categoryName) =>
        categoryName == HostingCategory ? NullLogger.Instance : inner.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}

/// <summary>Registration of <see cref="RequestPathPrivacyLoggerFactory"/>.</summary>
public static class RequestPathPrivacy
{
    /// <summary>Wraps the logger factory and quietens the routing matcher (its debug line holds the request path).</summary>
    public static IServiceCollection AddRequestPathPrivacy(this IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory>(sp =>
            new RequestPathPrivacyLoggerFactory(ActivatorUtilities.CreateInstance<LoggerFactory>(sp)));
        services.PostConfigure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "Microsoft.AspNetCore.Routing.Matching.DfaMatcher", logLevel: LogLevel.Warning, filter: null)));
        return services;
    }
}
