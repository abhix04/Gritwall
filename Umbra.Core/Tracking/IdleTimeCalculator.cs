namespace Umbra.Core.Tracking;

/// <summary>
/// Performs conservative, overflow-safe idle elapsed time calculations from 32-bit DWORD input ticks.
/// </summary>
public static class IdleTimeCalculator
{
    /// <summary>
    /// Maximum future sampling jitter allowed due to thread preemption between API calls (2 seconds).
    /// </summary>
    public const uint MaxFutureSkewMs = 2000;

    /// <summary>
    /// 32-bit modular half-range boundary (2^31 ms ≈ 24.855 days).
    /// </summary>
    public const uint HalfRangeMs = 0x80000000;

    /// <summary>
    /// Evaluates idle time at application cold startup when no prior historical observation of rollover exists.
    /// </summary>
    public static IdleCalculationResult CalculateStartup(ulong currentUptime64, uint rawInputTick32)
    {
        uint currentTick32 = unchecked((uint)currentUptime64);
        uint delta32 = unchecked(currentTick32 - rawInputTick32);
        uint futureSkew = unchecked(rawInputTick32 - currentTick32);

        // Case A: System uptime is less than 2^32 ms (~49.71 days).
        // No rollover has ever occurred since boot; the epoch is unambiguously k = 0.
        if (currentUptime64 < 0x100000000UL)
        {
            if (rawInputTick32 <= currentTick32)
            {
                return IdleCalculationResult.Valid(delta32, rawInputTick32);
            }

            if (futureSkew <= MaxFutureSkewMs)
            {
                return IdleCalculationResult.FutureSkewClamped(currentUptime64);
            }

            // Raw input tick exceeds current uptime by more than jitter tolerance: ambiguous
            return IdleCalculationResult.Ambiguous();
        }

        // Case B: System uptime is >= 2^32 ms (~49.71 days).
        // At cold startup without prior observation, a 32-bit timestamp could belong to the
        // current 49.7-day cycle or any previous cycle (49.7-day alias).
        // We explicitly return Ambiguous to prevent false zero-idle calculations.
        return IdleCalculationResult.Ambiguous();
    }

    /// <summary>
    /// Evaluates idle time during continuous application execution where input mutations
    /// and uptime are continuously observed.
    /// </summary>
    public static IdleCalculationResult CalculateRuntime(ulong currentUptime64, uint rawInputTick32)
    {
        uint currentTick32 = unchecked((uint)currentUptime64);
        uint delta32 = unchecked(currentTick32 - rawInputTick32);
        uint futureSkew = unchecked(rawInputTick32 - currentTick32);

        // Case 1: Unambiguous past event within half-range (< 24.85 days)
        if (delta32 < HalfRangeMs)
        {
            ulong reconstructed64 = currentUptime64 >= delta32 ? currentUptime64 - delta32 : 0;
            return IdleCalculationResult.Valid(delta32, reconstructed64);
        }

        // Case 2: Small future sampling skew (<= 2000ms)
        if (futureSkew <= MaxFutureSkewMs)
        {
            return IdleCalculationResult.FutureSkewClamped(currentUptime64);
        }

        // Case 3: Genuinely ambiguous older timestamp or multi-rollover gap (delta32 >= HalfRangeMs)
        return IdleCalculationResult.Ambiguous();
    }
}
