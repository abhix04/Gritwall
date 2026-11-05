namespace Umbra.Core.Tracking;

public enum IdleCalculationStatus
{
    /// <summary>
    /// Unambiguous elapsed duration calculated within verified boundaries.
    /// </summary>
    Valid,

    /// <summary>
    /// Input timestamp appears slightly in the future due to thread scheduling or multi-call jitter (clamped to 0).
    /// </summary>
    FutureSkewClamped,

    /// <summary>
    /// Input timestamp is ambiguous due to cold startup on long uptime (>= 2^32 ms) or multi-rollover uncertainty.
    /// </summary>
    Ambiguous
}

/// <summary>
/// Result of evaluating a 32-bit input tick against a 64-bit reference uptime.
/// </summary>
public readonly record struct IdleCalculationResult(
    IdleCalculationStatus Status,
    uint ElapsedMilliseconds,
    ulong ReconstructedInputUptime64
)
{
    public bool IsValid => Status == IdleCalculationStatus.Valid || Status == IdleCalculationStatus.FutureSkewClamped;
    public bool IsAmbiguous => Status == IdleCalculationStatus.Ambiguous;

    public static IdleCalculationResult Valid(uint elapsedMs, ulong reconstructedUptime64) =>
        new(IdleCalculationStatus.Valid, elapsedMs, reconstructedUptime64);

    public static IdleCalculationResult FutureSkewClamped(ulong currentUptime64) =>
        new(IdleCalculationStatus.FutureSkewClamped, 0, currentUptime64);

    public static IdleCalculationResult Ambiguous() =>
        new(IdleCalculationStatus.Ambiguous, 0, 0);
}
