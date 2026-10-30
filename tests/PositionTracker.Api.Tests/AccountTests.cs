using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        Assert.Equal("Zofia.Durcekova@example.sk", mail.ToAddress); // local part as entered, ASCII domain
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
    public async Task Repeated_sign_up_answers_identically_updates_an_unconfirmed_account_and_mails_the_right_link()
    {
        await using var host = Host();
        var client = Client(host);

        var first = await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var firstToken = Link(Assert.Single(await OutboxAsync(host))).Token;

        // Unconfirmed account (ownership not proven yet): name and language are taken over, a fresh confirmation in the
        // new language supersedes the old link.
        var second = await SignUpAsync(client, "Anna Nová", "ANNA@example.org", "sk");
        var mails = await OutboxAsync(host);
        Assert.Equal(2, mails.Count);
        Assert.Equal(EmailPurpose.OrganizerEmailConfirmation, mails[1].Purpose);
        Assert.Equal("sk", mails[1].Language);
        var secondToken = Link(mails[1]).Token;
        var organizer = Assert.Single(await OrganizersAsync(host));
        Assert.Equal("Anna Nová", organizer.Name);
        Assert.Equal("sk", organizer.Language);
        Assert.Equal("ANNA@example.org", organizer.Email); // the display form is replaced too
        Assert.Equal("ANNA@example.org", mails[1].ToAddress);
        await AssertErrorAsync(await ConfirmAsync(client, firstToken), HttpStatusCode.BadRequest, "link_invalid");
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, secondToken)).StatusCode);

        // Confirmed account: a sign-in link instead, and nothing about the account changes.
        var third = await SignUpAsync(client, "Mallory", "Anna@EXAMPLE.org", "en");
        mails = await OutboxAsync(host);
        Assert.Equal(3, mails.Count);
        Assert.Equal(EmailPurpose.SignIn, mails[2].Purpose);
        Assert.Equal("sk", mails[2].Language);
        Assert.Equal("signin", Link(mails[2]).Kind);
        Assert.Equal("ANNA@example.org", mails[2].ToAddress);
        organizer = Assert.Single(await OrganizersAsync(host));
        Assert.Equal("Anna Nová", organizer.Name);
        Assert.Equal("sk", organizer.Language);
        Assert.Equal("ANNA@example.org", organizer.Email);

        // Unknown, unconfirmed and confirmed: the same answer.
        await AssertSameAnswerAsync(first, second, third);
    }

    [Fact]
    public async Task Sign_in_answers_identically_and_mails_only_existing_accounts()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Unconfirmed", "unconfirmed@example.org", "en");
        await SignUpAsync(client, "Confirmed", "confirmed@example.org", "sk");
        await ConfirmByMailAsync(host, client, "confirmed@example.org");
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
        Assert.Equal("ANNA.NOVA@EXAMPLE.COM", organizer.Email); // unconfirmed: the latest form is displayed
        Assert.Equal("anna.nova@example.com", organizer.EmailNormalized);
        Assert.Equal(["Anna.Nova@example.com", "anna.nova@example.com", "ANNA.NOVA@example.com"],
            (await OutboxAsync(host)).Select(m => m.ToAddress));
    }

    [Fact]
    public async Task International_domain_forms_of_one_mailbox_are_one_account_and_one_budget_but_plus_addresses_are_not()
    {
        await using var host = Host();
        var client = Client(host);

        foreach (var address in new[]
                 {
                     "x@bücher.de", "x@xn--bcher-kva.de", "X@BÜCHER.de",
                     "x@ｂüｃｈｅｒ．ｄｅ", // fullwidth letters and full stop
                     "x@bücher。de",       // ideographic full stop
                 })
            Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(client, "Xaver", address, "en")).StatusCode);

        var organizer = Assert.Single(await OrganizersAsync(host));
        // Unconfirmed: the display form of the latest processed sign-up (the 4th and 5th were suppressed by the mail
        // limit before any database work).
        Assert.Equal("X@BÜCHER.de", organizer.Email);
        Assert.Equal("x@xn--bcher-kva.de", organizer.EmailNormalized);
        // One (address, IP) budget of 3 mails, each sent to the local part as entered and the ASCII domain.
        Assert.Equal(["x@xn--bcher-kva.de", "x@xn--bcher-kva.de", "X@xn--bcher-kva.de"], (await OutboxAsync(host)).Select(m => m.ToAddress));

        await SignUpAsync(client, "Xaver", "x+race@bücher.de", "en");
        Assert.Equal(2, (await OrganizersAsync(host)).Count);
        Assert.Contains(await OrganizersAsync(host), o => o.EmailNormalized == "x+race@xn--bcher-kva.de");
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

        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signup", "{not json"), HttpStatusCode.BadRequest, "validation_failed", "body");
        await AssertErrorAsync(await PostAsync(client, "/api/account/signin", new { email = "nope" }),
            HttpStatusCode.BadRequest, "validation_failed", "email");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signup", new string(' ', 9000)),
            HttpStatusCode.RequestEntityTooLarge, "validation_failed", "body");
    }

    // ----- browser headers (CSRF and cross-site simple requests) -----

    [Fact]
    public async Task Unsafe_account_requests_need_the_header_and_a_json_content_type_and_refused_ones_are_not_counted()
    {
        await using var host = Host();
        var client = Client(host);
        var body = JsonSerializer.Serialize(new { name = "Eve", email = "victim@example.org", language = "en" });

        // A cross-site "simple" request: text/plain, no custom header. Refused before any limiter or database work.
        for (var i = 0; i < 25; i++)
            await AssertErrorAsync(await PostRawAsync(client, "/api/account/signup", body, header: false, mediaType: "text/plain"),
                HttpStatusCode.BadRequest, "csrf_failed");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signin", """{"email":"victim@example.org"}""", header: false),
            HttpStatusCode.BadRequest, "csrf_failed");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signup", body, header: true, mediaType: "text/plain"),
            HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signup", body, header: true, mediaType: "application/x-www-form-urlencoded"),
            HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Empty(await OrganizersAsync(host));

        // The per-IP budget is untouched: 20 correct requests are all processed.
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(client, "User", $"u{i}@example.org", "en")).StatusCode);
        Assert.Equal(20, (await OrganizersAsync(host)).Count);
    }

    [Fact]
    public async Task Links_are_redeemed_only_by_POST_and_opening_the_emailed_page_consumes_nothing()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");
        var mails = await OutboxAsync(host);
        var confirm = Link(mails[0]).Token;
        var signIn = Link(mails[1]).Token;

        // The API routes do not answer GET (no session from a top-level navigation, no token in an API URL).
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/account/confirm?token=" + Uri.EscapeDataString(confirm))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/account/signin/redeem?token=" + Uri.EscapeDataString(signIn))).StatusCode);

        // A mail scanner fetching the emailed front-end URLs reaches no API route.
        foreach (var mail in mails)
        {
            var url = new Uri(LinkPattern.Match(mail.TextBody).Value);
            var scanned = await client.GetAsync(url.PathAndQuery);
            Assert.Equal(HttpStatusCode.NotFound, scanned.StatusCode);
            Assert.Null(SessionCookie(scanned));
        }

        await AssertErrorAsync(await PostAsync(client, "/api/account/signin/redeem", new { token = signIn }, header: false),
            HttpStatusCode.BadRequest, "csrf_failed");
        await AssertErrorAsync(await PostAsync(client, "/api/account/confirm", new { token = confirm }, header: false),
            HttpStatusCode.BadRequest, "csrf_failed");
        Assert.Null(Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, signIn)).StatusCode);
    }

    // ----- rate limits -----

    [Fact]
    public async Task At_most_three_mails_per_email_and_IP_and_hour_are_sent_and_the_answer_stays_the_same()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        await using var host = Host();
        var client = Client(host);

        var answers = new List<HttpResponseMessage>
        {
            await SignUpAsync(client, "Anna", "anna@example.org", "en"),
            await SignInAsync(client, "ANNA@example.org"),
            await SignUpAsync(client, "Anna", "anna@example.org", "en"),
            await SignUpAsync(client, "Anna", "Anna@Example.org", "en"), // 4th: over the limit
            await SignInAsync(client, "anna@example.org"),               // 5th: over the limit
        };

        Assert.Equal(3, (await OutboxAsync(host)).Count);
        await AssertSameAnswerAsync([.. answers]);
        Assert.Equal(["email_ip", "email_ip"], suppressed);

        // Another IP still gets mail for the address; after the hour this one does again.
        await SignInAsync(Client(host, "203.0.113.50"), "anna@example.org");
        Assert.Equal(4, (await OutboxAsync(host)).Count);
        host.Clock.Advance(TimeSpan.FromMinutes(61));
        await SignInAsync(client, "anna@example.org");
        Assert.Equal(5, (await OutboxAsync(host)).Count);
    }

    [Fact]
    public async Task At_most_ten_mails_per_email_and_hour_are_sent_whatever_the_IPs()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        await using var host = Host();
        await SignUpAsync(Client(host, "198.51.100.1"), "Anna", "anna@example.org", "en");

        for (var ip = 2; ip <= 5; ip++)
        {
            var client = Client(host, "198.51.100." + ip);
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await SignInAsync(client, "anna@example.org")).StatusCode);
        }

        Assert.Equal(10, (await OutboxAsync(host)).Count); // 1 + 12 requested
        Assert.Equal(["email", "email", "email"], suppressed);
    }

    [Fact]
    public async Task At_most_twenty_requests_per_IP_bucket_and_hour_are_processed_and_IPv6_counts_per_64()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        await using var host = Host();
        var answers = new List<HttpResponseMessage>();
        // 20 requests from 20 different addresses of one /64.
        for (var i = 0; i < 15; i++)
            answers.Add(await SignUpAsync(Client(host, $"2001:db8:1:2::{i + 1:x}"), "User " + i, $"user{i}@example.org", "en"));
        for (var i = 0; i < 5; i++)
            answers.Add(await SignInAsync(Client(host, $"2001:db8:1:2:abcd::{i + 1:x}"), $"user{i}@example.org"));
        Assert.Equal(15, (await OrganizersAsync(host)).Count);
        Assert.Equal(20, (await OutboxAsync(host)).Count);

        answers.Add(await SignUpAsync(Client(host, "2001:db8:1:2:ffff:ffff:ffff:fffe"), "Over", "over@example.org", "en"));
        answers.Add(await SignInAsync(Client(host, "2001:db8:1:2::99"), "user0@example.org"));
        Assert.Equal(15, (await OrganizersAsync(host)).Count);
        Assert.Equal(20, (await OutboxAsync(host)).Count);
        await AssertSameAnswerAsync([.. answers]);
        Assert.Equal(["ip", "ip"], suppressed);

        // The next /64 is another bucket; the first one is served again after the hour.
        Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(Client(host, "2001:db8:1:3::1"), "Other", "other@example.org", "en")).StatusCode);
        Assert.Equal(16, (await OrganizersAsync(host)).Count);
        host.Clock.Advance(TimeSpan.FromMinutes(61));
        await SignUpAsync(Client(host, "2001:db8:1:2::1"), "Later", "later@example.org", "en");
        Assert.Equal(17, (await OrganizersAsync(host)).Count);
    }

    [Fact]
    public async Task Mails_to_exotic_domain_forms_go_to_the_ASCII_domain()
    {
        await using var host = Host();
        await SignUpAsync(Client(host, "198.51.100.1"), "Eve", "Victim@\uFF47\uFF4D\uFF41\uFF49\uFF4C\uFF0E\uFF43\uFF4F\uFF4D", "en"); // fullwidth gmail.com
        await SignUpAsync(Client(host, "198.51.100.2"), "Eve", "other@gmail\u3002com", "en");                                          // ideographic full stop

        Assert.Equal(["Victim@gmail.com", "other@gmail.com"], (await OutboxAsync(host)).Select(m => m.ToAddress));
        Assert.Equal(["victim@gmail.com", "other@gmail.com"], (await OrganizersAsync(host)).Select(o => o.EmailNormalized));
    }

    [Fact]
    public async Task One_IPv6_48_cannot_use_more_than_its_prefix_budget_and_limit_hits_are_reported_to_the_operator()
    {
        await using var host = Host();
        // 30 different /64 buckets inside 2001:db8:aa::/48, 4 requests each (under the 20 per /64).
        for (var net = 1; net <= 30; net++)
        {
            var client = Client(host, $"2001:db8:aa:{net:x}::1");
            for (var i = 0; i < 4; i++)
                Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(client, "U", $"u{net}-{i}@example.org", "en")).StatusCode);
        }
        Assert.Equal(100, (await OrganizersAsync(host)).Count);

        // Another /48 is unaffected.
        Assert.Equal(HttpStatusCode.Accepted, (await SignUpAsync(Client(host, "2001:db8:bb:1::1"), "U", "other@example.org", "en")).StatusCode);
        Assert.Equal(101, (await OrganizersAsync(host)).Count);

        using var ops = host.CreateClient();
        ops.DefaultRequestHeaders.Add("X-Api-Key", host.OrganizerKey);
        using var json = JsonDocument.Parse(await ops.GetStringAsync("/api/ops/email-mode"));
        Assert.Equal(20, json.RootElement.GetProperty("limitHitsLast24h").GetInt32());
        var hits = json.RootElement.GetProperty("limitHits");
        Assert.Equal(20, hits.GetProperty("perPrefix").GetInt32());
        Assert.Equal(0, hits.GetProperty("perIp").GetInt32());
        Assert.Equal(0, hits.GetProperty("global").GetInt32());
        Assert.Equal(0, hits.GetProperty("perEmail").GetInt32());
        Assert.Equal(0, hits.GetProperty("perEmailAndIp").GetInt32());

        // The counts cover the last 24 hours.
        host.Clock.Advance(TimeSpan.FromHours(25));
        using var later = JsonDocument.Parse(await ops.GetStringAsync("/api/ops/email-mode"));
        Assert.Equal(0, later.RootElement.GetProperty("limitHitsLast24h").GetInt32());
    }

    [Fact]
    public async Task The_global_hourly_mail_cap_suppresses_further_mails_with_a_warning_and_the_same_answer()
    {
        var suppressed = new List<string>();
        using var listener = SuppressionListener(suppressed);
        var logs = new CapturingLoggerProvider();
        await using var host = Host(configure: f => f.Settings["Accounts:GlobalMailsPerHour"] = "5",
            services: s => s.AddSingleton<ILoggerProvider>(logs));

        var answers = new List<HttpResponseMessage>();
        for (var i = 0; i < 7; i++)
            answers.Add(await SignUpAsync(Client(host, "198.51.100." + (i + 1)), "User", $"user{i}@example.org", "en"));

        Assert.Equal(5, (await OutboxAsync(host)).Count);
        await AssertSameAnswerAsync([.. answers]);
        Assert.Equal(["global", "global"], suppressed);
        using var ops = host.CreateClient();
        ops.DefaultRequestHeaders.Add("X-Api-Key", host.OrganizerKey);
        using var json = JsonDocument.Parse(await ops.GetStringAsync("/api/ops/email-mode"));
        Assert.Equal(2, json.RootElement.GetProperty("limitHits").GetProperty("global").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("limitHitsLast24h").GetInt32());
        var text = logs.Text();
        Assert.Contains("Warning", text);
        Assert.Contains("global", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user5@", text, StringComparison.Ordinal);
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
    public async Task A_database_failure_answers_503_unavailable_after_the_minimum_response_time()
    {
        await using var host = Host(minResponseMs: 150);
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await using (var db = Db(host))
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Organizers" RENAME TO "OrganizersGone" """);

        foreach (var call in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => SignUpAsync(client, "New", "new@example.org", "en"),
                     () => SignInAsync(client, "anna@example.org"),
                 })
        {
            var watch = Stopwatch.StartNew();
            var response = await call();
            Assert.True(watch.ElapsedMilliseconds >= 145, $"answered after {watch.ElapsedMilliseconds} ms");
            await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "unavailable");
        }
    }

    [Fact]
    public async Task Link_redemption_is_rate_limited_per_IP_with_rate_limited()
    {
        await using var host = Host(configure: f => f.Settings["Accounts:LinkRedeemsPerIpPerHour"] = "3");
        var client = Client(host);

        for (var i = 0; i < 3; i++)
            await AssertErrorAsync(await ConfirmAsync(client, "x" + i), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await RedeemAsync(client, "y"), (HttpStatusCode)429, "rate_limited");
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
        var response = await ConfirmAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"confirmed":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(host.Clock.GetUtcNow(), Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, token)).StatusCode);
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
        await AssertErrorAsync(await RedeemAsync(client, signIn), HttpStatusCode.BadRequest, "link_expired");

        host.Clock.Advance(TimeSpan.FromDays(14));
        await AssertErrorAsync(await ConfirmAsync(client, confirm), HttpStatusCode.BadRequest, "link_expired");
        Assert.Null(Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);
    }

    [Fact]
    public async Task Sign_in_link_is_single_use_and_proves_the_mailbox()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");
        var token = Link((await OutboxAsync(host))[1]).Token;

        var first = await RedeemAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(SessionCookie(first));
        Assert.Equal(host.Clock.GetUtcNow(), Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);

        var second = await RedeemAsync(client, token);
        await AssertErrorAsync(second, HttpStatusCode.BadRequest, "link_used");
        Assert.Null(SessionCookie(second));
    }

    [Fact]
    public async Task Concurrent_POST_redemptions_of_one_sign_in_link_give_exactly_one_session()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");
        var token = Link((await OutboxAsync(host))[1]).Token;

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RedeemAsync(client, token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK && SessionCookie(r) is not null);
        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "link_used");
    }

    [Fact]
    public async Task Wrong_purpose_unknown_and_missing_tokens_fail_with_link_invalid_and_sign_in_links_are_not_superseded()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var confirm = Link((await OutboxAsync(host))[0]).Token;
        await SignInAsync(client, "anna@example.org");
        var olderSignIn = Link((await OutboxAsync(host))[1]).Token;
        await SignInAsync(client, "anna@example.org");
        var newerSignIn = Link((await OutboxAsync(host))[2]).Token;

        await AssertErrorAsync(await RedeemAsync(client, confirm), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await ConfirmAsync(client, newerSignIn), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await RedeemAsync(client, "not-a-token"), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await PostAsync(client, "/api/account/signin/redeem", new { }), HttpStatusCode.BadRequest, "link_invalid");
        await AssertErrorAsync(await PostAsync(client, "/api/account/confirm", new { token = (string?)null }), HttpStatusCode.BadRequest, "link_invalid");
        Assert.Null(Assert.Single(await OrganizersAsync(host)).EmailConfirmedAt);

        // Each sign-in link lives on its own (15 minutes, single use): a newer request does not cancel an older link.
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, olderSignIn)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, newerSignIn)).StatusCode);
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
    public async Task A_conditional_update_lost_to_another_request_reports_the_real_state()
    {
        await using var host = Host();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LinkTokenService>();
        var subject = Guid.NewGuid();
        var ct = CancellationToken.None;
        Func<Guid, Task> Meanwhile(bool revoke) => async id =>
        {
            await using var other = Db(host);
            var now = host.Clock.GetUtcNow();
            if (revoke) await other.LinkTokens.Where(t => t.Id == id).ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now));
            else await other.LinkTokens.Where(t => t.Id == id).ExecuteUpdateAsync(x => x.SetProperty(t => t.UsedAt, now));
        };

        // Another request opens a reusable token between this request's read and its conditional update: still Ok.
        var raw = await service.IssueAsync(LinkPurpose.ParticipantInvite, subject, TimeSpan.FromDays(14), ct);
        service.BeforeMarkUsed = Meanwhile(revoke: false);
        var opened = await service.RedeemAsync(LinkPurpose.ParticipantInvite, raw, singleUse: false, ct);
        Assert.Equal((LinkStatus.Ok, subject), (opened.Status, opened.SubjectId));

        // A newer token supersedes it in that gap: the real state is reported.
        var raw2 = await service.IssueAsync(LinkPurpose.ParticipantInvite, subject, TimeSpan.FromDays(14), ct);
        service.BeforeMarkUsed = Meanwhile(revoke: true);
        var superseded = await service.RedeemAsync(LinkPurpose.ParticipantInvite, raw2, singleUse: false, ct);
        Assert.Equal((LinkStatus.Superseded, subject), (superseded.Status, superseded.SubjectId));

        // A single-use token used by the other request: Used.
        var raw3 = await service.IssueAsync(LinkPurpose.OrganizerSignIn, subject, TimeSpan.FromMinutes(15), ct);
        service.BeforeMarkUsed = Meanwhile(revoke: false);
        Assert.Equal(LinkStatus.Used, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, raw3, singleUse: true, ct)).Status);
    }

    [Fact]
    public async Task Issued_tokens_are_random_hashed_and_supersede_older_ones_of_the_purpose_and_subject_except_sign_in()
    {
        await using var host = Host();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LinkTokenService>();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ct = CancellationToken.None;

        var invite1 = await service.IssueAsync(LinkPurpose.ParticipantInvite, a, TimeSpan.FromDays(14), ct);
        var signInA1 = await service.IssueAsync(LinkPurpose.OrganizerSignIn, a, TimeSpan.FromMinutes(15), ct);
        var signInB = await service.IssueAsync(LinkPurpose.OrganizerSignIn, b, TimeSpan.FromMinutes(15), ct);
        var signInA2 = await service.IssueAsync(LinkPurpose.OrganizerSignIn, a, TimeSpan.FromMinutes(15), ct);
        var inviteB = await service.IssueAsync(LinkPurpose.ParticipantInvite, b, TimeSpan.FromDays(14), ct);

        foreach (var raw in new[] { invite1, signInA1, signInB, signInA2, inviteB })
        {
            Assert.Equal(43, raw.Length);
            Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(raw).Length);
        }
        Assert.Equal(5, new[] { invite1, signInA1, signInB, signInA2, inviteB }.Distinct().Count());
        var stored = await LinkTokensAsync(host);
        Assert.Contains(stored, t => t.TokenHash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(invite1))));
        Assert.DoesNotContain(stored, t => t.TokenHash.SequenceEqual(System.Buffers.Text.Base64Url.DecodeFromChars(invite1)));

        // Sign-in links are never superseded.
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInA1, true, ct)).Status);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInA2, true, ct)).Status);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.OrganizerSignIn, signInB, true, ct)).Status);

        // A reopened (used) invitation is still superseded by a newer one; another subject's is not.
        var opened = await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite1, false, ct);
        Assert.Equal((LinkStatus.Ok, a), (opened.Status, opened.SubjectId));
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite1, false, ct)).Status);
        var invite2 = await service.IssueAsync(LinkPurpose.ParticipantInvite, a, TimeSpan.FromDays(14), ct);
        var superseded = await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite1, false, ct);
        Assert.Equal((LinkStatus.Superseded, a), (superseded.Status, superseded.SubjectId));
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.ParticipantInvite, invite2, false, ct)).Status);
        Assert.Equal(LinkStatus.Ok, (await service.RedeemAsync(LinkPurpose.ParticipantInvite, inviteB, false, ct)).Status);
        Assert.Equal(LinkStatus.Unknown, (await service.RedeemAsync(LinkPurpose.OrganizerEmailConfirm, invite2, false, ct)).Status);
    }

    // ----- session -----

    [Fact]
    public async Task Session_cookie_is_HttpOnly_Secure_SameSite_Lax_and_lasts_30_days()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        await SignInAsync(client, "anna@example.org");

        var response = await RedeemAsync(client, Link((await OutboxAsync(host))[1]).Token);
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
        Assert.True(body.RootElement.GetProperty("emailConfirmed").GetBoolean());
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
        Assert.Equal("""{"name":"Anna","email":"Anna@Example.org","language":"sk","emailConfirmed":true,"emailMode":"simulated"}""",
            await me.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sign_out_clears_the_cookie_and_invalidates_every_copy_of_the_session()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");
        var otherDevice = await SignInSessionAsync(host, client, "anna@example.org");

        var response = await PostAsync(client, "/api/account/signout", new { }, cookie: cookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieAuth.CookieName + "=;", cleared, StringComparison.Ordinal);
        await AssertErrorAsync(await MeAsync(client, cookie), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await MeAsync(client, otherDevice), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Cookie_authenticated_state_changing_requests_without_the_header_or_json_are_refused()
    {
        await using var host = Host();
        var client = Client(host);
        await SignUpAsync(client, "Anna", "anna@example.org", "en");
        var cookie = await SignInSessionAsync(host, client, "anna@example.org");

        await AssertErrorAsync(await PostAsync(client, "/api/account/signout", new { }, cookie: cookie, header: false),
            HttpStatusCode.BadRequest, "csrf_failed");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signout", "{}", header: false, cookie: cookie, headerValue: "XMLHttpRequest"),
            HttpStatusCode.BadRequest, "csrf_failed");
        await AssertErrorAsync(await PostRawAsync(client, "/api/account/signout", "x", cookie: cookie, mediaType: "text/plain"),
            HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        // Other /api paths that receive the cookie need the header too.
        await AssertErrorAsync(await PostRawAsync(client, "/api/demo/events", "{}", header: false, cookie: cookie),
            HttpStatusCode.BadRequest, "csrf_failed");

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(client, cookie)).StatusCode); // still signed in
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/account/signout", new { }, cookie: cookie)).StatusCode);
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
        await ConfirmByMailAsync(host, client, "anna@example.org");
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
            f.Settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning"; // as shipped
        }, services: s => s.AddSingleton<ILoggerProvider>(logs));
        var client = Client(host);

        await SignUpAsync(client, "Anna", "secret.person@example.org", "en");
        var confirm = Link((await OutboxAsync(host))[0]).Token;
        await ConfirmAsync(client, confirm);
        await SignUpAsync(client, "Anna", "secret.person@example.org", "en");
        await SignInAsync(client, "secret.person@example.org");
        await SignInAsync(client, "secret.person@example.org"); // over the limit: logged at debug level
        var signIn = Link((await OutboxAsync(host)).Last()).Token;
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, signIn)).StatusCode);

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

    /// <summary>A JSON POST as the front end sends it (the browser header unless <paramref name="header"/> is false).</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? cookie = null, bool header = true) =>
        PostRawAsync(client, path, JsonSerializer.Serialize(body), header, cookie: cookie);

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string body, bool header = true,
        string mediaType = "application/json", string? cookie = null, string? headerValue = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (header) request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        else if (headerValue is not null) request.Headers.Add(BrowserApiHeaders.HeaderName, headerValue);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SignUpAsync(HttpClient client, string name, string email, string language) =>
        PostAsync(client, "/api/account/signup", new { name, email, language });

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string email) =>
        PostAsync(client, "/api/account/signin", new { email });

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string token) =>
        PostAsync(client, "/api/account/confirm", new { token });

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string token) =>
        PostAsync(client, "/api/account/signin/redeem", new { token });

    private static async Task ConfirmByMailAsync(ApiFactory host, HttpClient client, string normalizedEmail)
    {
        var mail = (await OutboxAsync(host)).Last(m =>
            m.Purpose == EmailPurpose.OrganizerEmailConfirmation && m.ToAddress.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, Link(mail).Token)).StatusCode);
    }

    /// <summary>Requests a sign-in link, redeems it and returns the session cookie (<c>name=value</c>).</summary>
    private static async Task<string> SignInSessionAsync(ApiFactory host, HttpClient client, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await SignInAsync(client, email)).StatusCode);
        var mail = (await OutboxAsync(host)).Last(m => m.Purpose == EmailPurpose.SignIn);
        var response = await RedeemAsync(client, Link(mail).Token);
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

    private static Task<HttpResponseMessage> MeAsync(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/me");
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
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
            "unavailable" => "nedostupné",
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
