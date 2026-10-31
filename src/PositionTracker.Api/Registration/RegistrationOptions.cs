using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Registration;

/// <summary>
/// Configuration section <c>Registration</c>: participant registration and the abuse limits of invitation mail.
/// Invitations carry organizer-controlled names from the project's domain, so they are capped in total, per organizer
/// and per recipient address.
/// </summary>
public sealed class RegistrationOptions
{
    public const string Section = "Registration";

    /// <summary>Participants per event (all statuses); beyond, 409 <c>participant_limit</c>.</summary>
    public int MaxParticipantsPerEvent { get; set; } = 2000;

    /// <summary>
    /// Invitation mails (create, email change, resend, re-issue, reminder) per organizer and hour; beyond, 429
    /// <c>rate_limited</c> and nothing changes (a reminder skips the rest; a re-issue still revokes the device token).
    /// </summary>
    public int InvitationsPerOrganizerPerHour { get; set; } = 100;

    /// <summary>
    /// Invitation mails per hour in total. The default stays well below the sending mailbox's hourly quota and leaves
    /// room for the account mails (<c>Accounts:GlobalMailsPerHour</c>, 200).
    /// </summary>
    public int InvitationsGlobalPerHour { get; set; } = 300;

    /// <summary>Invitation mails per recipient address and hour, over all organizers and events.</summary>
    public int InvitationsPerAddressPerHour { get; set; } = 5;

    /// <summary>Invitation mails per recipient address and day (24 hours from the first), over all organizers and events.</summary>
    public int InvitationsPerAddressPerDay { get; set; } = 10;

    /// <summary>
    /// Without a confirmation deadline, a reminder goes only to invited participants whose current link is older than
    /// this.
    /// </summary>
    public int RemindMinAgeHours { get; set; } = 24;

    /// <summary>Front-end page (relative to <c>Email:PublicBaseUrl</c>) that receives the invitation token.</summary>
    public string InvitePath { get; set; } = "p";

    /// <summary>Keys each limiter keeps at most (memory bound).</summary>
    public int RateLimitMaxKeys { get; set; } = 100_000;

    internal bool IsValid(out string error)
    {
        error = "";
        if (MaxParticipantsPerEvent < 1 || InvitationsPerOrganizerPerHour < 1 || InvitationsGlobalPerHour < 1
            || InvitationsPerAddressPerHour < 1 || InvitationsPerAddressPerDay < 1 || RemindMinAgeHours < 0 || RateLimitMaxKeys < 1)
            error = "Registration:MaxParticipantsPerEvent, InvitationsPerOrganizerPerHour, InvitationsGlobalPerHour, " +
                "InvitationsPerAddressPerHour, InvitationsPerAddressPerDay and RateLimitMaxKeys must be positive and " +
                "RemindMinAgeHours not negative.";
        else if (string.IsNullOrWhiteSpace(InvitePath))
            error = "Registration:InvitePath must not be empty.";
        return error.Length == 0;
    }
}

/// <summary>
/// In-memory budgets of invitation mail (per process, on the injected clock): in total per hour
/// (<see cref="RegistrationOptions.InvitationsGlobalPerHour"/>), per organizer per hour
/// (<see cref="RegistrationOptions.InvitationsPerOrganizerPerHour"/>) and per normalized recipient address per hour and
/// per day (<see cref="RegistrationOptions.InvitationsPerAddressPerHour"/>,
/// <see cref="RegistrationOptions.InvitationsPerAddressPerDay"/>; addresses kept as a salted HMAC only). A mail takes
/// from all four or from none; <see cref="Refund"/> gives a unit back when the change that took it did not commit.
/// Refusals are counted per limit for 24 hours (<see cref="LimitHitsLast24h"/>, shown by <c>GET /api/ops/email-mode</c>
/// as <c>invitationLimitHits</c>); the global cap also logs a warning once per hour. A restart resets the counters
/// (the participant cap per event is in the database).
/// </summary>
public sealed class InvitationLimiter
{
    private static readonly string[] Kinds = ["global", "organizer", "address_hour", "address_day"];
    private const string GlobalKey = "global";
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly HourlyCounter _global;
    private readonly HourlyCounter _perOrganizer;
    private readonly HourlyCounter _perAddressHour;
    private readonly HourlyCounter _perAddressDay;
    private readonly LimitHitLog _hits;
    private readonly TimeProvider _clock;
    private readonly ILogger<InvitationLimiter> _logger;
    private readonly int _globalCap;
    private DateTimeOffset _globalWarnedAt = DateTimeOffset.MinValue;

    public InvitationLimiter(IOptions<RegistrationOptions> options, TimeProvider clock, ILogger<InvitationLimiter> logger)
    {
        var o = options.Value;
        _global = new HourlyCounter(clock, o.InvitationsGlobalPerHour, 1);
        _perOrganizer = new HourlyCounter(clock, o.InvitationsPerOrganizerPerHour, o.RateLimitMaxKeys);
        _perAddressHour = new HourlyCounter(clock, o.InvitationsPerAddressPerHour, o.RateLimitMaxKeys);
        _perAddressDay = new HourlyCounter(clock, o.InvitationsPerAddressPerDay, o.RateLimitMaxKeys, TimeSpan.FromDays(1));
        _hits = new LimitHitLog(clock, Kinds);
        _globalCap = o.InvitationsGlobalPerHour;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Takes one invitation mail of the organizer to the address; false (nothing taken, counted) over any budget.</summary>
    public bool TryTake(Guid organizerId, string normalizedEmail)
    {
        var (organizerKey, addressKey) = Keys(organizerId, normalizedEmail);
        lock (_global)
        {
            string? refused = null;
            if (!_perOrganizer.CanTake(organizerKey)) refused = "organizer";
            else if (!_perAddressDay.CanTake(addressKey)) refused = "address_day";
            else if (!_perAddressHour.CanTake(addressKey)) refused = "address_hour";
            else if (!HourlyCounter.TryTakeAll((_global, GlobalKey), (_perOrganizer, organizerKey),
                         (_perAddressHour, addressKey), (_perAddressDay, addressKey)))
                refused = "global";
            if (refused is null) return true;
            _hits.Add(refused);
            var now = _clock.GetUtcNow();
            if (refused == "global" && now - _globalWarnedAt >= TimeSpan.FromHours(1))
            {
                _globalWarnedAt = now;
                _logger.LogWarning("The global cap of {Cap} invitation emails per hour is reached; further invitations are refused this hour.", _globalCap);
            }
            else
            {
                _logger.LogInformation("Invitation mail of organizer {OrganizerId} refused by the {Limit} invitation limit.", organizerId, refused);
            }
            return false;
        }
    }

    /// <summary>Gives back a unit taken by <see cref="TryTake"/> for a mail that was not queued after all.</summary>
    public void Refund(Guid organizerId, string normalizedEmail)
    {
        var (organizerKey, addressKey) = Keys(organizerId, normalizedEmail);
        lock (_global)
        {
            _global.Give(GlobalKey);
            _perOrganizer.Give(organizerKey);
            _perAddressHour.Give(addressKey);
            _perAddressDay.Give(addressKey);
        }
    }

    /// <summary>Refusals of the last 24 hours per limit (counts only).</summary>
    public InvitationLimitHits LimitHitsLast24h()
    {
        var sums = _hits.Sums();
        return new InvitationLimitHits(sums[0], sums[1], sums[2], sums[3]);
    }

    private (string Organizer, string Address) Keys(Guid organizerId, string normalizedEmail) =>
        (organizerId.ToString("N"), Convert.ToBase64String(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(normalizedEmail))));
}

/// <summary>Invitation mails refused per limit in the last 24 hours (counts only); alert when <c>Global</c> is above zero.</summary>
public sealed record InvitationLimitHits(int Global, int PerOrganizer, int PerAddressHour, int PerAddressDay)
{
    public int Total => Global + PerOrganizer + PerAddressHour + PerAddressDay;
}

internal sealed class RegistrationOptionsValidator : IValidateOptions<RegistrationOptions>
{
    public ValidateOptionsResult Validate(string? name, RegistrationOptions options) =>
        options.IsValid(out var error) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
}
