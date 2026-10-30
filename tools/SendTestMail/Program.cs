using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PositionTracker.Core;
using PositionTracker.Data.Entities;
using PositionTracker.Email;

// Deliverability test sender (docs/ops/email-deliverability.md). Reads the same Email:* configuration as the API
// (user-secrets of the API project, then environment variables Email__...), prints what it would send (recipient
// domains only, never secrets) and sends only with --yes.

const string Usage = """
    Usage: dotnet run --project tools/SendTestMail -- [--yes] [--invitation] <address> [<address> ...]

      <address>     recipient(s) of the test message (for example a Gmail and an Outlook mailbox you own)
      --invitation  also send a sample invitation (the real template with sample data), one per recipient
      --yes         really send; without it the tool only prints the plan and exits with code 2

    Configuration (never on the command line): Email:Smtp:Host, Email:Smtp:Port, Email:Smtp:UseStartTls,
    Email:Smtp:User, Email:Smtp:Password, Email:From, Email:FromName, Email:PublicBaseUrl from
    `dotnet user-secrets` (API project) or environment variables such as Email__Smtp__Host.
    """;

var confirmed = false;
var invitation = false;
var recipients = new List<string>();
foreach (var arg in args)
{
    switch (arg)
    {
        case "--yes": confirmed = true; break;
        case "--invitation": invitation = true; break;
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        default:
            if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option {arg}.");
                Console.Error.WriteLine(Usage);
                return 64;
            }
            if (!EmailText.IsPlainAddress(arg))
            {
                Console.Error.WriteLine($"Argument {recipients.Count + 1} is not a plain email address.");
                return 64;
            }
            recipients.Add(arg);
            break;
    }
}
if (recipients.Count == 0)
{
    Console.Error.WriteLine(Usage);
    return 64;
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();
var options = new EmailOptions();
configuration.GetSection(EmailOptions.Section).Bind(options);

if (!options.SmtpEnabled)
{
    Console.Error.WriteLine("Email:Smtp:Host is not configured: nothing to test. Set it with dotnet user-secrets or Email__Smtp__Host.");
    return 3;
}
var validation = new EmailOptionsValidator().Validate(null, options);
if (validation.Failed)
{
    Console.Error.WriteLine("Invalid email configuration:");
    foreach (var failure in validation.Failures ?? []) Console.Error.WriteLine("  " + failure);
    return 3;
}

var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
var kinds = invitation ? "deliverability test + sample invitation" : "deliverability test";
Console.WriteLine($"Run id:            {runId}");
Console.WriteLine($"Messages:          {kinds}, to each of {recipients.Count} recipient(s)");
Console.WriteLine($"Recipient domains: {string.Join(", ", recipients.Select(EmailText.Domain).Distinct())}");
Console.WriteLine($"Sender domain:     {EmailText.Domain(options.From)}");
Console.WriteLine($"SMTP:              port {options.Smtp.Port}, " +
    (options.Smtp.UseStartTls ? "STARTTLS required" : options.Smtp.Port == 465 ? "implicit TLS" : "NO TLS") +
    $", authentication {(string.IsNullOrEmpty(options.Smtp.User) ? "none" : "configured")}");
if (!confirmed)
{
    Console.WriteLine("Nothing sent. Re-run with --yes to send.");
    return 2;
}

var sender = new SmtpEmailSender(Options.Create(options));
var failures = 0;
var number = 0;
foreach (var recipient in recipients)
{
    number++;
    var messages = new List<EmailMessage> { EmailTemplates.DeliverabilityTest(recipient, runId, DateTimeOffset.UtcNow) };
    if (invitation) messages.Add(SampleInvitation(recipient, options, runId));
    foreach (var message in messages)
    {
        try
        {
            await sender.SendAsync(message, CancellationToken.None);
            Console.WriteLine($"#{number} ({EmailText.Domain(recipient)}) {message.Purpose}: accepted by the SMTP server");
        }
        catch (EmailDeliveryException ex)
        {
            failures++;
            Console.WriteLine($"#{number} ({EmailText.Domain(recipient)}) {message.Purpose}: FAILED, {(ex.Permanent ? "permanent" : "transient")}: {ex.Message}");
        }
    }
}
Console.WriteLine(failures == 0
    ? $"Done. Search the mailboxes for \"{runId}\" and check SPF/DKIM/DMARC in the message source."
    : $"Done with {failures} failure(s).");
return failures == 0 ? 0 : 1;

static EmailMessage SampleInvitation(string recipient, EmailOptions options, string runId)
{
    var organizer = new OrganizerEntity
    {
        Name = "Testovací organizátor", Email = options.From, EmailNormalized = options.From.ToLowerInvariant(), Language = "sk",
    };
    var evt = new EventEntity
    {
        Name = "Testovacie podujatie " + runId, Location = "Žilina", StartTime = DateTimeOffset.UtcNow.AddDays(7),
        Sport = Sport.Cycling, Laps = 1, SpectatorDelaySeconds = 30,
    };
    var participant = new ParticipantEntity
    {
        Bib = "1", Name = "Test", Email = recipient, EmailNormalized = recipient.ToLowerInvariant(), Gender = "other", Language = "sk",
    };
    var url = EmailLinks.Build(options.PublicBaseUrl, "invite", "test-not-a-real-token");
    return EmailTemplates.Invitation(participant, evt, organizer, url, "sk");
}
