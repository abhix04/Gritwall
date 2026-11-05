using Microsoft.Data.Sqlite;
using Umbra.Core.Tracking;
using Xunit;

namespace Umbra.Tests.Tracking;

public sealed class ActivityDatabaseTests : IAsyncLifetime
{
    private readonly string _dbFilePath;
    private readonly string _connectionString;
    private ActivityDatabase? _database;

    public ActivityDatabaseTests()
    {
        _dbFilePath = Path.Combine(Path.GetTempPath(), $"gritwall_test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbFilePath};";
    }

    public async Task InitializeAsync()
    {
        _database = new ActivityDatabase(_connectionString);
        await _database.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database != null)
        {
            try
            {
                await _database.DisposeAsync();
            }
            catch
            {
                // Suppress disposal errors in tests
            }
        }

        // Clean up temporary database files (db, wal, shm)
        try
        {
            if (File.Exists(_dbFilePath)) File.Delete(_dbFilePath);
            string wal = _dbFilePath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            string shm = _dbFilePath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch
        {
            // Best effort file deletion
        }
    }

    private static AppActivityInterval CreateSampleInterval(
        string processName = "code.exe",
        ActivityState state = ActivityState.Active,
        DateTimeOffset? startUtc = null,
        IntervalStatus status = IntervalStatus.Open,
        DateTimeOffset? endUtc = null)
    {
        var start = startUtc ?? DateTimeOffset.UtcNow;
        return new AppActivityInterval
        {
            BootId = "BOOT-TEST-1234",
            ProcessName = processName,
            ExecutablePath = @"C:\Program Files\Microsoft VS Code\code.exe",
            State = state,
            StartTimeUtc = start,
            EndTimeUtc = endUtc,
            LastHeartbeatUtc = start,
            StartUnbiasedTime = 10_000_000,
            EndUnbiasedTime = endUtc.HasValue ? 10_000_000 + 3_000_000_000UL : null,
            LastHeartbeatUnbiasedTime = 10_000_000,
            DurationSeconds = endUtc.HasValue ? 300.0 : null,
            Status = status
        };
    }

    [Fact]
    public async Task InitializeAsync_ConfiguresWalModeAndSchemaVersion()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version; PRAGMA journal_mode;";
        using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        int version = reader.GetInt32(0);
        Assert.Equal(DatabaseSchema.CurrentVersion, version);

        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        string journalMode = reader.GetString(0);
        Assert.Equal("wal", journalMode.ToLowerInvariant());
    }

    [Fact]
    public async Task InsertInterval_PersistsRecordAndReturnsGeneratedId()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        Assert.True(id > 0);

        var open = await _database.GetOpenIntervalAsync();
        Assert.NotNull(open);
        Assert.Equal(id, open.Id);
        Assert.Equal("code.exe", open.ProcessName);
        Assert.Equal(IntervalStatus.Open, open.Status);
        Assert.Null(open.EndTimeUtc);
    }

    [Fact]
    public async Task InsertInterval_DuplicateOpenInterval_IsRejectedByPartialUniqueIndex()
    {
        var open1 = CreateSampleInterval("app1.exe");
        long id1 = await _database!.InsertIntervalAsync(open1);
        Assert.True(id1 > 0);

        var open2 = CreateSampleInterval("app2.exe");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.InsertIntervalAsync(open2));

        Assert.Contains("open activity interval already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateHeartbeat_UpdatesHeartbeatTimestampOnOpenInterval()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        var newHeartbeatUtc = sample.StartTimeUtc.AddSeconds(15);
        ulong newHeartbeatUnbiased = sample.StartUnbiasedTime + 150_000_000;

        await _database.UpdateHeartbeatAsync(id, newHeartbeatUtc, newHeartbeatUnbiased);

        var retrieved = await _database.GetOpenIntervalAsync();
        Assert.NotNull(retrieved);
        Assert.Equal(id, retrieved.Id);
        Assert.Equal(newHeartbeatUtc, retrieved.LastHeartbeatUtc);
        Assert.Equal(newHeartbeatUnbiased, retrieved.LastHeartbeatUnbiasedTime);
    }

    [Fact]
    public async Task UpdateHeartbeat_OnClosedInterval_ThrowsInvalidOperationException()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);
        await _database.CloseIntervalAsync(id, sample.StartTimeUtc.AddMinutes(1), sample.StartUnbiasedTime + 600_000_000, 60.0, IntervalStatus.Completed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.UpdateHeartbeatAsync(id, DateTimeOffset.UtcNow, 100_000_000));

        Assert.Contains("not in an Open state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseInterval_FinalizesRecordAndMarksCompleted()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        var endUtc = sample.StartTimeUtc.AddMinutes(5);
        ulong endUnbiased = sample.StartUnbiasedTime + 3_000_000_000UL;
        double durationSeconds = 300.0;

        await _database.CloseIntervalAsync(id, endUtc, endUnbiased, durationSeconds, IntervalStatus.Completed);

        var open = await _database.GetOpenIntervalAsync();
        Assert.Null(open);

        var intervals = await _database.GetIntervalsAsync(sample.StartTimeUtc.AddMinutes(-1), endUtc.AddMinutes(1));
        Assert.Single(intervals);
        var closed = intervals[0];
        Assert.Equal(id, closed.Id);
        Assert.Equal(IntervalStatus.Completed, closed.Status);
        Assert.Equal(endUtc, closed.EndTimeUtc);
        Assert.Equal(endUnbiased, closed.EndUnbiasedTime);
        Assert.Equal(300.0, closed.DurationSeconds);
    }

    [Fact]
    public async Task CloseInterval_WithOpenStatus_ThrowsArgumentException_AndLeavesRecordOpen()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        var endUtc = sample.StartTimeUtc.AddMinutes(5);
        ulong endUnbiased = sample.StartUnbiasedTime + 3_000_000_000UL;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _database.CloseIntervalAsync(id, endUtc, endUnbiased, 300.0, IntervalStatus.Open));

        Assert.Contains("Cannot finalize an interval with 'Open' status", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify the record is completely unchanged and remains open
        var openRecord = await _database.GetOpenIntervalAsync();
        Assert.NotNull(openRecord);
        Assert.Equal(id, openRecord.Id);
        Assert.Equal(IntervalStatus.Open, openRecord.Status);
        Assert.Null(openRecord.EndTimeUtc);
        Assert.Null(openRecord.EndUnbiasedTime);
        Assert.Null(openRecord.DurationSeconds);
    }

    [Fact]
    public async Task CloseInterval_AlreadyClosed_ThrowsInvalidOperationException()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        var endUtc = sample.StartTimeUtc.AddMinutes(5);
        await _database.CloseIntervalAsync(id, endUtc, sample.StartUnbiasedTime + 3_000_000_000UL, 300.0, IntervalStatus.Completed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.CloseIntervalAsync(id, endUtc, sample.StartUnbiasedTime + 3_000_000_000UL, 300.0, IntervalStatus.Completed));

        Assert.Contains("not in an Open state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverOpenIntervals_CalculatesDurationFromUnbiasedDifference()
    {
        var now = DateTimeOffset.UtcNow;
        var start1 = now.AddMinutes(-20);
        var hb1 = start1.AddMinutes(5); // 300 seconds

        var interval1 = new AppActivityInterval
        {
            BootId = "BOOT-PREV-001",
            ProcessName = "chrome.exe",
            ExecutablePath = @"C:\Program Files\Google\Chrome\chrome.exe",
            State = ActivityState.Active,
            StartTimeUtc = start1,
            LastHeartbeatUtc = hb1,
            StartUnbiasedTime = 100_000_000UL,
            LastHeartbeatUnbiasedTime = 100_000_000UL + 3_000_000_000UL, // Exactly 300s in 100ns units
            Status = IntervalStatus.Open
        };

        long id1 = await _database!.InsertIntervalAsync(interval1);

        int recoveredCount = await _database.RecoverOpenIntervalsAsync("BOOT-CURRENT-002");
        Assert.Equal(1, recoveredCount);

        var all = await _database.GetIntervalsAsync(now.AddHours(-1), now);
        var recovered1 = all.First(x => x.Id == id1);

        Assert.Equal(IntervalStatus.TruncatedByCrash, recovered1.Status);
        Assert.Equal(hb1, recovered1.EndTimeUtc);
        Assert.Equal(100_000_000UL + 3_000_000_000UL, recovered1.EndUnbiasedTime);
        Assert.Equal(300.0, recovered1.DurationSeconds);
    }

    [Fact]
    public async Task RecoverOpenIntervals_WhenUtcHasShifted_PreservesUnbiasedDuration()
    {
        var startUtc = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        // Simulate backward UTC clock jump (e.g. NTP time sync shifted wall clock back 1 hour)
        var shiftedLastHbUtc = new DateTimeOffset(2026, 11, 6, 11, 5, 0, TimeSpan.Zero);

        // However, monotonic interrupt time advanced forward by exactly 300 seconds (3,000,000,000 in 100ns units)
        ulong startUnbiased = 1_000_000_000UL;
        ulong lastHbUnbiased = 1_000_000_000UL + 3_000_000_000UL;

        var interval = new AppActivityInterval
        {
            BootId = "BOOT-SHIFT-TEST",
            ProcessName = "discord.exe",
            ExecutablePath = @"C:\Users\User\AppData\Local\Discord\Discord.exe",
            State = ActivityState.Active,
            StartTimeUtc = startUtc,
            LastHeartbeatUtc = shiftedLastHbUtc,
            StartUnbiasedTime = startUnbiased,
            LastHeartbeatUnbiasedTime = lastHbUnbiased,
            Status = IntervalStatus.Open
        };

        long id = await _database!.InsertIntervalAsync(interval);

        int recovered = await _database.RecoverOpenIntervalsAsync("BOOT-SHIFT-TEST");
        Assert.Equal(1, recovered);

        var retrieved = (await _database.GetIntervalsAsync(startUtc.AddHours(-2), startUtc.AddHours(2))).First(x => x.Id == id);

        Assert.Equal(IntervalStatus.TruncatedByCrash, retrieved.Status);
        // Duration reflects the monotonic unbiased progression (300.0s), NOT the shifted UTC negative difference
        Assert.Equal(300.0, retrieved.DurationSeconds);
    }

    [Fact]
    public async Task RecoverOpenIntervals_WhenMonotonicInvalid_UsesUtcFallbackAndSetsEndUnbiasedNull()
    {
        var startUtc = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var hbUtc = startUtc.AddMinutes(2); // 120 seconds difference

        // Corrupted monotonic counters: LastHeartbeatUnbiasedTime is smaller than StartUnbiasedTime
        ulong startUnbiased = 5_000_000_000UL;
        ulong corruptLastHbUnbiased = 1_000_000_000UL; // Less than start!

        var interval = new AppActivityInterval
        {
            BootId = "BOOT-CORRUPT-MONOTONIC",
            ProcessName = "notepad.exe",
            ExecutablePath = @"C:\Windows\notepad.exe",
            State = ActivityState.Active,
            StartTimeUtc = startUtc,
            LastHeartbeatUtc = hbUtc,
            StartUnbiasedTime = startUnbiased,
            LastHeartbeatUnbiasedTime = corruptLastHbUnbiased,
            Status = IntervalStatus.Open
        };

        long id = await _database!.InsertIntervalAsync(interval);

        int recoveredCount = await _database.RecoverOpenIntervalsAsync("BOOT-CURRENT-NEW");
        Assert.Equal(1, recoveredCount);

        var retrieved = (await _database.GetIntervalsAsync(startUtc.AddMinutes(-5), hbUtc.AddMinutes(5))).First(x => x.Id == id);

        Assert.Equal(IntervalStatus.TruncatedByCrash, retrieved.Status);
        Assert.Equal(120.0, retrieved.DurationSeconds);
        Assert.Null(retrieved.EndUnbiasedTime);
        Assert.Equal(hbUtc, retrieved.EndTimeUtc);
    }

    [Fact]
    public async Task DeleteOlderThan_NeverDeletesOpenIntervals()
    {
        var now = DateTimeOffset.UtcNow;

        // Old completed interval (10 days ago)
        var oldClosed = CreateSampleInterval("old.exe", startUtc: now.AddDays(-10), status: IntervalStatus.Completed, endUtc: now.AddDays(-10).AddMinutes(30));
        long oldId = await _database!.InsertIntervalAsync(oldClosed);

        // Old OPEN interval (started 10 days ago, never closed)
        var oldOpen = CreateSampleInterval("open_old.exe", startUtc: now.AddDays(-10), status: IntervalStatus.Open);
        long openId = await _database.InsertIntervalAsync(oldOpen);

        // Purge records older than 7 days
        int deleted = await _database.DeleteOlderThanAsync(now.AddDays(-7));
        Assert.Equal(1, deleted);

        // Verify old open interval was NOT deleted
        var openRecord = await _database.GetOpenIntervalAsync();
        Assert.NotNull(openRecord);
        Assert.Equal(openId, openRecord.Id);
    }

    [Fact]
    public async Task CheckpointAsync_ReturnsValidWalCheckpointResult()
    {
        await _database!.InsertIntervalAsync(CreateSampleInterval());

        var result = await _database.CheckpointAsync(WalCheckpointMode.Passive);

        Assert.True(result.Succeeded);
        Assert.False(result.IsNonWal);
        Assert.Equal(0, result.Busy);
        Assert.True(result.LogPages >= 0);
    }

    [Fact]
    public async Task CheckpointAsync_OnInMemoryDatabase_ReturnsNonWalResult()
    {
        var inMemoryDb = new ActivityDatabase("Data Source=:memory:;");
        await inMemoryDb.InitializeAsync();

        try
        {
            var result = await inMemoryDb.CheckpointAsync(WalCheckpointMode.Passive);
            Assert.True(result.IsNonWal);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.LogPages);
            Assert.Equal(-1, result.CheckpointedPages);
        }
        finally
        {
            await inMemoryDb.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentAccess_SerializedCorrectlyThroughSingleWriter()
    {
        // Enqueue 20 concurrent inserts of completed records across multiple tasks
        var tasks = Enumerable.Range(0, 20)
            .Select(i =>
            {
                var now = DateTimeOffset.UtcNow.AddMinutes(i);
                var sample = CreateSampleInterval($"app_{i}.exe", startUtc: now, status: IntervalStatus.Completed, endUtc: now.AddSeconds(10));
                return _database!.InsertIntervalAsync(sample);
            })
            .ToArray();

        long[] ids = await Task.WhenAll(tasks);

        Assert.Equal(20, ids.Distinct().Count());
        var all = await _database!.GetIntervalsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(20, all.Count);
    }

    [Fact]
    public async Task ConcurrentShutdown_AwaitsSameShutdownOperation()
    {
        var sample = CreateSampleInterval();
        await _database!.InsertIntervalAsync(sample);

        // Dispatch 5 concurrent ShutdownAsync calls
        var shutdownTasks = Enumerable.Range(0, 5)
            .Select(_ => _database.ShutdownAsync())
            .ToArray();

        await Task.WhenAll(shutdownTasks);

        // Database is cleanly disposed
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _database.InsertIntervalAsync(CreateSampleInterval()));
    }

    [Fact]
    public async Task ShutdownAsync_WhenWorkerBlocked_ThrowsTimeoutException_AndCompletesWhenReleased()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gritwall_timeout_{Guid.NewGuid():N}.db");
        var db = new ActivityDatabase($"Data Source={dbPath};");
        await db.InitializeAsync();

        var barrierTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        db.WorkerShutdownBarrier = () => barrierTcs.Task;

        try
        {
            // Call ShutdownAsync with a short timeout (50ms) and an uncancelled caller token
            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                db.ShutdownAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);

            // While the worker is held at the barrier, the worker is still alive and connection has not been disposed
            Assert.False(db.IsWorkerCompleted);

            // Release the worker barrier
            barrierTcs.SetResult(true);

            // Await the shared shutdown operation to verify eventual clean completion
            await db.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(db.IsWorkerCompleted);
        }
        finally
        {
            barrierTcs.TrySetResult(true);
            try { await db.DisposeAsync(); } catch { }
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task ShutdownAsync_WhenWorkerFaulted_PropagatesTerminalFaultException()
    {
        string invalidPath = @"Z:\NonExistentDirectory_ShutdownFault\invalid.db";
        var db = new ActivityDatabase($"Data Source={invalidPath};");

        await Assert.ThrowsAnyAsync<Exception>(() => db.InitializeAsync());

        // ShutdownAsync must propagate the terminal fault exception rather than reporting clean success
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.ShutdownAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.IsType<SqliteException>(ex);
        await Assert.ThrowsAnyAsync<Exception>(async () => await db.DisposeAsync());
    }

    [Fact]
    public async Task DisposeAsync_WhenWorkerFaulted_PropagatesTerminalFaultException()
    {
        string invalidPath = @"Z:\NonExistentDirectory_DisposeFault\invalid.db";
        var db = new ActivityDatabase($"Data Source={invalidPath};");

        await Assert.ThrowsAnyAsync<Exception>(() => db.InitializeAsync());

        // DisposeAsync must not silently swallow terminal worker faults
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await db.DisposeAsync());
        Assert.IsType<SqliteException>(ex);
    }

    [Fact]
    public async Task InitializationFailure_TransitionsToTerminalFaultState()
    {
        // Point to an invalid directory path that cannot be opened/created
        string invalidPath = @"Z:\NonExistentDirectory_123456\invalid.db";
        var db = new ActivityDatabase($"Data Source={invalidPath};");

        // InitializeAsync should throw due to connection failure
        await Assert.ThrowsAnyAsync<Exception>(() => db.InitializeAsync());

        // Subsequent calls must fail immediately rather than hang
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.InsertIntervalAsync(CreateSampleInterval()));

        Assert.Contains("faulted state", ex.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAnyAsync<Exception>(async () => await db.DisposeAsync());
    }

    [Fact]
    public async Task InitializeAsync_WhenPersistentDatabaseCannotActivateWal_ThrowsInvalidOperationException()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gritwall_readonly_{Guid.NewGuid():N}.db");
        await using (var setupConn = new SqliteConnection($"Data Source={dbPath};"))
        {
            await setupConn.OpenAsync();
            using var cmd = setupConn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = DELETE; CREATE TABLE Dummy (Id INT);";
            await cmd.ExecuteNonQueryAsync();
        }

        // Open in ReadOnly mode: PRAGMA journal_mode = WAL will fail to activate WAL
        var readOnlyDb = new ActivityDatabase($"Data Source={dbPath};Mode=ReadOnly;");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => readOnlyDb.InitializeAsync());
            Assert.Contains("Failed to activate WAL journal mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { await readOnlyDb.DisposeAsync(); } catch { }
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:;", true)]
    [InlineData("Data Source=test.db;Mode=Memory;", true)]
    [InlineData("Mode=Memory;", true)]
    [InlineData("Data Source=file:memdb1?mode=memory&cache=shared;", true)]
    [InlineData("Data Source=gritwall_test.db;", false)]
    [InlineData("Data Source=C:\\Data\\gritwall.db;Mode=ReadWriteCreate;", false)]
    public void DetermineIsInMemory_AccuratelyIdentifiesInMemoryConfigurations(string connectionString, bool expectedInMemory)
    {
        bool actual = ActivityDatabase.DetermineIsInMemory(connectionString);
        Assert.Equal(expectedInMemory, actual);
    }

    [Fact]
    public async Task RecoverOpenIntervals_SafeAgainstConcurrentUpdates_DoesNotOverwriteFinalizedInterval()
    {
        var sample = CreateSampleInterval();
        long id = await _database!.InsertIntervalAsync(sample);

        // Simulate another connection/worker concurrently finalizing the interval before or during recovery
        await using (var externalConn = new SqliteConnection(_connectionString))
        {
            await externalConn.OpenAsync();
            using var updateCmd = externalConn.CreateCommand();
            var finalizedEndUtc = sample.StartTimeUtc.AddMinutes(10);
            updateCmd.CommandText = $"""
                UPDATE AppActivityIntervals
                SET Status = 'Completed',
                    EndTimeUtc = '{finalizedEndUtc:o}',
                    DurationSeconds = 600.0
                WHERE Id = {id};
                """;
            int rowsUpdated = await updateCmd.ExecuteNonQueryAsync();
            Assert.Equal(1, rowsUpdated);
        }

        // Recovery running now must not overwrite the already-completed interval and must return 0 recovered rows
        int recoveredCount = await _database.RecoverOpenIntervalsAsync("BOOT-NEW");
        Assert.Equal(0, recoveredCount);

        // Verify the record remains in Completed status with its finalized duration
        var intervals = await _database.GetIntervalsAsync(sample.StartTimeUtc.AddMinutes(-5), sample.StartTimeUtc.AddMinutes(20));
        var record = Assert.Single(intervals);
        Assert.Equal(IntervalStatus.Completed, record.Status);
        Assert.Equal(600.0, record.DurationSeconds);
    }

    [Fact]
    public async Task GetIntervals_WithDifferentTimeZonesAndOffsets_OrdersAndFiltersCorrectlyChronologically()
    {
        // 4 intervals with various positive, negative, and zero UTC offsets:
        // Int 1: 08:00:00 +05:30 -> UTC 02:30:00
        // Int 2: 04:00:00 +01:00 -> UTC 03:00:00
        // Int 3: 04:00:00 +00:00 -> UTC 04:00:00
        // Int 4: 00:00:00 -05:00 -> UTC 05:00:00

        var baseDate = new DateOnly(2026, 11, 6);

        var t1Start = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 8, 0, 0, TimeSpan.FromHours(5.5));
        var t1End = t1Start.AddMinutes(10); // UTC 02:40:00

        var t2Start = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 4, 0, 0, TimeSpan.FromHours(1));
        var t2End = t2Start.AddMinutes(10); // UTC 03:10:00

        var t3Start = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 4, 0, 0, TimeSpan.Zero);
        var t3End = t3Start.AddMinutes(10); // UTC 04:10:00

        var t4Start = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 0, 0, 0, TimeSpan.FromHours(-5));
        var t4End = t4Start.AddMinutes(10); // UTC 05:10:00

        var int1 = CreateSampleInterval("app1.exe", startUtc: t1Start, status: IntervalStatus.Completed, endUtc: t1End);
        var int2 = CreateSampleInterval("app2.exe", startUtc: t2Start, status: IntervalStatus.Completed, endUtc: t2End);
        var int3 = CreateSampleInterval("app3.exe", startUtc: t3Start, status: IntervalStatus.Completed, endUtc: t3End);
        var int4 = CreateSampleInterval("app4.exe", startUtc: t4Start, status: IntervalStatus.Completed, endUtc: t4End);

        await _database!.InsertIntervalAsync(int1);
        await _database.InsertIntervalAsync(int2);
        await _database.InsertIntervalAsync(int3);
        await _database.InsertIntervalAsync(int4);

        // Query with an arbitrary timezone offset (-08:00 Pacific) covering UTC 02:45 to 04:30
        // Should exclude Int 1 (ends at 02:40 UTC) and Int 4 (starts at 05:00 UTC)
        // Should include Int 2 (03:00 - 03:10 UTC) and Int 3 (04:00 - 04:10 UTC)
        var queryStart = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day - 1, 18, 45, 0, TimeSpan.FromHours(-8)); // UTC 02:45:00
        var queryEnd = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day - 1, 20, 30, 0, TimeSpan.FromHours(-8));   // UTC 04:30:00

        var results = await _database.GetIntervalsAsync(queryStart, queryEnd);

        Assert.Equal(2, results.Count);
        Assert.Equal("app2.exe", results[0].ProcessName);
        Assert.Equal("app3.exe", results[1].ProcessName);

        // Verify ordering: results[0] (03:00 UTC) is strictly before results[1] (04:00 UTC)
        Assert.True(results[0].StartTimeUtc < results[1].StartTimeUtc);
    }
}
