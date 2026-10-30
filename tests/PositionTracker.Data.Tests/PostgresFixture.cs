using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PositionTracker.Data.Tests;

/// <summary>
/// One PostgreSQL container per test class; every test gets its own empty database from <see cref="NewDatabase"/>,
/// so tests do not see each other's rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>The connection string of a new, not yet created database (EF migrations create it).</summary>
    public string NewDatabase() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "t_" + Guid.NewGuid().ToString("N"),
        }.ConnectionString;

    public static DbContextOptions<AppDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AppDbContext>().UseAppDatabase(connectionString).Options;
}
