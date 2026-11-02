using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PositionTracker.Api.Accounts;
using PositionTracker.Api.Auth;
using PositionTracker.Api.Email;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Api.Registration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PositionTracker.Core;
using PositionTracker.Core.Simulation;
using PositionTracker.Data;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

namespace PositionTracker.Api.Tests;

/// <summary>
/// Participant registration by the organizer (<c>/api/events/{id}/participants</c>): creation with invitation, the
/// status list, edits, resend, reminders, device-token re-issue, deletion, ownership and the abuse limits, through the
/// HTTP host against PostgreSQL (Testcontainers). Fixtures are seeded through <see cref="AppDbContext"/> (ruling P2).
/// </summary>
public sealed class RegistrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BaseUrl = "https://tracker.example.org";
    private const string IpHeader = "X-Test-Remote-Ip";
    private static readonly Regex SignInLink = new(@"https://tracker\.example\.org/account/signin\?token=([A-Za-z0-9%_\-]+)");
    private static readonly Regex InviteLink = new(@"https://tracker\.example\.org/p\?token=([A-Za-z0-9%_\-]+)");
    private static readonly DateTimeOffset Start = ApiFactory.T0.AddDays(1);
    private int _ip;

    // ----- creation -----

    [Fact]
    public async Task Creating_a_participant_sets_Invited_assigns_the_next_bib_and_enqueues_an_invitation_with_a_valid_link()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);

        var response = await org.PostAsync(Participants(ev), new
        {
            name = "  Jana Nováková ", email = " Jana.Novakova@Example.ORG ", gender = "female", language = "en",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = await JsonAsync(response);
        var pid = Guid.ParseExact(created.RootElement.GetProperty("participantId").GetString()!, "D");
        Assert.Equal($"/api/events/{ev}/participants/{pid}", response.Headers.Location?.OriginalString);
        Assert.Equal("1", created.RootElement.GetProperty("bib").GetString());
        Assert.Equal("Invited", created.RootElement.GetProperty("status").GetString());
        Assert.Equal(EmailModes.Simulated, created.RootElement.GetProperty("emailMode").GetString());

        await using var db = Db(host);
        var p = await db.Participants.SingleAsync(x => x.Id == pid);
        Assert.Equal("Jana Nováková", p.Name);
        Assert.Equal("Jana.Novakova@Example.ORG", p.Email); // display form kept
        Assert.Equal("jana.novakova@example.org", p.EmailNormalized);
        Assert.Equal(ParticipantStatus.Invited, p.Status);
        Assert.Equal("en", p.Language);
        Assert.Equal("female", p.Gender);

        var mail = await db.Outbox.SingleAsync(m => m.Purpose == EmailPurpose.Invitation);
        Assert.Equal(pid, mail.ParticipantId);
        Assert.Equal("Jana.Novakova@example.org", mail.ToAddress); // local part as typed, ASCII domain
        Assert.Equal("en", mail.Language);
        Assert.StartsWith("Invitation to ", mail.Subject);
        Assert.Equal("org@example.org", mail.ReplyToAddress);
        var token = InviteToken(mail);
        var links = new LinkTokenService(db, host.Clock);
        var redeemed = await links.RedeemAsync(LinkPurpose.ParticipantInvite, token, singleUse: false, CancellationToken.None);
        Assert.Equal(LinkStatus.Ok, redeemed.Status);
        Assert.Equal(pid, redeemed.SubjectId);

        // Auto bibs continue after the highest numeric bib, also after an explicit one.
        Assert.Equal("2", await CreateBibAsync(org, ev, "b@example.org"));
        Assert.Equal("17", await CreateBibAsync(org, ev, "c@example.org", bib: "17"));
        Assert.Equal("A-5", await CreateBibAsync(org, ev, "d@example.org", bib: "a-5"));
        Assert.Equal("18", await CreateBibAsync(org, ev, "e@example.org"));
    }

    [Fact]
    public async Task A_taken_explicit_bib_is_409_bib_taken_and_a_duplicate_email_409_email_duplicate()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        await CreateBibAsync(org, ev, "anna@example.org", bib: "5");

        await AssertErrorAsync(await CreateAsync(org, ev, "other@example.org", bib: "5"), HttpStatusCode.Conflict, "bib_taken", "bib");
        await AssertErrorAsync(await CreateAsync(org, ev, "anna@example.org"), HttpStatusCode.Conflict, "email_duplicate", "email");
        await AssertErrorAsync(await CreateAsync(org, ev, "  ANNA@Example.Org\t"), HttpStatusCode.Conflict, "email_duplicate", "email");

        await using var db = Db(host);
        Assert.Single(await db.Participants.ToListAsync());
        Assert.Single(await db.Outbox.Where(m => m.Purpose == EmailPurpose.Invitation).ToListAsync());

        // The same address in another event is fine.
        var other = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(org, other, "anna@example.org")).StatusCode);
    }

    public static TheoryData<string, string> InvalidCreations => new()
    {
        { "email", "email-missing" },
        { "email", "email-invalid" },
        { "email", "email-two" },
        { "email", "email-crlf" },
        { "name", "name-empty" },
        { "name", "name-201" },
        { "name", "name-crlf" },
        { "name", "name-bidi" },
        { "gender", "gender-missing" },
        { "gender", "gender-other" },
        { "language", "language-de" },
        { "category", "category-unknown" },
        { "wave", "wave-unknown" },
        { "bib", "bib-space" },
        { "bib", "bib-long" },
        { "bib", "bib-dash" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreations))]
    public async Task Invalid_input_is_400_with_the_field_and_nothing_is_stored_or_sent(string field, string variant)
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        await SeedCategoryAsync(host, ev, "Elite");
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Jana", ["email"] = "jana@example.org", ["gender"] = "female", ["language"] = "sk",
            ["category"] = "Elite",
        };
        switch (variant)
        {
            case "email-missing": body.Remove("email"); break;
            case "email-invalid": body["email"] = "jana@"; break;
            case "email-two": body["email"] = "a@example.org, b@example.org"; break;
            case "email-crlf": body["email"] = "a@example.org\r\nBcc: x@example.org"; break;
            case "name-empty": body["name"] = "   "; break;
            case "name-201": body["name"] = new string('é', 201); break;
            case "name-crlf": body["name"] = "Jana\r\nBcc: victim@example.org"; break;
            case "name-bidi": body["name"] = "Jana ‮evil"; break;
            case "gender-missing": body.Remove("gender"); break;
            case "gender-other": body["gender"] = "x"; break;
            case "language-de": body["language"] = "de"; break;
            case "category-unknown": body["category"] = "Masters"; break;
            case "wave-unknown": body["wave"] = "Wave 9"; break;
            case "bib-space": body["bib"] = "1 2"; break;
            case "bib-long": body["bib"] = new string('9', 17); break;
            case "bib-dash": body["bib"] = "-"; break;
        }

        await AssertErrorAsync(await org.PostAsync(Participants(ev), body), HttpStatusCode.BadRequest, "validation_failed", field);
        await using var db = Db(host);
        Assert.Empty(await db.Participants.ToListAsync());
        Assert.Empty(await db.Outbox.Where(m => m.Purpose == EmailPurpose.Invitation).ToListAsync());
    }

    [Fact]
    public async Task Unicode_emoji_and_200_character_names_are_stored_exactly_and_mails_carry_single_line_headers()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        const string prefix = "Ľubica Šťastná 🚴‍♀️ ";
        var name = prefix + new string('ž', 198 - prefix.EnumerateRunes().Count()) + "🏁🏁"; // 200 scalar values
        Assert.Equal(200, name.EnumerateRunes().Count());

        var response = await org.PostAsync(Participants(ev), new { name, email = "lubica@example.org", gender = "female", language = "sk" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = Db(host);
        Assert.Equal(name, (await db.Participants.SingleAsync()).Name);
        var mail = await db.Outbox.SingleAsync(m => m.Purpose == EmailPurpose.Invitation);
        foreach (var header in new[] { mail.ToName, mail.Subject, mail.ToAddress, mail.ReplyToName ?? "", mail.ReplyToAddress ?? "" })
            Assert.DoesNotContain(header, c => char.IsControl(c));
        Assert.Equal("sk", mail.Language);
        Assert.StartsWith("Pozvánka na ", mail.Subject);
    }

    [Fact]
    public async Task The_participant_cap_per_event_is_409_participant_limit()
    {
        await using var host = Host(settings: s => s["Registration:MaxParticipantsPerEvent"] = "2");
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        await CreateBibAsync(org, ev, "a@example.org");
        await CreateBibAsync(org, ev, "b@example.org");

        await AssertErrorAsync(await CreateAsync(org, ev, "c@example.org"), HttpStatusCode.Conflict, "participant_limit");
        await using var db = Db(host);
        Assert.Equal(2, await db.Participants.CountAsync());
    }

    [Fact]
    public async Task Invitation_mails_are_capped_per_organizer_per_hour_and_nothing_is_stored_over_the_cap()
    {
        await using var host = Host(settings: s => s["Registration:InvitationsPerOrganizerPerHour"] = "2");
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var other = await SessionAsync(host, "other@example.org");
        var otherEvent = await SeedEventAsync(host, other.OrganizerId, EventState.Draft);
        await CreateBibAsync(org, ev, "a@example.org");
        var second = await CreateAsync(org, ev, "b@example.org");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var pid = await ParticipantIdAsync(second);

        await AssertErrorAsync(await CreateAsync(org, ev, "c@example.org"), HttpStatusCode.TooManyRequests, "rate_limited");
        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { }), HttpStatusCode.TooManyRequests, "rate_limited");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { email = "new@example.org" }),
            HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(other, otherEvent, "c@example.org")).StatusCode); // per organizer

        await using (var db = Db(host))
        {
            Assert.Equal(2, await db.Participants.CountAsync(p => p.EventId == ev));
            Assert.Equal("b@example.org", (await db.Participants.SingleAsync(p => p.Id == pid)).Email); // edit refused whole
        }

        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(org, ev, "c@example.org")).StatusCode);
    }

    [Fact]
    public async Task Invitation_mails_to_one_address_are_capped_per_hour_across_events_and_organizers()
    {
        await using var host = Host(settings: s => s["Registration:InvitationsPerAddressPerHour"] = "2");
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var other = await SessionAsync(host, "other@example.org");
        var otherEvent = await SeedEventAsync(host, other.OrganizerId, EventState.Draft);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "victim@example.org"));

        Assert.Equal(HttpStatusCode.Accepted, (await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { })).StatusCode);
        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { }), HttpStatusCode.TooManyRequests, "rate_limited");
        await AssertErrorAsync(await CreateAsync(other, otherEvent, " VICTIM@example.org"), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    // ----- list -----

    [Fact]
    public async Task The_list_filters_by_status_category_and_text_and_counts_every_status()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var elite = await SeedCategoryAsync(host, ev, "Elite");
        await SeedParticipantAsync(host, ev, ParticipantStatus.Invited, "Adam Malý", elite);
        await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Beáta Veľká", elite);
        await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Cyril Krátky");
        await SeedParticipantAsync(host, ev, ParticipantStatus.Declined, "Dana Dlhá");
        await SeedParticipantAsync(host, ev, ParticipantStatus.EmailFailed, "Emil Široký");
        var foreignOrg = Guid.NewGuid();
        await SeedOrganizerAsync(host, foreignOrg);
        await SeedParticipantAsync(host, await SeedEventAsync(host, foreignOrg, EventState.Draft), ParticipantStatus.Confirmed, "Foreign");

        using var all = await JsonAsync(await org.GetAsync(Participants(ev)));
        var root = all.RootElement;
        Assert.Equal(EmailModes.Simulated, root.GetProperty("emailMode").GetString());
        var counts = root.GetProperty("counts");
        Assert.Equal(5, counts.GetProperty("all").GetInt32());
        Assert.Equal(2, counts.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, counts.GetProperty("invited").GetInt32());
        Assert.Equal(1, counts.GetProperty("declined").GetInt32());
        Assert.Equal(1, counts.GetProperty("emailFailed").GetInt32());
        var first = root.GetProperty("participants")[0];
        Assert.Equal("Adam Malý", first.GetProperty("name").GetString());
        Assert.Equal("Invited", first.GetProperty("status").GetString());
        Assert.Equal("Elite", first.GetProperty("category").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("email").GetString()));
        Assert.Equal(5, root.GetProperty("participants").GetArrayLength());

        Assert.Equal(["Beáta Veľká", "Cyril Krátky"], await NamesAsync(org, $"{Participants(ev)}?status=Confirmed"));
        Assert.Equal(["Emil Široký"], await NamesAsync(org, $"{Participants(ev)}?status=emailFailed"));
        Assert.Equal(["Adam Malý", "Beáta Veľká"], await NamesAsync(org, $"{Participants(ev)}?category=elite"));
        Assert.Equal(["Beáta Veľká"], await NamesAsync(org, $"{Participants(ev)}?status=confirmed&category=Elite"));
        Assert.Equal(["Beáta Veľká"], await NamesAsync(org, $"{Participants(ev)}?q=VEĽ"));
        Assert.Equal(["Cyril Krátky"], await NamesAsync(org, $"{Participants(ev)}?q=rider3@"));
        Assert.Empty(await NamesAsync(org, $"{Participants(ev)}?q=%25"));
        // Counts stay for the whole event whatever the filter.
        using var filtered = await JsonAsync(await org.GetAsync($"{Participants(ev)}?status=declined"));
        Assert.Equal(5, filtered.RootElement.GetProperty("counts").GetProperty("all").GetInt32());

        await AssertErrorAsync(await org.GetAsync($"{Participants(ev)}?status=gone"), HttpStatusCode.BadRequest, "validation_failed", "status");
    }

    [Fact]
    public async Task An_invitation_for_a_distant_event_expires_a_week_after_the_start()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var start = host.Clock.GetUtcNow().AddDays(30);
        await SetStartAsync(host, ev, start);

        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "far@example.org"));

        string link;
        await using (var db = Db(host))
        {
            Assert.Equal(start + LinkTokenService.InviteAfterStart, (await db.LinkTokens.SingleAsync(t => t.SubjectId == pid)).ExpiresAt);
            link = InviteToken(await db.Outbox.SingleAsync(m => m.ParticipantId == pid));
        }
        host.Clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(HttpStatusCode.OK, (await Client(host).GetAsync($"/api/p/{link}")).StatusCode);
    }

    [Fact]
    public async Task Moving_the_start_later_extends_the_invitation_links_and_moving_it_earlier_keeps_them()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var now = host.Clock.GetUtcNow();
        await SetStartAsync(host, ev, now.AddDays(20));
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "move@example.org"));
        Assert.Equal(HttpStatusCode.Accepted, (await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { })).StatusCode); // supersedes the first token

        Task<HttpResponseMessage> Move(DateTimeOffset start) => org.PutAsync($"/api/events/{ev}",
            new { name = "Jarná časovka", location = "Žilina", startTime = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture), sport = "cycling" });

        List<LinkTokenEntity> tokens;
        await using (var db = Db(host)) tokens = await db.LinkTokens.Where(t => t.SubjectId == pid).ToListAsync();
        var revoked = tokens.Single(t => t.RevokedAt != null);
        var revokedExpiry = revoked.ExpiresAt;

        Assert.Equal(HttpStatusCode.OK, (await Move(now.AddDays(40))).StatusCode);
        await using (var db = Db(host))
        {
            Assert.Equal(now.AddDays(47), (await db.LinkTokens.SingleAsync(t => t.SubjectId == pid && t.RevokedAt == null)).ExpiresAt);
            Assert.Equal(revokedExpiry, (await db.LinkTokens.SingleAsync(t => t.Id == revoked.Id)).ExpiresAt);
        }

        Assert.Equal(HttpStatusCode.OK, (await Move(now.AddDays(25))).StatusCode);
        await using (var db = Db(host))
            Assert.Equal(now.AddDays(47), (await db.LinkTokens.SingleAsync(t => t.SubjectId == pid && t.RevokedAt == null)).ExpiresAt);
    }

    private static async Task SetStartAsync(ApiFactory host, Guid eventId, DateTimeOffset start)
    {
        await using var db = Db(host);
        await db.Events.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s.SetProperty(e => e.StartTime, start));
    }

    // ----- edit -----

    [Fact]
    public async Task Editing_the_email_resends_the_invitation_in_the_participants_language_and_supersedes_the_old_link()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "typo@exmaple.org", language: "en"));
        string oldToken;
        await using (var db = Db(host)) oldToken = InviteToken(await db.Outbox.SingleAsync(m => m.ParticipantId == pid));

        var response = await org.PutAsync($"{Participants(ev)}/{pid}", new { email = " Fixed@Example.org " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var view = await JsonAsync(response))
        {
            Assert.Equal("Fixed@Example.org", view.RootElement.GetProperty("email").GetString());
            Assert.Equal("Invited", view.RootElement.GetProperty("status").GetString());
            Assert.Equal(EmailModes.Simulated, view.RootElement.GetProperty("emailMode").GetString());
        }
        await using (var db = Db(host))
        {
            var p = await db.Participants.SingleAsync(x => x.Id == pid);
            Assert.Equal("fixed@example.org", p.EmailNormalized);
            // The unsent invitation to the old address is withdrawn; the new one goes to the new address in English.
            var mail = await db.Outbox.SingleAsync(m => m.ParticipantId == pid);
            Assert.Equal("Fixed@example.org", mail.ToAddress);
            Assert.Equal("en", mail.Language);
            var links = new LinkTokenService(db, host.Clock);
            Assert.Equal(LinkStatus.Superseded, (await links.RedeemAsync(LinkPurpose.ParticipantInvite, oldToken, false, default)).Status);
            Assert.Equal(LinkStatus.Ok, (await links.RedeemAsync(LinkPurpose.ParticipantInvite, InviteToken(mail), false, default)).Status);
        }

        // A change of case only keeps the link and sends nothing.
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{pid}", new { email = "fixed@example.org" })).StatusCode);
        await using (var db = Db(host)) Assert.Single(await db.Outbox.Where(m => m.ParticipantId == pid).ToListAsync());

        // Another participant's address is a duplicate.
        await CreateBibAsync(org, ev, "taken@example.org");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { email = "TAKEN@example.org" }),
            HttpStatusCode.Conflict, "email_duplicate", "email");
    }

    [Fact]
    public async Task Edits_change_the_fields_validate_them_and_keep_the_bib_unique()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        await SeedCategoryAsync(host, ev, "Elite");
        var waveId = await SeedWaveAsync(host, ev, "Vlna A");
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "a@example.org"));
        await CreateBibAsync(org, ev, "b@example.org", bib: "9");

        var response = await org.PutAsync($"{Participants(ev)}/{pid}", new
        {
            name = "Nové Meno", gender = "male", language = "en", category = "ELITE", wave = "vlna a", bib = "42",
            nickname = " Rýchly 🚀 ", hideNickname = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using (var db = Db(host))
        {
            var p = await db.Participants.Include(x => x.Category).SingleAsync(x => x.Id == pid);
            Assert.Equal("Nové Meno", p.Name);
            Assert.Equal("male", p.Gender);
            Assert.Equal("en", p.Language);
            Assert.Equal("Elite", p.Category!.Name);
            Assert.Equal(waveId, p.WaveId);
            Assert.Equal("42", p.Bib);
            Assert.Equal("Rýchly 🚀", p.Nickname);
            Assert.True(p.NicknameHidden);
            Assert.Null(RuntimeFactory.ParticipantInfoOf(p).Nickname); // hidden from the runtime and spectators
            Assert.Single(await db.Outbox.Where(m => m.ParticipantId == pid).ToListAsync()); // no new mail
        }

        // Empty strings clear the optional fields.
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{pid}", new { category = "", wave = "", nickname = "", hideNickname = false })).StatusCode);
        await using (var db = Db(host))
        {
            var p = await db.Participants.SingleAsync(x => x.Id == pid);
            Assert.Null(p.CategoryId);
            Assert.Null(p.WaveId);
            Assert.Null(p.Nickname);
            Assert.False(p.NicknameHidden);
        }

        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { bib = "9" }), HttpStatusCode.Conflict, "bib_taken", "bib");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { nickname = new string('n', 31) }), HttpStatusCode.BadRequest, "validation_failed", "nickname");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { nickname = "a\nb" }), HttpStatusCode.BadRequest, "validation_failed", "nickname");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { name = "x\r\ny" }), HttpStatusCode.BadRequest, "validation_failed", "name");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { category = "Nope" }), HttpStatusCode.BadRequest, "validation_failed", "category");
        await AssertErrorAsync(await org.PutAsync($"{Participants(ev)}/{Guid.NewGuid()}", new { name = "x" }), HttpStatusCode.NotFound, "not_found");
    }

    // ----- delivery failure and resend -----

    [Fact]
    public async Task A_failed_delivery_shows_as_EmailFailed_and_fixing_the_address_and_resend_bring_it_back_to_Invited()
    {
        var sender = new BouncingSender();
        await using var host = Host(services: s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(sender);
        });
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "bounce@example.org"));
        var worker = host.Services.GetRequiredService<EmailOutboxWorker>();

        await worker.ProcessDueAsync(CancellationToken.None);

        using (var list = await JsonAsync(await org.GetAsync(Participants(ev))))
        {
            Assert.Equal(EmailModes.Smtp, list.RootElement.GetProperty("emailMode").GetString());
            Assert.Equal(1, list.RootElement.GetProperty("counts").GetProperty("emailFailed").GetInt32());
            Assert.Equal("EmailFailed", list.RootElement.GetProperty("participants")[0].GetProperty("status").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{pid}", new { email = "good@example.org" })).StatusCode);
        await using (var db = Db(host)) Assert.Equal(ParticipantStatus.Invited, (await db.Participants.SingleAsync()).Status);
        await worker.ProcessDueAsync(CancellationToken.None);
        Assert.Equal("good@example.org", sender.Sent[^1]);

        // A failure again, then resend to the (now correct) address.
        await using (var db = Db(host))
            await db.Participants.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ParticipantStatus.EmailFailed));
        var resend = await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        using (var json = await JsonAsync(resend)) Assert.Equal(EmailModes.Smtp, json.RootElement.GetProperty("emailMode").GetString());
        await worker.ProcessDueAsync(CancellationToken.None);
        await using (var db = Db(host)) Assert.Equal(ParticipantStatus.Invited, (await db.Participants.SingleAsync()).Status);
        Assert.Equal(3, sender.Sent.Count);
    }

    [Fact]
    public async Task A_late_failure_of_a_superseded_invitation_does_not_mark_the_participant_EmailFailed()
    {
        var sender = new BouncingSender();
        await using var host = Host(services: s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(sender);
        });
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "bounce@example.org"));
        var worker = host.Services.GetRequiredService<EmailOutboxWorker>();
        // In flight: claimed, not yet finished (the organizer's sign-in mail is claimed with it).
        Assert.Contains(await worker.ClaimAsync(CancellationToken.None), m => m.ParticipantId == pid);

        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{pid}", new { email = "good@example.org" })).StatusCode);
        host.Clock.Advance(TimeSpan.FromDays(1)); // the old lease ends: the old row is claimed again and bounces
        await worker.ProcessDueAsync(CancellationToken.None);

        await using var db = Db(host);
        Assert.Contains("bounce@example.org", sender.Sent);
        Assert.Equal(ParticipantStatus.Invited, (await db.Participants.SingleAsync()).Status);
    }

    // ----- reminders -----

    [Fact]
    public async Task Remind_mails_only_invited_participants_whose_invitation_is_older_than_the_reminder_age()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var old1 = await ParticipantIdAsync(await CreateAsync(org, ev, "a@example.org"));
        var old2 = await ParticipantIdAsync(await CreateAsync(org, ev, "b@example.org"));
        var confirmed = await ParticipantIdAsync(await CreateAsync(org, ev, "c@example.org"));
        var declined = await ParticipantIdAsync(await CreateAsync(org, ev, "d@example.org"));
        await using (var db = Db(host))
        {
            await db.Participants.Where(p => p.Id == confirmed).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ParticipantStatus.Confirmed));
            await db.Participants.Where(p => p.Id == declined).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ParticipantStatus.Declined));
        }
        host.Clock.Advance(TimeSpan.FromHours(25));
        var fresh = await ParticipantIdAsync(await CreateAsync(org, ev, "e@example.org"));

        var response = await org.PostAsync($"{Participants(ev)}/remind", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var json = await JsonAsync(response))
        {
            Assert.Equal(2, json.RootElement.GetProperty("sent").GetInt32());
            Assert.Equal(EmailModes.Simulated, json.RootElement.GetProperty("emailMode").GetString());
        }
        await using (var db = Db(host))
        {
            Assert.Equal(1, await db.Outbox.CountAsync(m => m.ParticipantId == old1)); // the unsent first mail was replaced
            Assert.Equal(1, await db.Outbox.CountAsync(m => m.ParticipantId == old2));
            Assert.Equal(1, await db.Outbox.CountAsync(m => m.ParticipantId == fresh));
            var reminded = await db.Outbox.SingleAsync(m => m.ParticipantId == old1);
            Assert.Equal(host.Clock.GetUtcNow(), reminded.CreatedAt);
        }

        // Right after a reminder nobody is due again.
        using var again = await JsonAsync(await org.PostAsync($"{Participants(ev)}/remind", new { }));
        Assert.Equal(0, again.RootElement.GetProperty("sent").GetInt32());
    }

    // ----- device token -----

    [Fact]
    public async Task Reissue_token_revokes_the_old_device_token_at_once_mails_a_new_link_and_returns_nothing_secret()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "rider@example.org"));
        string deviceToken, oldLink;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            deviceToken = await tokens.IssueAsync(ev.ToString("D"), pid.ToString("D"), CancellationToken.None);
            Assert.NotNull(await tokens.ResolveAsync(deviceToken, CancellationToken.None));
        }
        await using (var db = Db(host)) oldLink = InviteToken(await db.Outbox.SingleAsync(m => m.ParticipantId == pid));

        var response = await org.PostAsync($"{Participants(ev)}/{pid}/reissue-token", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using (var json = JsonDocument.Parse(body))
        {
            Assert.True(json.RootElement.GetProperty("deviceTokenIssued").GetBoolean());
            Assert.True(json.RootElement.GetProperty("mailQueued").GetBoolean());
            Assert.Equal(["deviceTokenIssued", "emailMode", "mailQueued"], json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        }
        Assert.DoesNotContain(deviceToken, body);
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ITokenService>().ResolveAsync(deviceToken, CancellationToken.None));
        await using (var db = Db(host))
        {
            Assert.All(await db.DeviceTokens.Where(t => t.ParticipantId == pid).ToListAsync(), t => Assert.NotNull(t.RevokedAt));
            var mail = await db.Outbox.SingleAsync(m => m.ParticipantId == pid);
            Assert.DoesNotContain(oldLink, mail.TextBody);
            Assert.DoesNotContain(InviteToken(mail), body);
            var links = new LinkTokenService(db, host.Clock);
            Assert.Equal(LinkStatus.Superseded, (await links.RedeemAsync(LinkPurpose.ParticipantInvite, oldLink, false, default)).Status);
            Assert.Equal(LinkStatus.Ok, (await links.RedeemAsync(LinkPurpose.ParticipantInvite, InviteToken(mail), false, default)).Status);
        }
    }

    [Fact]
    public async Task Database_device_tokens_issue_resolve_and_revoke_and_a_new_issue_revokes_the_previous_one()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Rider");
        await using var scope = host.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();

        var first = await tokens.IssueAsync(ev.ToString("D"), pid.ToString("D"), default);
        var second = await tokens.IssueAsync(ev.ToString("D"), pid.ToString("D"), default);

        Assert.Null(await tokens.ResolveAsync(first, default));
        Assert.Equal((ev.ToString("D"), pid.ToString("D")), await tokens.ResolveAsync(second, default));
        Assert.Null(await tokens.ResolveAsync("", default));
        Assert.Null(await tokens.ResolveAsync(new string('x', 500), default));
        await tokens.RevokeAsync(ev.ToString("D"), pid.ToString("D"), default);
        Assert.Null(await tokens.ResolveAsync(second, default));
        await using var db = Db(host);
        Assert.Equal(2, await db.DeviceTokens.CountAsync(t => t.ParticipantId == pid && t.RevokedAt != null));
        Assert.DoesNotContain(await db.DeviceTokens.Select(t => t.TokenHash).ToListAsync(), h => Encoding.UTF8.GetString(h).Contains(second));

        // Simulated (demo) events keep their tokens in memory and never write rows.
        var demo = await tokens.IssueAsync("demo-abc", "sim-001", default);
        Assert.Equal(("demo-abc", "sim-001"), await tokens.ResolveAsync(demo, default));
        Assert.Equal(2, await db.DeviceTokens.CountAsync());
    }

    // ----- delete -----

    [Fact]
    public async Task Delete_is_allowed_before_live_withdraws_unsent_mail_and_links_and_is_409_once_live_or_finished()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "a@example.org"));

        Assert.Equal(HttpStatusCode.NoContent, (await org.SendAsync(HttpMethod.Delete, $"{Participants(ev)}/{pid}", Json("{}"))).StatusCode);
        await using (var db = Db(host))
        {
            Assert.Empty(await db.Participants.ToListAsync());
            Assert.Empty(await db.Outbox.Where(m => m.Purpose == EmailPurpose.Invitation).ToListAsync());
            Assert.Empty(await db.LinkTokens.Where(t => t.SubjectId == pid).ToListAsync());
        }
        await AssertErrorAsync(await org.SendAsync(HttpMethod.Delete, $"{Participants(ev)}/{pid}", Json("{}")), HttpStatusCode.NotFound, "not_found");

        var live = await SeedEventAsync(host, org.OrganizerId, EventState.Live);
        var livePid = await SeedParticipantAsync(host, live, ParticipantStatus.Confirmed, "Live Rider");
        await AssertErrorAsync(await org.SendAsync(HttpMethod.Delete, $"{Participants(live)}/{livePid}", Json("{}")), HttpStatusCode.Conflict, "event_live");
        var finished = await SeedEventAsync(host, org.OrganizerId, EventState.Finished);
        var finishedPid = await SeedParticipantAsync(host, finished, ParticipantStatus.Confirmed, "Done Rider");
        await AssertErrorAsync(await org.SendAsync(HttpMethod.Delete, $"{Participants(finished)}/{finishedPid}", Json("{}")), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await CreateAsync(org, finished, "late@example.org"), HttpStatusCode.Conflict, "event_finished");
        await using (var db = Db(host)) Assert.Equal(2, await db.Participants.CountAsync());
    }

    // ----- live events -----

    [Fact]
    public async Task On_a_live_event_a_new_participant_is_stored_as_Invited_but_not_added_to_the_runtime_and_edits_update_it()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, Gpx(DemoCourses.Loop()));
        var confirmed = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Rider One");
        Assert.Equal(HttpStatusCode.OK, (await org.PostAsync($"/api/events/{ev}/state", new { state = "live" })).StatusCode);
        var runtime = host.Registry.Get(ev.ToString("D"))!;

        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "late@example.org"));

        Assert.False(runtime.HasParticipant(pid.ToString("D"))); // added only when the participant confirms (Task 7)
        Assert.Single(runtime.Participants);

        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{confirmed}", new { name = "Rider Renamed", nickname = "Rocket" })).StatusCode);
        var info = runtime.Participants[confirmed.ToString("D")];
        Assert.Equal("Rider Renamed", info.Name);
        Assert.Equal("Rocket", info.Nickname);
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{confirmed}", new { hideNickname = true })).StatusCode);
        Assert.Null(runtime.Participants[confirmed.ToString("D")].Nickname);
    }

    // ----- access -----

    [Fact]
    public async Task Another_organizers_event_or_participant_is_404_and_sessions_confirmation_and_browser_headers_are_required()
    {
        await using var host = Host();
        var owner = await SessionAsync(host, "owner@example.org");
        var intruder = await SessionAsync(host, "intruder@example.org");
        var ev = await SeedEventAsync(host, owner.OrganizerId, EventState.Draft);
        var pid = await ParticipantIdAsync(await CreateAsync(owner, ev, "a@example.org"));
        var own = await SeedEventAsync(host, intruder.OrganizerId, EventState.Draft);

        await AssertErrorAsync(await intruder.GetAsync(Participants(ev)), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await CreateAsync(intruder, ev, "b@example.org"), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.PutAsync($"{Participants(ev)}/{pid}", new { name = "x" }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.SendAsync(HttpMethod.Delete, $"{Participants(ev)}/{pid}", Json("{}")), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.PostAsync($"{Participants(ev)}/{pid}/resend", new { }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.PostAsync($"{Participants(ev)}/{pid}/reissue-token", new { }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.PostAsync($"{Participants(ev)}/remind", new { }), HttpStatusCode.NotFound, "not_found");
        // Another event's participant through the intruder's own event.
        await AssertErrorAsync(await intruder.PutAsync($"{Participants(own)}/{pid}", new { name = "x" }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await intruder.PostAsync($"{Participants(own)}/{pid}/resend", new { }), HttpStatusCode.NotFound, "not_found");
        // Non-canonical ids and demo ids.
        await AssertErrorAsync(await owner.GetAsync(Participants(ev.ToString("D").ToUpperInvariant())), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await owner.PutAsync($"{Participants(ev)}/{pid.ToString("N")}", new { name = "x" }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await owner.GetAsync(Participants("demo-abc")), HttpStatusCode.NotFound, "not_found");

        var unconfirmed = await SessionAsync(host, "new@example.org", confirmed: false);
        var draft = await SeedEventAsync(host, unconfirmed.OrganizerId, EventState.Draft);
        await AssertErrorAsync(await CreateAsync(unconfirmed, draft, "b@example.org"), HttpStatusCode.Forbidden, "email_not_confirmed");
        await AssertErrorAsync(await unconfirmed.GetAsync(Participants(draft)), HttpStatusCode.Forbidden, "email_not_confirmed");

        var anonymous = Client(host);
        await AssertErrorAsync(await anonymous.GetAsync(Participants(ev)), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertErrorAsync(await owner.SendAsync(HttpMethod.Post, Participants(ev),
            Json(JsonSerializer.Serialize(new { name = "x", email = "c@example.org", gender = "male", language = "sk" })), header: false),
            HttpStatusCode.BadRequest, "csrf_failed");

        await using var db = Db(host);
        Assert.Equal("a@example.org", (await db.Participants.SingleAsync()).Email);
        Assert.Single(await db.Outbox.Where(m => m.Purpose == EmailPurpose.Invitation).ToListAsync());
    }

    // ----- fix round 1 -----

    [Fact]
    public async Task Reissue_over_the_mail_budget_still_revokes_the_device_token_and_says_no_mail_was_queued()
    {
        await using var host = Host(settings: s => s["Registration:InvitationsPerOrganizerPerHour"] = "1");
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "rider@example.org")); // the only unit
        string deviceToken;
        await using (var scope = host.Services.CreateAsyncScope())
            deviceToken = await scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAsync(ev.ToString("D"), pid.ToString("D"), default);

        var response = await org.PostAsync($"{Participants(ev)}/{pid}/reissue-token", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var json = await JsonAsync(response))
        {
            Assert.True(json.RootElement.GetProperty("deviceTokenIssued").GetBoolean());
            Assert.False(json.RootElement.GetProperty("mailQueued").GetBoolean());
        }
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ITokenService>().ResolveAsync(deviceToken, default));
        await using var db = Db(host);
        Assert.Single(await db.Outbox.Where(m => m.ParticipantId == pid).ToListAsync()); // no second mail
    }

    [Fact]
    public async Task The_global_hourly_invitation_cap_refuses_every_organizer_and_its_hits_show_in_the_ops_view()
    {
        await using var host = Host(settings: s => s["Registration:InvitationsGlobalPerHour"] = "2");
        var a = await SessionAsync(host, "a-org@example.org");
        var b = await SessionAsync(host, "b-org@example.org");
        var evA = await SeedEventAsync(host, a.OrganizerId, EventState.Draft);
        var evB = await SeedEventAsync(host, b.OrganizerId, EventState.Draft);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(a, evA, "1@example.org")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(b, evB, "2@example.org")).StatusCode);

        await AssertErrorAsync(await CreateAsync(b, evB, "3@example.org"), HttpStatusCode.TooManyRequests, "rate_limited");
        await AssertErrorAsync(await CreateAsync(a, evA, "4@example.org"), HttpStatusCode.TooManyRequests, "rate_limited");

        var ops = Client(host);
        ops.DefaultRequestHeaders.Add(OrganizerOptions.ApiKeyHeader, host.OrganizerKey);
        using var json = JsonDocument.Parse(await ops.GetStringAsync("/api/ops/email-mode"));
        var hits = json.RootElement.GetProperty("invitationLimitHits");
        Assert.Equal(2, hits.GetProperty("global").GetInt32());
        Assert.Equal(2, hits.GetProperty("total").GetInt32());
        Assert.Equal(0, hits.GetProperty("perOrganizer").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("limitHitsLast24h").GetInt32()); // account limits unchanged

        host.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(a, evA, "4@example.org")).StatusCode);
    }

    [Fact]
    public async Task Invitations_to_one_address_are_capped_per_day()
    {
        await using var host = Host(settings: s =>
        {
            s["Registration:InvitationsPerAddressPerDay"] = "2";
            s["Registration:InvitationsPerAddressPerHour"] = "5";
        });
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "victim@example.org"));
        host.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Accepted, (await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { })).StatusCode);
        host.Clock.Advance(TimeSpan.FromHours(2));

        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { }), HttpStatusCode.TooManyRequests, "rate_limited");

        host.Clock.Advance(TimeSpan.FromHours(21));
        Assert.Equal(HttpStatusCode.Accepted, (await org.PostAsync($"{Participants(ev)}/{pid}/resend", new { })).StatusCode);
    }

    [Fact]
    public void The_invitation_defaults_leave_headroom_below_the_mailbox_quota_and_refunds_restore_the_budgets()
    {
        var defaults = new RegistrationOptions();
        Assert.Equal(100, defaults.InvitationsPerOrganizerPerHour);
        Assert.Equal(300, defaults.InvitationsGlobalPerHour);
        Assert.Equal(5, defaults.InvitationsPerAddressPerHour);
        Assert.Equal(10, defaults.InvitationsPerAddressPerDay);

        var limiter = new InvitationLimiter(Options.Create(new RegistrationOptions { InvitationsGlobalPerHour = 1 }),
            new FakeTimeProvider(ApiFactory.T0), NullLogger<InvitationLimiter>.Instance);
        var org = Guid.NewGuid();
        Assert.True(limiter.TryTake(org, "a@example.org"));
        Assert.False(limiter.TryTake(org, "b@example.org"));
        limiter.Refund(org, "a@example.org");
        Assert.True(limiter.TryTake(org, "b@example.org"));
        Assert.Equal(1, limiter.LimitHitsLast24h().Global);
    }

    [Fact]
    public async Task Deleting_the_only_confirmed_participant_during_go_live_waits_for_it_and_never_leaves_a_runtime_without_its_row()
    {
        var hooks = new EventServiceHooks();
        await using var host = Host(services: s => s.AddSingleton(hooks));
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, Gpx(DemoCourses.Loop()));
        var only = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Only Rider");
        Task<HttpResponseMessage>? delete = null;
        var completedDuringLive = false;
        hooks.BeforeGoLiveRead = async eventId =>
        {
            if (eventId != ev || delete is not null) return;
            delete = org.SendAsync(HttpMethod.Delete, $"{Participants(ev)}/{only}", Json("{}"));
            await Task.WhenAny(delete, Task.Delay(1500));
            completedDuringLive = delete.IsCompleted;
        };

        var live = await org.PostAsync($"/api/events/{ev}/state", new { state = "live" });

        Assert.NotNull(delete);
        var deleted = await delete!;
        Assert.False(completedDuringLive, "The delete was not serialized with go-live.");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        await AssertErrorAsync(deleted, HttpStatusCode.Conflict, "event_live");
        await using var db = Db(host);
        var runtime = host.Registry.Get(ev.ToString("D"))!;
        Assert.NotEmpty(runtime.Participants);
        foreach (var id in runtime.Participants.Keys)
            Assert.True(await db.Participants.AnyAsync(p => p.Id == Guid.Parse(id)));
    }

    [Fact]
    public async Task Removing_participants_before_a_concurrent_go_live_leaves_the_event_ready()
    {
        var hooks = new EventServiceHooks();
        await using var host = Host(services: s => s.AddSingleton(hooks));
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready, Gpx(DemoCourses.Loop()));
        var only = await SeedParticipantAsync(host, ev, ParticipantStatus.Confirmed, "Only Rider");
        // The delete commits between the state request's first read and its row lock.
        hooks.BeforeStateAttempt = async (eventId, attempt) =>
        {
            if (eventId != ev || attempt != 1) return;
            Assert.Equal(HttpStatusCode.NoContent, (await org.SendAsync(HttpMethod.Delete, $"{Participants(ev)}/{only}", Json("{}"))).StatusCode);
        };

        await AssertErrorAsync(await org.PostAsync($"/api/events/{ev}/state", new { state = "live" }), HttpStatusCode.Conflict, "no_confirmed_participants");
        Assert.Null(host.Registry.Get(ev.ToString("D")));
    }

    [Fact]
    public async Task The_confirmation_deadline_is_set_validated_shown_and_cleared_and_locked_once_live()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var deadline = Utc(host.Clock.GetUtcNow().AddHours(12));

        var set = await org.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = deadline });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var view = await JsonAsync(set)) Assert.Equal(deadline, view.RootElement.GetProperty("confirmationDeadline").GetString());
        using (var manage = await JsonAsync(await org.GetAsync($"/api/events/{ev}/manage")))
            Assert.Equal(deadline, manage.RootElement.GetProperty("confirmationDeadline").GetString());
        await AssertErrorAsync(await org.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = Utc(host.Clock.GetUtcNow().AddMinutes(-1)) }),
            HttpStatusCode.BadRequest, "validation_failed", "confirmationDeadline");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = Utc(Start.AddMinutes(1)) }),
            HttpStatusCode.BadRequest, "validation_failed", "confirmationDeadline");
        await AssertErrorAsync(await org.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = "tomorrow" }),
            HttpStatusCode.BadRequest, "validation_failed", "confirmationDeadline");

        using (var cleared = await JsonAsync(await org.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = (string?)null })))
            Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("confirmationDeadline").ValueKind);
        await using (var db = Db(host)) Assert.Null((await db.Events.SingleAsync(e => e.Id == ev)).ConfirmationDeadline);

        var live = await SeedEventAsync(host, org.OrganizerId, EventState.Live);
        await AssertErrorAsync(await org.PutAsync($"/api/events/{live}/confirmation-deadline", new { confirmationDeadline = deadline }),
            HttpStatusCode.Conflict, "event_locked");
        var other = await SessionAsync(host, "other@example.org");
        await AssertErrorAsync(await other.PutAsync($"/api/events/{ev}/confirmation-deadline", new { confirmationDeadline = deadline }),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task With_a_deadline_remind_is_refused_before_it_and_reaches_every_invited_participant_once_after_it()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"/api/events/{ev}/confirmation-deadline",
            new { confirmationDeadline = Utc(host.Clock.GetUtcNow().AddHours(2)) })).StatusCode);
        await CreateAsync(org, ev, "a@example.org");
        await CreateAsync(org, ev, "b@example.org");

        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/remind", new { }), HttpStatusCode.Conflict, "remind_too_early");

        host.Clock.Advance(TimeSpan.FromHours(3)); // past the deadline, the links are only 3 h old
        using (var json = await JsonAsync(await org.PostAsync($"{Participants(ev)}/remind", new { })))
            Assert.Equal(2, json.RootElement.GetProperty("sent").GetInt32());
        using (var again = await JsonAsync(await org.PostAsync($"{Participants(ev)}/remind", new { })))
            Assert.Equal(0, again.RootElement.GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Remind_reports_participants_left_out_by_a_limit()
    {
        await using var host = Host(settings: s => s["Registration:InvitationsGlobalPerHour"] = "3");
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        for (var i = 0; i < 3; i++) await CreateAsync(org, ev, $"r{i}@example.org");
        host.Clock.Advance(TimeSpan.FromHours(1));
        await CreateAsync(org, ev, "r3@example.org");
        host.Clock.Advance(TimeSpan.FromHours(25));

        using var json = await JsonAsync(await org.PostAsync($"{Participants(ev)}/remind", new { }));

        Assert.Equal(3, json.RootElement.GetProperty("sent").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("limited").GetInt32());
    }

    [Fact]
    public async Task An_empty_bib_means_automatic_on_create_and_unchanged_on_edit()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);

        Assert.Equal("1", await CreateBibAsync(org, ev, "a@example.org", bib: ""));
        Assert.Equal("2", await CreateBibAsync(org, ev, "b@example.org", bib: "  "));
        var pid = await ParticipantIdAsync(await CreateAsync(org, ev, "c@example.org", bib: "7"));
        using var edited = await JsonAsync(await org.PutAsync($"{Participants(ev)}/{pid}", new { bib = "", name = "Renamed" }));
        Assert.Equal("7", edited.RootElement.GetProperty("bib").GetString());
    }

    [Fact]
    public async Task Resend_and_reissue_refuse_declined_participants_and_finished_events_refuse_edits_and_mail()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Ready);
        var declined = await SeedParticipantAsync(host, ev, ParticipantStatus.Declined, "No Thanks");
        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/{declined}/resend", new { }), HttpStatusCode.Conflict, "participant_declined");
        await AssertErrorAsync(await org.PostAsync($"{Participants(ev)}/{declined}/reissue-token", new { }), HttpStatusCode.Conflict, "participant_declined");

        var finished = await SeedEventAsync(host, org.OrganizerId, EventState.Finished);
        var done = await SeedParticipantAsync(host, finished, ParticipantStatus.Confirmed, "Done");
        await AssertErrorAsync(await org.PutAsync($"{Participants(finished)}/{done}", new { name = "x" }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await org.PostAsync($"{Participants(finished)}/{done}/resend", new { }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await org.PostAsync($"{Participants(finished)}/{done}/reissue-token", new { }), HttpStatusCode.Conflict, "event_finished");
        await AssertErrorAsync(await org.PostAsync($"{Participants(finished)}/remind", new { }), HttpStatusCode.Conflict, "event_finished");
        await using var db = Db(host);
        Assert.Empty(await db.Outbox.Where(m => m.Purpose == EmailPurpose.Invitation).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_creates_get_distinct_bibs_and_only_one_of_the_same_address_wins()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);

        var distinct = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => CreateAsync(org, ev, $"p{i}@example.org")));
        var same = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateAsync(org, ev, "same@example.org")));

        Assert.All(distinct, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(same, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var r in same.Where(r => r.StatusCode != HttpStatusCode.Created))
            await AssertErrorAsync(r, HttpStatusCode.Conflict, "email_duplicate", "email");
        await using var db = Db(host);
        var bibs = await db.Participants.Select(p => p.Bib).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 9).Select(i => i.ToString(CultureInfo.InvariantCulture)).Order(), bibs.Order());
    }

    [Fact]
    public async Task An_internationalized_domain_and_its_ASCII_form_are_the_same_address()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(org, ev, "x@bücher.de")).StatusCode);

        await AssertErrorAsync(await CreateAsync(org, ev, "x@xn--bcher-kva.de"), HttpStatusCode.Conflict, "email_duplicate", "email");
        await AssertErrorAsync(await CreateAsync(org, ev, "X@BÜCHER.de"), HttpStatusCode.Conflict, "email_duplicate", "email");
        await using var db = Db(host);
        Assert.Equal("x@xn--bcher-kva.de", (await db.Outbox.SingleAsync(m => m.Purpose == EmailPurpose.Invitation)).ToAddress);
    }

    [Fact]
    public async Task Fields_outside_the_contract_in_the_body_are_ignored()
    {
        await using var host = Host();
        var org = await SessionAsync(host, "org@example.org");
        var ev = await SeedEventAsync(host, org.OrganizerId, EventState.Draft);
        var forged = Guid.NewGuid();

        var response = await org.PostAsync(Participants(ev), new
        {
            name = "Jana", email = "jana@example.org", gender = "female", language = "sk",
            participantId = forged, id = forged, status = "Confirmed", isMinor = true, showNameToSpectators = true, eventId = Guid.NewGuid(),
        });
        var pid = await ParticipantIdAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await org.PutAsync($"{Participants(ev)}/{pid}", new { status = "Confirmed", isMinor = true, emailNormalized = "x@y.z" })).StatusCode);

        Assert.NotEqual(forged, pid);
        await using var db = Db(host);
        var p = await db.Participants.SingleAsync();
        Assert.Equal(pid, p.Id);
        Assert.Equal(ev, p.EventId);
        Assert.Equal(ParticipantStatus.Invited, p.Status);
        Assert.False(p.IsMinor);
        Assert.False(p.ShowNameToSpectators);
        Assert.Equal("jana@example.org", p.EmailNormalized);
    }

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    // ----- helpers -----

    private static string Participants(Guid eventId) => Participants(eventId.ToString("D"));

    private static string Participants(string eventId) => $"/api/events/{eventId}/participants";

    private static Task<HttpResponseMessage> CreateAsync(Organizer org, Guid ev, string email, string? bib = null, string language = "sk") =>
        org.PostAsync(Participants(ev), new { name = "Rider " + email.Trim(), email, gender = "female", language, bib });

    private static async Task<string> CreateBibAsync(Organizer org, Guid ev, string email, string? bib = null)
    {
        var response = await CreateAsync(org, ev, email, bib);
        using var json = await JsonAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return json.RootElement.GetProperty("bib").GetString()!;
    }

    private static async Task<Guid> ParticipantIdAsync(HttpResponseMessage response)
    {
        using var json = await JsonAsync(response);
        return Guid.ParseExact(json.RootElement.GetProperty("participantId").GetString()!, "D");
    }

    private static async Task<List<string>> NamesAsync(Organizer org, string path)
    {
        using var json = await JsonAsync(await org.GetAsync(path));
        return json.RootElement.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList();
    }

    private static string InviteToken(EmailOutboxEntity mail)
    {
        var match = InviteLink.Match(mail.TextBody);
        Assert.True(match.Success, "The invitation has no link.");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private ApiFactory Host(Action<IServiceCollection>? services = null, Action<Dictionary<string, string?>>? settings = null)
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
        factory.Settings["Email:Outbox:PollIntervalSeconds"] = "3888000";
        factory.Settings["Email:Outbox:RetentionDays"] = "3650";
        factory.Settings["Accounts:MinResponseMilliseconds"] = "0";
        settings?.Invoke(factory.Settings);
        return factory;
    }

    private HttpClient Client(ApiFactory host)
    {
        var client = host.CreateDefaultClient(new Uri("https://localhost"));
        client.DefaultRequestHeaders.Add(IpHeader, $"192.0.2.{Interlocked.Increment(ref _ip)}");
        return client;
    }

    /// <summary>An organizer with a session: the account is seeded, then a sign-in link is requested and redeemed.</summary>
    private async Task<Organizer> SessionAsync(ApiFactory host, string email, bool confirmed = true)
    {
        var id = Guid.NewGuid();
        await SeedOrganizerAsync(host, id, email);
        var client = Client(host);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/account/signin", Json(JsonSerializer.Serialize(new { email })))).StatusCode);
        string token;
        await using (var db = Db(host))
        {
            var mail = await db.Outbox.Where(m => m.Purpose == EmailPurpose.SignIn && m.ToAddress == email).OrderBy(m => m.Id).LastAsync();
            token = Uri.UnescapeDataString(SignInLink.Match(mail.TextBody).Groups[1].Value);
        }
        var redeemed = await Send(client, HttpMethod.Post, "/api/account/signin/redeem", Json(JsonSerializer.Serialize(new { token })));
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(CookieAuth.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        if (!confirmed)
        {
            await using var db = Db(host);
            await db.Organizers.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.EmailConfirmedAt, (DateTimeOffset?)null));
        }
        return new Organizer(client, cookie, id);
    }

    private static async Task SeedOrganizerAsync(ApiFactory host, Guid id, string? email = null)
    {
        email ??= $"{id:N}@example.org";
        await using var db = Db(host);
        db.Organizers.Add(new OrganizerEntity
        {
            Id = id, Name = "Org " + email, Email = email, EmailNormalized = email, Language = "sk",
            CreatedAt = host.Clock.GetUtcNow(), EmailConfirmedAt = host.Clock.GetUtcNow(), SecurityStamp = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
    }

    private sealed class Organizer(HttpClient client, string cookie, Guid organizerId)
    {
        public Guid OrganizerId { get; } = organizerId;

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

        public Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, Json(JsonSerializer.Serialize(body)));

        public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, Json(JsonSerializer.Serialize(body)));

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, bool header = true)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            if (header) request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
            request.Headers.Add("Cookie", cookie);
            return client.SendAsync(request);
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(BrowserApiHeaders.HeaderName, BrowserApiHeaders.HeaderValue);
        return client.SendAsync(request);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<Guid> SeedEventAsync(ApiFactory host, Guid organizerId, EventState state, byte[]? gpx = null)
    {
        await using var db = Db(host);
        var ev = new EventEntity
        {
            Id = Guid.NewGuid(), OrganizerId = organizerId, Name = "Jarná časovka", Location = "Žilina", StartTime = Start,
            Sport = Sport.Cycling, State = state, Laps = 1, SpectatorDelaySeconds = 30, GpxBytes = gpx,
            CreatedAt = host.Clock.GetUtcNow(), Version = Guid.NewGuid(),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<Guid> SeedCategoryAsync(ApiFactory host, Guid eventId, string name)
    {
        await using var db = Db(host);
        var c = new CategoryEntity { Id = Guid.NewGuid(), EventId = eventId, Name = name };
        db.Categories.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<Guid> SeedWaveAsync(ApiFactory host, Guid eventId, string name)
    {
        await using var db = Db(host);
        var w = new WaveEntity { Id = Guid.NewGuid(), EventId = eventId, Name = name, StartTime = Start };
        db.Waves.Add(w);
        await db.SaveChangesAsync();
        return w.Id;
    }

    private static async Task<Guid> SeedParticipantAsync(ApiFactory host, Guid eventId, ParticipantStatus status, string name, Guid? categoryId = null)
    {
        await using var db = Db(host);
        var n = await db.Participants.CountAsync(p => p.EventId == eventId) + 1;
        var p = new ParticipantEntity
        {
            Id = Guid.NewGuid(), EventId = eventId, Bib = n.ToString(CultureInfo.InvariantCulture), Name = name,
            Email = $"rider{n}@example.com", EmailNormalized = $"rider{n}@example.com", Gender = "female", Language = "sk",
            CategoryId = categoryId, Status = status, ShowNameToSpectators = true, CreatedAt = host.Clock.GetUtcNow().AddSeconds(n),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static AppDbContext Db(ApiFactory host)
    {
        _ = host.Services;
        return new AppDbContext(PostgresFixture.Options(host.Settings["ConnectionStrings:Default"]!));
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("message");
        var sk = message.GetProperty("sk").GetString();
        var en = message.GetProperty("en").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sk));
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(sk, en);
        if (field is not null) Assert.Equal(field, json.RootElement.GetProperty("field").GetString());
    }

    private static byte[] Gpx(IReadOnlyList<GeoPoint> points)
    {
        var gpx = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>""");
        foreach (var p in points)
            gpx.Append(CultureInfo.InvariantCulture, $"""<trkpt lat="{p.Lat:R}" lon="{p.Lon:R}"></trkpt>""");
        return Encoding.UTF8.GetBytes(gpx.Append("</trkseg></trk></gpx>").ToString());
    }

    /// <summary>
    /// A real-mode sender (not simulated) that records invitations and rejects every message to <c>bounce@…</c>
    /// permanently.
    /// </summary>
    private sealed class BouncingSender : IEmailSender
    {
        private readonly Lock _lock = new();
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get { lock (_lock) return _sent.ToList(); }
        }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (message.Purpose != EmailPurpose.Invitation) return Task.CompletedTask; // the organizers' sign-in mails
            lock (_lock) _sent.Add(message.ToAddress);
            if (message.ToAddress.StartsWith("bounce@", StringComparison.OrdinalIgnoreCase))
                throw new EmailDeliveryException(true, "Recipient rejected (5.1.1).");
            return Task.CompletedTask;
        }
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
}
