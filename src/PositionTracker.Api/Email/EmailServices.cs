using Microsoft.Extensions.Options;
using PositionTracker.Email;

namespace PositionTracker.Api.Email;

public static class EmailServices
{
    /// <summary>
    /// Registers <see cref="EmailOptions"/> (validated at startup), the sender and the outbox worker. The sender is
    /// <see cref="SmtpEmailSender"/> only when <c>Email:Smtp:Host</c> is configured; otherwise
    /// <see cref="LoggingEmailSender"/>, so real sending is an explicit opt-in.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services)
    {
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value.SmtpEnabled
            ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp)
            : ActivatorUtilities.CreateInstance<LoggingEmailSender>(sp));
        services.AddSingleton<EmailOutboxWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailOutboxWorker>());
        return services;
    }
}
