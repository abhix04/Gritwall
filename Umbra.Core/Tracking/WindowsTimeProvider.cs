namespace Umbra.Core.Tracking;

/// <summary>
/// Production implementation of ITimeProvider backed by Win32 system APIs.
/// </summary>
public sealed class WindowsTimeProvider : ITimeProvider
{
    private readonly INativeClockSource _clockSource;

    public WindowsTimeProvider(INativeClockSource? clockSource = null)
    {
        _clockSource = clockSource ?? DefaultNativeClockSource.Instance;
    }

    public DateTimeOffset UtcNow => _clockSource.GetUtcNow();

    public uint GetTickCount32() => _clockSource.GetTickCount();

    public ulong GetTickCount64() => _clockSource.GetTickCount64();

    public ulong GetUnbiasedInterruptTime()
    {
        if (!_clockSource.QueryUnbiasedInterruptTime(out ulong unbiased))
        {
            throw new InvalidOperationException("Failed to query unbiased interrupt time from native API.");
        }
        return unbiased;
    }

    public bool TryCaptureReading(out ClockReading reading)
    {
        return ClockSampler.TrySample(out reading, clockSource: _clockSource);
    }

    public ClockReading CaptureReading()
    {
        if (!TryCaptureReading(out var reading))
        {
            throw new InvalidOperationException("Failed to capture a consistent system clock reading within the acceptable sampling span.");
        }
        return reading;
    }
}
