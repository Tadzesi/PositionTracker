using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PositionTracker.Api.Accounts;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Organizer sign-up, email confirmation, passwordless sign-in and the cookie session, through the HTTP host against
/// PostgreSQL (Testcontainers). Emails are read from the outbox rows (the worker never runs a second round: its poll
/// interval is far beyond the fake clock's jumps, and no SMTP host is configured, so nothing can be delivered).
/// </summary>
public sealed class AccountTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BaseUrl = "https://tracker.example.org";
    private const string IpHeader = "X-Test-Remote-Ip";
    private static readonly Regex LinkPattern = new(@"https://tracker\.example\.org/account/(confirm|signin)\?token=([A-Za-z0-9%_\-]+)");

    // ----- sign-up -----

    [Fact]
    public async Task Sign_up_creates_an_unconfirmed_organizer_and_enqueues_exactly_one_confirmation_email()
    {
        await using var host = Host();
        var client = Client(host);

        var response = await SignUpAsync(client, "Žofia Ďurčeková", "Zofia.Durcekova@Example.SK", "sk");
        var at = host.Clock.GetUtcNow();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var organizer = Assert.Single(await OrganizersAsync(host));
        Assert.Equal("Žofia Ďurčeková", organizer.Name);
        Assert.Equal("Zofia.Durcekova@Example.SK", organizer.Email);
        Assert.Equal("zofia.durcekova@example.sk", organizer.EmailNormalized);
        Assert.Equal("sk", organizer.Language);
        Assert.Null(organizer.EmailConfirmedAt);
        Assert.Equal(at, organizer.CreatedAt);

        var mail = Assert.Single(await OutboxAsync(host));
        Assert.Equal(EmailPurpose.OrganizerEmailConfirmation, mail.Purpose);
        Assert.Equal("Zofia.Durcekova@Example.SK", mail.ToAddress);
        Assert.Equal("sk", mail.Language);
        var (kind, token) = Link(mail);
        Assert.Equal("confirm", kind);

        var stored = Assert.Single(await LinkTokensAsync(host));
        Assert.Equal(nameof(LinkPurpose.OrganizerEmailConfirm), stored.Purpose);
        Assert.Equal(organizer.Id, stored.SubjectId);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), stored.TokenHash);
        Assert.Equal(at.AddDays(14), stored.ExpiresAt);
    }

    [Fact]
    public async Task Repeated_sign_up_answers_identically_and_mails_a_fresh_confirmation_or_a_sign_in_link()
    {
        await using var host = Host();
        var client = Client(host);

        var first = await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var firstToken = Link(Assert.Single(await OutboxAsync(host))).Token;

        // Unconfirmed account: a fresh confirmation in the account's language, the old link superseded, nothing changed.
        var second = await SignUpAsync(client, "Mallory", "ANNA@example.org", "sk");
        var mails = await OutboxAsync(host);
        Assert.Equal(2, mails.Count);
        Assert.Equal(EmailPurpose.OrganizerEmailConfirmation, mails[1].Purpose);
        Assert.Equal("en", mails[1].Language);
        var secondToken = Link(mails[1]).Token;
        var organizer = Assert.Single(await OrganizersAsync(host));
        Assert.Equal("Anna", organizer.Name);
        Assert.Equal("en", organizer.Language);
        await AssertErrorAsync(await client.GetAsync(ConfirmPath(firstToken)), HttpStatusCode.BadRequest, "link_invalid");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ConfirmPath(secondToken))).StatusCode);

        // Confirmed account: a sign-in link instead.
        var third = await SignUpAsync(client, "Anna", "anna@example.org", "en");
        mails = await OutboxAsync(host);
        Assert.Equal(3, mails.Count);
        Assert.Equal(EmailPurpose.SignIn, mails[2].Purpose);
        Assert.Equal("signin", Link(mails[2]).Kind);

        // Unknown, unconfirmed and confirmed: the same answer.
        await AssertSameAnswerAsync(first, second, third);
        Assert.Single(await OrganizersAsync(host));
    }

    [Fact]
    public async Task Sign_in_answers_identically_and_mails_only_existing_accounts()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Unconfirmed", "unconfirmed@example.org", "en");
        await SignUpAsync(client, "Confirmed", "confirmed@example.org", "sk");
        await ConfirmAsync(host, client, "confirmed@example.org");
        var before = (await OutboxAsync(host)).Count;

        var unknown = await SignInAsync(client, "nobody@example.org");
        Assert.Equal(before, (await OutboxAsync(host)).Count);

        var unconfirmed = await SignInAsync(client, "unconfirmed@example.org");
        var confirmed = await SignInAsync(client, " Confirmed@Example.org ");
        var mails = (await OutboxAsync(host)).Skip(before).ToList();
        Assert.Equal(2, mails.Count);
        Assert.All(mails, m => Assert.Equal(EmailPurpose.SignIn, m.Purpose));
        Assert.Equal(["unconfirmed@example.org", "confirmed@example.org"], mails.Select(m => m.ToAddress));
        Assert.Equal("sk", mails[1].Language);

        await AssertSameAnswerAsync(unknown, unconfirmed, confirmed);
    }

    [Fact]
    public async Task Emails_differing_only_by_case_or_surrounding_whitespace_map_to_one_account()
    {
        await using var host = Host();
        var client = Client(host);

        await SignUpAsync(client, "Anna", "  Anna.Nova@Example.COM\t", "en");
        await SignUpAsync(client, "Anna", "anna.nova@example.com", "en");
        await SignUpAsync(client, "Anna", "ANNA.NOVA@EXAMPLE.COM", "en");

        var organizer = Assert.Single(await OrganizersAsync(host));
        Assert.Equal("Anna.Nova@Example.COM", organizer.Email);
        Assert.Equal("anna.nova@example.com", organizer.EmailNormalized);
        Assert.All(await OutboxAsync(host), m => Assert.Equal("Anna.Nova@Example.COM", m.ToAddress));
    }

    [Fact]
    public async Task Unicode_names_are_stored_and_returned_intact()
    {
        const string name = "Žofia Ďurčeková-Łukasiewicz 李小龍 🚴‍♀️";
        await using var host = Host();
        var client = Client(host);

        await SignUpAsync(client, name, "zofia@example.org", "sk");
        var cookie = await SignInSessionAsync(host, client, "zofia@example.org");

        Assert.Equal(name, Assert.Single(await OrganizersAsync(host)).Name);
        using var me = JsonDocument.Parse(await (await MeAsync(client, cookie)).Content.ReadAsStringAsync());
        Assert.Equal(name, me.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_name_of_200_characters_is_accepted_and_one_of_201_is_rejected()
    {
        await using var host = Host();
        var client = Client(host);

        // 200 characters, including characters outside the BMP (each counts once).
        var ok = await SignUpAsync(client, new string('ž', 198) + "🚴🚴", "ok@example.org", "sk");
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        var tooLong = await SignUpAsync(client, new string('a', 201), "long@example.org", "sk");
        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, "validation_failed", "name");
        Assert.Single(await OrganizersAsync(host));
    }

    [Theory]
    [InlineData("", "a@example.org", "en", "name")]
    [InlineData("   ", "a@example.org", "en", "name")]
    [InlineData("Eve\r\nBcc: x@example.net", "a@example.org", "en", "name")]
    [InlineData("Anna", "", "en", "email")]
    [InlineData("Anna", "no-at-sign", "en", "email")]
    [InlineData("Anna", "a@localhost", "en", "email")]
    [InlineData("Anna", "Anna <a@example.org>", "en", "email")]
    [InlineData("Anna", "\"Anna\" a@example.org", "en", "email")]
    [InlineData("Anna", "a@example.org\r\nBcc: x@example.net", "en", "email")]
    [InlineData("Anna", "a@example.org, b@example.org", "en", "email")]
    [InlineData("Anna", "a b@example.org", "en", "email")]
    [InlineData("Anna", "a@@example.org", "en", "email")]
    [InlineData("Anna", ".a@example.org", "en", "email")]
    [InlineData("Anna", "a..b@example.org", "en", "email")]
    [InlineData("Anna", "a@-example.org", "en", "email")]
    [InlineData("Anna", "a@example..org", "en", "email")]
    [InlineData("Anna", "a@example.org", "de", "language")]
    [InlineData("Anna", "a@example.org", "", "language")]
    public async Task Invalid_sign_up_input_is_rejected_with_the_field_and_nothing_is_stored(
        string name, string email, string language, string field)
    {
        await using var host = Host();
        var client = Client(host);

        var response = await SignUpAsync(client, name, email, language);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.Empty(await OrganizersAsync(host));
        Assert.Empty(await OutboxAsync(host));
    }

    [Fact]
    public async Task Emails_longer_than_254_characters_and_malformed_bodies_are_rejected()
    {
        await using var host = Host();
        var client = Client(host);
        string Address(int lastLabel) =>
            new string('a', 64) + "@" + new string('c', 63) + "." + new string('d', 63) + "." + new string('e', lastLabel) + ".sk";
        Assert.Equal(254, Address(58).Length);

        Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(client, "Max", Address(58), "en")).StatusCode);
        await AssertErrorAsync(await SignUpAsync(client, "Max", Address(59), "en"), HttpStatusCode.BadRequest, "validation_failed", "email");

        var malformed = await client.PostAsync("/api/account/signup", new StringContent("{not json", Encoding.UTF8, "application/json"));
        await AssertErrorAsync(malformed, HttpStatusCode.BadRequest, "validation_failed");
        await AssertErrorAsync(await client.PostAsJsonAsync("/api/account/signin", new { email = "nope" }),
            HttpStatusCode.BadRequest, "validation_failed", "email");
    }

    // ----- rate limits -----

    [Fact]
    public async Task At_most_three_mails_per_email_and_hour_are_sent_and_the_answer_stays_the_same()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        await using var host = Host();
        var client = Client(host);

        var answers = new List<HttpResponseMessage>();
        answers.Add(await SignUpAsync(client, "Anna", "anna@example.org", "en"));
        answers.Add(await SignInAsync(client, "ANNA@example.org"));
        answers.Add(await SignUpAsync(client, "Anna", "anna@example.org", "en"));
        answers.Add(await SignUpAsync(client, "Anna", "Anna@Example.org", "en")); // 4th: over the limit
        answers.Add(await SignInAsync(client, "anna@example.org"));               // 5th: over the limit

        Assert.Equal(3, (await OutboxAsync(host)).Count);
        await AssertSameAnswerAsync([.. answers]);
        Assert.Equal(["email", "email"], suppressed);

        host.Clock.Advance(TimeSpan.FromMinutes(61));
        await SignInAsync(client, "anna@example.org");
        Assert.Equal(4, (await OutboxAsync(host)).Count);
    }

    [Fact]
    public async Task At_most_twenty_sign_up_or_sign_in_requests_per_IP_and_hour_are_processed()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        await using var host = Host();
        var a = Client(host, "198.51.100.7");
        var b = Client(host, "203.0.113.9");

        var answers = new List<HttpResponseMessage>();
        for (var i = 0; i < 15; i++) answers.Add(await SignUpAsync(a, "User " + i, $"user{i}@example.org", "en"));
        for (var i = 0; i < 5; i++) answers.Add(await SignInAsync(a, $"user{i}@example.org"));
        Assert.Equal(15, (await OrganizersAsync(host)).Count);
        Assert.Equal(20, (await OutboxAsync(host)).Count);

        answers.Add(await SignUpAsync(a, "Over", "over@example.org", "en"));   // 21st from this IP
        answers.Add(await SignInAsync(a, "user0@example.org"));
        Assert.Equal(15, (await OrganizersAsync(host)).Count);
        Assert.Equal(20, (await OutboxAsync(host)).Count);
        await AssertSameAnswerAsync([.. answers]);
        Assert.Equal(["ip", "ip"], suppressed);

        // Another IP is not affected; the first one is served again after the hour.
        Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(b, "Other", "other@example.org", "en")).StatusCode);
        Assert.Equal(16, (await OrganizersAsync(host)).Count);
        host.Clock.Advance(TimeSpan.FromMinutes(61));
        await SignUpAsync(a, "Later", "later@example.org", "en");
        Assert.Equal(17, (await OrganizersAsync(host)).Count);
    }

    [Fact]
    public async Task Sign_up_and_sign_in_take_at_least_the_minimum_response_time_on_every_path()
    {
        await using var host = Host(minResponseMs: 150);
        var client = Client(host);
        await SignUpAsync(client, "Existing", "existing@example.org", "en"); // warm up

        foreach (var call in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => SignUpAsync(client, "New", "new@example.org", "en"),
                     () => SignUpAsync(client, "Existing", "existing@example.org", "en"),
                     () => SignInAsync(client, "unknown@example.org"),
                     () => SignInAsync(client, "existing@example.org"),
                 })
        {
            var watch = Stopwatch.StartNew();
            Assert.Equal(HttpStatusCode.Accepted, (await call()).StatusCode);
            Assert.True(watch.ElapsedMilliseconds >= 145, $"answered after {watch.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public async Task Link_redemption_is_rate_limited_per_IP_with_rate_limited()
    {
        await using var host = Host(configure: f => f.Settings["Accounts:LinkRedeemsPerIpPerHour"] = "3");
        var client = Client(host);

        for (var i = 0; i < 3; i++)
            await AssertErrorAsync(await client.GetAsync(ConfirmPath("x" + i)), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync(RedeemPath("y")), (HttpStatusCode)429, "rate_limited");
    }

    // ----- links -----

    [Fact]
    public async Task Confirmation_link_confirms_the_email_and_can_be_reopened_until_it_expires()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var token = Link(Assert.Single(await OutboxAsync(host))).Token;

        host.Clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));
        var response = await client.GetAsync(ConfirmPath(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"confirmed":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(host.Clock.GetUtcNow(), Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ConfirmPath(token))).StatusCode);
    }

    [Fact]
    public async Task Expired_confirmation_and_sign_in_links_fail_with_link_expired()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var confirm = Link((await OutboxAsync(host))[0]).Token;
        await SignInAsync(client, "anna@example.org");
        var signIn = Link((await OutboxAsync(host))[1]).Token;

        host.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await AssertErrorAsync(await client.GetAsync(RedeemPath(signIn)), HttpStatusCode.BadRequest, "link_expired");

        host.Clock.Advance(TimeSpan.FromDays(14));
        await AssertErrorAsync(await client.GetAsync(ConfirmPath(confirm)), HttpStatusCode.BadRequest, "link_expired");
        Assert.Null(Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);
    }

    [Fact]
    public async Task Sign_in_link_is_single_use()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");
        var token = Link((await OutboxAsync(host))[1]).Token;

        var first = await client.GetAsync(RedeemPath(token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(SessionCookie(first));

        var second = await client.GetAsync(RedeemPath(token));
        await AssertErrorAsync(second, HttpStatusCode.BadRequest, "link_used");
        Assert.Null(SessionCookie(second));
    }

    [Fact]
    public async Task Wrong_purpose_superseded_unknown_and_missing_tokens_fail_with_link_invalid()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var confirm = Link((await OutboxAsync(host))[0]).Token;
        await SignInAsync(client, "anna@example.org");
        var oldSignIn = Link((await OutboxAsync(host))[1]).Token;
        await SignInAsync(client, "anna@example.org");
        var newSignIn = Link((await OutboxAsync(host))[2]).Token;

        await AssertErrorAsync(await client.GetAsync(RedeemPath(confirm)), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync(ConfirmPath(newSignIn)), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync(RedeemPath(oldSignIn)), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync(RedeemPath("not-a-token")), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync("/api/account/signin/redeem"), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await client.GetAsync("/api/account/confirm"), HttpStatusCode.BadRequest, "link_invalid");
        Assert.Null(Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);

        // The newest link still works (the failures above did not consume it).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(RedeemPath(newSignIn))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_redemptions_of_a_single_use_token_let_exactly_one_win()
    {
        await using var host = Host();
        _ = host.Services;
        for (var round = 0; round < 10; round++)
        {
            var subject = Guid.NewGuid();
            string raw;
            using (var scope = host.Services.CreateScope())
                raw = await scope.ServiceProvider.GetRequiredService<LinkTokenService>()
                    .IssueAsync(LinkPurpose.OrganizerSignIn, subject, TimeSpan.FromMinutes(15), CancellationToken.None);

            using var gate = new ManualResetEventSlim();
            var redeems = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                using var scope = host.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<LinkTokenService>();
                gate.Wait();
                return await service.RedeemAsync(LinkPurpose.OrganizerSignIn, raw, singleUse: true, CancellationToken.None);
            })).ToList();
            gate.Set();
            var results = await Task.WhenAll(redeems);

            Assert.Single(results, r => r.Status == LinkStatus.Ok && r.SubjectId == subject);
            Assert.Single(results, r => r.Status == LinkStatus.Used);
        }
    }

    [Fact]
    public async Task Issued_tokens_are_random_32_bytes_stored_only_as_hash_and_supersede_only_the_same_purpose_and_subject()
    {
        await using var host = Host();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LinkTokenService>();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ct = CancellationToken.None;

        var invite = await service.IssueAsync(LinkPurpose.ParticipantInvite, a, TimeSpan.FromDays(14), ct);
        var signInA1 = await service.IssueAsync(LinkPurpose.OrganizerSignIn, a, TimeSpan.FromMinutes(15), ct);
        var signInB = await service.IssueAsync(LinkPurpose.OrganizerSignIn, b, TimeSpan.FromMinutes(15), ct);
        var signInA2 = await service.IssueAsync(LinkPurpose.OrganizerSignIn, a, TimeSpan.FromMinutes(15), ct);

        foreach (var raw in new[] { invite, signInA1, signInB, signInA2 })
        {
            Assert.Equal(43, raw.Length);
            Assert.Equal(32, Base64UrlDecode(raw).Length);
        }
        Assert.Equal(4, new[] { invite, signInA1, signInB, signInA2 }.Distinct().Count());
        var stored = await LinkTokensAsync(host);
        Assert.All(stored, t => Assert.Equal(32, t.TokenHash.Length));
        Assert.Contains(stored, t => t.TokenHash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(invite))));
        Assert.DoesNotContain(stored, t => t.TokenHash.SequenceEqual(Base64UrlDecode(invite)));

        Assert.Equal(LinkStatus.Superseded, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInA1, true, ct)).Status);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInB, true, ct)).Status);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInA2, true, ct)).Status);
        var reopened = await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite, false, ct);
        Assert.Equal(LinkStatus.Ok, reopened.Status);
        Assert.Equal(a, reopened.SubjectId);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite, false, ct)).Status);
        Assert.Equal(LinkStatus.Unknown, (await service.RedeemAsync(LinkPurpose.OrganizerEmailConfirm, invite, false, ct)).Status);
    }

    // ----- session -----

    [Fact]
    public async Task Session_cookie_is_HttpOnly_Secure_SameSite_Lax_and_lasts_30_days()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");

        var response = await client.GetAsync(RedeemPath(Link((await OutboxAsync(host))[1]).Token));
        var at = host.Clock.GetUtcNow();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal));
        var attributes = setCookie.Split(';').Select(p => p.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.Ordinal));
        Assert.Equal(Seconds(at.AddDays(30)), Expires(setCookie));
        Assert.DoesNotContain("anna", setCookie, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("anna@example.org", body.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Session_expiry_slides_with_use_and_ends_30_days_after_the_last_renewal()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");

        host.Clock.Advance(TimeSpan.FromDays(20));
        var renewed = await MeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedCookie = Assert.Single(renewed.Headers.GetValues("Set-Cookie"));
        Assert.Equal(Seconds(host.Clock.GetUtcNow().AddDays(30)), Expires(renewedCookie));
        var newCookie = renewedCookie.Split(';')[0];

        host.Clock.Advance(TimeSpan.FromDays(25)); // 45 days after sign-in, 25 after the renewal
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(client, newCookie)).StatusCode);
        await AssertErrorAsync(await MeAsync(client, cookie), HttpStatusCode.Unauthorized, "unauthorized"); // the old ticket ran out

        host.Clock.Advance(TimeSpan.FromDays(31));
        await AssertErrorAsync(await MeAsync(client, newCookie), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Me_answers_401_without_a_session_and_the_profile_with_one()
    {
        await using var host = Host();
        var client = Client(host);

        await AssertErrorAsync(await client.GetAsync("/api/account/me"), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await MeAsync(client, CookieAuth.CookieName + "=garbage"), HttpStatusCode.Unauthorized, "unauthorized");

        await SignUpAsync(client, "Anna", "Anna@Example.org", "sk");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");
        var me = await MeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("""{"name":"Anna","email":"Anna@Example.org","language":"sk","emailConfirmed":false,"emailMode":"simulated"}""",
            await me.Content.ReadAsStringAsync());

        await ConfirmAsync(host, client, "anna@example.org");
        using var after = JsonDocument.Parse(await (await MeAsync(client, cookie)).Content.ReadAsStringAsync());
        Assert.True(after.RootElement.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task Sign_out_clears_the_cookie_and_invalidates_every_copy_of_the_session()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");
        var otherDevice = await SignInSessionAsync(host, client, "anna@example.org");

        var signOut = Request(HttpMethod.Post, "/api/account/signout", cookie, csrf: true);
        var response = await client.SendAsync(signOut);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieAuth.CookieName + "=;", cleared, StringComparison.Ordinal);
        await AssertErrorAsync(await MeAsync(client, cookie), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await MeAsync(client, otherDevice), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Cookie_authenticated_state_changing_requests_without_the_csrf_header_are_refused()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/account/signout", cookie, csrf: false));
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "csrf_failed");
        var wrong = Request(HttpMethod.Post, "/api/account/signout", cookie, csrf: false);
        wrong.Headers.Add(CookieAuth.CsrfHeader, "XMLHttpRequest");
        await AssertErrorAsync(await client.SendAsync(wrong), HttpStatusCode.Forbidden, "csrf_failed");
        var signIn = Request(HttpMethod.Post, "/api/account/signin", cookie, csrf: false);
        signIn.Content = JsonContent.Create(new { email = "anna@example.org" });
        await AssertErrorAsync(await client.SendAsync(signIn), HttpStatusCode.Forbidden, "csrf_failed");

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(client, cookie)).StatusCode); // still signed in
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Post, "/api/account/signout", cookie, csrf: true))).StatusCode);
    }

    [Fact]
    public async Task EmailConfirmedOrganizer_policy_requires_a_confirmed_email()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var organizer = Assert.Single(await OrganizersAsync(host));

        async Task<(bool Session, bool Confirmed)> EvaluateAsync()
        {
            var current = Assert.Single(await OrganizersAsync(host));
            using var scope = host.Services.CreateScope();
            var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            var principal = CookieAuth.CreatePrincipal(current);
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            return ((await authorization.AuthorizeAsync(principal, http, CookieAuth.OrganizerSessionPolicy)).Succeeded,
                (await authorization.AuthorizeAsync(principal, http, CookieAuth.EmailConfirmedOrganizerPolicy)).Succeeded);
        }

        Assert.Equal((true, false), await EvaluateAsync());
        await ConfirmAsync(host, client, "anna@example.org");
        Assert.Equal((true, true), await EvaluateAsync());
        Assert.Single(CookieAuth.CreatePrincipal(organizer).Claims, c => c.Type == CookieAuth.OrganizerIdClaim);
        Assert.Equal(2, CookieAuth.CreatePrincipal(organizer).Claims.Count());
    }

    [Fact]
    public async Task Raw_tokens_and_email_addresses_never_reach_the_logs()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = Host(configure: f =>
        {
            f.Settings["Logging:LogLevel:Default"] = "Trace";
            f.Settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning"; // as shipped: the request line carries the query string
        }, services: s => s.AddSingleton<ILoggerProvider>(logs));
        var client = Client(host);

        await SignUpAsync(client, "Anna", "secret.person@example.org", "en");
        var confirm = Link((await OutboxAsync(host))[0]).Token;
        await client.GetAsync(ConfirmPath(confirm));
        await SignUpAsync(client, "Anna", "secret.person@example.org", "en");
        await SignInAsync(client, "secret.person@example.org");
        await SignInAsync(client, "secret.person@example.org");
        await SignInAsync(client, "secret.person@example.org"); // over the limit: logged at debug level
        var signIn = Link((await OutboxAsync(host)).Last()).Token;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(RedeemPath(signIn))).StatusCode);

        var text = logs.Text();
        Assert.Contains("rate limit", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(confirm, text, StringComparison.Ordinal);
        Assert.DoesNotContain(signIn, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.person", text, StringComparison.OrdinalIgnoreCase);
    }

    // ----- helpers -----

    private ApiFactory Host(int minResponseMs = 0, Action<ApiFactory>? configure = null, Action<IServiceCollection>? services = null)
    {
        var factory = new ApiFactory
        {
            SeedTestEvent = false,
            ConfigureServices = s =>
            {
                s.AddSingleton<IStartupFilter, TestRemoteIp>();
                services?.Invoke(s);
            },
        };
        factory.Settings["ConnectionStrings:Default"] = postgres.NewDatabase();
        factory.Settings["Persistence:MigrateOnStartup"] = "true";
        factory.Settings["Live:IntervalMs"] = "3600000";
        factory.Settings["Email:PublicBaseUrl"] = BaseUrl;
        // The worker's first round runs at startup (nothing queued yet); the next one only after 45 days of fake time
        // (Task.Delay allows at most about 49 days), and finished rows are kept.
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Email:Outbox:RetentionDays"] = "3650";
        factory.Settings["Accounts:MinResponseMilliseconds"] = minResponseMs.ToString(CultureInfo.InvariantCulture);
        configure?.Invoke(factory);
        return factory;
    }

    /// <summary>
    /// A client without cookie handling (cookies are handled by hand: the fake clock is not the cookie container's
    /// clock) that moves the fake clock 1 ms forward before each request, so rows written by different requests have
    /// increasing (version 7) ids and times.
    /// </summary>
    private static HttpClient Client(ApiFactory host, string ip = "192.0.2.1")
    {
        var client = host.CreateDefaultClient(new Uri("https://localhost"), new TickingHandler(host));
        client.DefaultRequestHeaders.Add(IpHeader, ip);
        return client;
    }

    private sealed class TickingHandler(ApiFactory host) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            host.Clock.Advance(TimeSpan.FromMilliseconds(1));
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Cookie dates have whole seconds.</summary>
    private static DateTimeOffset Seconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Offset);

    private static Task<HttpResponseMessage> SignUpAsync(HttpClient client, string name, string email, string language) =>
        client.PostAsJsonAsync("/api/account/signup", new { name, email, language });

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/account/signin", new { email });

    private static string ConfirmPath(string token) => "/api/account/confirm?token=" + Uri.EscapeDataString(token);

    private static string RedeemPath(string token) => "/api/account/signin/redeem?token=" + Uri.EscapeDataString(token);

    private static async Task ConfirmAsync(ApiFactory host, HttpClient client, string normalizedEmail)
    {
        var mail = (await OutboxAsync(host)).Last(m =>
            m.Purpose == EmailPurpose.OrganizerEmailConfirmation && m.ToAddress.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ConfirmPath(Link(mail).Token))).StatusCode);
    }

    /// <summary>Requests a sign-in link, redeems it and returns the session cookie (<c>name=value</c>).</summary>
    private static async Task<string> SignInSessionAsync(ApiFactory host, HttpClient client, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await SignInAsync(client, email)).StatusCode);
        var mail = (await OutboxAsync(host)).Last(m => m.Purpose == EmailPurpose.SignIn);
        var response = await client.GetAsync(RedeemPath(Link(mail).Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return SessionCookie(response) ?? throw new InvalidOperationException("No session cookie.");
    }

    private static string? SessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Select(v => v.Split(';')[0]).FirstOrDefault()
            : null;

    private static DateTimeOffset Expires(string setCookie)
    {
        var part = setCookie.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return DateTimeOffset.ParseExact(part["expires=".Length..], "r", CultureInfo.InvariantCulture);
    }

    private static Task<HttpResponseMessage> MeAsync(HttpClient client, string cookie) =>
        client.SendAsync(Request(HttpMethod.Get, "/api/account/me", cookie, csrf: false));

    private static HttpRequestMessage Request(HttpMethod method, string path, string cookie, bool csrf)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (csrf) request.Headers.Add(CookieAuth.CsrfHeader, CookieAuth.CsrfHeaderValue);
        return request;
    }

    private static (string Kind, string Token) Link(EmailOutboxEntity mail)
    {
        var match = LinkPattern.Match(mail.TextBody);
        Assert.True(match.Success, "The mail has no account link.");
        Assert.Contains(match.Value.Replace("&", "&amp;", StringComparison.Ordinal), mail.HtmlBody);
        return (match.Groups[1].Value, Uri.UnescapeDataString(match.Groups[2].Value));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("message");
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("sk").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("en").GetString()));
        Assert.NotEqual(message.GetProperty("sk").GetString(), message.GetProperty("en").GetString());
        if (field is not null) Assert.Equal(field, json.RootElement.GetProperty("field").GetString());
        var expectedSk = code switch
        {
            "link_expired" => "Platnosť odkazu vypršala",
            "link_used" => "už bol použitý",
            "link_invalid" => "neplatný",
            "unauthorized" => "Nie ste prihlásený",
            "rate_limited" => "Príliš veľa",
            "csrf_failed" => "bezpečnostných",
            _ => null,
        };
        if (expectedSk is not null) Assert.Contains(expectedSk, message.GetProperty("sk").GetString());
    }

    private static async Task AssertSameAnswerAsync(params HttpResponseMessage[] responses)
    {
        static async Task<string> Describe(HttpResponseMessage r) =>
            $"{(int)r.StatusCode} {r.Content.Headers.ContentType} {string.Join(",", r.Headers.Select(h => h.Key).Order())} " +
            await r.Content.ReadAsStringAsync();
        var first = await Describe(responses[0]);
        Assert.Equal("""202 application/json; charset=utf-8 Cache-Control,Referrer-Policy {"status":"accepted"}""", first);
        foreach (var response in responses.Skip(1)) Assert.Equal(first, await Describe(response));
    }

    private static MeterListener SuppressionListener(List<string> limits)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AccountRateLimiter.MeterName && instrument.Name == AccountRateLimiter.SuppressedCounterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "limit")
                    lock (limits) limits.Add((string)tag.Value!);
        });
        listener.Start();
        return listener;
    }

    private static byte[] Base64UrlDecode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);

    private static async Task<List<OrganizerEntity>> OrganizersAsync(ApiFactory host)
    {
        await using var db = Db(host);
        return await db.Organizers.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync();
    }

    private static async Task<List<EmailOutboxEntity>> OutboxAsync(ApiFactory host)
    {
        await using var db = Db(host);
        return await db.Outbox.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    private static async Task<List<LinkTokenEntity>> LinkTokensAsync(ApiFactory host)
    {
        await using var db = Db(host);
        return await db.LinkTokens.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
    }

    private static AppDbContext Db(ApiFactory host)
    {
        _ = host.Services;
        return new AppDbContext(PostgresFixture.Options(host.Settings["ConnectionStrings:Default"]!));
    }

    /// <summary>Sets the remote IP from a test header (the test server has none).</summary>
    private sealed class TestRemoteIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[IpHeader].ToString(), out var ip))
                    context.Connection.RemoteIpAddress = ip;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public string Text()
        {
            lock (_lines) return string.Join('\n', _lines);
        }

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines) owner._lines.Add($"{logLevel} {category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}
