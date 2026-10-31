using System.Text;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MimeKit;
using PositionTracker.Api.Email;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Templates, sanitising, the MIME mapping and error classification of the SMTP sender (no SMTP server is ever
/// contacted), the logging sender, and the outbox worker against a PostgreSQL container with a fake sender.
/// </summary>
public sealed class EmailTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private const string ConfirmUrl = "https://tracker.example.org/confirm?t=abc123&x=1";
    private const string InjectedName = "Jozef\r\nBcc: x@y.z";
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    // ----- templates -----

    [Theory]
    [InlineData("sk", "Pozvánka na", "Potvrdiť účasť", "Šport", "cyklistika", "14 dní", "v mene organizátora", "7. 11. 2026")]
    [InlineData("en", "Invitation to", "Confirm participation", "Sport", "cycling", "14 days", "on behalf of the organizer", "7 November 2026")]
    public void Invitation_renders_in_the_language_with_link_and_event_data(
        string language, string subjectStart, string button, string sportLabel, string sport, string validity, string footer, string date)
    {
        var message = EmailTemplates.Invitation(Participant("Anna Nováková"), Event(), Organizer(), ConfirmUrl, language);

        Assert.Equal(language, message.Language);
        Assert.Equal(EmailPurpose.Invitation, message.Purpose);
        Assert.Equal("anna@example.org", message.ToAddress);
        Assert.Equal("Anna Nováková", message.ToName);
        Assert.StartsWith(subjectStart, message.Subject);
        Assert.Contains("Jarný okruh", message.Subject);
        Assert.Equal("organizer@example.org", message.ReplyToAddress);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Jarný okruh", body);
            Assert.Contains("Žilina – Bôrik", body);
            Assert.Contains("Peter Organizátor", body);
            Assert.Contains(date, body); // 7 Nov 2026 10:00 Bratislava (CET) = 09:00 UTC
            Assert.Contains("10:00", body);
            Assert.Contains(sportLabel, body);
            Assert.Contains(sport, body);
            Assert.Contains(button, body);
            Assert.Contains(validity, body);
            Assert.Contains(footer, body);
        }
        Assert.Contains(ConfirmUrl, message.TextBody);
        Assert.Contains("href=\"https://tracker.example.org/confirm?t=abc123&amp;x=1\"", message.HtmlBody);
    }

    [Fact]
    public void Invitation_falls_back_to_English_for_an_unknown_language()
    {
        var message = EmailTemplates.Invitation(Participant("Anna"), Event(), Organizer(), ConfirmUrl, "de");
        Assert.Equal("en", message.Language);
        Assert.StartsWith("Invitation to", message.Subject);
    }

    [Theory]
    [InlineData("sk", "Prihlásenie", "Prihlásiť sa", "15 minút")]
    [InlineData("en", "Sign in", "Sign in", "15 minutes")]
    public void Sign_in_renders_in_the_organizer_language(string language, string subject, string button, string validity)
    {
        var organizer = Organizer();
        organizer.Language = language;
        var message = EmailTemplates.SignIn(organizer, "https://tracker.example.org/signin?t=s1");

        Assert.Equal(EmailPurpose.SignIn, message.Purpose);
        Assert.Equal(language, message.Language);
        Assert.Equal("organizer@example.org", message.ToAddress);
        Assert.Contains(subject, message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains(button, body);
            Assert.Contains(validity, body);
        }
        Assert.Contains("https://tracker.example.org/signin?t=s1", message.TextBody);
        Assert.Contains("href=\"https://tracker.example.org/signin?t=s1\"", message.HtmlBody);
    }

    [Theory]
    [InlineData("sk", "Potvrďte svoju e-mailovú adresu", "Potvrdiť e-mail")]
    [InlineData("en", "Confirm your email address", "Confirm email")]
    public void Organizer_email_confirmation_renders_in_the_organizer_language(string language, string subject, string button)
    {
        var organizer = Organizer();
        organizer.Language = language;
        var message = EmailTemplates.ConfirmOrganizerEmail(organizer, "https://tracker.example.org/c?t=c1");

        Assert.Equal(EmailPurpose.OrganizerEmailConfirmation, message.Purpose);
        Assert.Equal(subject, message.Subject);
        Assert.Contains(button, message.HtmlBody);
        Assert.Contains(button, message.TextBody);
        Assert.Contains("Peter Organizátor", message.TextBody);
        Assert.Contains("https://tracker.example.org/c?t=c1", message.TextBody);
    }

    [Fact]
    public void User_values_are_html_escaped()
    {
        var evt = Event();
        evt.Name = "<script>alert(1)</script> & \"Run\"";
        evt.Location = "<b>Town</b>";
        var organizer = Organizer();
        organizer.Name = "Eve <img src=x onerror=alert(1)>";
        var message = EmailTemplates.Invitation(Participant("<i>Anna</i>"), evt, organizer, ConfirmUrl, "en");

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.DoesNotContain("<i>Anna", message.HtmlBody);
        Assert.DoesNotContain("<b>Town", message.HtmlBody);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;Run&quot;", message.HtmlBody);
        // Plain text is not HTML: values stay as typed.
        Assert.Contains("<script>alert(1)</script>", message.TextBody);
    }

    [Fact]
    public void Header_injection_characters_never_reach_name_subject_or_mime_headers()
    {
        var evt = Event();
        evt.Name = "Race\r\nBcc: evil@example.net" + (char)0 + LineSeparator + "X";
        var organizer = Organizer();
        organizer.Name = InjectedName;
        var message = EmailTemplates.Invitation(Participant(InjectedName), evt, organizer, ConfirmUrl, "sk");

        foreach (var header in new[] { message.ToName, message.Subject, message.ReplyToName ?? "" })
            Assert.DoesNotContain(header, c => char.IsControl(c) || c == LineSeparator || c == ParagraphSeparator);
        Assert.Equal("Jozef Bcc: x@y.z", message.ToName);
        Assert.Equal("Pozvánka na Race Bcc: evil@example.net X", message.Subject);

        var mime = SmtpEmailSender.ToMime(message with { ToName = InjectedName, Subject = "S\r\nBcc: z@y.z" }, Options());
        var raw = Render(mime);
        var headers = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.DoesNotContain("\r\nBcc:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(mime.Headers.Contains(HeaderId.Bcc));
        Assert.Single(mime.To.Mailboxes);
        Assert.Equal("Jozef Bcc: x@y.z", mime.To.Mailboxes.Single().Name);
        Assert.Equal("S Bcc: z@y.z", mime.Subject);
    }

    [Fact]
    public void Long_values_are_truncated()
    {
        var evt = Event();
        evt.Name = new string('E', 500);
        var message = EmailTemplates.Invitation(Participant(new string('N', 500)), evt, Organizer(), ConfirmUrl, "en");

        Assert.True(message.Subject.Length <= EmailText.MaxSubjectLength, message.Subject.Length.ToString());
        Assert.True(message.ToName.Length <= EmailText.MaxNameLength, message.ToName.Length.ToString());
        Assert.EndsWith("…", message.ToName);
        Assert.DoesNotContain(new string('E', 200), message.HtmlBody);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var value = new string('a', EmailText.MaxNameLength - 2) + "😀😀";
        var cut = EmailText.SingleLine(value, EmailText.MaxNameLength);
        Assert.True(cut.Length <= EmailText.MaxNameLength);
        Assert.EndsWith("…", cut);
        Assert.False(char.IsHighSurrogate(cut[^2]), "a lone high surrogate before the ellipsis");
        Assert.Equal(cut, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut))); // valid UTF-16 round-trips
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.org/x")]
    public void Templates_reject_links_that_are_not_absolute_http(string url)
    {
        Assert.Throws<ArgumentException>(() => EmailTemplates.SignIn(Organizer(), url));
    }

    [Fact]
    public void Links_are_built_from_the_public_base_url_and_an_escaped_token()
    {
        Assert.Equal("https://tracker.example.org/app/confirm?token=a%2Bb%2Fc%3D",
            EmailLinks.Build("https://tracker.example.org/app/", "confirm", "a+b/c="));
        Assert.Equal("https://tracker.example.org/signin?token=x",
            EmailLinks.Build("https://tracker.example.org", "/signin", "x"));
        Assert.Throws<InvalidOperationException>(() => EmailLinks.Build("", "signin", "x"));
    }

    // ----- SMTP mapping and classification -----

    [Fact]
    public void Mime_mapping_sets_sender_recipient_reply_to_and_both_bodies()
    {
        var message = EmailTemplates.Invitation(Participant("Anna"), Event(), Organizer(), ConfirmUrl, "sk");
        var mime = SmtpEmailSender.ToMime(message, Options());

        Assert.Equal("noreply@example.org", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Tracker Test", mime.From.Mailboxes.Single().Name);
        Assert.Equal("anna@example.org", mime.To.Mailboxes.Single().Address);
        Assert.Equal("organizer@example.org", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal(message.Subject, mime.Subject);
        Assert.Equal(message.TextBody.ReplaceLineEndings(), mime.TextBody!.ReplaceLineEndings());
        Assert.Equal(message.HtmlBody, mime.HtmlBody);
        Assert.Equal("sk", mime.Headers[HeaderId.ContentLanguage]);
    }

    [Theory]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, 550, "550 5.1.1 <anna@example.org> user unknown", true, "5.1.1")]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, 550, "mailbox anna@example.org unavailable", true, null)]
    [InlineData(SmtpErrorCode.MessageNotAccepted, 552, "552 5.3.4 message too big", true, "5.3.4")]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, 550, "550 5.1.10 recipient anna@example.org not found", true, "5.1.10")]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, 550, "550 5.7.1 <anna@example.org>: Relay access denied", false, "5.7.1")]
    [InlineData(SmtpErrorCode.MessageNotAccepted, 554, "554 5.7.1 Service unavailable; client host blocked", false, "5.7.1")]
    [InlineData(SmtpErrorCode.MessageNotAccepted, 554, "554 Transaction failed", false, null)]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, 554, "554 mx.example.org ESMTP not accepting messages", false, null)]
    [InlineData(SmtpErrorCode.SenderNotAccepted, 553, "553 sender not allowed", false, null)]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, 450, "450 4.2.1 <anna@example.org> try later", false, "4.2.1")]
    public void Smtp_replies_are_classified_without_personal_data(
        SmtpErrorCode code, int status, string reply, bool permanent, string? enhanced)
    {
        var e = SmtpEmailSender.Classify(new SmtpCommandException(code, (SmtpStatusCode)status,
            new MailboxAddress("Anna", "anna@example.org"), reply));

        Assert.Equal(permanent, e.Permanent);
        Assert.StartsWith($"SMTP {status} ", e.Message);
        if (enhanced is not null) Assert.Contains(enhanced, e.Message);
        Assert.DoesNotContain("@", e.Message);
        Assert.DoesNotContain("anna", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relay", e.Message, StringComparison.OrdinalIgnoreCase); // no server text
    }

    [Fact]
    public void Other_errors_are_classified_without_personal_data()
    {
        var io = SmtpEmailSender.Classify(new IOException("connection reset for anna@example.org"));
        var badAddress = SmtpEmailSender.Classify(new ParseException("bad address anna@", 0, 0));
        var auth = SmtpEmailSender.Classify(new MailKit.Security.AuthenticationException("user anna@example.org"));

        Assert.False(io.Permanent);
        Assert.True(badAddress.Permanent);
        Assert.False(auth.Permanent);
        foreach (var e in new[] { io, badAddress, auth })
            Assert.DoesNotContain("@", e.Message);
    }

    [Fact]
    public async Task An_accepted_message_stays_sent_when_QUIT_fails_or_shutdown_cancels_it()
    {
        using var cts = new CancellationTokenSource();
        var client = FakeSmtpClient.Create(onSend: cts.Cancel, disconnectThrows: true);
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(Options(o => o.Smtp.Host = "smtp.invalid")),
            () => client);
        var message = EmailTemplates.SignIn(Organizer(), "https://tracker.example.org/s?t=1");

        await sender.SendAsync(message, cts.Token); // the token is cancelled while the server accepts the message

        var calls = FakeSmtpClient.Calls(client);
        Assert.Equal(["ConnectAsync", "SendAsync", "DisconnectAsync"], calls.Select(c => c.Method));
        Assert.False(calls[^1].Token.CanBeCanceled); // QUIT does not use the shutdown token
    }

    [Fact]
    public async Task A_failure_before_acceptance_is_classified()
    {
        var client = FakeSmtpClient.Create(sendThrows: new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxUnavailable, new MailboxAddress("", "anna@example.org"), "550 5.1.1 unknown"));
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(Options(o => o.Smtp.Host = "smtp.invalid")),
            () => client);

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(
            EmailTemplates.SignIn(Organizer(), "https://tracker.example.org/s?t=1"), CancellationToken.None));
        Assert.True(ex.Permanent);
    }

    [Fact]
    public async Task Logging_sender_logs_only_domain_purpose_and_outbox_id_and_warns_on_the_first_send()
    {
        var logs = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var sender = new LoggingEmailSender(factory.CreateLogger<LoggingEmailSender>());
        var id = Guid.NewGuid();
        var message = EmailTemplates.Invitation(Participant("Anna Nováková"), Event(), Organizer(), ConfirmUrl, "sk")
            with { OutboxId = id };

        await sender.SendAsync(message, CancellationToken.None);
        await sender.SendAsync(message, CancellationToken.None);

        Assert.True(sender.IsSimulated);
        var lines = logs.Lines;
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Warning:", lines[0]);
        Assert.Contains("simulated", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("Information:", l));
        Assert.Contains("example.org", lines[1]);
        Assert.Contains(EmailPurpose.Invitation, lines[1]);
        Assert.Contains(id.ToString(), lines[1]);
        foreach (var line in lines)
            foreach (var secret in new[] { "anna@", "Anna", "Nováková", "abc123", "Jarný", "Peter" })
                Assert.DoesNotContain(secret, line);
    }

    // ----- options and startup -----

    [Theory]
    [InlineData(false, false, "Email:AllowSimulated")]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    public void Simulated_mode_outside_Development_needs_the_explicit_flag(bool development, bool allowSimulated, string? error)
    {
        var result = new EmailOptionsValidator(development).Validate(null, new EmailOptions { AllowSimulated = allowSimulated });
        AssertValidation(result, error);
    }

    [Theory]
    [InlineData(465, true, "", "UseStartTls=false")]
    [InlineData(587, false, "user", "clear text")]
    [InlineData(25, false, "user", "clear text")]
    [InlineData(465, false, "user", null)]
    [InlineData(587, true, "user", null)]
    [InlineData(25, false, "", null)]
    [InlineData(0, true, "", "Port")]
    public void Port_and_TLS_combinations_are_validated(int port, bool startTls, string user, string? error)
    {
        var options = Options(o =>
        {
            o.Smtp.Host = "smtp.invalid";
            o.Smtp.Port = port;
            o.Smtp.UseStartTls = startTls;
            o.Smtp.User = user;
        });
        AssertValidation(new EmailOptionsValidator(false).Validate(null, options), error);
    }

    [Theory]
    [InlineData("https://tracker.example.org", false, null)]
    [InlineData("http://tracker.example.org", false, "https outside Development")]
    [InlineData("http://localhost:5298", true, null)]
    [InlineData("https://tracker.example.org/app?x=1", false, "query or a fragment")]
    [InlineData("https://tracker.example.org/#top", false, "query or a fragment")]
    [InlineData("ftp://tracker.example.org", true, "absolute http or https")]
    [InlineData("tracker.example.org", true, "absolute http or https")]
    public void Public_base_url_is_validated(string url, bool development, string? error)
    {
        var options = Options(o =>
        {
            o.Smtp.Host = "smtp.invalid";
            o.PublicBaseUrl = url;
        });
        AssertValidation(new EmailOptionsValidator(development).Validate(null, options), error);
    }

    [Fact]
    public void A_missing_event_time_zone_fails_validation_instead_of_falling_back_to_UTC()
    {
        var result = new EmailOptionsValidator(true, () => null).Validate(null, new EmailOptions());
        AssertValidation(result, "Europe/Bratislava");
        Assert.Null(EmailTemplates.ResolveTimeZone(["No/Such_Zone"]));
        Assert.NotNull(EmailTemplates.EventTimeZoneOrNull);
    }

    [Fact]
    public void Production_without_an_smtp_host_fails_to_start()
    {
        using var factory = new ApiFactory { SeedTestEvent = false, EnvironmentName = "Production" };
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("Email:AllowSimulated", ex.ToString());
    }

    [Fact]
    public void Production_with_AllowSimulated_starts_in_simulated_mode()
    {
        using var factory = new ApiFactory { SeedTestEvent = false, EnvironmentName = "Production" };
        factory.Settings["Email:AllowSimulated"] = "true";
        Assert.True(factory.Services.GetRequiredService<IEmailMode>().IsSimulated);
        Assert.Equal("simulated", factory.Services.GetRequiredService<IEmailMode>().Mode);
    }

    [Fact]
    public void Development_without_an_smtp_host_starts_in_simulated_mode()
    {
        using var factory = new ApiFactory { SeedTestEvent = false, EnvironmentName = "Development" };
        Assert.True(factory.Services.GetRequiredService<IEmailMode>().IsSimulated);
    }

    [Fact]
    public void Production_with_an_smtp_host_runs_in_smtp_mode_with_a_normalised_base_url()
    {
        using var factory = new ApiFactory { SeedTestEvent = false, EnvironmentName = "Production" };
        factory.Settings["Email:Smtp:Host"] = "smtp.invalid";
        factory.Settings["Email:From"] = "noreply@example.org";
        factory.Settings["Email:PublicBaseUrl"] = " https://tracker.example.org/app/ ";
        Assert.Equal("smtp", factory.Services.GetRequiredService<IEmailMode>().Mode);
        Assert.Equal("https://tracker.example.org/app",
            factory.Services.GetRequiredService<IOptions<EmailOptions>>().Value.PublicBaseUrl);
    }

    [Fact]
    public async Task Email_mode_endpoint_requires_the_operator_key()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/email-mode")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ops/email-mode")).StatusCode);
    }

    [Fact]
    public async Task Email_mode_endpoint_reports_the_mode_without_a_database()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.OrganizerKey);

        var body = await client.GetStringAsync("/api/ops/email-mode");

        Assert.Equal("""{"mode":"simulated","simulatedLast24h":null,"limitHitsLast24h":0,"limitHits":{"global":0,"perEmail":0,"perEmailAndIp":0,"perIp":0,"perPrefix":0,"total":0},"invitationLimitHits":{"global":0,"perOrganizer":0,"perAddressHour":0,"perAddressDay":0,"total":0}}""", body);
    }

    [Fact]
    public async Task Email_mode_endpoint_counts_simulated_rows_of_the_last_24_hours()
    {
        var db = await NewDatabaseAsync();
        await EnqueueInvitationAsync(db, ApiFactory.T0);
        await EnqueueInvitationAsync(db, ApiFactory.T0, "second@example.org");
        await using (var ctx = new AppDbContext(PostgresFixture.Options(db)))
        {
            // A simulated row finished two days ago does not count.
            var old = EmailOutbox.Create(EmailTemplates.SignIn(await ctx.Organizers.SingleAsync(), "https://tracker.example.org/s?t=1"),
                null, ApiFactory.T0.AddDays(-2));
            old.Status = EmailOutboxStatus.Simulated;
            old.FinishedAt = ApiFactory.T0.AddDays(-2);
            ctx.Outbox.Add(old);
            await ctx.SaveChangesAsync();
        }
        using var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["ConnectionStrings:Default"] = db;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.OrganizerKey);

        var body = "";
        for (var i = 0; i < 300 && !body.Contains("\"simulatedLast24h\":2"); i++)
        {
            if (i > 0) await Task.Delay(50);
            body = await client.GetStringAsync("/api/ops/email-mode");
        }
        Assert.Equal("""{"mode":"simulated","simulatedLast24h":2,"limitHitsLast24h":0,"limitHits":{"global":0,"perEmail":0,"perEmailAndIp":0,"perIp":0,"perPrefix":0,"total":0},"invitationLimitHits":{"global":0,"perOrganizer":0,"perAddressHour":0,"perAddressDay":0,"total":0}}""", body);
    }

    private static void AssertValidation(ValidateOptionsResult result, string? error)
    {
        if (error is null)
        {
            Assert.True(result.Succeeded, result.FailureMessage);
            return;
        }
        Assert.True(result.Failed);
        Assert.Contains(error, result.FailureMessage);
    }

    // ----- outbox worker -----

    [Fact]
    public async Task Worker_sends_a_due_row_marks_it_sent_and_drops_the_bodies()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock);
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => o.Outbox.RetentionDays = 7);

        Assert.Equal(1, await worker.ProcessDueAsync(CancellationToken.None));

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(id, sent.Message.OutboxId);
        Assert.Equal("anna@example.org", sent.Message.ToAddress);
        Assert.Equal("organizer@example.org", sent.Message.ReplyToAddress);
        Assert.Contains(ConfirmUrl, sent.Message.TextBody);
        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Sent, row!.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(T0, row.SentAt);
        Assert.Equal("", row.HtmlBody); // the link is a credential until it expires
        Assert.Equal("", row.TextBody);
        Assert.Equal(0, await worker.ProcessDueAsync(CancellationToken.None));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Worker_retries_a_transient_failure_three_times_with_growing_delay()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock, Transient(), Transient(), Transient());
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => { o.Outbox.MaxAttempts = 5; o.Outbox.RetryBaseSeconds = 30; });

        for (var i = 0; i < 20 && sender.Sent.Count < 4; i++)
        {
            await worker.ProcessDueAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(4, sender.Sent.Count);
        var delays = sender.Sent.Zip(sender.Sent.Skip(1), (a, b) => b.At - a.At).ToList();
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)], delays);
        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Sent, row!.Status);
        Assert.Equal(4, row.Attempts);
        Assert.Equal(ParticipantStatus.Invited, (await ParticipantAsync(db)).Status);
    }

    [Fact]
    public async Task Worker_does_not_send_before_the_retry_time()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock, Transient());
        await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => o.Outbox.RetryBaseSeconds = 30);

        await worker.ProcessDueAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await worker.ProcessDueAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await worker.ProcessDueAsync(CancellationToken.None));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Permanent_failure_marks_the_row_failed_and_the_invited_participant_EmailFailed()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock, new EmailDeliveryException(true, "SMTP 550 RecipientNotAccepted"));
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => o.Outbox.RetentionDays = 7);

        await worker.ProcessDueAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        await worker.ProcessDueAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Failed, row!.Status);
        Assert.Equal("SMTP 550 RecipientNotAccepted", row.LastError);
        Assert.Equal("", row.HtmlBody);
        Assert.Equal(ParticipantStatus.EmailFailed, (await ParticipantAsync(db)).Status);
    }

    [Fact]
    public async Task Failure_of_a_non_invitation_leaves_participants_alone()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock, new EmailDeliveryException(true, "SMTP 550"));
        await using (var ctx = new AppDbContext(PostgresFixture.Options(db)))
        {
            var organizer = await ctx.Organizers.SingleAsync();
            var participant = Participant("Anna");
            participant.EventId = (await ctx.Events.SingleAsync()).Id;
            ctx.Participants.Add(participant);
            ctx.Outbox.Add(EmailOutbox.Create(EmailTemplates.SignIn(organizer, "https://tracker.example.org/s?t=1"),
                null, clock.GetUtcNow()));
            await ctx.SaveChangesAsync();
        }

        await Worker(db, sender, clock).ProcessDueAsync(CancellationToken.None);

        Assert.Equal(ParticipantStatus.Invited, (await ParticipantAsync(db)).Status);
    }

    [Fact]
    public async Task Transient_failures_become_Failed_after_the_last_attempt()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock, Transient(), Transient(), Transient(), Transient(), Transient());
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => { o.Outbox.MaxAttempts = 3; o.Outbox.RetentionDays = 7; });

        for (var i = 0; i < 10; i++)
        {
            await worker.ProcessDueAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(3, sender.Sent.Count);
        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Failed, row!.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal("SMTP 451 transient Gave up after 3 attempts.", row.LastError);
        Assert.Equal(ParticipantStatus.EmailFailed, (await ParticipantAsync(db)).Status);
    }

    [Fact]
    public async Task A_crash_after_claiming_resends_at_most_until_the_attempts_run_out()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock);
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => { o.Outbox.MaxAttempts = 2; o.Outbox.RetentionDays = 7; });

        // Two "crashes": the row was claimed (and possibly handed to the SMTP server) but the outcome never saved.
        Assert.Single(await worker.ClaimAsync(CancellationToken.None));
        Assert.Empty(await worker.ClaimAsync(CancellationToken.None)); // leased, not due again yet
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(await worker.ClaimAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, await worker.ProcessDueAsync(CancellationToken.None));

        Assert.Empty(sender.Sent); // no third attempt: the outcome of the earlier ones is unknown
        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Failed, row!.Status);
        Assert.Contains("unknown", row.LastError);
    }

    [Fact]
    public async Task Simulated_sender_marks_rows_Simulated_not_Sent()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, new LoggingEmailSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<LoggingEmailSender>.Instance),
            clock, o => o.Outbox.RetentionDays = 7);

        await worker.ProcessDueAsync(CancellationToken.None);

        var row = await RowAsync(db, id);
        Assert.Equal(EmailOutboxStatus.Simulated, row!.Status);
        Assert.Null(row.SentAt);
        Assert.Equal(T0, row.FinishedAt);
    }

    [Fact]
    public async Task Finished_rows_are_kept_for_the_retention_period_then_removed()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock);
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());
        var worker = Worker(db, sender, clock, o => o.Outbox.RetentionDays = 7);

        await worker.ProcessDueAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        await worker.ProcessDueAsync(CancellationToken.None);
        Assert.NotNull(await RowAsync(db, id));
        clock.Advance(TimeSpan.FromSeconds(1));
        await worker.ProcessDueAsync(CancellationToken.None);
        Assert.Null(await RowAsync(db, id));
    }

    [Fact]
    public async Task With_zero_retention_a_sent_row_is_removed_at_once()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var id = await EnqueueInvitationAsync(db, clock.GetUtcNow());

        await Worker(db, new RecordingSender(clock), clock, o => o.Outbox.RetentionDays = 0)
            .ProcessDueAsync(CancellationToken.None);

        Assert.Null(await RowAsync(db, id));
    }

    [Fact]
    public async Task Hosted_worker_sends_queued_rows_while_running()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var sender = new RecordingSender(clock);
        await EnqueueInvitationAsync(db, clock.GetUtcNow());
        using var worker = Worker(db, sender, clock, o => o.Outbox.RetentionDays = 7);

        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent.Count == 1);
        await EnqueueInvitationAsync(db, clock.GetUtcNow(), "second@example.org");
        // Step the clock by the poll interval until the worker's next round picks the second row up.
        await WaitUntilAsync(() => sender.Sent.Count == 2, () => clock.Advance(TimeSpan.FromSeconds(5)));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        Assert.Equal("second@example.org", sender.Sent[1].Message.ToAddress);
    }

    [Fact]
    public void Without_an_smtp_host_the_app_simulates_email()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        Assert.IsType<LoggingEmailSender>(factory.Services.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void With_an_smtp_host_the_app_sends_through_smtp()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["Email:Smtp:Host"] = "smtp.invalid";
        factory.Settings["Email:From"] = "noreply@example.org";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        Assert.IsType<SmtpEmailSender>(factory.Services.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void An_smtp_host_without_a_sender_address_fails_at_startup()
    {
        using var factory = new ApiFactory { SeedTestEvent = false };
        factory.Settings["Email:Smtp:Host"] = "smtp.invalid";
        factory.Settings["Email:PublicBaseUrl"] = "https://tracker.example.org";
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
        Assert.Contains("Email:From", ex.ToString());
    }

    [Fact]
    public async Task Concurrent_claims_never_take_the_same_row()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        for (var i = 0; i < 12; i++) await EnqueueInvitationAsync(db, T0, $"p{i}@example.org");
        var a = Worker(db, new RecordingSender(clock), clock, o => o.Outbox.BatchSize = 5);
        var b = Worker(db, new RecordingSender(clock), clock, o => o.Outbox.BatchSize = 5);

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => (i % 2 == 0 ? a : b).ClaimAsync(CancellationToken.None)));

        var ids = claims.SelectMany(c => c).Select(r => r.Id).ToList();
        Assert.Equal(12, ids.Count);
        Assert.Equal(12, ids.Distinct().Count());
    }

    [Fact]
    public async Task A_claim_skips_rows_locked_by_another_transaction_instead_of_waiting()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        await EnqueueInvitationAsync(db, T0);
        var worker = Worker(db, new RecordingSender(clock), clock);

        await using (var other = new AppDbContext(PostgresFixture.Options(db)))
        {
            await using var tx = await other.Database.BeginTransactionAsync();
            await other.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "Outbox" FOR UPDATE""");
            Assert.Empty(await worker.ClaimAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            await tx.RollbackAsync();
        }

        Assert.Single(await worker.ClaimAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Worker_logs_contain_no_address_name_subject_or_link()
    {
        var db = await NewDatabaseAsync();
        var clock = new FakeTimeProvider(T0);
        var logs = new ListLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        var sender = new RecordingSender(clock, Transient(), new EmailDeliveryException(true, "SMTP 550 5.1.1 RecipientNotAccepted"));
        await EnqueueInvitationAsync(db, T0);
        var worker = Worker(db, sender, clock, o => o.Outbox.RetentionDays = 7, loggers.CreateLogger<EmailOutboxWorker>());

        for (var i = 0; i < 3; i++)
        {
            await worker.ProcessDueAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(2, logs.Lines.Count); // one retry warning, one failure warning
        foreach (var line in logs.Lines)
            foreach (var secret in new[] { "anna", "Anna", "example.org", "abc123", "confirm", "Jarný", "Pozvánka", "Peter" })
                Assert.DoesNotContain(secret, line);
    }

    // ----- helpers -----

    /// <summary>
    /// A fake <see cref="ISmtpClient"/> (DispatchProxy): records calls with their cancellation token; SendAsync
    /// succeeds (running <c>onSend</c>) or throws <c>sendThrows</c>; DisconnectAsync can throw.
    /// </summary>
    public class FakeSmtpClient : System.Reflection.DispatchProxy
    {
        private readonly List<(string Method, CancellationToken Token)> _calls = [];
        private Action? _onSend;
        private Exception? _sendThrows;
        private bool _disconnectThrows;

        public static ISmtpClient Create(Action? onSend = null, bool disconnectThrows = false, Exception? sendThrows = null)
        {
            var client = Create<ISmtpClient, FakeSmtpClient>();
            var fake = (FakeSmtpClient)(object)client;
            fake._onSend = onSend;
            fake._sendThrows = sendThrows;
            fake._disconnectThrows = disconnectThrows;
            return client;
        }

        public static IReadOnlyList<(string Method, CancellationToken Token)> Calls(ISmtpClient client) =>
            ((FakeSmtpClient)(object)client)._calls.ToList();

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            var name = method!.Name;
            if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)
                || name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)
                || name == "Dispose")
                return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
            var token = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
            _calls.Add((name, token));
            switch (name)
            {
                case "SendAsync":
                    if (_sendThrows is not null) return Task.FromException<string>(_sendThrows);
                    _onSend?.Invoke();
                    return Task.FromResult("250 2.0.0 OK");
                case "DisconnectAsync":
                    return _disconnectThrows ? Task.FromException(new OperationCanceledException(token)) : Task.CompletedTask;
                default:
                    return method.ReturnType == typeof(Task) ? Task.CompletedTask
                        : throw new NotSupportedException("Unexpected SMTP client call " + name);
            }
        }
    }

    private static EmailOptions Options(Action<EmailOptions>? configure = null)
    {
        var options = new EmailOptions
        {
            From = "noreply@example.org",
            FromName = "Tracker Test",
            PublicBaseUrl = "https://tracker.example.org",
        };
        configure?.Invoke(options);
        return options;
    }

    private static EmailOutboxWorker Worker(string db, IEmailSender sender, TimeProvider clock, Action<EmailOptions>? configure = null,
        ILogger<EmailOutboxWorker>? logger = null) =>
        new(new PooledDbContextFactory<AppDbContext>(PostgresFixture.Options(db)), sender,
            Microsoft.Extensions.Options.Options.Create(Options(configure)),
            Microsoft.Extensions.Options.Options.Create(new PersistenceOptions { ConnectionString = db }),
            clock, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailOutboxWorker>.Instance);

    private static EmailDeliveryException Transient() => new(false, "SMTP 451 transient");

    private static OrganizerEntity Organizer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Peter Organizátor",
        Email = "organizer@example.org",
        EmailNormalized = "organizer@example.org",
        Language = "sk",
        CreatedAt = T0,
    };

    private static EventEntity Event() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Jarný okruh",
        Location = "Žilina – Bôrik",
        StartTime = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero),
        Sport = Sport.Cycling,
        State = EventState.Draft,
        Laps = 1,
        SpectatorDelaySeconds = 30,
        CreatedAt = T0,
    };

    private static ParticipantEntity Participant(string name, string email = "anna@example.org") => new()
    {
        Id = Guid.NewGuid(),
        Bib = "1",
        Name = name,
        Email = email,
        EmailNormalized = email.ToLowerInvariant(),
        Gender = "female",
        Language = "sk",
        Status = ParticipantStatus.Invited,
        CreatedAt = T0,
    };

    private async Task<string> NewDatabaseAsync()
    {
        var cs = postgres.NewDatabase();
        await using var ctx = new AppDbContext(PostgresFixture.Options(cs));
        await ctx.Database.MigrateAsync();
        var organizer = Organizer();
        var evt = Event();
        evt.OrganizerId = organizer.Id;
        ctx.Organizers.Add(organizer);
        ctx.Events.Add(evt);
        await ctx.SaveChangesAsync();
        return cs;
    }

    private static async Task<Guid> EnqueueInvitationAsync(string db, DateTimeOffset now, string email = "anna@example.org")
    {
        await using var ctx = new AppDbContext(PostgresFixture.Options(db));
        var organizer = await ctx.Organizers.SingleAsync();
        var evt = await ctx.Events.SingleAsync();
        var participant = Participant("Anna", email);
        participant.EventId = evt.Id;
        participant.Bib = (await ctx.Participants.CountAsync() + 1).ToString();
        ctx.Participants.Add(participant);
        var row = EmailOutbox.Create(EmailTemplates.Invitation(participant, evt, organizer, ConfirmUrl, "sk"), participant.Id, now);
        ctx.Outbox.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<EmailOutboxEntity?> RowAsync(string db, Guid id)
    {
        await using var ctx = new AppDbContext(PostgresFixture.Options(db));
        return await ctx.Outbox.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
    }

    private static async Task<ParticipantEntity> ParticipantAsync(string db)
    {
        await using var ctx = new AppDbContext(PostgresFixture.Options(db));
        return await ctx.Participants.AsNoTracking().OrderBy(p => p.Bib).FirstAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Action? step = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(50);
            step?.Invoke();
        }
    }

    private static string Render(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(FormatOptions.Default.Clone(), stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Records every send with the fake clock's time; throws the queued outcomes first, then succeeds.</summary>
    private sealed class RecordingSender(TimeProvider clock, params Exception[] outcomes) : IEmailSender
    {
        private readonly Queue<Exception> _outcomes = new(outcomes);
        private readonly Lock _lock = new();
        private readonly List<(EmailMessage Message, DateTimeOffset At)> _sent = [];

        public IReadOnlyList<(EmailMessage Message, DateTimeOffset At)> Sent
        {
            get { lock (_lock) return _sent.ToList(); }
        }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            lock (_lock)
            {
                _sent.Add((message, clock.GetUtcNow()));
                if (_outcomes.TryDequeue(out var failure)) throw failure;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void Dispose() { }

        private sealed class ListLogger(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : "";
                lock (owner._lines) owner._lines.Add(logLevel + ": " + formatter(state, exception) + " || " + values + " || " + exception);
            }
        }
    }
}
