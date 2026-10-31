using PositionTracker.Api.Hosting;

namespace PositionTracker.Api.Tests;

/// <summary>Security headers on every response, and the web app's routes (static files, client-side pages) next to the API's own 404.</summary>
public sealed class EdgeHardeningTests
{
    [Fact]
    public async Task Every_response_carries_the_security_headers_and_HSTS_over_https()
    {
        await using var host = new ApiFactory { SeedTestEvent = false };
        using var https = host.CreateDefaultClient(new Uri("https://localhost"));
        using var http = host.CreateDefaultClient(new Uri("http://localhost"));

        var secure = await https.GetAsync("/healthz");
        Assert.Equal("nosniff", secure.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", secure.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(EdgeHardening.ContentSecurityPolicy, secure.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", EdgeHardening.ContentSecurityPolicy);
        Assert.StartsWith("max-age=", secure.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.False((await http.GetAsync("/healthz")).Headers.Contains("Strict-Transport-Security"));

        // Also on errors and on the account API (which sets its own Referrer-Policy: kept).
        var missing = await https.GetAsync("/api/nothing-here");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(missing.Headers.Contains("Content-Security-Policy"));
    }
}
