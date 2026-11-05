using System.Globalization;

namespace Umbra.Core.Tracking;

/// <summary>
/// Represents a ruler tick marking an hour boundary within a timezone-aware day timeline.
/// </summary>
public sealed record TimelineRulerTick(
    string Label,
    double Fraction,
    DateTimeOffset TimeLocal
);

/// <summary>
/// Represents a normalized application activity segment positioned within a daily timeline.
/// </summary>
public sealed record TimelineBlock(
    long IntervalId,
    string ProcessName,
    string ExecutablePath,
    ActivityCategory Category,
    DateTimeOffset StartTimeLocal,
    DateTimeOffset EndTimeLocal,
    double StartFraction,
    double EndFraction,
    double DurationSeconds,
    string FormattedDuration,
    string TooltipText,
    string ColorHex
);

/// <summary>
/// Encapsulates the complete timeline analysis for a specific calendar day.
/// </summary>
public sealed record TimelineDayResult(
    DateTime Date,
    IReadOnlyList<TimelineBlock> Blocks,
    IReadOnlyList<TimelineRulerTick> RulerTicks,
    double TotalActiveSeconds,
    string FormattedTotalActive
);

/// <summary>
/// Computes timeline positions, boundary clipping, and duration metrics from raw activity intervals
/// using TimeZoneInfo adjustment rules for Daylight Saving Time and multi-day boundaries.
/// </summary>
public static class ActivityTimelineCalculator
{
    /// <summary>
    /// Computes the UTC boundaries for the start (00:00:00) and end (24:00:00) of a local calendar day
    /// independently using the specified timezone's adjustment rules.
    /// </summary>
    public static void GetDayBoundaries(
        DateTime selectedDate,
        TimeZoneInfo timeZone,
        out DateTimeOffset dayStartUtc,
        out DateTimeOffset dayEndUtc)
    {
        var dateOnly = selectedDate.Date;
        var startLocalDt = new DateTime(dateOnly.Year, dateOnly.Month, dateOnly.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocalDt = startLocalDt.AddDays(1);

        var startOffset = timeZone.GetUtcOffset(startLocalDt);
        var endOffset = timeZone.GetUtcOffset(endLocalDt);

        var dayStartLocal = new DateTimeOffset(startLocalDt, startOffset);
        var dayEndLocal = new DateTimeOffset(endLocalDt, endOffset);

        dayStartUtc = dayStartLocal.ToUniversalTime();
        dayEndUtc = dayEndLocal.ToUniversalTime();
    }

    /// <summary>
    /// Generates hour-ruler tick positions from actual timezone-aware clock boundaries.
    /// During spring-forward, the nonexistent local hour is omitted.
    /// During fall-back, both occurrences of the repeated hour are represented separately.
    /// </summary>
    public static IReadOnlyList<TimelineRulerTick> GenerateRulerTicks(
        DateTime selectedDate,
        TimeZoneInfo timeZone)
    {
        GetDayBoundaries(selectedDate, timeZone, out var dayStartUtc, out var dayEndUtc);
        double totalDurationSeconds = (dayEndUtc - dayStartUtc).TotalSeconds;
        if (totalDurationSeconds <= 0)
        {
            return Array.Empty<TimelineRulerTick>();
        }

        var ticks = new List<TimelineRulerTick>();
        int totalPhysicalHours = (int)Math.Round(totalDurationSeconds / 3600.0);

        for (int h = 0; h <= totalPhysicalHours; h++)
        {
            var currentUtc = dayStartUtc.AddHours(h);
            if (currentUtc > dayEndUtc)
            {
                currentUtc = dayEndUtc;
            }

            double elapsedSeconds = (currentUtc - dayStartUtc).TotalSeconds;
            double fraction = Math.Clamp(elapsedSeconds / totalDurationSeconds, 0.0, 1.0);

            var localTime = TimeZoneInfo.ConvertTime(currentUtc, timeZone);
            string label;
            if (h == totalPhysicalHours || currentUtc == dayEndUtc)
            {
                label = "24:00";
            }
            else
            {
                label = localTime.ToString("HH:00", CultureInfo.InvariantCulture);
            }

            ticks.Add(new TimelineRulerTick(label, fraction, localTime));
        }

        return ticks;
    }

    /// <summary>
    /// Processes a collection of activity intervals for a designated calendar day,
    /// clipping boundaries, resolving missing timestamps, and generating proportional timeline blocks.
    /// </summary>
    public static TimelineDayResult CalculateDayTimeline(
        DateTime selectedDate,
        IReadOnlyList<AppActivityInterval> intervals,
        TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var dateOnly = selectedDate.Date;

        GetDayBoundaries(dateOnly, tz, out var dayStartUtc, out var dayEndUtc);
        double dayDurationSeconds = (dayEndUtc - dayStartUtc).TotalSeconds;

        var blocks = new List<TimelineBlock>();
        double totalSeconds = 0.0;

        if (intervals != null && dayDurationSeconds > 0)
        {
            foreach (var interval in intervals)
            {
                var block = TryCreateBlock(interval, dayStartUtc, dayEndUtc, dayDurationSeconds, tz);
                if (block != null)
                {
                    blocks.Add(block);
                    totalSeconds += block.DurationSeconds;
                }
            }
        }

        // Sort blocks chronologically by start fraction, then end fraction
        blocks.Sort((a, b) =>
        {
            int cmp = a.StartFraction.CompareTo(b.StartFraction);
            return cmp != 0 ? cmp : a.EndFraction.CompareTo(b.EndFraction);
        });

        var rulerTicks = GenerateRulerTicks(dateOnly, tz);

        return new TimelineDayResult(
            Date: dateOnly,
            Blocks: blocks,
            RulerTicks: rulerTicks,
            TotalActiveSeconds: totalSeconds,
            FormattedTotalActive: FormatDuration(totalSeconds));
    }

    private static TimelineBlock? TryCreateBlock(
        AppActivityInterval interval,
        DateTimeOffset dayStartUtc,
        DateTimeOffset dayEndUtc,
        double dayDurationSeconds,
        TimeZoneInfo tz)
    {
        // Resolve effective end timestamp in UTC
        DateTimeOffset effectiveEndUtc;
        if (interval.EndTimeUtc.HasValue)
        {
            effectiveEndUtc = interval.EndTimeUtc.Value;
        }
        else if (interval.LastHeartbeatUtc > interval.StartTimeUtc)
        {
            effectiveEndUtc = interval.LastHeartbeatUtc;
        }
        else if (interval.DurationSeconds.HasValue && interval.DurationSeconds.Value > 0)
        {
            effectiveEndUtc = interval.StartTimeUtc.AddSeconds(interval.DurationSeconds.Value);
        }
        else
        {
            effectiveEndUtc = interval.StartTimeUtc;
        }

        if (effectiveEndUtc < interval.StartTimeUtc)
        {
            effectiveEndUtc = interval.StartTimeUtc;
        }

        // Determine UTC overlap with the calendar day's UTC boundaries
        var overlapStartUtc = interval.StartTimeUtc > dayStartUtc ? interval.StartTimeUtc : dayStartUtc;
        var overlapEndUtc = effectiveEndUtc < dayEndUtc ? effectiveEndUtc : dayEndUtc;

        if (overlapEndUtc <= overlapStartUtc)
        {
            return null;
        }

        // Physical active duration in seconds (UTC subtraction is invariant to DST clock shifts)
        double visibleSeconds = (overlapEndUtc - overlapStartUtc).TotalSeconds;

        // Calculate timeline fractions using elapsed UTC time between the actual local-day UTC boundaries.
        // Use the actual duration of the selected local day as the denominator.
        double startElapsedSeconds = (overlapStartUtc - dayStartUtc).TotalSeconds;
        double endElapsedSeconds = (overlapEndUtc - dayStartUtc).TotalSeconds;

        double startFraction = Math.Clamp(startElapsedSeconds / dayDurationSeconds, 0.0, 1.0);
        double endFraction = Math.Clamp(endElapsedSeconds / dayDurationSeconds, 0.0, 1.0);

        // Convert the overlapping endpoints to the actual local time under the timezone's adjustment rules
        var overlapStartLocal = TimeZoneInfo.ConvertTime(overlapStartUtc, tz);
        var overlapEndLocal = TimeZoneInfo.ConvertTime(overlapEndUtc, tz);

        var category = ActivityCategoryClassifier.Classify(interval.ProcessName, interval.ExecutablePath);
        var colorHex = ActivityCategoryClassifier.GetCategoryColorHex(category);
        var formattedDuration = FormatDuration(visibleSeconds);

        var appName = !string.IsNullOrWhiteSpace(interval.ProcessName)
            ? interval.ProcessName
            : (Path.GetFileName(interval.ExecutablePath) is { Length: > 0 } name ? name : "Unknown Application");

        // Include UTC offsets in tooltips when necessary to distinguish ambiguous local times
        bool isAmbiguous = tz.IsAmbiguousTime(overlapStartLocal.DateTime)
            || tz.IsAmbiguousTime(overlapEndLocal.DateTime)
            || overlapStartLocal.Offset != overlapEndLocal.Offset;

        string rangeStr = isAmbiguous
            ? $"{overlapStartLocal.ToString("HH:mm:ss (zzz)", CultureInfo.InvariantCulture)} - {overlapEndLocal.ToString("HH:mm:ss (zzz)", CultureInfo.InvariantCulture)}"
            : $"{overlapStartLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} - {overlapEndLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";

        var tooltip = $"""
            {appName}
            Category: {category}
            Active Duration: {formattedDuration}
            Range: {rangeStr}
            """.Trim();

        return new TimelineBlock(
            IntervalId: interval.Id,
            ProcessName: appName,
            ExecutablePath: interval.ExecutablePath,
            Category: category,
            StartTimeLocal: overlapStartLocal,
            EndTimeLocal: overlapEndLocal,
            StartFraction: startFraction,
            EndFraction: endFraction,
            DurationSeconds: visibleSeconds,
            FormattedDuration: formattedDuration,
            TooltipText: tooltip,
            ColorHex: colorHex);
    }

    /// <summary>
    /// Formats seconds into human-readable duration string (e.g. "45s", "12m 30s", "1h 15m").
    /// </summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "0 min";
        long totalSec = (long)Math.Round(seconds);

        if (totalSec < 60)
        {
            return $"{totalSec}s";
        }

        long minutes = totalSec / 60;
        long remainingSec = totalSec % 60;

        if (minutes < 60)
        {
            return remainingSec > 0 ? $"{minutes}m {remainingSec}s" : $"{minutes} min";
        }

        long hours = minutes / 60;
        long remMinutes = minutes % 60;
        return remMinutes > 0 ? $"{hours}h {remMinutes:D2}m" : $"{hours}h";
    }
}
