using Umbra.Core.Tracking;
using Xunit;

namespace Umbra.Tests.Tracking;

public class IdleDetectorTests
{
    private class MockInputTimeProvider : IInputTimeProvider
    {
        public bool Succeeds { get; set; } = true;
        public uint LastInputTick { get; set; } = 10_000;

        public bool TryGetLastInputTick(out uint lastInputTick)
        {
            if (Succeeds)
            {
                lastInputTick = LastInputTick;
                return true;
            }

            lastInputTick = 0;
            return false;
        }
    }

    private static ClockReading CreateReading(ulong tick64, uint span = 1)
    {
        return new ClockReading(
            TickCount32: unchecked((uint)tick64),
            TickCount64: tick64,
            UtcNow: DateTimeOffset.UtcNow,
            UnbiasedInterruptTime100Ns: tick64 * 10_000,
            SamplingSpanMs: span
        );
    }

    [Fact]
    public void TryCheckIdle_StartupWithRecentInput_InitializesActive()
    {
        var input = new MockInputTimeProvider { LastInputTick = 9_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        var reading = CreateReading(10_000); // 1s after input
        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        Assert.False(result.IsIdle);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_StartupWithOldInput_InitializesIdle()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        var reading = CreateReading(320_000); // 310s after input (> 300s threshold)
        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        Assert.True(result.IsIdle);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(TimeSpan.FromMilliseconds(310_000), result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_StartupWithLongUptimeExceeding32Bit_InitializesAmbiguousAndIdle()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // System uptime is ~50 days (> 2^32 ms)
        ulong uptime64 = 0x100000000UL + 10_000;
        var reading = CreateReading(uptime64);

        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        Assert.True(result.IsIdle);
        Assert.True(result.IsAmbiguous);
    }

    [Fact]
    public void TryCheckIdle_InputMutation_ResetsIdleDuration()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // First poll at 10_000
        detector.TryCheckIdle(CreateReading(10_000), out _);

        // Advance time to 150_000 with unchanged input
        detector.TryCheckIdle(CreateReading(150_000), out var result1);
        Assert.Equal(TimeSpan.FromMilliseconds(140_000), result1.IdleDuration);

        // User moves mouse: input mutates to 150_000
        input.LastInputTick = 150_000;
        detector.TryCheckIdle(CreateReading(150_000), out var result2);

        Assert.False(result2.IsIdle);
        Assert.Equal(TimeSpan.Zero, result2.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_PostResumeStaleInput_DoesNotTriggerInstantIdle()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // Initial setup before sleep
        detector.TryCheckIdle(CreateReading(10_000), out _);

        // Machine sleeps for 2 hours (7,200,000 ms) and resumes at 7,210_000
        // dwTime still reports pre-sleep 10_000!
        var resumeReading = CreateReading(7_210_000);
        detector.ResetEpoch(resumeReading);

        // First poll right after resume (100ms later)
        var postResumeReading = CreateReading(7_210_100);
        bool success = detector.TryCheckIdle(postResumeReading, out var result);

        Assert.True(success);
        Assert.False(result.IsIdle);
        // Inactivity is measured strictly from the resume epoch (100ms), NOT from 10_000 (2 hours)!
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_PostResumeNewInput_ClearsEpochWait()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        detector.TryCheckIdle(CreateReading(10_000), out _);

        // Resume at 500_000
        detector.ResetEpoch(CreateReading(500_000));

        // User types at 502_000
        input.LastInputTick = 502_000;
        var reading = CreateReading(502_500);

        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        Assert.False(result.IsIdle);
        Assert.Equal(TimeSpan.Zero, result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_ProviderFailure_ReturnsFalseWithoutThrowing()
    {
        var input = new MockInputTimeProvider { Succeeds = false };
        var detector = new IdleDetector(input);

        bool success = detector.TryCheckIdle(CreateReading(10_000), out var result);

        Assert.False(success);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryCheckIdle_StartupAmbiguous_PersistsAmbiguityAcrossRepeatedPolls()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // System uptime is ~50 days (> 2^32 ms)
        ulong uptimeBase = 0x100000000UL + 10_000;

        // Poll 1 at startup
        bool s1 = detector.TryCheckIdle(CreateReading(uptimeBase), out var r1);
        Assert.True(s1);
        Assert.True(r1.IsAmbiguous);
        Assert.True(r1.IsIdle);
        Assert.Null(r1.EstimatedLastInputUtc);
        Assert.Null(r1.EstimatedLastInputUnbiased100Ns);
        Assert.Equal(detector.IdleThreshold, r1.IdleDuration);

        // Poll 2 at +5s without input
        bool s2 = detector.TryCheckIdle(CreateReading(uptimeBase + 5_000), out var r2);
        Assert.True(s2);
        Assert.True(r2.IsAmbiguous);
        Assert.True(r2.IsIdle);
        Assert.Null(r2.EstimatedLastInputUtc);
        Assert.Null(r2.EstimatedLastInputUnbiased100Ns);
        // Crucial: IdleDuration is NOT fabricated from uptime (e.g. 50 days)
        Assert.Equal(detector.IdleThreshold, r2.IdleDuration);

        // Poll 3 at +10s without input
        bool s3 = detector.TryCheckIdle(CreateReading(uptimeBase + 10_000), out var r3);
        Assert.True(s3);
        Assert.True(r3.IsAmbiguous);
        Assert.True(r3.IsIdle);

        // User actively types at +12s
        input.LastInputTick = unchecked((uint)(uptimeBase + 12_000));
        bool s4 = detector.TryCheckIdle(CreateReading(uptimeBase + 12_200), out var r4);
        Assert.True(s4);
        Assert.False(r4.IsAmbiguous);
        Assert.False(r4.IsIdle);
        Assert.NotNull(r4.EstimatedLastInputUtc);
        Assert.NotNull(r4.EstimatedLastInputUnbiased100Ns);
        Assert.Equal(TimeSpan.Zero, r4.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_RegressingTimestamp_TreatedAsActiveUsingObservationBaseline()
    {
        var input = new MockInputTimeProvider { LastInputTick = 99_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // Initial sample at 100_000
        detector.TryCheckIdle(CreateReading(100_000), out var initial);
        Assert.False(initial.IsIdle);

        // Synthetic input (SendInput) produces a regressed dwTime of 50_000 at observation time 101_000
        input.LastInputTick = 50_000;
        bool success = detector.TryCheckIdle(CreateReading(101_000), out var result);

        Assert.True(success);
        // The mutation proves new input: must NOT be mistaken for 51 seconds of inactivity
        Assert.False(result.IsIdle);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(TimeSpan.Zero, result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_RepeatedRegressingTimestamps_MaintainsActiveState()
    {
        var input = new MockInputTimeProvider { LastInputTick = 90_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        detector.TryCheckIdle(CreateReading(100_000), out _);

        // Regression 1 at 101_000
        input.LastInputTick = 80_000;
        detector.TryCheckIdle(CreateReading(101_000), out var r1);
        Assert.False(r1.IsIdle);
        Assert.Equal(TimeSpan.Zero, r1.IdleDuration);

        // Regression 2 at 102_000
        input.LastInputTick = 70_000;
        detector.TryCheckIdle(CreateReading(102_000), out var r2);
        Assert.False(r2.IsIdle);
        Assert.Equal(TimeSpan.Zero, r2.IdleDuration);

        // Input stops; advance past 300s threshold relative to observation baseline 102_000 (e.g. 403_000 = +301s)
        detector.TryCheckIdle(CreateReading(403_000), out var r3);
        Assert.True(r3.IsIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(301_000), r3.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_UnavailableClockReading_ReturnsFalseWithoutMutatingState()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input);

        // Reading with preemption span exceeding threshold (60ms > 50ms)
        var invalidReading = new ClockReading(10_000, 10_000, DateTimeOffset.UtcNow, 100_000, SamplingSpanMs: 60);
        bool success = detector.TryCheckIdle(invalidReading, out var result);

        Assert.False(success);
        Assert.Equal(default, result);

        // Default uninitialized reading
        bool defaultSuccess = detector.TryCheckIdle(default, out var defaultResult);
        Assert.False(defaultSuccess);
        Assert.Equal(default, defaultResult);
    }

    [Fact]
    public void ResetEpoch_InvalidClockReading_ThrowsArgumentException()
    {
        var input = new MockInputTimeProvider();
        var detector = new IdleDetector(input);

        Assert.Throws<ArgumentException>(() => detector.ResetEpoch(default));
    }

    [Fact]
    public void TryCheckIdle_SyntheticInputForwardTimestamp_DoesNotClassifyAsIdle()
    {
        // Previous raw timestamp: 10,000
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        detector.TryCheckIdle(CreateReading(10_000), out _);

        // Current uptime: 700,000; newly observed raw timestamp: 400,000
        // (e.g. synthetic SendInput injected with custom timestamp 400,000)
        input.LastInputTick = 400_000;
        var reading = CreateReading(700_000);

        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        // The mutation itself proves new user/synthetic activity occurred;
        // must NOT be classified as 300s of inactivity!
        Assert.False(result.IsIdle);
        Assert.Equal(TimeSpan.Zero, result.IdleDuration);
    }

    [Fact]
    public void TryCheckIdle_ContinuousInactivityExceeding49Days_PreservesFull64BitDurationWithoutTruncation()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        detector.TryCheckIdle(CreateReading(10_000), out _);

        // Continuous inactivity for 50 days (4,320,000,000 ms > uint.MaxValue = 4,294,967,295 ms)
        ulong elapsedMs = 50UL * 24 * 60 * 60 * 1000;
        ulong futureUptime = 10_000 + elapsedMs;
        var reading = CreateReading(futureUptime);

        bool success = detector.TryCheckIdle(reading, out var result);

        Assert.True(success);
        Assert.True(result.IsIdle);
        // Preserves full 64-bit duration without uint.MaxValue truncation
        Assert.Equal(TimeSpan.FromMilliseconds(elapsedMs), result.IdleDuration);
        Assert.True(result.IdleDuration > TimeSpan.FromMilliseconds(uint.MaxValue));
        Assert.NotNull(result.EstimatedLastInputUtc);
        Assert.Equal(reading.UtcNow - result.IdleDuration, result.EstimatedLastInputUtc.Value);
    }

    [Fact]
    public void ResetEpoch_CalledBeforeFirstTryCheckIdle_EstablishesBaselineAndAwaitsInput()
    {
        var input = new MockInputTimeProvider { LastInputTick = 10_000 };
        var detector = new IdleDetector(input) { IdleThreshold = TimeSpan.FromSeconds(300) };

        // ResetEpoch is called before any TryCheckIdle call
        detector.ResetEpoch(CreateReading(50_000));

        // First poll at 50_100 (100ms later) with unchanged input
        bool success = detector.TryCheckIdle(CreateReading(50_100), out var result);

        Assert.True(success);
        Assert.False(result.IsIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.IdleDuration);

        // When user types, epoch wait is cleared and activity is recognized
        input.LastInputTick = 50_500;
        bool postInputSuccess = detector.TryCheckIdle(CreateReading(50_500), out var postInputResult);

        Assert.True(postInputSuccess);
        Assert.False(postInputResult.IsIdle);
        Assert.Equal(TimeSpan.Zero, postInputResult.IdleDuration);
    }
}
