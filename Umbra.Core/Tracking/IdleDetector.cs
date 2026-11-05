namespace Umbra.Core.Tracking;

/// <summary>
/// Detects user inactivity using input timestamp mutation tracking, epoch baseline resets,
/// and conservative ambiguity handling.
/// </summary>
public sealed class IdleDetector : IIdleDetector
{
    private readonly IInputTimeProvider _inputProvider;
    private uint? _lastObservedRawTick;
    private ulong? _lastConfirmedActivityUptime64;
    private bool _awaitingPostResumeInput;
    private bool _isInitialized;
    private bool _isAmbiguousState;

    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(300);

    public IdleDetector(IInputTimeProvider inputProvider)
    {
        _inputProvider = inputProvider ?? throw new ArgumentNullException(nameof(inputProvider));
    }

    public void ResetEpoch(ClockReading reading)
    {
        if (!reading.IsValid)
        {
            throw new ArgumentException("Cannot reset epoch with an invalid or rejected clock reading.", nameof(reading));
        }

        _isInitialized = true;
        _awaitingPostResumeInput = true;
        _isAmbiguousState = false;
        _lastConfirmedActivityUptime64 = reading.TickCount64;
        // Retain _lastObservedRawTick (if already captured) so we can detect when dwTime actually mutates post-resume
    }

    public bool TryCheckIdle(ClockReading reading, out IdleCheckResult result)
    {
        result = default;

        // Rejected, unverified, or unavailable clock readings cannot be used to evaluate idle state
        if (!reading.IsValid)
        {
            return false;
        }

        if (!_inputProvider.TryGetLastInputTick(out uint rawInputTick))
        {
            // Input query failed (Win32 error); caller handles unverified state
            return false;
        }

        if (!_isInitialized)
        {
            // First execution at startup without prior ResetEpoch
            _isInitialized = true;
            _lastObservedRawTick = rawInputTick;

            var startupCalc = IdleTimeCalculator.CalculateStartup(reading.TickCount64, rawInputTick);
            if (startupCalc.IsAmbiguous)
            {
                // Cold-start ambiguity on long uptime (>= 49.7 days).
                // Enter persistent ambiguous state without fabricating an activity baseline or timestamps.
                _isAmbiguousState = true;
                _lastConfirmedActivityUptime64 = null;

                result = new IdleCheckResult(
                    IsIdle: true,
                    IdleDuration: IdleThreshold,
                    Reading: reading,
                    EstimatedLastInputUtc: null,
                    EstimatedLastInputUnbiased100Ns: null,
                    IsAmbiguous: true
                );
                return true;
            }

            _isAmbiguousState = false;
            _lastConfirmedActivityUptime64 = startupCalc.ReconstructedInputUptime64;
        }
        else if (!_lastObservedRawTick.HasValue)
        {
            // ResetEpoch was called before the first TryCheckIdle call.
            // dwTime at the epoch is captured as the pre-epoch reference tick.
            _lastObservedRawTick = rawInputTick;
        }
        else if (rawInputTick != _lastObservedRawTick.Value)
        {
            // dwTime has mutated! A new input event was directly observed during runtime.
            // Every newly observed timestamp mutation is treated as an activity observation at the current
            // ClockReading.TickCount64. This avoids calculating prolonged inactivity from synthetic or custom
            // SendInput timestamps that may be greater than previous observations but arbitrarily in the past.
            // The timing uncertainty is bounded by the application's polling interval.
            _isAmbiguousState = false;
            _awaitingPostResumeInput = false;
            _lastObservedRawTick = rawInputTick;
            _lastConfirmedActivityUptime64 = reading.TickCount64;
        }
        else if (_isAmbiguousState)
        {
            // Persistent ambiguity: no new input mutation has been observed since ambiguous startup.
            // Never calculate elapsed time from an uninitialized baseline (0).
            // Do not fabricate an exact last-input UTC or unbiased timestamp.
            result = new IdleCheckResult(
                IsIdle: true,
                IdleDuration: IdleThreshold,
                Reading: reading,
                EstimatedLastInputUtc: null,
                EstimatedLastInputUnbiased100Ns: null,
                IsAmbiguous: true
            );
            return true;
        }
        else if (_awaitingPostResumeInput)
        {
            // dwTime is identical to pre-resume/pre-lock tick.
            // User has not provided input since resume; inactivity is measured from the resume epoch
        }

        // Verify that an activity baseline has been established before computing elapsed time
        if (!_lastConfirmedActivityUptime64.HasValue)
        {
            // Defensive guard: if baseline is uninitialized, enter persistent ambiguous state
            _isAmbiguousState = true;
            result = new IdleCheckResult(
                IsIdle: true,
                IdleDuration: IdleThreshold,
                Reading: reading,
                EstimatedLastInputUtc: null,
                EstimatedLastInputUnbiased100Ns: null,
                IsAmbiguous: true
            );
            return true;
        }

        // Calculate elapsed idle time using the verified 64-bit activity uptime reference
        ulong elapsedMs64 = reading.TickCount64 >= _lastConfirmedActivityUptime64.Value
            ? reading.TickCount64 - _lastConfirmedActivityUptime64.Value
            : 0;

        TimeSpan idleDuration = TimeSpan.FromMilliseconds(elapsedMs64);
        bool isIdle = idleDuration >= IdleThreshold;

        // Estimated retroactive conversions based on elapsed 64-bit activity delta
        ulong elapsed100Ns = (elapsedMs64 <= ulong.MaxValue / 10_000)
            ? elapsedMs64 * 10_000
            : ulong.MaxValue;

        DateTimeOffset estimatedLastInputUtc = reading.UtcNow - idleDuration;
        ulong estimatedLastInputUnbiased = reading.UnbiasedInterruptTime100Ns >= elapsed100Ns
            ? reading.UnbiasedInterruptTime100Ns - elapsed100Ns
            : 0;

        result = new IdleCheckResult(
            IsIdle: isIdle,
            IdleDuration: idleDuration,
            Reading: reading,
            EstimatedLastInputUtc: estimatedLastInputUtc,
            EstimatedLastInputUnbiased100Ns: estimatedLastInputUnbiased,
            IsAmbiguous: false
        );
        return true;
    }
}
