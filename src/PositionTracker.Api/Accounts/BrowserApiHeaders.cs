using Microsoft.Net.Http.Headers;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// Browser API request rules (controller rulings P9/P10), as middleware that runs before authentication, the
/// limiters and the database:
/// <list type="bullet">
/// <item><b>Browser routes</b> (<see cref="IsBrowserRoute"/>: <c>/api/account/*</c>, <c>/api/p/*</c> and
/// <c>/api/events/**</c> except the device ingest <c>/api/events/{id}/positions|actions</c>): every POST, PUT, PATCH
/// and DELETE needs <c>X-Requested-With: PositionTracker</c> (else 400 <c>csrf_failed</c>) and a
/// <c>Content-Type: application/json</c> (else 415 <c>unsupported_media_type</c>), with or without a cookie. A cross-site
/// page can send neither without a CORS preflight, which this API does not allow, so "simple" cross-site requests
/// (form posts, <c>text/plain</c> fetches) never reach a handler, a limiter or the database.</item>
/// <item><b>Other <c>/api</c> routes</b>: an unsafe request that carries the session cookie needs the header too.</item>
/// </list>
/// Paths outside <c>/api</c> (the SignalR hub, whose negotiate POST is not cookie-authenticated and whose client sends
/// its own <c>X-Requested-With</c>) are not checked. Chosen over antiforgery tokens: the API is JSON only, with no
/// server-rendered forms, and the header needs no token round trip.
/// </summary>
public static class BrowserApiHeaders
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "PositionTracker";

    public static IApplicationBuilder UseBrowserApiHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (Check(context.Request) is { } refused)
            {
                await refused.ExecuteAsync(context);
                return;
            }
            await next(context);
        });

    /// <summary>The refusal for <paramref name="request"/>, or null when it may go on.</summary>
    internal static IResult? Check(HttpRequest request)
    {
        var method = request.Method;
        var unsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);
        if (!unsafeMethod || !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return null;
        var browserRoute = IsBrowserRoute(request.Path);
        if (!browserRoute && !request.Cookies.ContainsKey(CookieAuth.CookieName)) return null;
        if (!string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
            return AccountErrors.CsrfFailed();
        if (browserRoute && !IsJson(request.ContentType)) return AccountErrors.UnsupportedMediaType();
        return null;
    }

    /// <summary>The routes used by the browser front end (organizer and participant pages), not by devices or tools.</summary>
    public static bool IsBrowserRoute(PathString path)
    {
        if (path.StartsWithSegments("/api/account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/p", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase, out var rest)) return false;
        // Device ingest (bearer token, native client): /api/events/{id}/positions and /api/events/{id}/actions.
        var segments = (rest.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !(segments.Length == 2 && (segments[1].Equals("positions", StringComparison.OrdinalIgnoreCase)
            || segments[1].Equals("actions", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var media)
        && media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
}
