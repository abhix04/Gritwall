using Umbra.Core;

namespace Umbra.Tests;

public class StatsRangeTests : IDisposable
{
    private readonly string _tempDir;
    // Un mercredi, pour que les bornes de semaine ne tombent pas par hasard juste.
    private static readonly DateTime Now = new(2026, 10, 7, 15, 30, 0);

    public StatsRangeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "umbra-statsrange-" + Guid.NewGuid());
        Config.DataDir = _tempDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void Add(DateTime endedAt, double minutes, string quest = "Thèse", string kind = "custom")
    {
        var data = History.Load();
        data.Add(new HistoryEntry
        {
            EndedAt = new DateTimeOffset(endedAt).ToUnixTimeMilliseconds(),
            Kind = kind,
            QuestName = quest,
            FocusedMinutes = minutes,
        });
        History.Save(data);
    }

    [Fact]
    public void Week_StartsOnMonday_AndCoversSevenDays()
    {
        var p = StatsPeriod.For(StatsGranularity.Week, 0, Now);

        Assert.Equal(DayOfWeek.Monday, p.From.DayOfWeek);
        Assert.Equal(new DateTime(2026, 10, 5), p.From);
        Assert.Equal(new DateTime(2026, 10, 12), p.To);
        Assert.Equal(7, p.DayCount);
    }

    [Fact]
    public void Previous_StepsBackExactlyOnePeriod_WithoutDoubleCountingTheOffset()
    {
        var week = StatsPeriod.For(StatsGranularity.Week, -1, Now);   // semaine du 28 sept
        var before = week.Previous();                                  // doit etre celle du 21

        Assert.Equal(new DateTime(2026, 9, 28), week.From);
        Assert.Equal(new DateTime(2026, 9, 21), before.From);
        Assert.Equal(new DateTime(2026, 9, 28), before.To);
        Assert.Equal(-2, before.Offset);
    }

    [Fact]
    public void Previous_OnMonth_LandsOnTheMonthBefore()
    {
        var month = StatsPeriod.For(StatsGranularity.Month, 0, Now);
        var before = month.Previous();

        Assert.Equal(new DateTime(2026, 10, 1), month.From);
        Assert.Equal(new DateTime(2026, 9, 1), before.From);
        Assert.Equal(new DateTime(2026, 10, 1), before.To);
    }

    [Fact]
    public void Summarize_CountsOnlyThePeriod_AndComparesWithTheOneBefore()
    {
        Add(new DateTime(2026, 10, 6, 10, 0, 0), 60);   // cette semaine
        Add(new DateTime(2026, 10, 7, 9, 0, 0), 30);    // cette semaine
        Add(new DateTime(2026, 9, 30, 9, 0, 0), 25);    // semaine precedente

        var s = StatsRange.Summarize(StatsPeriod.For(StatsGranularity.Week, 0, Now));

        Assert.Equal(90, s.Minutes);
        Assert.Equal(2, s.Sessions);
        Assert.Equal(60, s.LongestSessionMinutes);
        Assert.Equal(45, s.AverageSessionMinutes);
        Assert.Equal(2, s.ActiveDays);
        Assert.Equal(25, s.PreviousMinutes);
        Assert.Equal(1, s.PreviousSessions);
    }

    [Fact]
    public void QuestsIn_SplitsTheTimePerTask_HighestFirst()
    {
        Add(new DateTime(2026, 10, 6, 10, 0, 0), 20, "Coréen");
        Add(new DateTime(2026, 10, 6, 14, 0, 0), 50, "Thèse");
        Add(new DateTime(2026, 10, 7, 11, 0, 0), 10, "Coréen");
        Add(new DateTime(2026, 9, 30, 9, 0, 0), 99, "Hors période");

        var rows = StatsRange.QuestsIn(StatsPeriod.For(StatsGranularity.Week, 0, Now));

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Thèse", 50), (rows[0].QuestName, rows[0].Minutes));
        Assert.Equal(("Coréen", 30), (rows[1].QuestName, rows[1].Minutes));
    }

    [Fact]
    public void DaysIn_ReturnsOneRowPerDay_IncludingEmptyOnes()
    {
        Add(new DateTime(2026, 10, 6, 10, 0, 0), 40);

        var rows = StatsRange.DaysIn(StatsPeriod.For(StatsGranularity.Week, 0, Now));

        Assert.Equal(7, rows.Count);
        Assert.Equal("2026-10-05", rows[0].Date);
        Assert.Equal(0, rows[0].Minutes);
        Assert.Equal(40, rows[1].Minutes);     // mardi 6
        Assert.All(rows.Skip(2), r => Assert.Equal(0, r.Minutes));
    }

    [Fact]
    public void SessionsIn_ListsTheMostRecentFirst()
    {
        Add(new DateTime(2026, 10, 6, 10, 0, 0), 20, "Matin");
        Add(new DateTime(2026, 10, 7, 14, 0, 0), 35, "Après-midi", "pomodoro");

        var rows = StatsRange.SessionsIn(StatsPeriod.For(StatsGranularity.Week, 0, Now));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Après-midi", rows[0].QuestName);
        Assert.Equal("pomodoro", rows[0].Kind);
        Assert.Equal(35, rows[0].Minutes);
        Assert.Equal("Matin", rows[1].QuestName);
    }

    [Fact]
    public void KindsIn_GroupsBySessionType()
    {
        Add(new DateTime(2026, 10, 6, 10, 0, 0), 20, "a", "custom");
        Add(new DateTime(2026, 10, 6, 12, 0, 0), 45, "b", "pomodoro");
        Add(new DateTime(2026, 10, 7, 9, 0, 0), 15, "c", "custom");

        var rows = StatsRange.KindsIn(StatsPeriod.For(StatsGranularity.Week, 0, Now));

        Assert.Equal(2, rows.Count);
        Assert.Equal(("pomodoro", 45), (rows[0].QuestName, rows[0].Minutes));
        Assert.Equal(("custom", 35), (rows[1].QuestName, rows[1].Minutes));
    }

    [Fact]
    public void Day_CoversExactlyThatDay()
    {
        Add(new DateTime(2026, 10, 7, 0, 30, 0), 10);    // dans la journee
        Add(new DateTime(2026, 10, 6, 23, 50, 0), 99);   // la veille

        var s = StatsRange.Summarize(StatsPeriod.For(StatsGranularity.Day, 0, Now));

        Assert.Equal(10, s.Minutes);
        Assert.Equal(99, s.PreviousMinutes);
    }

    // Le passage Jour -> Semaine doit bien revenir sur la semaine courante
    // complète : l'écran remet l'offset à zéro en changeant d'échelle, et
    // c'est ce retour qui semblait "ne plus rien afficher".
    [Fact]
    public void SwitchingDayToWeek_ComesBackToTheWholeCurrentWeek()
    {
        Add(new DateTime(2026, 10, 5, 9, 0, 0), 60, "Thèse");     // lundi
        Add(new DateTime(2026, 10, 7, 20, 0, 0), 45, "Coréen");   // mercredi (le "maintenant")

        // En vue Jour, seul le mercredi compte.
        var day = StatsPeriod.For(StatsGranularity.Day, 0, Now);
        Assert.Equal(45, StatsRange.Summarize(day).Minutes);

        // Retour en Semaine, offset remis à zéro : les deux sessions reviennent.
        var week = StatsPeriod.For(StatsGranularity.Week, 0, Now);
        Assert.Equal(105, StatsRange.Summarize(week).Minutes);
        Assert.Equal(2, StatsRange.QuestsIn(week).Count);        // les deux tâches reviennent
        Assert.Equal(7, StatsRange.DaysIn(week).Count);
    }

    [Fact]
    public void HoursIn_SpreadsTheDayOverTwentyFourPoints()
    {
        Add(new DateTime(2026, 10, 7, 9, 30, 0), 50);
        Add(new DateTime(2026, 10, 7, 21, 0, 0), 25);

        var rows = StatsRange.HoursIn(StatsPeriod.For(StatsGranularity.Day, 0, Now));

        Assert.Equal(24, rows.Count);
        Assert.Equal("09:00", rows[9].Date);
        Assert.Equal(50, rows[9].Minutes);
        Assert.Equal(25, rows[21].Minutes);
        Assert.Equal(0, rows[0].Minutes);
    }
}
