namespace Umbra.Core.Tracking;

/// <summary>
/// Contract for persisting and querying application activity intervals with crash recovery,
/// WAL checkpointing, and single-writer command channel serialization.
/// </summary>
public interface IActivityDatabase : IAsyncDisposable
{
    /// <summary>
    /// Initializes the SQLite connection, WAL mode, pragmas, and schema migrations.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new activity interval and returns its database-generated row ID.
    /// Throws <see cref="InvalidOperationException"/> if another interval is already Open.
    /// </summary>
    Task<long> InsertIntervalAsync(AppActivityInterval interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the heartbeat timestamp for an active open interval.
    /// Throws <see cref="InvalidOperationException"/> if the interval is not in an Open state.
    /// </summary>
    Task UpdateHeartbeatAsync(long intervalId, DateTimeOffset heartbeatUtc, ulong heartbeatUnbiased, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes an open interval with its completion timestamp, duration, and terminal status.
    /// Throws <see cref="ArgumentException"/> if <paramref name="status"/> is <see cref="IntervalStatus.Open"/>.
    /// Throws <see cref="InvalidOperationException"/> if the interval is not in an Open state.
    /// </summary>
    Task CloseIntervalAsync(long intervalId, DateTimeOffset endUtc, ulong endUnbiased, double durationSeconds, IntervalStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovers all unfinalized open intervals across boots or process restarts, truncating them to their last confirmed heartbeat.
    /// Prefers monotonic unbiased duration calculation when valid, falling back to UTC subtraction if unavailable.
    /// </summary>
    Task<int> RecoverOpenIntervalsAsync(string currentBootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all intervals overlapping the specified UTC time range.
    /// </summary>
    Task<IReadOnlyList<AppActivityInterval>> GetIntervalsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the currently open interval, if one exists.
    /// </summary>
    Task<AppActivityInterval?> GetOpenIntervalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Purges completed historical intervals whose completion timestamp is older than the specified cutoff UTC.
    /// Never deletes an Open interval.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a WAL checkpoint in the specified mode, inspecting busy status and log page counts.
    /// </summary>
    Task<WalCheckpointResult> CheckpointAsync(WalCheckpointMode mode = WalCheckpointMode.Passive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully flushes all queued commands, performs a final TRUNCATE WAL checkpoint, and closes the database connection.
    /// </summary>
    Task ShutdownAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default);
}
