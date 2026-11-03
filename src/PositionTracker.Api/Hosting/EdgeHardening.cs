using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace PositionTracker.Api.Hosting;

/// <summary>
/// What the public deployment needs at the edge (Plan 5):
/// <list type="bullet">
/// <item><b>Forwarded headers</b> (<c>Proxy:KnownProxies</c>, a list of IP addresses, and <c>Proxy:KnownNetworks</c>, CIDR ranges).
/// Without a configured proxy <c>X-Forwarded-*</c> is ignored, so a client cannot choose its own address; with one, only that proxy
/// is trusted and the real client address (and scheme) reach the per-IP limiters. Behind a reverse proxy this is required: otherwise
/// every client shares the proxy's address and one attacker could use up the sign-up and sign-in budget of everyone.</item>
/// <item><b>Security headers</b> on every response: <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>, <c>X-Frame-Options</c>,
/// a CSP that allows only the app itself, OpenStreetMap tiles (the fonts are self-hosted), and (over HTTPS) HSTS. The CSP carries
/// a fresh script nonce per response: the app has no inline script, but Cloudflare's JavaScript detections inject one in front of
/// the app and put the header's nonce on it (a meta-tag nonce is not read), so the page runs it instead of logging a violation.</item>
/// </list>
/// </summary>
public static class EdgeHardening
{
    /// <summary>The CSP with the given script nonce (base64, from <see cref="NewNonce"/>); no 'unsafe-inline' for scripts.</summary>
    public static string ContentSecurityPolicy(string nonce) =>
        $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; "
        + "font-src 'self'; img-src 'self' data: https://*.tile.openstreetmap.org https://tile.openstreetmap.org; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    /// <summary>128 random bits, base64: unguessable per response, as a CSP nonce must be.</summary>
    public static string NewNonce() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static IServiceCollection AddEdgeHardening(this IServiceCollection services, IConfiguration config)
    {
        var proxies = config.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
        var networks = config.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var p in proxies) o.KnownProxies.Add(IPAddress.Parse(p));
            foreach (var n in networks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
            }
            o.ForwardLimit = 1;
        });
        services.AddSingleton(new ProxyConfigured(proxies.Length + networks.Length > 0));
        return services;
    }

    public sealed record ProxyConfigured(bool Value);

    /// <summary>Forwarded headers (only when a proxy is configured) and the security headers; first in the pipeline.</summary>
    public static IApplicationBuilder UseEdgeHardening(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetRequiredService<ProxyConfigured>().Value) app.UseForwardedHeaders();
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = h.ContainsKey("Referrer-Policy") ? h["Referrer-Policy"] : "strict-origin-when-cross-origin";
                h["Content-Security-Policy"] = ContentSecurityPolicy(NewNonce());
                h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
                if (context.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=15552000";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
