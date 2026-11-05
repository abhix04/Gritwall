using Umbra.Core.Tracking;
using Xunit;

namespace Umbra.Tests.Tracking;

public class IdleTimeCalculatorTests
{
    [Fact]
    public void CalculateStartup_WhenUptimeLessThan32Bit_AndInputInPast_ReturnsValidElapsed()
    {
        ulong uptime64 = 60_000; // 60 seconds uptime
        uint rawInput32 = 45_000; // 15 seconds ago

        var result = IdleTimeCalculator.CalculateStartup(uptime64, rawInput32);

        Assert.True(result.IsValid);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(15_000u, result.ElapsedMilliseconds);
        Assert.Equal(45_000ul, result.ReconstructedInputUptime64);
    }

    [Fact]
    public void CalculateStartup_WhenUptimeLessThan32Bit_AndSmallFutureSkew_ClampsToZero()
    {
        ulong uptime64 = 60_000;
        uint rawInput32 = 60_500; // 500ms in the future

        var result = IdleTimeCalculator.CalculateStartup(uptime64, rawInput32);

        Assert.True(result.IsValid);
        Assert.Equal(0u, result.ElapsedMilliseconds);
        Assert.Equal(60_000ul, result.ReconstructedInputUptime64);
    }

    [Fact]
    public void CalculateStartup_WhenUptimeLessThan32Bit_AndLargeFutureSkew_ReturnsAmbiguous()
    {
        ulong uptime64 = 60_000;
        uint rawInput32 = 90_000; // 30 seconds in the future (exceeds 2000ms skew tolerance)

        var result = IdleTimeCalculator.CalculateStartup(uptime64, rawInput32);

        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void CalculateStartup_WhenUptimeExceeds32Bit_ReturnsAmbiguous_ToPrevent49DayAlias()
    {
        // System uptime is ~50 days (> 2^32 ms)
        ulong uptime64 = 0x100000000UL + 10_000; // 10s into epoch 1
        uint rawInput32 = 10_000; // Low 32 bits match exactly!

        // Even though modular difference is 0, startup cannot know if input was 0ms ago or 49.71 days ago
        var result = IdleTimeCalculator.CalculateStartup(uptime64, rawInput32);

        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void CalculateRuntime_NormalPastEvent_ReturnsAccurateElapsed()
    {
        ulong uptime64 = 100_000;
        uint rawInput32 = 92_000; // 8 seconds ago

        var result = IdleTimeCalculator.CalculateRuntime(uptime64, rawInput32);

        Assert.True(result.IsValid);
        Assert.Equal(8_000u, result.ElapsedMilliseconds);
        Assert.Equal(92_000ul, result.ReconstructedInputUptime64);
    }

    [Fact]
    public void CalculateRuntime_32BitRollover_CalculatesCorrectDelta()
    {
        // Current uptime has rolled past 2^32
        // Uptime is 256ms past 2^32
        ulong uptime64 = 0x100000000UL + 256;
        uint currentTick32 = (uint)uptime64; // 256 (0x00000100)

        // Input occurred 256ms before the 2^32 boundary
        uint rawInput32 = 0xFFFFFF00; // 512ms ago

        var result = IdleTimeCalculator.CalculateRuntime(uptime64, rawInput32);

        Assert.True(result.IsValid);
        Assert.Equal(512u, result.ElapsedMilliseconds);
        Assert.Equal(uptime64 - 512, result.ReconstructedInputUptime64);
    }

    [Fact]
    public void CalculateRuntime_SmallFutureSkew_ClampsToZero()
    {
        ulong uptime64 = 50_000;
        uint rawInput32 = 51_500; // 1500ms in future (within 2000ms tolerance)

        var result = IdleTimeCalculator.CalculateRuntime(uptime64, rawInput32);

        Assert.True(result.IsValid);
        Assert.Equal(0u, result.ElapsedMilliseconds);
        Assert.Equal(50_000ul, result.ReconstructedInputUptime64);
    }

    [Fact]
    public void CalculateRuntime_DeltaExceedsHalfRange_ReturnsAmbiguous()
    {
        ulong uptime64 = 100_000;
        // Raw input is beyond 2^31 ms in the past modularly
        uint rawInput32 = unchecked((uint)(uptime64 - (IdleTimeCalculator.HalfRangeMs + 100)));

        var result = IdleTimeCalculator.CalculateRuntime(uptime64, rawInput32);

        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsValid);
    }
}
