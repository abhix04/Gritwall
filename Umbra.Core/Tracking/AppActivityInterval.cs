namespace Umbra.Core.Tracking;

/// <summary>
/// Immutable record representing a contiguous duration of time spent in an application or system state.
/// </summary>
public record AppActivityInterval
{
    public long Id { get; init; }
    public string BootId { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public ActivityState State { get; init; } = ActivityState.Active;
    public DateTimeOffset StartTimeUtc { get; init; }
    public DateTimeOffset? EndTimeUtc { get; init; }
    public DateTimeOffset LastHeartbeatUtc { get; init; }
    public ulong StartUnbiasedTime { get; init; }
    public ulong? EndUnbiasedTime { get; init; }
    public ulong LastHeartbeatUnbiasedTime { get; init; }
    public double? DurationSeconds { get; init; }
    public IntervalStatus Status { get; init; } = IntervalStatus.Open;
}
