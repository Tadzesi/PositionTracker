using System.Globalization;
using System.Text;
using PositionTracker.Core;
using PositionTracker.Data.Entities;

namespace PositionTracker.Email;

/// <summary>
/// The Slovak and English emails (HTML and plain text). Every user value is made single-line and length-limited
/// with <see cref="EmailText.SingleLine"/> and HTML-escaped in the HTML body; links must be absolute http(s) URLs
/// (built by the caller with <see cref="EmailLinks.Build"/>). A language other than <c>sk</c> gets English. Event
/// dates and times are shown in the event's local time zone (<see cref="EventTimeZone"/>).
/// </summary>
public static class EmailTemplates
{
    public const string ProductName = "PositionTracker";

    public const string EventTimeZoneId = "Europe/Bratislava";

    /// <summary>
    /// Events take place in Slovakia; their stored UTC start is shown in Central European time. Null when the time
    /// zone data is missing on this machine (startup validation then fails; there is no silent UTC fallback).
    /// </summary>
    public static TimeZoneInfo? EventTimeZoneOrNull { get; } = ResolveTimeZone([EventTimeZoneId, "Central Europe Standard Time"]);

    public static TimeZoneInfo EventTimeZone => EventTimeZoneOrNull
        ?? throw new InvalidOperationException($"The time zone {EventTimeZoneId} cannot be resolved (install tzdata/ICU).");

    /// <summary>The participant invitation, sent on behalf of the organizer (replies go to the organizer).</summary>
    public static EmailMessage Invitation(ParticipantEntity p, EventEntity e, OrganizerEntity organizer, string confirmUrl, string language)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(organizer);
        RequireHttpUrl(confirmUrl);
        var sk = IsSlovak(language);
        var name = EmailText.SingleLine(p.Name, EmailText.MaxNameLength);
        var organizerName = EmailText.SingleLine(organizer.Name, EmailText.MaxNameLength);
        var eventName = EmailText.SingleLine(e.Name, EmailText.MaxValueLength);
        var location = EmailText.SingleLine(e.Location, EmailText.MaxValueLength);
        var start = TimeZoneInfo.ConvertTime(e.StartTime, EventTimeZone);
        var date = sk ? start.ToString("d. M. yyyy", CultureInfo.InvariantCulture) : start.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        var time = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        var sport = e.Sport switch
        {
            Sport.Running => sk ? "beh" : "running",
            Sport.Cycling => sk ? "cyklistika" : "cycling",
            _ => e.Sport.ToString().ToLowerInvariant(),
        };

        var content = new Content
        {
            Greeting = sk ? $"Dobrý deň, {name}," : $"Hello {name},",
            Intro = sk
                ? $"{organizerName} vás pozýva na podujatie {eventName}."
                : $"{organizerName} invites you to the event {eventName}.",
            Details = sk
                ? [("Podujatie", eventName), ("Miesto", location), ("Dátum", date), ("Štart", time), ("Šport", sport)]
                : [("Event", eventName), ("Location", location), ("Date", date), ("Start", time), ("Sport", sport)],
            BeforeButton = sk ? "Účasť potvrďte kliknutím na tlačidlo:" : "Please confirm your participation:",
            Button = sk ? "Potvrdiť účasť" : "Confirm participation",
            Url = confirmUrl,
            After = sk
                ?
                [
                    "Odkaz je platný 14 dní.",
                    "Ak sa nechcete zúčastniť, môžete účasť na stránke z odkazu odmietnuť alebo tento e-mail jednoducho ignorovať.",
                ]
                :
                [
                    "The link is valid for 14 days.",
                    "If you do not want to take part, you can decline on the page behind the link or simply ignore this email.",
                ],
            Footer = sk
                ? $"Túto správu poslal systém {ProductName} v mene organizátora {organizerName}. Nejde o reklamnú ani marketingovú správu. " +
                  "Ak si neželáte dostávať ďalšie správy k tomuto podujatiu, odpovedzte na tento e-mail – odpoveď dostane organizátor."
                : $"This message was sent by {ProductName} on behalf of the organizer {organizerName}. It is not advertising or marketing. " +
                  "If you do not want any further messages about this event, reply to this email – your reply goes to the organizer.",
        };
        var subject = EmailText.SingleLine((sk ? "Pozvánka na " : "Invitation to ") + eventName, EmailText.MaxSubjectLength);
        return Render(p.Email, name, subject, sk, content, EmailPurpose.Invitation) with
        {
            ReplyToAddress = EmailText.IsPlainAddress(organizer.Email) ? organizer.Email : null,
            ReplyToName = organizerName,
        };
    }

    /// <summary>The organizer's one-time sign-in link (valid 15 minutes, single use).</summary>
    public static EmailMessage SignIn(OrganizerEntity o, string signInUrl)
    {
        ArgumentNullException.ThrowIfNull(o);
        RequireHttpUrl(signInUrl);
        var sk = IsSlovak(o.Language);
        var name = EmailText.SingleLine(o.Name, EmailText.MaxNameLength);
        var content = new Content
        {
            Greeting = sk ? $"Dobrý deň, {name}," : $"Hello {name},",
            Intro = sk ? $"na prihlásenie do {ProductName} kliknite na tlačidlo:" : $"click the button to sign in to {ProductName}:",
            Button = sk ? "Prihlásiť sa" : "Sign in",
            Url = signInUrl,
            After = sk
                ?
                [
                    "Odkaz je platný 15 minút a dá sa použiť iba raz.",
                    "Ak ste o prihlásenie nežiadali, tento e-mail ignorujte; bez kliknutia na odkaz sa nikto neprihlási.",
                ]
                :
                [
                    "The link is valid for 15 minutes and can be used only once.",
                    "If you did not ask to sign in, ignore this email; nobody is signed in without the link.",
                ],
            Footer = sk
                ? $"Túto správu ste dostali, pretože pre túto adresu bolo vyžiadané prihlásenie do {ProductName}. Nejde o marketingovú správu."
                : $"You received this message because a sign-in to {ProductName} was requested for this address. It is not marketing.",
        };
        var subject = sk ? $"Prihlásenie do {ProductName}" : $"Sign in to {ProductName}";
        return Render(o.Email, name, subject, sk, content, EmailPurpose.SignIn);
    }

    /// <summary>Confirmation of a new organizer's email address (valid 14 days).</summary>
    public static EmailMessage ConfirmOrganizerEmail(OrganizerEntity o, string confirmUrl)
    {
        ArgumentNullException.ThrowIfNull(o);
        RequireHttpUrl(confirmUrl);
        var sk = IsSlovak(o.Language);
        var name = EmailText.SingleLine(o.Name, EmailText.MaxNameLength);
        var content = new Content
        {
            Greeting = sk ? $"Dobrý deň, {name}," : $"Hello {name},",
            Intro = sk
                ? $"ďakujeme za registráciu organizátora v {ProductName}. Potvrďte, prosím, že táto e-mailová adresa patrí vám:"
                : $"thank you for signing up as an organizer in {ProductName}. Please confirm that this email address is yours:",
            Button = sk ? "Potvrdiť e-mail" : "Confirm email",
            Url = confirmUrl,
            After = sk
                ?
                [
                    "Odkaz je platný 14 dní.",
                    "Ak ste sa neregistrovali, tento e-mail ignorujte; bez potvrdenia sa účet nedá použiť na pozývanie účastníkov.",
                ]
                :
                [
                    "The link is valid for 14 days.",
                    "If you did not sign up, ignore this email; without confirmation the account cannot invite participants.",
                ],
            Footer = sk
                ? $"Túto správu ste dostali, pretože s touto adresou bola vytvorená registrácia v {ProductName}. Nejde o marketingovú správu."
                : $"You received this message because this address was used to sign up in {ProductName}. It is not marketing.",
        };
        var subject = sk ? "Potvrďte svoju e-mailovú adresu" : "Confirm your email address";
        return Render(o.Email, name, subject, sk, content, EmailPurpose.OrganizerEmailConfirmation);
    }

    /// <summary>The plain deliverability test message of <c>tools/SendTestMail</c>.</summary>
    public static EmailMessage DeliverabilityTest(string toAddress, string runId, DateTimeOffset sentAt)
    {
        var id = EmailText.SingleLine(runId, 40);
        var content = new Content
        {
            Greeting = "Dobrý deň / Hello,",
            Intro = $"toto je testovacia správa doručiteľnosti {ProductName}. / This is a {ProductName} deliverability test message.",
            Details = [("Test", id), ("UTC", sentAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))],
            After = ["Netreba odpovedať. / No reply is needed."],
            Footer = $"{ProductName} – test SPF/DKIM/DMARC.",
        };
        return Render(toAddress, "", $"{ProductName} deliverability test {id}", true, content, EmailPurpose.DeliverabilityTest)
            with { Language = "sk" };
    }

    private static bool IsSlovak(string? language) => string.Equals(language?.Trim(), "sk", StringComparison.OrdinalIgnoreCase);

    private static void RequireHttpUrl(string url)
    {
        if (!EmailLinks.IsHttpUrl(url))
            throw new ArgumentException("The link must be an absolute http or https URL.", nameof(url));
    }

    private sealed class Content
    {
        public required string Greeting { get; init; }
        public required string Intro { get; init; }
        public (string Label, string Value)[] Details { get; init; } = [];
        public string? BeforeButton { get; init; }
        public string? Button { get; init; }
        public string? Url { get; init; }
        public string[] After { get; init; } = [];
        public required string Footer { get; init; }
    }

    private static EmailMessage Render(string toAddress, string toName, string subject, bool sk, Content c, string purpose)
    {
        var language = sk ? "sk" : "en";
        var fallback = sk ? "Ak tlačidlo nefunguje, skopírujte do prehliadača tento odkaz:" : "If the button does not work, copy this link into your browser:";

        var text = new StringBuilder();
        text.Append(c.Greeting).Append("\n\n").Append(c.Intro).Append("\n\n");
        foreach (var (label, value) in c.Details) text.Append(label).Append(": ").Append(value).Append('\n');
        if (c.Details.Length > 0) text.Append('\n');
        if (c.BeforeButton is not null) text.Append(c.BeforeButton).Append('\n');
        if (c.Url is not null) text.Append(c.Button).Append(": ").Append(c.Url).Append("\n\n");
        foreach (var line in c.After) text.Append(line).Append('\n');
        text.Append("\n--\n").Append(c.Footer).Append('\n');

        var h = EmailText.Html;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(language).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(h(subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:24px;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1f2933;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:8px;padding:24px;\"><tr><td>")
            .Append("<p style=\"margin:0 0 16px;\">").Append(h(c.Greeting)).Append("</p>")
            .Append("<p style=\"margin:0 0 16px;\">").Append(h(c.Intro)).Append("</p>");
        if (c.Details.Length > 0)
        {
            html.Append("<table role=\"presentation\" cellpadding=\"4\" cellspacing=\"0\" style=\"margin:0 0 16px;\">");
            foreach (var (label, value) in c.Details)
                html.Append("<tr><td style=\"color:#52606d;\">").Append(h(label)).Append("</td><td><strong>")
                    .Append(h(value)).Append("</strong></td></tr>");
            html.Append("</table>");
        }
        if (c.BeforeButton is not null) html.Append("<p style=\"margin:0 0 12px;\">").Append(h(c.BeforeButton)).Append("</p>");
        if (c.Url is not null)
        {
            var url = h(c.Url);
            html.Append("<p style=\"margin:0 0 16px;\"><a href=\"").Append(url)
                .Append("\" style=\"display:inline-block;padding:12px 20px;background:#1e6fd9;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;\">")
                .Append(h(c.Button ?? "")).Append("</a></p>")
                .Append("<p style=\"margin:0 0 16px;font-size:13px;color:#52606d;\">").Append(h(fallback))
                .Append("<br><a href=\"").Append(url).Append("\" style=\"color:#1e6fd9;word-break:break-all;\">").Append(url).Append("</a></p>");
        }
        foreach (var line in c.After) html.Append("<p style=\"margin:0 0 12px;\">").Append(h(line)).Append("</p>");
        html.Append("<hr style=\"border:none;border-top:1px solid #e4e7eb;margin:24px 0 12px;\">")
            .Append("<p style=\"margin:0;font-size:12px;color:#7b8794;\">").Append(h(c.Footer)).Append("</p>")
            .Append("</td></tr></table></td></tr></table></body></html>");

        return new EmailMessage(toAddress.Trim(), toName, subject, html.ToString(), text.ToString(), language) { Purpose = purpose };
    }

    internal static TimeZoneInfo? ResolveTimeZone(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        }
        return null;
    }
}
