using Umbra.Core.Tracking;
using Xunit;

namespace Umbra.Tests.Tracking;

public class ClockSamplerTests
{
    [Fact]
    public void ClockReading_Properties_MatchConstructorParameters()
    {
        var utc = DateTimeOffset.UtcNow;
        var reading = new ClockReading(
            TickCount32: 12345u,
            TickCount64: 67890ul,
            UtcNow: utc,
            UnbiasedInterruptTime100Ns: 999999999ul,
            SamplingSpanMs: 2u
        );

        Assert.Equal(12345u, reading.TickCount32);
        Assert.Equal(67890ul, reading.TickCount64);
        Assert.Equal(utc, reading.UtcNow);
        Assert.Equal(999999999ul, reading.UnbiasedInterruptTime100Ns);
        Assert.Equal(2u, reading.SamplingSpanMs);
    }

    [Fact]
    public void ClockSampler_TrySample_ReturnsConsistentReading()
    {
        bool success = ClockSampler.TrySample(out var reading);

        Assert.True(success);
        Assert.True(reading.TickCount64 > 0);
        Assert.True(reading.UnbiasedInterruptTime100Ns > 0);
        Assert.True(reading.UtcNow > DateTimeOffset.MinValue);
        Assert.True(reading.SamplingSpanMs <= ClockSampler.MaxAcceptableSpanMs);
    }

    [Fact]
    public void WindowsTimeProvider_ReturnsValidTimeProperties()
    {
        var provider = new WindowsTimeProvider();

        Assert.True(provider.UtcNow > DateTimeOffset.MinValue);
        Assert.True(provider.GetTickCount64() > 0);
        Assert.True(provider.GetUnbiasedInterruptTime() > 0);

        var reading = provider.CaptureReading();
        Assert.True(reading.TickCount64 > 0);
    }

    [Fact]
    public void WindowsInputTimeProvider_TryGetLastInputTick_ExecutesWithoutException()
    {
        var inputProvider = new WindowsInputTimeProvider();

        // On a real Windows session, GetLastInputInfo succeeds
        bool success = inputProvider.TryGetLastInputTick(out uint tick);

        // Even in headless or automated environments, it should return a boolean without throwing
        if (success)
        {
            Assert.True(tick >= 0);
        }
    }

    private sealed class MockNativeClockSource : INativeClockSource
    {
        public uint StartTick { get; set; } = 10_000;
        public uint EndTick { get; set; } = 10_010;
        public ulong TickCount64Value { get; set; } = 10_000;
        public ulong UnbiasedValue { get; set; } = 100_000_000;
        public bool QueryUnbiasedSuccess { get; set; } = true;
        public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UtcNow;

        private bool _isEndTick;

        public uint GetTickCount()
        {
            _isEndTick = !_isEndTick;
            return _isEndTick ? StartTick : EndTick;
        }

        public ulong GetTickCount64() => TickCount64Value;

        public bool QueryUnbiasedInterruptTime(out ulong lpUnbiasedInterruptTime)
        {
            lpUnbiasedInterruptTime = UnbiasedValue;
            return QueryUnbiasedSuccess;
        }

        public DateTimeOffset GetUtcNow() => UtcNowValue;
    }

    [Fact]
    public void ClockSampler_RepeatedPreemption_RejectsReadingAndReturnsFalse()
    {
        // Each attempt has a span of 60ms (exceeding MaxAcceptableSpanMs of 50ms)
        var mock = new MockNativeClockSource
        {
            StartTick = 1_000,
            EndTick = 1_060
        };

        bool success = ClockSampler.TrySample(out var reading, maxAttempts: 3, clockSource: mock);

        Assert.False(success);
        Assert.Equal(default, reading);
        Assert.False(reading.IsValid);
    }

    [Fact]
    public void ClockSampler_NativeApiFailure_RejectsReadingAndReturnsFalse()
    {
        var mock = new MockNativeClockSource
        {
            QueryUnbiasedSuccess = false
        };

        bool success = ClockSampler.TrySample(out var reading, maxAttempts: 3, clockSource: mock);

        Assert.False(success);
        Assert.Equal(default, reading);
        Assert.False(reading.IsValid);
    }

    [Fact]
    public void WindowsTimeProvider_CaptureReading_ThrowsWhenSamplingFails()
    {
        var mock = new MockNativeClockSource
        {
            StartTick = 1_000,
            EndTick = 1_060 // 60ms preemption
        };

        var provider = new WindowsTimeProvider(mock);

        Assert.False(provider.TryCaptureReading(out _));
        Assert.Throws<InvalidOperationException>(() => provider.CaptureReading());
    }

    [Fact]
    public void WindowsTimeProvider_GetUnbiasedInterruptTime_ThrowsWhenNativeApiFails()
    {
        var mock = new MockNativeClockSource
        {
            QueryUnbiasedSuccess = false
        };

        var provider = new WindowsTimeProvider(mock);

        Assert.Throws<InvalidOperationException>(() => provider.GetUnbiasedInterruptTime());
    }
}
