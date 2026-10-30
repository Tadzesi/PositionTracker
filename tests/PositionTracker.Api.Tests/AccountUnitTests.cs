using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Tests;

/// <summary>Account building blocks without a database: address normalization, IP buckets, the capped hourly counter.</summary>
public sealed class AccountUnitTests
{
    [Theory]
    [InlineData("x@bücher.de", "x@bücher.de")]
    [InlineData("x@xn--bcher-kva.de", "x@xn--bcher-kva.de")]
    [InlineData("X@BÜCHER.de", "X@BÜCHER.de")]
    [InlineData("  x@BüCHER.DE ", "x@BüCHER.DE")]
    [InlineData("x@ｂüｃｈｅｒ．ｄｅ", "x@ｂüｃｈｅｒ．ｄｅ")]
    [InlineData("x@bücher。de", "x@bücher。de")]
    [InlineData("x@bücher｡de", "x@bücher｡de")]
    public void International_domains_normalize_to_their_lower_case_ASCII_form(string input, string display)
    {
        Assert.True(EmailAddress.TryParse(input, out var address, out var normalized));
        Assert.Equal(display, address);
        Assert.Equal("x@xn--bcher-kva.de", normalized);
    }

    [Theory]
    [InlineData("x+tag@example.org", "x+tag@example.org")]
    [InlineData("First.Last@Example.ORG", "first.last@example.org")]
    public void Plus_addresses_and_ASCII_addresses_keep_their_local_part(string input, string normalized)
    {
        Assert.True(EmailAddress.TryParse(input, out _, out var actual));
        Assert.Equal(normalized, actual);
    }

    [Fact]
    public void Deviation_characters_keep_their_own_domain_and_never_merge_with_the_transitional_form()
    {
        Assert.True(EmailAddress.TryParse("x@straße.de", out _, out var sharpS));
        Assert.True(EmailAddress.TryParse("x@strasse.de", out _, out var doubleS));
        Assert.Equal("x@xn--strae-oqa.de", sharpS); // nontransitional IDNA (UTS 46 / IDNA2008), as on this platform
        Assert.Equal("x@strasse.de", doubleS);
    }

    [Theory]
    [InlineData("Victim@\uFF47\uFF4D\uFF41\uFF49\uFF4C\uFF0E\uFF43\uFF4F\uFF4D", "Victim@gmail.com")]
    [InlineData("Victim@gmail\u3002com", "Victim@gmail.com")]
    [InlineData("Jan.Novak@BÜCHER.de", "Jan.Novak@xn--bcher-kva.de")]
    [InlineData("  a+b@Example.ORG ", "a+b@example.org")]
    public void The_delivery_address_keeps_the_local_part_and_uses_the_ASCII_domain(string input, string delivery)
    {
        Assert.True(EmailAddress.TryParse(input, out var address, out _));
        Assert.Equal(delivery, EmailAddress.ToDeliveryAddress(address));
    }

    [Theory]
    [InlineData("x@bü cher.de")]
    [InlineData("x@bücher..de")]
    [InlineData("x@-bücher.de")]
    [InlineData("x@bücher")]
    [InlineData("ｘ@example.org")]   // non-ASCII local part
    [InlineData("x@exa​mple.org")] // zero-width space
    [InlineData("x@1.2.3.4")]
    public void Invalid_or_unmappable_domains_are_rejected(string input) =>
        Assert.False(EmailAddress.TryParse(input, out _, out _));

    [Fact]
    public void The_ASCII_form_must_fit_254_characters_too()
    {
        // 63 two-byte letters encode to a label longer than 63 characters.
        Assert.False(EmailAddress.TryParse("x@" + new string('ü', 60) + ".de", out _, out _));
    }

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void IPv6_addresses_are_bucketed_by_64_and_mapped_IPv4_is_plain_IPv4(string ip, string bucket) =>
        Assert.Equal(bucket, AccountRateLimiter.IpBucket(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("2001:db8:aa:1::1", "2001:db8:aa::/48")]
    [InlineData("2001:db8:aa:ffff:1:2:3:4", "2001:db8:aa::/48")]
    [InlineData("2001:db8:ab:1::1", "2001:db8:ab::/48")]
    [InlineData("198.51.100.7", null)]
    [InlineData("::ffff:198.51.100.7", null)]
    public void IPv6_addresses_also_count_per_48_and_IPv4_does_not(string ip, string? prefix) =>
        Assert.Equal(prefix, AccountRateLimiter.IpPrefix48(IPAddress.Parse(ip)));

    [Fact]
    public void A_missing_address_has_its_own_bucket() => Assert.Equal("unknown", AccountRateLimiter.IpBucket(null));

    [Fact]
    public void The_counter_is_capped_by_evicting_the_oldest_keys_and_keeps_counting_recent_ones()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var counter = new HourlyCounter(clock, limit: 2, maxKeys: 10);

        for (var i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(counter.TryTake("k" + i));
            Assert.True(counter.Count <= 10, $"{counter.Count} keys after {i + 1}");
        }

        Assert.True(counter.TryTake("k24"));
        Assert.False(counter.TryTake("k24")); // the newest key kept its count
    }

    [Fact]
    public void Ended_windows_are_swept_by_time_not_per_call()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var counter = new HourlyCounter(clock, limit: 3, maxKeys: 1000);
        for (var i = 0; i < 500; i++) counter.TryTake("k" + i);
        Assert.Equal(500, counter.Count);

        clock.Advance(TimeSpan.FromMinutes(61));
        counter.TryTake("fresh");
        Assert.Equal(1, counter.Count);
        Assert.True(counter.TryTake("k0")); // a new window
    }

    [Fact]
    public void A_mail_takes_from_all_its_budgets_or_none()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        var a = new HourlyCounter(clock, limit: 5, maxKeys: 100);
        var b = new HourlyCounter(clock, limit: 1, maxKeys: 100);

        Assert.True(HourlyCounter.TryTakeAll((a, "x"), (b, "y")));
        Assert.False(HourlyCounter.TryTakeAll((a, "x"), (b, "y")));
        for (var i = 0; i < 4; i++) Assert.True(a.TryTake("x")); // the refused call took nothing from a
        Assert.False(a.TryTake("x"));
    }

    [Fact]
    public void Only_413_read_errors_become_body_too_large()
    {
        Assert.Equal(StatusCodes.Status413PayloadTooLarge,
            Status(AccountEndpoints.MapReadError(Results.Problem("too large", statusCode: StatusCodes.Status413PayloadTooLarge))));
        Assert.Equal(StatusCodes.Status400BadRequest,
            Status(AccountEndpoints.MapReadError(Results.Problem("bad", statusCode: StatusCodes.Status400BadRequest))));
        Assert.Equal(StatusCodes.Status408RequestTimeout,
            Status(AccountEndpoints.MapReadError(Results.Problem("slow", statusCode: StatusCodes.Status408RequestTimeout))));
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
}
