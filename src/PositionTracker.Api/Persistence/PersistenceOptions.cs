namespace PositionTracker.Api.Persistence;

/// <summary>
/// Configuration section <c>Persistence</c>, plus the connection string <c>ConnectionStrings:Default</c>. Without a
/// connection string the app runs in memory only (Plan 1 behaviour): nothing is written and nothing is rebuilt.
/// </summary>
public sealed class PersistenceOptions
{
    public const string Section = "Persistence";

    /// <summary>From <c>ConnectionStrings:Default</c> (not from this section).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Writes (one per ingested batch or action) accepted but not yet in the database, including those the writer
    /// holds while it writes. When all are taken, ingest waits up to <see cref="EnqueueTimeoutMs"/> and then answers 503.
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>How long a request waits for a free queue slot before 503 (milliseconds, real time).</summary>
    public int EnqueueTimeoutMs { get; set; } = 2000;

    /// <summary>The writer writes once it holds this many fixes ...</summary>
    public int BatchFixes { get; set; } = 200;

    /// <summary>... or this long after the oldest held write was queued (milliseconds).</summary>
    public int BatchIntervalMs { get; set; } = 500;

    /// <summary>Apply pending EF migrations at startup (meant for development).</summary>
    public bool MigrateOnStartup { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
