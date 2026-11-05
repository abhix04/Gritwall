namespace Umbra.Core.Tracking;

/// <summary>
/// Captures sequential operating system clock readings with sampling span validation.
/// </summary>
public static class ClockSampler
{
    /// <summary>
    /// Rejection threshold: if sequential sampling span exceeds 50ms,
    /// it indicates thread preemption or system freeze during sampling.
    /// </summary>
    public const uint MaxAcceptableSpanMs = 50;

    /// <summary>
    /// Attempts to capture a consistent clock reading sequentially.
    /// Rejects samples where preemption exceeds <paramref name="maxSpanMs"/> or native clock queries fail.
    /// Never silently accepts or fabricates a fallback reading when sampling fails.
    /// </summary>
    public static bool TrySample(
        out ClockReading reading,
        int maxAttempts = 3,
        uint maxSpanMs = MaxAcceptableSpanMs,
        INativeClockSource? clockSource = null)
    {
        clockSource ??= DefaultNativeClockSource.Instance;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            uint startTick = clockSource.GetTickCount();
            ulong tick64 = clockSource.GetTickCount64();
            bool querySuccess = clockSource.QueryUnbiasedInterruptTime(out ulong unbiased);
            DateTimeOffset utc = clockSource.GetUtcNow();
            uint endTick = clockSource.GetTickCount();

            if (!querySuccess)
            {
                // Native API failure; reject this sample attempt
                continue;
            }

            uint span = unchecked(endTick - startTick);
            if (span <= maxSpanMs)
            {
                reading = new ClockReading(startTick, tick64, utc, unbiased, span);
                return true;
            }
        }

        // Sampling failed across all attempts; never return a fabricated fallback reading
        reading = default;
        return false;
    }
}
