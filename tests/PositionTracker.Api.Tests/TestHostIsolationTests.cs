using Microsoft.Extensions.DependencyInjection;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>Process environment variables are shared, so these tests never run in parallel with others.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// The test hosts can never send real mail: neither user secrets nor environment variables of the developer's shell
/// or CI runner (the README's documented ways to configure SMTP) reach them, and a host that would still talk to a real
/// SMTP server fails the test.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class TestHostIsolationTests
{
    private static readonly string[] Variables =
        ["Email__Smtp__Host", "Email__Smtp__User", "Email__Smtp__Password", "Email__From", "Email__Smtp__Port"];

    [Fact]
    public void Smtp_settings_in_environment_variables_do_not_reach_a_test_host()
    {
        var saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("Email__Smtp__Host", "smtp.example.com");
            Environment.SetEnvironmentVariable("Email__Smtp__User", "mailbox@example.com");
            Environment.SetEnvironmentVariable("Email__Smtp__Password", "not-a-real-password");
            Environment.SetEnvironmentVariable("Email__From", "mailbox@example.com");
            Environment.SetEnvironmentVariable("Email__Smtp__Port", "2525");

            using var factory = new ApiFactory { SeedTestEvent = false };
            Assert.IsType<LoggingEmailSender>(factory.Services.GetRequiredService<IEmailSender>());
            Assert.True(factory.Services.GetRequiredService<IEmailMode>().IsSimulated);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void A_test_host_that_would_use_a_real_smtp_server_fails()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["Email:Smtp:Host"] = "smtp.example.com";
        factory.Settings["Email:From"] = "noreply@example.org";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";

        var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("real SMTP", ex.ToString());
    }

    [Fact]
    public void A_host_with_a_reserved_invalid_smtp_name_is_allowed()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["Email:Smtp:Host"] = "smtp.invalid";
        factory.Settings["Email:From"] = "noreply@example.org";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";

        Assert.IsType<SmtpEmailSender>(factory.Services.GetRequiredService<IEmailSender>());
    }
}
