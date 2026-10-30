using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PositionTracker.Data.Entities;

namespace PositionTracker.Data;

/// <summary>
/// The PostgreSQL database of accounts, events, registrations and the position history. Enums are stored as their
/// names. Ordinary times are <c>timestamptz</c> and must be written in UTC (offset zero); the times of the position
/// history (fix time, status receive time, action time) are stored as bigint UTC ticks so a replay sees exactly the
/// value the runtime used.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OrganizerEntity> Organizers => Set<OrganizerEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<CourseEntity> Courses => Set<CourseEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<WaveEntity> Waves => Set<WaveEntity>();
    public DbSet<ParticipantEntity> Participants => Set<ParticipantEntity>();
    public DbSet<LinkTokenEntity> LinkTokens => Set<LinkTokenEntity>();
    public DbSet<DeviceTokenEntity> DeviceTokens => Set<DeviceTokenEntity>();
    public DbSet<FixEntity> Fixes => Set<FixEntity>();
    public DbSet<DeviceStatusEntity> DeviceStatuses => Set<DeviceStatusEntity>();
    public DbSet<ActionLogEntity> ActionLog => Set<ActionLogEntity>();
    public DbSet<EmailOutboxEntity> Outbox => Set<EmailOutboxEntity>();
    public DbSet<AlarmAckEntity> AlarmAcks => Set<AlarmAckEntity>();
    public DbSet<ConsentEntity> Consents => Set<ConsentEntity>();

    private static readonly ValueConverter<DateTimeOffset, long> UtcTicks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OrganizerEntity>(e =>
        {
            e.Property(o => o.Name).HasMaxLength(200);
            e.Property(o => o.Email).HasMaxLength(320);
            e.Property(o => o.EmailNormalized).HasMaxLength(320);
            e.Property(o => o.Language).HasMaxLength(2);
            e.HasIndex(o => o.EmailNormalized).IsUnique();
        });

        model.Entity<EventEntity>(e =>
        {
            e.Property(v => v.Name).HasMaxLength(120);
            e.Property(v => v.Location).HasMaxLength(120);
            e.Property(v => v.CheckpointsJson).HasColumnType("jsonb");
            e.HasOne<OrganizerEntity>().WithMany().HasForeignKey(v => v.OrganizerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => v.State);
        });

        model.Entity<CourseEntity>(e =>
        {
            e.HasKey(c => c.EventId);
            e.HasOne<EventEntity>().WithOne().HasForeignKey<CourseEntity>(c => c.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CategoryEntity>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(60);
            e.Property(c => c.Gender).HasMaxLength(16);
            e.HasOne<EventEntity>().WithMany().HasForeignKey(c => c.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.EventId, c.Name }).IsUnique();
        });

        model.Entity<WaveEntity>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(60);
            e.HasOne<EventEntity>().WithMany().HasForeignKey(w => w.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(w => new { w.EventId, w.Name }).IsUnique();
        });

        model.Entity<ParticipantEntity>(e =>
        {
            e.Property(p => p.Bib).HasMaxLength(16);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Nickname).HasMaxLength(30);
            e.Property(p => p.Email).HasMaxLength(320);
            e.Property(p => p.EmailNormalized).HasMaxLength(320);
            e.Property(p => p.Gender).HasMaxLength(16);
            e.Property(p => p.Language).HasMaxLength(2);
            e.HasOne<EventEntity>().WithMany().HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Category).WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<WaveEntity>().WithMany().HasForeignKey(p => p.WaveId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => new { p.EventId, p.EmailNormalized }).IsUnique();
            e.HasIndex(p => new { p.EventId, p.Bib }).IsUnique();
        });

        model.Entity<LinkTokenEntity>(e =>
        {
            e.Property(t => t.Purpose).HasMaxLength(32);
            e.Property(t => t.TokenHash).HasMaxLength(32);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.Purpose, t.SubjectId });
        });

        model.Entity<DeviceTokenEntity>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(32);
            e.HasIndex(t => t.TokenHash).IsUnique();
            ParticipantOwned(e, t => t.ParticipantId);
        });

        model.Entity<FixEntity>(e =>
        {
            e.Property(f => f.Time).HasConversion(UtcTicks).HasColumnName("TimeUtcTicks");
            History(e, f => f.EventId, f => f.ParticipantId);
            e.HasIndex(f => new { f.EventId, f.Time, f.Id });
        });

        model.Entity<DeviceStatusEntity>(e =>
        {
            e.Property(s => s.ReceivedAt).HasConversion(UtcTicks).HasColumnName("ReceivedAtUtcTicks");
            History(e, s => s.EventId, s => s.ParticipantId);
            e.HasIndex(s => new { s.EventId, s.Id });
        });

        model.Entity<ActionLogEntity>(e =>
        {
            e.ToTable("ActionLog");
            e.Property(a => a.Time).HasConversion(UtcTicks).HasColumnName("TimeUtcTicks");
            History(e, a => a.EventId, a => a.ParticipantId);
            e.HasIndex(a => new { a.EventId, a.Id });
        });

        model.Entity<EmailOutboxEntity>(e =>
        {
            e.ToTable("Outbox");
            e.Property(m => m.ToAddress).HasMaxLength(320);
            e.Property(m => m.ToName).HasMaxLength(200);
            e.Property(m => m.Subject).HasMaxLength(300);
            e.Property(m => m.Language).HasMaxLength(2);
            e.Property(m => m.Purpose).HasMaxLength(32);
            e.Property(m => m.ReplyToAddress).HasMaxLength(320);
            e.Property(m => m.ReplyToName).HasMaxLength(200);
            e.Property(m => m.LastError).HasMaxLength(1000);
            e.HasOne<ParticipantEntity>().WithMany().HasForeignKey(m => m.ParticipantId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(m => new { m.Status, m.NextAttemptAt });
            e.HasIndex(m => m.FinishedAt);
        });

        model.Entity<AlarmAckEntity>(e =>
        {
            e.HasKey(a => new { a.EventId, a.ParticipantId, a.Kind });
            ParticipantOwned(e, a => a.ParticipantId);
        });

        model.Entity<ConsentEntity>(e =>
        {
            e.Property(c => c.TextVersion).HasMaxLength(32);
            ParticipantOwned(e, c => c.ParticipantId);
        });
    }

    /// <summary>Rows that belong to a participant and go with it.</summary>
    private static void ParticipantOwned<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, object?>> participantId)
        where T : class =>
        e.HasOne<ParticipantEntity>().WithMany().HasForeignKey(participantId).OnDelete(DeleteBehavior.Cascade);

    /// <summary>Position history rows: owned by the participant and the event (PostgreSQL allows both cascades).</summary>
    private static void History<T>(EntityTypeBuilder<T> e,
        System.Linq.Expressions.Expression<Func<T, object?>> eventId,
        System.Linq.Expressions.Expression<Func<T, object?>> participantId) where T : class
    {
        e.HasOne<EventEntity>().WithMany().HasForeignKey(eventId).OnDelete(DeleteBehavior.Cascade);
        ParticipantOwned(e, participantId);
    }
}

public static class AppDatabase
{
    /// <summary>Configures the PostgreSQL provider for <see cref="AppDbContext"/>.</summary>
    public static DbContextOptionsBuilder UseAppDatabase(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseNpgsql(connectionString);
    }

    /// <inheritdoc cref="UseAppDatabase(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<AppDbContext> UseAppDatabase(
        this DbContextOptionsBuilder<AppDbContext> builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseNpgsql(connectionString);
    }
}

/// <summary>
/// For the EF tools (<c>dotnet ef migrations add</c>, <c>dotnet ef database update</c>): the connection string comes
/// from the environment variable <c>ConnectionStrings__Default</c>, else a local development database without a
/// password (enough for adding migrations, which does not connect).
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=positiontracker;Username=positiontracker";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseAppDatabase(connectionString).Options);
    }
}
