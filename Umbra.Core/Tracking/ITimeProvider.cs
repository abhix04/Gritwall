namespace Umbra.Core.Tracking;

/// <summary>
/// Abstraction for operating system time counters to enable deterministic unit testing.
/// </summary>
public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
    uint GetTickCount32();
    ulong GetTickCount64();
    ulong GetUnbiasedInterruptTime();

    /// <summary>
    /// Attempts to capture a verified, sequential clock reading within the maximum preemption span.
    /// </summary>
    bool TryCaptureReading(out ClockReading reading);

    /// <summary>
    /// Captures a verified clock reading, throwing <see cref="InvalidOperationException"/> if sampling fails.
    /// </summary>
    ClockReading CaptureReading();
}
