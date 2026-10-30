using Microsoft.Extensions.Options;
using PositionTracker.Data;

namespace PositionTracker.Api.Persistence;

public static class PersistenceServices
{
    /// <summary>
    /// Registers the database, the fix store, the write queue and its writer, and the startup rehydration. All read
    /// <see cref="PersistenceOptions"/> lazily, so configuration overrides made after the builder (tests) apply.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddOptions<PersistenceOptions>()
            .BindConfiguration(PersistenceOptions.Section)
            .Configure<IConfiguration>((o, config) => o.ConnectionString = config.GetConnectionString("Default"))
            .Validate(o => o.QueueCapacity > 0 && o.EnqueueTimeoutMs >= 0 && o.BatchFixes > 0 && o.BatchIntervalMs > 0,
                "Persistence:QueueCapacity, BatchFixes and BatchIntervalMs must be positive and EnqueueTimeoutMs not negative.")
            .ValidateOnStart();
        services.AddDbContextFactory<AppDbContext>((sp, db) =>
        {
            // Without a connection string no context is ever created (everything checks Enabled first).
            if (sp.GetRequiredService<IOptions<PersistenceOptions>>().Value.ConnectionString is { Length: > 0 } cs)
                db.UseAppDatabase(cs);
        });
        services.AddSingleton<IFixStore, FixStore>();
        services.AddSingleton<PersistenceQueue>();
        services.AddSingleton<RuntimeRehydrator>();
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeRehydrator>());
        services.AddSingleton<PersistenceWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
        return services;
    }
}
