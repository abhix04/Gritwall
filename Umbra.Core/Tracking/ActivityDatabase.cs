using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Umbra.Core.Tracking;

/// <summary>
/// Production SQLite persistence engine for application activity intervals,
/// enforcing single-writer command channel serialization, WAL mode, crash recovery, and schema versioning.
/// </summary>
public sealed class ActivityDatabase : IActivityDatabase
{
    private readonly string _connectionString;
    private readonly Channel<DatabaseCommand> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly CancellationTokenSource _periodicTimerCts = new();
    private readonly Task? _periodicTimerTask;

    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;
    private Exception? _terminalFaultException;
    private volatile bool _isFaulted;
    private volatile bool _isWalMode;
    private readonly bool _isInMemory;

    internal Func<Task>? WorkerShutdownBarrier { get; set; }
    internal bool IsWorkerCompleted => _workerTask.IsCompleted;

    public ActivityDatabase(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _isInMemory = DetermineIsInMemory(_connectionString);

        _channel = Channel.CreateUnbounded<DatabaseCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _workerTask = Task.Run(ProcessCommandsAsync);

        // Run the 15-second PASSIVE WAL checkpoint scheduler (unless in-memory database)
        if (!_isInMemory)
        {
            _periodicTimerTask = Task.Run(RunPeriodicCheckpointLoopAsync);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueCommandAsync(new InitCommand(tcs), cancellationToken).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    public async Task<long> InsertIntervalAsync(AppActivityInterval interval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interval);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new InsertIntervalCommand(interval, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task UpdateHeartbeatAsync(long intervalId, DateTimeOffset heartbeatUtc, ulong heartbeatUnbiased, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new UpdateHeartbeatCommand(intervalId, heartbeatUtc, heartbeatUnbiased, tcs), cancellationToken).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    public async Task CloseIntervalAsync(long intervalId, DateTimeOffset endUtc, ulong endUnbiased, double durationSeconds, IntervalStatus status, CancellationToken cancellationToken = default)
    {
        if (status == IntervalStatus.Open)
        {
            throw new ArgumentException("Cannot finalize an interval with 'Open' status. A terminal status must be specified.", nameof(status));
        }

        if (status is not (IntervalStatus.Completed or IntervalStatus.TruncatedByCrash or IntervalStatus.TruncatedByUnnotifiedSleep))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Invalid terminal interval status specified.");
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new CloseIntervalCommand(intervalId, endUtc, endUnbiased, durationSeconds, status, tcs), cancellationToken).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    public async Task<int> RecoverOpenIntervalsAsync(string currentBootId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentBootId);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new RecoverOpenIntervalsCommand(currentBootId, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AppActivityInterval>> GetIntervalsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<AppActivityInterval>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new GetIntervalsCommand(startUtc, endUtc, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<AppActivityInterval?> GetOpenIntervalAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<AppActivityInterval?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new GetOpenIntervalCommand(tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new DeleteOlderThanCommand(cutoffUtc, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<WalCheckpointResult> CheckpointAsync(WalCheckpointMode mode = WalCheckpointMode.Passive, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<WalCheckpointResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueCommandAsync(new CheckpointCommand(mode, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task ShutdownAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        Task shutdownTask;
        lock (_shutdownLock)
        {
            _shutdownTask ??= PerformShutdownAsync();
            shutdownTask = _shutdownTask;
        }

        return AwaitShutdownAsync(shutdownTask, timeout, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task PerformShutdownAsync()
    {
        try
        {
            if (!_periodicTimerCts.IsCancellationRequested)
            {
                _periodicTimerCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _channel.Writer.TryComplete();

        await _workerTask.ConfigureAwait(false);

        if (_terminalFaultException != null)
        {
            ExceptionDispatchInfo.Capture(_terminalFaultException).Throw();
        }
    }

    private static async Task AwaitShutdownAsync(Task shutdownTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await shutdownTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ActivityDatabase shutdown timed out after {effectiveTimeout.TotalSeconds:F1}s before the worker task finished processing.");
        }
    }

    private async ValueTask EnqueueCommandAsync(DatabaseCommand command, CancellationToken cancellationToken)
    {
        if (_terminalFaultException != null)
        {
            throw new InvalidOperationException("The activity database is in a faulted state due to a startup failure.", _terminalFaultException);
        }

        if (_shutdownTask != null || _isFaulted)
        {
            throw new ObjectDisposedException(nameof(ActivityDatabase), "The activity database is disposed or shutting down.");
        }

        // Token cancellation applies strictly to channel enqueuing, never after entering the queue
        await _channel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPeriodicCheckpointLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(_periodicTimerCts.Token).ConfigureAwait(false))
            {
                if (_shutdownTask != null || _isFaulted)
                {
                    break;
                }

                var tcs = new TaskCompletionSource<WalCheckpointResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_channel.Writer.TryWrite(new CheckpointCommand(WalCheckpointMode.Passive, tcs)))
                {
                    try
                    {
                        var result = await tcs.Task.ConfigureAwait(false);
                        Trace.TraceInformation(
                            $"[ActivityDatabase] Periodic WAL checkpoint ({result.Mode}): Succeeded={result.Succeeded}, Busy={result.Busy}, LogPages={result.LogPages}, CheckpointedPages={result.CheckpointedPages}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Trace.TraceWarning($"[ActivityDatabase] Periodic WAL checkpoint failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ProcessCommandsAsync()
    {
        SqliteConnection? connection = null;

        try
        {
            connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(_workerCts.Token).ConfigureAwait(false);

            // Configure WAL mode and performance pragmas
            using (var pragmaCmd = connection.CreateCommand())
            {
                string? actualJournalMode;
                try
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode = WAL;";
                    var journalModeObj = await pragmaCmd.ExecuteScalarAsync(_workerCts.Token).ConfigureAwait(false);
                    actualJournalMode = Convert.ToString(journalModeObj, CultureInfo.InvariantCulture);
                }
                catch (SqliteException ex) when (!_isInMemory)
                {
                    throw new InvalidOperationException(
                        "Failed to activate WAL journal mode on persistent database due to an SQLite error.", ex);
                }

                _isWalMode = string.Equals(actualJournalMode, "wal", StringComparison.OrdinalIgnoreCase);

                if (!_isInMemory && !_isWalMode)
                {
                    throw new InvalidOperationException(
                        $"Failed to activate WAL journal mode on persistent database. SQLite returned journal mode '{actualJournalMode}'.");
                }

                // PRAGMA synchronous = NORMAL: WAL mode guarantees database consistency with NORMAL,
                // but recently committed transactions may be lost following a power failure or system crash.
                // This is an intentional performance/durability trade-off for high-frequency activity tracking.
                pragmaCmd.CommandText = """
                    PRAGMA synchronous = NORMAL;
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = 5000;
                    """;
                await pragmaCmd.ExecuteNonQueryAsync(_workerCts.Token).ConfigureAwait(false);
            }

            // Ensure schema migrations are applied
            await ApplySchemaMigrationsAsync(connection, _workerCts.Token).ConfigureAwait(false);

            // Sequential message pump loop: strictly 1 operation at a time on the single connection
            await foreach (var command in _channel.Reader.ReadAllAsync(_workerCts.Token).ConfigureAwait(false))
            {
                try
                {
                    await ExecuteCommandAsync(connection, command, _workerCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RejectCommand(command, ex);
                }
            }

            // Test barrier hook: hold worker before final shutdown checkpoint and connection teardown
            if (WorkerShutdownBarrier != null)
            {
                await WorkerShutdownBarrier().ConfigureAwait(false);
            }

            // Channel completed normally: perform final controlled TRUNCATE WAL checkpoint
            if (_isWalMode)
            {
                var truncateResult = await ExecuteCheckpointAsync(connection, WalCheckpointMode.Truncate, CancellationToken.None).ConfigureAwait(false);
                if (!truncateResult.Succeeded)
                {
                    throw new InvalidOperationException($"Final shutdown TRUNCATE checkpoint failed (Busy={truncateResult.Busy}, LogPages={truncateResult.LogPages}, CheckpointedPages={truncateResult.CheckpointedPages}).");
                }
            }
        }
        catch (Exception ex)
        {
            // Transition database into terminal faulted state
            _terminalFaultException = ex;
            _isFaulted = true;
            _channel.Writer.TryComplete(ex);

            // Reject all remaining pending commands
            while (_channel.Reader.TryRead(out var pendingCmd))
            {
                RejectCommand(pendingCmd, ex);
            }
        }
        finally
        {
            if (connection != null)
            {
                try
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress connection disposal errors during final teardown
                }
            }
        }
    }

    private static async Task ApplySchemaMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        int version = 0;
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            var result = await versionCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long longVal)
            {
                version = (int)longVal;
            }
            else if (result is int intVal)
            {
                version = intVal;
            }
        }

        if (version < 1)
        {
            using var tx = connection.BeginTransaction();
            using var schemaCmd = connection.CreateCommand();
            schemaCmd.Transaction = tx;
            schemaCmd.CommandText = DatabaseSchema.CreateSchemaV1;
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using var updateVersionCmd = connection.CreateCommand();
            updateVersionCmd.Transaction = tx;
            updateVersionCmd.CommandText = "PRAGMA user_version = 1;";
            await updateVersionCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            tx.Commit();
        }
    }

    private static async Task ExecuteCommandAsync(SqliteConnection connection, DatabaseCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case InitCommand init:
                init.Tcs.TrySetResult(true);
                break;

            case InsertIntervalCommand insert:
                long rowId = await ExecuteInsertAsync(connection, insert.Interval, cancellationToken).ConfigureAwait(false);
                insert.Tcs.TrySetResult(rowId);
                break;

            case UpdateHeartbeatCommand hb:
                bool hbSuccess = await ExecuteUpdateHeartbeatAsync(connection, hb.IntervalId, hb.HeartbeatUtc, hb.HeartbeatUnbiased, cancellationToken).ConfigureAwait(false);
                hb.Tcs.TrySetResult(hbSuccess);
                break;

            case CloseIntervalCommand close:
                bool closeSuccess = await ExecuteCloseIntervalAsync(connection, close.IntervalId, close.EndUtc, close.EndUnbiased, close.DurationSeconds, close.Status, cancellationToken).ConfigureAwait(false);
                close.Tcs.TrySetResult(closeSuccess);
                break;

            case RecoverOpenIntervalsCommand recover:
                int recoveredCount = await ExecuteRecoverOpenIntervalsAsync(connection, recover.CurrentBootId, cancellationToken).ConfigureAwait(false);
                recover.Tcs.TrySetResult(recoveredCount);
                break;

            case GetIntervalsCommand getIntervals:
                var intervals = await ExecuteGetIntervalsAsync(connection, getIntervals.StartUtc, getIntervals.EndUtc, cancellationToken).ConfigureAwait(false);
                getIntervals.Tcs.TrySetResult(intervals);
                break;

            case GetOpenIntervalCommand getOpen:
                var openInterval = await ExecuteGetOpenIntervalAsync(connection, cancellationToken).ConfigureAwait(false);
                getOpen.Tcs.TrySetResult(openInterval);
                break;

            case DeleteOlderThanCommand del:
                int deletedCount = await ExecuteDeleteOlderThanAsync(connection, del.CutoffUtc, cancellationToken).ConfigureAwait(false);
                del.Tcs.TrySetResult(deletedCount);
                break;

            case CheckpointCommand cp:
                var cpResult = await ExecuteCheckpointAsync(connection, cp.Mode, cancellationToken).ConfigureAwait(false);
                if (cp.Mode == WalCheckpointMode.Truncate && !cpResult.Succeeded)
                {
                    throw new InvalidOperationException($"PRAGMA wal_checkpoint(TRUNCATE) failed (Busy={cpResult.Busy}, LogPages={cpResult.LogPages}, CheckpointedPages={cpResult.CheckpointedPages}). Checkpoint could not complete.");
                }
                cp.Tcs.TrySetResult(cpResult);
                break;
        }
    }

    private static async Task<long> ExecuteInsertAsync(SqliteConnection connection, AppActivityInterval interval, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppActivityIntervals (
                BootId, ProcessName, ExecutablePath, State,
                StartTimeUtc, EndTimeUtc, LastHeartbeatUtc,
                StartUnbiasedTime, EndUnbiasedTime, LastHeartbeatUnbiasedTime,
                DurationSeconds, Status
            ) VALUES (
                @BootId, @ProcessName, @ExecutablePath, @State,
                @StartTimeUtc, @EndTimeUtc, @LastHeartbeatUtc,
                @StartUnbiasedTime, @EndUnbiasedTime, @LastHeartbeatUnbiasedTime,
                @DurationSeconds, @Status
            );
            SELECT last_insert_rowid();
            """;

        cmd.Parameters.AddWithValue("@BootId", interval.BootId);
        cmd.Parameters.AddWithValue("@ProcessName", interval.ProcessName);
        cmd.Parameters.AddWithValue("@ExecutablePath", interval.ExecutablePath);
        cmd.Parameters.AddWithValue("@State", interval.State.ToString());
        cmd.Parameters.AddWithValue("@StartTimeUtc", interval.StartTimeUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@EndTimeUtc", interval.EndTimeUtc.HasValue ? interval.EndTimeUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@LastHeartbeatUtc", interval.LastHeartbeatUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@StartUnbiasedTime", ToCheckedInt64(interval.StartUnbiasedTime));
        cmd.Parameters.AddWithValue("@EndUnbiasedTime", interval.EndUnbiasedTime.HasValue ? ToCheckedInt64(interval.EndUnbiasedTime.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("@LastHeartbeatUnbiasedTime", ToCheckedInt64(interval.LastHeartbeatUnbiasedTime));
        cmd.Parameters.AddWithValue("@DurationSeconds", interval.DurationSeconds.HasValue ? interval.DurationSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", interval.Status.ToString());

        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new InvalidOperationException("Cannot insert open interval: an open activity interval already exists in the database.", ex);
        }
    }

    private static async Task<bool> ExecuteUpdateHeartbeatAsync(SqliteConnection connection, long intervalId, DateTimeOffset heartbeatUtc, ulong heartbeatUnbiased, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE AppActivityIntervals
            SET LastHeartbeatUtc = @HeartbeatUtc,
                LastHeartbeatUnbiasedTime = @HeartbeatUnbiased
            WHERE Id = @Id AND Status = 'Open';
            """;

        cmd.Parameters.AddWithValue("@Id", intervalId);
        cmd.Parameters.AddWithValue("@HeartbeatUtc", heartbeatUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@HeartbeatUnbiased", ToCheckedInt64(heartbeatUnbiased));

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException($"Cannot update heartbeat: Interval {intervalId} is not in an Open state or does not exist.");
        }
        return true;
    }

    private static async Task<bool> ExecuteCloseIntervalAsync(SqliteConnection connection, long intervalId, DateTimeOffset endUtc, ulong endUnbiased, double durationSeconds, IntervalStatus status, CancellationToken cancellationToken)
    {
        if (status == IntervalStatus.Open || status is not (IntervalStatus.Completed or IntervalStatus.TruncatedByCrash or IntervalStatus.TruncatedByUnnotifiedSleep))
        {
            throw new ArgumentException($"Cannot finalize interval {intervalId} with non-terminal status '{status}'.", nameof(status));
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE AppActivityIntervals
            SET EndTimeUtc = @EndUtc,
                EndUnbiasedTime = @EndUnbiased,
                DurationSeconds = @DurationSeconds,
                Status = @Status
            WHERE Id = @Id AND Status = 'Open';
            """;

        cmd.Parameters.AddWithValue("@Id", intervalId);
        cmd.Parameters.AddWithValue("@EndUtc", endUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@EndUnbiased", ToCheckedInt64(endUnbiased));
        cmd.Parameters.AddWithValue("@DurationSeconds", durationSeconds);
        cmd.Parameters.AddWithValue("@Status", status.ToString());

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException($"Cannot finalize interval: Interval {intervalId} is not in an Open state or does not exist.");
        }
        return true;
    }

    private static async Task<int> ExecuteRecoverOpenIntervalsAsync(SqliteConnection connection, string currentBootId, CancellationToken cancellationToken)
    {
        using var tx = connection.BeginTransaction();

        var openRows = new List<(long Id, string BootId, DateTimeOffset StartUtc, DateTimeOffset LastHbUtc, ulong StartUnbiased, ulong LastHbUnbiased)>();

        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT Id, BootId, StartTimeUtc, LastHeartbeatUtc, StartUnbiasedTime, LastHeartbeatUnbiasedTime
                FROM AppActivityIntervals
                WHERE Status = 'Open';
                """;
            using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long id = reader.GetInt64(0);
                string bootId = reader.GetString(1);
                var startUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var lastHbUtc = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                ulong startUnbiased = ToCheckedUInt64(reader.GetInt64(4));
                ulong lastHbUnbiased = ToCheckedUInt64(reader.GetInt64(5));

                openRows.Add((id, bootId, startUtc, lastHbUtc, startUnbiased, lastHbUnbiased));
            }
        }

        if (openRows.Count == 0)
        {
            tx.Commit();
            return 0;
        }

        int recoveredCount = 0;
        using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE AppActivityIntervals
                SET Status = 'TruncatedByCrash',
                    EndTimeUtc = @EndTimeUtc,
                    EndUnbiasedTime = @EndUnbiasedTime,
                    DurationSeconds = @DurationSeconds
                WHERE Id = @Id AND Status = 'Open';
                """;

            var idParam = updateCmd.Parameters.Add("@Id", SqliteType.Integer);
            var endUtcParam = updateCmd.Parameters.Add("@EndTimeUtc", SqliteType.Text);
            var endUnbiasedParam = updateCmd.Parameters.Add("@EndUnbiasedTime", SqliteType.Integer);
            var durParam = updateCmd.Parameters.Add("@DurationSeconds", SqliteType.Real);

            foreach (var row in openRows)
            {
                double durationSec;
                object endUnbiasedVal;

                bool isMonotonicValid = row.StartUnbiased > 0 && row.LastHbUnbiased >= row.StartUnbiased;
                if (isMonotonicValid)
                {
                    // Prefer monotonic unbiased duration when valid (100ns units: 1s = 10,000,000 units)
                    durationSec = (row.LastHbUnbiased - row.StartUnbiased) / 10_000_000.0;
                    endUnbiasedVal = ToCheckedInt64(row.LastHbUnbiased);
                }
                else
                {
                    // Monotonic counters are invalid (e.g. heartbeat < start or missing counter)
                    // Fallback to UTC subtraction and persist EndUnbiasedTime = NULL
                    durationSec = Math.Max(0.0, (row.LastHbUtc.ToUniversalTime() - row.StartUtc.ToUniversalTime()).TotalSeconds);
                    endUnbiasedVal = DBNull.Value;
                }

                idParam.Value = row.Id;
                endUtcParam.Value = row.LastHbUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                endUnbiasedParam.Value = endUnbiasedVal;
                durParam.Value = durationSec;

                int affected = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                recoveredCount += affected;
            }
        }

        tx.Commit();
        return recoveredCount;
    }

    private static async Task<IReadOnlyList<AppActivityInterval>> ExecuteGetIntervalsAsync(SqliteConnection connection, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var list = new List<AppActivityInterval>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, BootId, ProcessName, ExecutablePath, State,
                   StartTimeUtc, EndTimeUtc, LastHeartbeatUtc,
                   StartUnbiasedTime, EndUnbiasedTime, LastHeartbeatUnbiasedTime,
                   DurationSeconds, Status
            FROM AppActivityIntervals
            WHERE StartTimeUtc <= @EndUtc AND (EndTimeUtc >= @StartUtc OR EndTimeUtc IS NULL)
            ORDER BY StartTimeUtc ASC;
            """;

        cmd.Parameters.AddWithValue("@StartUtc", startUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@EndUtc", endUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadInterval(reader));
        }

        return list;
    }

    private static async Task<AppActivityInterval?> ExecuteGetOpenIntervalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, BootId, ProcessName, ExecutablePath, State,
                   StartTimeUtc, EndTimeUtc, LastHeartbeatUtc,
                   StartUnbiasedTime, EndUnbiasedTime, LastHeartbeatUnbiasedTime,
                   DurationSeconds, Status
            FROM AppActivityIntervals
            WHERE Status = 'Open'
            ORDER BY Id DESC
            LIMIT 1;
            """;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadInterval(reader);
        }

        return null;
    }

    private static async Task<int> ExecuteDeleteOlderThanAsync(SqliteConnection connection, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM AppActivityIntervals
            WHERE Status != 'Open'
              AND EndTimeUtc IS NOT NULL
              AND EndTimeUtc < @CutoffUtc;
            """;

        cmd.Parameters.AddWithValue("@CutoffUtc", cutoffUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WalCheckpointResult> ExecuteCheckpointAsync(SqliteConnection connection, WalCheckpointMode mode, CancellationToken cancellationToken)
    {
        string modeStr = mode switch
        {
            WalCheckpointMode.Passive => "PASSIVE",
            WalCheckpointMode.Full => "FULL",
            WalCheckpointMode.Restart => "RESTART",
            WalCheckpointMode.Truncate => "TRUNCATE",
            _ => "PASSIVE"
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA wal_checkpoint({modeStr});";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount < 3)
        {
            throw new InvalidOperationException($"PRAGMA wal_checkpoint({modeStr}) did not return the expected result row.");
        }

        int busy = reader.GetInt32(0);
        int log = reader.GetInt32(1);
        int checkpointed = reader.GetInt32(2);
        return new WalCheckpointResult(mode, busy, log, checkpointed);
    }

    private static long ToCheckedInt64(ulong value)
    {
        return checked((long)value);
    }

    private static ulong ToCheckedUInt64(long value)
    {
        if (value < 0)
        {
            throw new OverflowException($"Negative database integer {value} cannot be converted to unsigned 64-bit counter.");
        }
        return (ulong)value;
    }

    private static AppActivityInterval ReadInterval(SqliteDataReader reader)
    {
        long id = reader.GetInt64(0);
        string bootId = reader.GetString(1);
        string processName = reader.GetString(2);
        string executablePath = reader.GetString(3);
        var state = Enum.Parse<ActivityState>(reader.GetString(4), ignoreCase: true);
        var startTimeUtc = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTimeOffset? endTimeUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var lastHeartbeatUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        ulong startUnbiased = ToCheckedUInt64(reader.GetInt64(8));
        ulong? endUnbiased = reader.IsDBNull(9) ? null : ToCheckedUInt64(reader.GetInt64(9));
        ulong lastHeartbeatUnbiased = ToCheckedUInt64(reader.GetInt64(10));
        double? durationSeconds = reader.IsDBNull(11) ? null : reader.GetDouble(11);
        var status = Enum.Parse<IntervalStatus>(reader.GetString(12), ignoreCase: true);

        return new AppActivityInterval
        {
            Id = id,
            BootId = bootId,
            ProcessName = processName,
            ExecutablePath = executablePath,
            State = state,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = endTimeUtc,
            LastHeartbeatUtc = lastHeartbeatUtc,
            StartUnbiasedTime = startUnbiased,
            EndUnbiasedTime = endUnbiased,
            LastHeartbeatUnbiasedTime = lastHeartbeatUnbiased,
            DurationSeconds = durationSeconds,
            Status = status
        };
    }

    private static void RejectCommand(DatabaseCommand command, Exception ex)
    {
        switch (command)
        {
            case InitCommand c: c.Tcs.TrySetException(ex); break;
            case InsertIntervalCommand c: c.Tcs.TrySetException(ex); break;
            case UpdateHeartbeatCommand c: c.Tcs.TrySetException(ex); break;
            case CloseIntervalCommand c: c.Tcs.TrySetException(ex); break;
            case RecoverOpenIntervalsCommand c: c.Tcs.TrySetException(ex); break;
            case GetIntervalsCommand c: c.Tcs.TrySetException(ex); break;
            case GetOpenIntervalCommand c: c.Tcs.TrySetException(ex); break;
            case DeleteOlderThanCommand c: c.Tcs.TrySetException(ex); break;
            case CheckpointCommand c: c.Tcs.TrySetException(ex); break;
        }
    }

    internal static bool DetermineIsInMemory(string connectionString)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (builder.Mode == SqliteOpenMode.Memory)
            {
                return true;
            }

            if (string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
