namespace Umbra.Core.Tracking;

/// <summary>
/// Detailed evaluation result produced by IIdleDetector.
/// </summary>
public readonly record struct IdleCheckResult(
    bool IsIdle,
    TimeSpan IdleDuration,
    ClockReading Reading,
    DateTimeOffset? EstimatedLastInputUtc,
    ulong? EstimatedLastInputUnbiased100Ns,
    bool IsAmbiguous
);
