using System.Globalization;
using Umbra.Core.Tracking;
using Xunit;

namespace Umbra.Tests.Tracking;

public sealed class ActivityTimelineTests
{
    private static AppActivityInterval CreateInterval(
        string processName,
        DateTimeOffset startTimeUtc,
        DateTimeOffset? endTimeUtc = null,
        DateTimeOffset? lastHeartbeatUtc = null,
        double? durationSeconds = null)
    {
        var hb = lastHeartbeatUtc ?? endTimeUtc ?? startTimeUtc;
        var dur = durationSeconds ?? (endTimeUtc.HasValue ? (endTimeUtc.Value - startTimeUtc).TotalSeconds : (hb - startTimeUtc).TotalSeconds);

        return new AppActivityInterval
        {
            Id = 1,
            BootId = "BOOT-TEST",
            ProcessName = processName,
            ExecutablePath = $@"C:\Program Files\{processName}",
            State = ActivityState.Active,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = endTimeUtc,
            LastHeartbeatUtc = hb,
            StartUnbiasedTime = 10_000_000,
            EndUnbiasedTime = 10_000_000 + (ulong)(dur * 10_000_000),
            LastHeartbeatUnbiasedTime = 10_000_000 + (ulong)(dur * 10_000_000),
            DurationSeconds = dur,
            Status = endTimeUtc.HasValue ? IntervalStatus.Completed : IntervalStatus.Open
        };
    }

    [Fact]
    public void TimelinePositioningAndDuration_WithinSingleDay_CalculatesAccurateFractions()
    {
        var targetDate = new DateTime(2026, 11, 6);
        var tz = TimeZoneInfo.Utc;

        // Interval from 06:00 to 12:00 UTC (exactly 6 hours = 21,600s)
        var start = new DateTimeOffset(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var interval = CreateInterval("code.exe", start, end);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [interval], tz);

        Assert.Equal(targetDate.Date, result.Date);
        var block = Assert.Single(result.Blocks);

        Assert.Equal(0.25, block.StartFraction, 4); // 6 / 24
        Assert.Equal(0.50, block.EndFraction, 4);   // 12 / 24
        Assert.Equal(21600.0, block.DurationSeconds);
        Assert.Equal(21600.0, result.TotalActiveSeconds);
        Assert.Equal("6h", block.FormattedDuration);
        Assert.Equal("code.exe", block.ProcessName);
        Assert.Equal(ActivityCategory.Unclassified, block.Category);
        Assert.Equal("#7E8B99", block.ColorHex);
        Assert.Contains("06:00:00 - 12:00:00", block.TooltipText);
    }

    [Fact]
    public void IntervalsCrossingMidnight_ClipsToDayBoundariesCorrectly()
    {
        var tz = TimeZoneInfo.Utc;
        // Interval starts at 23:00 on Day 1 (Nov 6) and ends at 02:00 on Day 2 (Nov 7) -> 3 hours total
        var start = new DateTimeOffset(2026, 11, 6, 23, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
        var interval = CreateInterval("discord.exe", start, end);

        // Day 1 evaluation
        var day1 = new DateTime(2026, 11, 6);
        var resultDay1 = ActivityTimelineCalculator.CalculateDayTimeline(day1, [interval], tz);
        var block1 = Assert.Single(resultDay1.Blocks);

        Assert.Equal(23.0 / 24.0, block1.StartFraction, 4);
        Assert.Equal(1.0, block1.EndFraction, 4);
        Assert.Equal(3600.0, block1.DurationSeconds); // 1 hour on Day 1
        Assert.Equal("1h", block1.FormattedDuration);

        // Day 2 evaluation
        var day2 = new DateTime(2026, 11, 7);
        var resultDay2 = ActivityTimelineCalculator.CalculateDayTimeline(day2, [interval], tz);
        var block2 = Assert.Single(resultDay2.Blocks);

        Assert.Equal(0.0, block2.StartFraction, 4);
        Assert.Equal(2.0 / 24.0, block2.EndFraction, 4);
        Assert.Equal(7200.0, block2.DurationSeconds); // 2 hours on Day 2
        Assert.Equal("2h", block2.FormattedDuration);

        // Sum of both segments equals total interval duration (3 hours = 10,800s)
        Assert.Equal(10800.0, block1.DurationSeconds + block2.DurationSeconds);
    }

    [Fact]
    public void IntervalsSpanningMultipleDays_FillsEntire24HourDay()
    {
        var tz = TimeZoneInfo.Utc;
        // Interval starts 2 days before and ends 2 days after
        var start = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
        var interval = CreateInterval("steam.exe", start, end);

        var middleDay = new DateTime(2026, 11, 6);
        var result = ActivityTimelineCalculator.CalculateDayTimeline(middleDay, [interval], tz);

        var block = Assert.Single(result.Blocks);
        Assert.Equal(0.0, block.StartFraction);
        Assert.Equal(1.0, block.EndFraction);
        Assert.Equal(86400.0, block.DurationSeconds);
        Assert.Equal("24h", block.FormattedDuration);
    }

    [Fact]
    public void MissingEndTimestamp_FallsBackToHeartbeatOrDurationSafely()
    {
        var targetDate = new DateTime(2026, 11, 6);
        var tz = TimeZoneInfo.Utc;

        var start = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);
        var heartbeat = start.AddMinutes(45); // 45 minutes = 2700s

        // Open interval with EndTimeUtc = null
        var openInterval = CreateInterval("notepad.exe", start, endTimeUtc: null, lastHeartbeatUtc: heartbeat);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [openInterval], tz);

        var block = Assert.Single(result.Blocks);
        Assert.Equal(14.0 / 24.0, block.StartFraction, 4);
        Assert.Equal(14.75 / 24.0, block.EndFraction, 4);
        Assert.Equal(2700.0, block.DurationSeconds);
        Assert.Equal("45 min", block.FormattedDuration);
    }

    [Fact]
    public void MissingEndTimestampAndHeartbeat_ClampsWithoutException()
    {
        var targetDate = new DateTime(2026, 11, 6);
        var tz = TimeZoneInfo.Utc;

        var start = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        // Corrupt heartbeat earlier than start and no EndTimeUtc
        var corruptInterval = CreateInterval("unknown.exe", start, endTimeUtc: null, lastHeartbeatUtc: start.AddMinutes(-10), durationSeconds: 0);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [corruptInterval], tz);

        // Clamps to 0 duration without throwing
        Assert.Empty(result.Blocks);
        Assert.Equal(0.0, result.TotalActiveSeconds);
    }

    [Theory]
    [InlineData("code.exe", @"C:\Program Files\Microsoft VS Code\code.exe")]
    [InlineData("devenv.exe", @"C:\Program Files\Microsoft Visual Studio\devenv.exe")]
    [InlineData("notepad.exe", @"C:\Windows\System32\notepad.exe")]
    [InlineData("discord.exe", @"C:\Users\User\AppData\Local\Discord\app.exe")]
    [InlineData("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData("custom_tool.exe", @"C:\Tools\custom_tool.exe")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void CategoryClassifier_DefaultsAllApplicationActivityToUnclassified(
        string? processName,
        string? executablePath)
    {
        var category = ActivityCategoryClassifier.Classify(processName, executablePath);
        var color = ActivityCategoryClassifier.GetCategoryColorHex(category);

        Assert.Equal(ActivityCategory.Unclassified, category);
        Assert.Equal("#7E8B99", color);
    }

    [Fact]
    public void CategoryClassifier_AllFourColorsPreserved()
    {
        Assert.Equal("#4CAF50", ActivityCategoryClassifier.GetCategoryColorHex(ActivityCategory.Productive));
        Assert.Equal("#F7A21B", ActivityCategoryClassifier.GetCategoryColorHex(ActivityCategory.Social));
        Assert.Equal("#B98BE0", ActivityCategoryClassifier.GetCategoryColorHex(ActivityCategory.Games));
        Assert.Equal("#7E8B99", ActivityCategoryClassifier.GetCategoryColorHex(ActivityCategory.Unclassified));
    }

    [Fact]
    public void EmptyActivityDays_ReturnsEmptyResultGracefully()
    {
        var date = new DateTime(2026, 11, 6);
        var result = ActivityTimelineCalculator.CalculateDayTimeline(date, []);

        Assert.Equal(date.Date, result.Date);
        Assert.Empty(result.Blocks);
        Assert.Equal(0.0, result.TotalActiveSeconds);
        Assert.Equal("0 min", result.FormattedTotalActive);
    }

    [Fact]
    public void TimeZoneAndDateBoundaryHandling_MapsToLocalCalendarDayAccurately()
    {
        // 2026-11-06 20:00:00 UTC
        var startUtc = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        var endUtc = startUtc.AddHours(2); // 22:00:00 UTC
        var interval = CreateInterval("slack.exe", startUtc, endUtc);

        // When viewed in UTC:
        // 20:00 to 22:00 belongs to Nov 6
        var utcDayResult = ActivityTimelineCalculator.CalculateDayTimeline(new DateTime(2026, 11, 6), [interval], TimeZoneInfo.Utc);
        Assert.Single(utcDayResult.Blocks);
        Assert.Equal(20.0 / 24.0, utcDayResult.Blocks[0].StartFraction, 4);

        // When viewed in UTC+5:30 (IST):
        // 20:00 UTC + 5:30 = 01:30 AM on Nov 7!
        // It must NOT appear on Nov 6 local day:
        var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var istDay6Result = ActivityTimelineCalculator.CalculateDayTimeline(new DateTime(2026, 11, 6), [interval], ist);
        Assert.Empty(istDay6Result.Blocks);

        // And it MUST appear on Nov 7 local day at 01:30 AM (1.5 / 24):
        var istDay7Result = ActivityTimelineCalculator.CalculateDayTimeline(new DateTime(2026, 11, 7), [interval], ist);
        var istBlock = Assert.Single(istDay7Result.Blocks);
        Assert.Equal(1.5 / 24.0, istBlock.StartFraction, 4);
        Assert.Equal(3.5 / 24.0, istBlock.EndFraction, 4);
    }

    [Fact]
    public void SpringForward_DaylightSavingTransition_RulerPositionsSkip0200AndCalculatesPhysicalFractions()
    {
        // US Eastern: 2026-03-08 is spring-forward (02:00 -> 03:00, UTC-5 becomes UTC-4)
        var est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var targetDate = new DateTime(2026, 3, 8);

        // Verify day boundaries (23 hours total: 05:00 UTC to 04:00 UTC next day)
        ActivityTimelineCalculator.GetDayBoundaries(targetDate, est, out var dayStartUtc, out var dayEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), dayStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), dayEndUtc);
        Assert.Equal(23.0, (dayEndUtc - dayStartUtc).TotalHours);

        // Verify ruler positions omit nonexistent local hour 02:00
        var rulerTicks = ActivityTimelineCalculator.GenerateRulerTicks(targetDate, est);
        Assert.Equal(24, rulerTicks.Count); // 0 to 23 hours inclusive
        Assert.DoesNotContain(rulerTicks, t => t.Label == "02:00");
        Assert.DoesNotContain(rulerTicks, t => t.TimeLocal.Hour == 2);
        Assert.Contains(rulerTicks, t => t.Label == "01:00");
        Assert.Contains(rulerTicks, t => t.Label == "03:00");

        // Interval from 06:30 UTC to 07:30 UTC (exactly 1 physical hour = 3600s)
        var startUtc = new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero);
        var endUtc = new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero);
        var interval = CreateInterval("work.exe", startUtc, endUtc);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [interval], est);

        var block = Assert.Single(result.Blocks);
        // Physical fraction on 23-hour day: elapsed UTC 1.5h to 2.5h
        Assert.Equal(1.5 / 23.0, block.StartFraction, 4);
        Assert.Equal(2.5 / 23.0, block.EndFraction, 4);
        Assert.Equal(1.0 / 23.0, block.EndFraction - block.StartFraction, 4);
        // Physical duration is invariant UTC duration (1 hour)
        Assert.Equal(3600.0, block.DurationSeconds);
        Assert.Equal("1h", block.FormattedDuration);
        Assert.Equal(3600.0, result.TotalActiveSeconds);
    }

    [Fact]
    public void FallBack_RepeatedHourInterval_RepresentsDistinctOccurrencesAndOneHourPhysicalWidth()
    {
        // US Eastern: 2026-11-01 is fall-back (02:00 repeats 01:00, UTC-4 becomes UTC-5)
        var est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var targetDate = new DateTime(2026, 11, 1);

        // Verify day boundaries (25 hours total: 04:00 UTC to 05:00 UTC next day)
        ActivityTimelineCalculator.GetDayBoundaries(targetDate, est, out var dayStartUtc, out var dayEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), dayStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), dayEndUtc);
        Assert.Equal(25.0, (dayEndUtc - dayStartUtc).TotalHours);

        // Verify ruler represents both occurrences of the repeated hour separately
        var rulerTicks = ActivityTimelineCalculator.GenerateRulerTicks(targetDate, est);
        var hourOneTicks = rulerTicks.Where(t => t.TimeLocal.Hour == 1).ToList();
        Assert.Equal(2, hourOneTicks.Count);
        Assert.Equal(1.0 / 25.0, hourOneTicks[0].Fraction, 4);
        Assert.Equal(2.0 / 25.0, hourOneTicks[1].Fraction, 4);
        Assert.Equal(TimeSpan.FromHours(-4), hourOneTicks[0].TimeLocal.Offset); // First occurrence (EDT)
        Assert.Equal(TimeSpan.FromHours(-5), hourOneTicks[1].TimeLocal.Offset); // Second occurrence (EST)

        // Interval from 05:30 UTC to 06:30 UTC on November 1, 2026 in US Eastern time
        // 05:30 UTC = 01:30 AM (EDT, offset -4)
        // 06:30 UTC = 01:30 AM (EST, offset -5)
        var startUtc = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var endUtc = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var interval = CreateInterval("reading.exe", startUtc, endUtc);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [interval], est);

        var block = Assert.Single(result.Blocks);
        // Represents the two distinct occurrences of 01:30
        Assert.Equal(1, block.StartTimeLocal.Hour);
        Assert.Equal(30, block.StartTimeLocal.Minute);
        Assert.Equal(TimeSpan.FromHours(-4), block.StartTimeLocal.Offset);

        Assert.Equal(1, block.EndTimeLocal.Hour);
        Assert.Equal(30, block.EndTimeLocal.Minute);
        Assert.Equal(TimeSpan.FromHours(-5), block.EndTimeLocal.Offset);

        // Tooltip includes UTC offsets to distinguish ambiguous local times
        Assert.Contains("-04:00", block.TooltipText);
        Assert.Contains("-05:00", block.TooltipText);

        // Verified visible one-hour physical width: (2.5 - 1.5) / 25 = 1 / 25
        Assert.Equal(1.5 / 25.0, block.StartFraction, 4);
        Assert.Equal(2.5 / 25.0, block.EndFraction, 4);
        Assert.Equal(1.0 / 25.0, block.EndFraction - block.StartFraction, 4);

        // Physical elapsed duration is exactly 1 hour = 3600s
        Assert.Equal(3600.0, block.DurationSeconds);
        Assert.Equal("1h", block.FormattedDuration);
        Assert.Equal(3600.0, result.TotalActiveSeconds);
    }

    [Fact]
    public void FallBack_IntervalSpanningBothRepeatedHours_CalculatesFractionsAndDurationCorrectly()
    {
        // US Eastern: 2026-11-01 fall-back day
        var est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var targetDate = new DateTime(2026, 11, 1);

        // Interval from 05:00 UTC (01:00 EDT) to 07:00 UTC (02:00 EST) -> spans both repeated hours
        var startUtc = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
        var endUtc = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero);
        var interval = CreateInterval("gaming.exe", startUtc, endUtc);

        var result = ActivityTimelineCalculator.CalculateDayTimeline(targetDate, [interval], est);

        var block = Assert.Single(result.Blocks);
        // Start: 1.0 hour elapsed from day start (04:00 UTC) -> 1.0 / 25.0
        // End: 3.0 hours elapsed from day start (04:00 UTC) -> 3.0 / 25.0
        Assert.Equal(1.0 / 25.0, block.StartFraction, 4);
        Assert.Equal(3.0 / 25.0, block.EndFraction, 4);
        // Visible physical width spans 2 physical hours: 2.0 / 25.0
        Assert.Equal(2.0 / 25.0, block.EndFraction - block.StartFraction, 4);

        // Physical duration is 2 hours = 7200s
        Assert.Equal(7200.0, block.DurationSeconds);
        Assert.Equal("2h", block.FormattedDuration);
        Assert.Equal(7200.0, result.TotalActiveSeconds);

        // Local times reflect offsets across the transition
        Assert.Equal(TimeSpan.FromHours(-4), block.StartTimeLocal.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), block.EndTimeLocal.Offset);
        Assert.Contains("-04:00", block.TooltipText);
        Assert.Contains("-05:00", block.TooltipText);
    }

    [Fact]
    public async Task ActivityDatabase_IntegrationQuery_FeedsTimelineAccurately()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gritwall_timeline_{Guid.NewGuid():N}.db");
        var db = new ActivityDatabase($"Data Source={dbPath};");
        await db.InitializeAsync();

        try
        {
            var date = new DateTime(2026, 11, 6);
            var tz = TimeZoneInfo.Utc;

            var int1 = CreateInterval("code.exe",
                new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero));

            var int2 = CreateInterval("discord.exe",
                new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 6, 14, 30, 0, TimeSpan.Zero));

            await db.InsertIntervalAsync(int1);
            await db.InsertIntervalAsync(int2);

            ActivityTimelineCalculator.GetDayBoundaries(date, tz, out var queryStart, out var queryEnd);

            var intervals = await db.GetIntervalsAsync(queryStart, queryEnd);
            Assert.Equal(2, intervals.Count);

            var timeline = ActivityTimelineCalculator.CalculateDayTimeline(date, intervals, tz);
            Assert.Equal(2, timeline.Blocks.Count);
            Assert.Equal("code.exe", timeline.Blocks[0].ProcessName);
            Assert.Equal(ActivityCategory.Unclassified, timeline.Blocks[0].Category);
            Assert.Equal("discord.exe", timeline.Blocks[1].ProcessName);
            Assert.Equal(ActivityCategory.Unclassified, timeline.Blocks[1].Category);

            // Total active = 2h + 1.5h = 3.5h = 12,600s
            Assert.Equal(12600.0, timeline.TotalActiveSeconds);
            Assert.Equal("3h 30m", timeline.FormattedTotalActive);
        }
        finally
        {
            await db.DisposeAsync();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
