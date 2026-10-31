using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PositionTracker.Api.Persistence;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// The organizer session: an ASP.NET Core authentication cookie (<see cref="CookieName"/>; <c>HttpOnly</c>,
/// <c>Secure</c>, <c>SameSite=Lax</c>, path <c>/</c> (a proxy may narrow it to its sub-path), no domain, 30 days sliding). The encrypted ticket holds only
/// the organizer id and the account's security stamp; every request re-reads the account and rejects the cookie when
/// the account is gone or its stamp changed (sign-out rotates it, which ends every session of the account).
/// <para>Policies: <see cref="OrganizerSessionPolicy"/> (a valid session) and
/// <see cref="EmailConfirmedOrganizerPolicy"/> (a valid session of an account whose email is confirmed). A missing
/// session answers 401 <c>unauthorized</c>, an unconfirmed email 403 <c>email_not_confirmed</c> (JSON error bodies,
/// never a redirect).</para>
/// <para>CSRF: <c>SameSite=Lax</c> plus <see cref="BrowserApiHeaders"/> (custom header and JSON content type).</para>
/// </summary>
public static class CookieAuth
{
    public const string Scheme = "OrganizerCookie";
    /// <summary>
    /// The <c>__Secure-</c> prefix makes browsers insist on Secure. It is deliberately not <c>__Host-</c>: that prefix also
    /// demands <c>Path=/</c>, and the reverse proxy of a sub-path deployment (<c>/tracker/app/</c>) rewrites the cookie path,
    /// so browsers silently dropped the cookie and nobody could stay signed in.
    /// </summary>
    public const string CookieName = "__Secure-pt-session";
    public const string OrganizerSessionPolicy = "OrganizerSession";
    public const string EmailConfirmedOrganizerPolicy = "EmailConfirmedOrganizer";
    public const string OrganizerIdClaim = "oid";
    public const string SecurityStampClaim = "stamp";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private static readonly object OrganizerItemKey = new();

    /// <summary>The ticket of a session: the organizer id and security stamp, nothing else.</summary>
    public static ClaimsPrincipal CreatePrincipal(OrganizerEntity organizer) =>
        new(new ClaimsIdentity(
            [
                new Claim(OrganizerIdClaim, organizer.Id.ToString("D")),
                new Claim(SecurityStampClaim, organizer.SecurityStamp.ToString("D")),
            ],
            Scheme, OrganizerIdClaim, null));

    /// <summary>Starts a persistent session (the cookie expires <see cref="SessionLifetime"/> after the last renewal).</summary>
    public static Task SignInAsync(HttpContext context, OrganizerEntity organizer) =>
        context.SignInAsync(Scheme, CreatePrincipal(organizer), new AuthenticationProperties { IsPersistent = true });

    /// <summary>The organizer of the validated session of this request (loaded once per request), or null.</summary>
    public static OrganizerEntity? CurrentOrganizer(HttpContext context) =>
        context.Items.TryGetValue(OrganizerItemKey, out var value) ? value as OrganizerEntity : null;

    public static Guid? OrganizerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(OrganizerIdClaim), CultureInfo.InvariantCulture, out var id) ? id : null;

    public static IServiceCollection AddOrganizerAuthentication(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("PositionTracker");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<AccountOptions>, ILoggerFactory>((keys, accounts, loggers) =>
        {
            if (accounts.Value.DataProtectionKeysPath is { Length: > 0 } path)
                keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(path), loggers);
        });

        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = SessionLifetime;
            o.SlidingExpiration = true;
            o.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = ValidateAsync,
                // Never redirect: the authorization result handler answers with JSON.
                OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<TimeProvider>((o, clock) => o.TimeProvider = clock);

        services.AddAuthorizationBuilder()
            .AddPolicy(OrganizerSessionPolicy, p => p
                .AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(OrganizerIdClaim))
            .AddPolicy(EmailConfirmedOrganizerPolicy, p => p
                .AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(OrganizerIdClaim)
                .AddRequirements(new EmailConfirmedRequirement()));
        services.AddSingleton<IAuthorizationHandler, EmailConfirmedHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();
        return services;
    }

    /// <summary>Re-reads the account for every request; a missing account or a changed stamp ends the session.</summary>
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var organizer = await LoadAsync(context.HttpContext.RequestServices, context.Principal, context.HttpContext.RequestAborted);
        if (organizer is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
            return;
        }
        context.HttpContext.Items[OrganizerItemKey] = organizer;
    }

    /// <summary>The account of the principal when its stamp still matches; null otherwise (or without a database).</summary>
    private static async Task<OrganizerEntity?> LoadAsync(IServiceProvider services, ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (principal is null || OrganizerId(principal) is not { } id
            || !Guid.TryParse(principal.FindFirstValue(SecurityStampClaim), CultureInfo.InvariantCulture, out var stamp))
            return null;
        if (!services.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled) return null;
        await using var db = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
        var organizer = await db.Organizers.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct);
        return organizer is not null && organizer.SecurityStamp == stamp ? organizer : null;
    }

    private sealed class EmailConfirmedRequirement : IAuthorizationRequirement;

    private sealed class EmailConfirmedHandler : AuthorizationHandler<EmailConfirmedRequirement>
    {
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EmailConfirmedRequirement requirement)
        {
            var http = context.Resource as HttpContext;
            var organizer = http is not null ? CurrentOrganizer(http) : null;
            if (organizer is null && http is not null)
                organizer = await LoadAsync(http.RequestServices, context.User, http.RequestAborted);
            if (organizer is not null && OrganizerId(context.User) == organizer.Id && organizer.EmailConfirmedAt is not null)
                context.Succeed(requirement);
        }
    }

    /// <summary>401 <c>unauthorized</c> / 403 <c>email_not_confirmed</c> or <c>forbidden</c> as JSON error bodies.</summary>
    private sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Challenged) return AccountErrors.Unauthorized().ExecuteAsync(context);
            if (authorizeResult.Forbidden)
            {
                var unconfirmed = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<EmailConfirmedRequirement>().Any() == true;
                return (unconfirmed ? AccountErrors.EmailNotConfirmed() : AccountErrors.Forbidden()).ExecuteAsync(context);
            }
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
