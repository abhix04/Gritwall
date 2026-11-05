namespace Umbra.Core.Tracking;

public enum WalCheckpointMode
{
    Passive,
    Full,
    Restart,
    Truncate
}

public readonly record struct WalCheckpointResult(
    WalCheckpointMode Mode,
    int Busy,
    int LogPages,
    int CheckpointedPages
)
{
    /// <summary>
    /// Indicates whether the database is operating in non-WAL mode (e.g. in-memory database).
    /// SQLite returns LogPages = -1 and CheckpointedPages = -1 when wal_checkpoint cannot run.
    /// </summary>
    public bool IsNonWal => LogPages == -1 && CheckpointedPages == -1;

    /// <summary>
    /// Evaluates checkpoint completion based on the checkpoint mode and WAL state.
    /// Non-WAL results are not considered successful WAL checkpoints.
    /// PASSIVE is considered successful if executed without errors (Busy indicates whether active readers/writers were encountered).
    /// FULL, RESTART, and TRUNCATE require Busy == 0 to have completed fully.
    /// </summary>
    public bool Succeeded => !IsNonWal && LogPages >= 0 && CheckpointedPages >= 0 && Mode switch
    {
        WalCheckpointMode.Passive => true,
        WalCheckpointMode.Full => Busy == 0,
        WalCheckpointMode.Restart => Busy == 0,
        WalCheckpointMode.Truncate => Busy == 0,
        _ => Busy == 0
    };
}

internal abstract record DatabaseCommand;

internal sealed record InitCommand(TaskCompletionSource<bool> Tcs) : DatabaseCommand;

internal sealed record InsertIntervalCommand(
    AppActivityInterval Interval,
    TaskCompletionSource<long> Tcs
) : DatabaseCommand;

internal sealed record UpdateHeartbeatCommand(
    long IntervalId,
    DateTimeOffset HeartbeatUtc,
    ulong HeartbeatUnbiased,
    TaskCompletionSource<bool> Tcs
) : DatabaseCommand;

internal sealed record CloseIntervalCommand(
    long IntervalId,
    DateTimeOffset EndUtc,
    ulong EndUnbiased,
    double DurationSeconds,
    IntervalStatus Status,
    TaskCompletionSource<bool> Tcs
) : DatabaseCommand;

internal sealed record RecoverOpenIntervalsCommand(
    string CurrentBootId,
    TaskCompletionSource<int> Tcs
) : DatabaseCommand;

internal sealed record GetIntervalsCommand(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TaskCompletionSource<IReadOnlyList<AppActivityInterval>> Tcs
) : DatabaseCommand;

internal sealed record GetOpenIntervalCommand(
    TaskCompletionSource<AppActivityInterval?> Tcs
) : DatabaseCommand;

internal sealed record DeleteOlderThanCommand(
    DateTimeOffset CutoffUtc,
    TaskCompletionSource<int> Tcs
) : DatabaseCommand;

internal sealed record CheckpointCommand(
    WalCheckpointMode Mode,
    TaskCompletionSource<WalCheckpointResult> Tcs
) : DatabaseCommand;
