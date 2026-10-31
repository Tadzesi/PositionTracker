using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Accounts;

namespace PositionTracker.Api.Registration;

/// <summary>
/// Configuration section <c>Registration</c>: participant registration and the abuse limits of invitation mail.
/// Invitations carry organizer-controlled names from the project's domain, so they are capped per organizer and per
/// recipient address.
/// </summary>
public sealed class RegistrationOptions
{
    public const string Section = "Registration";

    /// <summary>Participants per event (all statuses); beyond, 409 <c>participant_limit</c>.</summary>
    public int MaxParticipantsPerEvent { get; set; } = 2000;

    /// <summary>
    /// Invitation mails (create, email change, resend, re-issue, reminder) per organizer and hour; beyond, 429
    /// <c>rate_limited</c> and nothing changes (a reminder skips the rest).
    /// </summary>
    public int InvitationsPerOrganizerPerHour { get; set; } = 500;

    /// <summary>Invitation mails per recipient address and hour, over all organizers and events.</summary>
    public int InvitationsPerAddressPerHour { get; set; } = 5;

    /// <summary>A reminder goes only to invited participants whose current link is older than this.</summary>
    public int RemindMinAgeHours { get; set; } = 24;

    /// <summary>Front-end page (relative to <c>Email:PublicBaseUrl</c>) that receives the invitation token.</summary>
    public string InvitePath { get; set; } = "p";

    /// <summary>Keys each limiter keeps at most (memory bound).</summary>
    public int RateLimitMaxKeys { get; set; } = 100_000;

    internal bool IsValid(out string error)
    {
        error = "";
        if (MaxParticipantsPerEvent < 1 || InvitationsPerOrganizerPerHour < 1 || InvitationsPerAddressPerHour < 1
            || RemindMinAgeHours < 0 || RateLimitMaxKeys < 1)
            error = "Registration:MaxParticipantsPerEvent, InvitationsPerOrganizerPerHour, InvitationsPerAddressPerHour and " +
                "RateLimitMaxKeys must be positive and RemindMinAgeHours not negative.";
        else if (string.IsNullOrWhiteSpace(InvitePath))
            error = "Registration:InvitePath must not be empty.";
        return error.Length == 0;
    }
}

/// <summary>
/// In-memory hourly budgets of invitation mail (per process, on the injected clock): per organizer
/// (<see cref="RegistrationOptions.InvitationsPerOrganizerPerHour"/>) and per normalized recipient address
/// (<see cref="RegistrationOptions.InvitationsPerAddressPerHour"/>, kept as a salted HMAC only). A mail takes from both
/// or from none. A restart resets the counters (documented; the participant cap per event is in the database).
/// </summary>
public sealed class InvitationLimiter
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly HourlyCounter _perOrganizer;
    private readonly HourlyCounter _perAddress;
    private readonly ILogger<InvitationLimiter> _logger;

    public InvitationLimiter(IOptions<RegistrationOptions> options, TimeProvider clock, ILogger<InvitationLimiter> logger)
    {
        var o = options.Value;
        _perOrganizer = new HourlyCounter(clock, o.InvitationsPerOrganizerPerHour, o.RateLimitMaxKeys);
        _perAddress = new HourlyCounter(clock, o.InvitationsPerAddressPerHour, o.RateLimitMaxKeys);
        _logger = logger;
    }

    /// <summary>Takes one invitation mail of the organizer to the address; false (nothing taken) over either budget.</summary>
    public bool TryTake(Guid organizerId, string normalizedEmail)
    {
        var organizerKey = organizerId.ToString("N");
        var addressKey = Convert.ToBase64String(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(normalizedEmail)));
        lock (_perAddress)
        {
            if (HourlyCounter.TryTakeAll((_perOrganizer, organizerKey), (_perAddress, addressKey))) return true;
        }
        _logger.LogInformation("Invitation mail of organizer {OrganizerId} refused by the hourly invitation limit.", organizerId);
        return false;
    }
}

internal sealed class RegistrationOptionsValidator : IValidateOptions<RegistrationOptions>
{
    public ValidateOptionsResult Validate(string? name, RegistrationOptions options) =>
        options.IsValid(out var error) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
}
