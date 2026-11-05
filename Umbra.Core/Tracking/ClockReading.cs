namespace Umbra.Core.Tracking;

/// <summary>
/// Sequential reading of operating system time counters sampled on a single thread.
/// </summary>
/// <param name="TickCount32">Unsigned 32-bit millisecond tick count (approx. 10-16ms resolution, includes sleep).</param>
/// <param name="TickCount64">Monotonic 64-bit uptime in milliseconds (includes sleep).</param>
/// <param name="UtcNow">UTC wall-clock time.</param>
/// <param name="UnbiasedInterruptTime100Ns">S0 working-state interrupt time in 100ns units (pauses during sleep/hibernation).</param>
/// <param name="SamplingSpanMs">Elapsed tick milliseconds between start and end of reading to detect thread preemption.</param>
public readonly record struct ClockReading(
    uint TickCount32,
    ulong TickCount64,
    DateTimeOffset UtcNow,
    ulong UnbiasedInterruptTime100Ns,
    uint SamplingSpanMs
)
{
    /// <summary>
    /// Indicates whether this reading represents a verified capture within acceptable preemption boundaries.
    /// </summary>
    public bool IsValid => SamplingSpanMs <= ClockSampler.MaxAcceptableSpanMs && UtcNow != default;
}
