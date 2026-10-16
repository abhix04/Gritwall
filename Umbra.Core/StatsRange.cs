namespace Umbra.Core;

// Les agrégations de History sont toutes du type "les N derniers jours à
// partir de maintenant". L'écran de statistiques, lui, navigue dans des
// périodes calendaires précises ("cette semaine", puis la précédente) avec
// des flèches, donc il faut des bornes explicites. Cette couche se pose à
// côté de History sans rien modifier de son API existante, déjà utilisée et
// testée ailleurs.
public enum StatsGranularity
{
    Day,
    Week,
    Month,
}

// Borne basse incluse, borne haute exclue, en heure locale.
public sealed record StatsPeriod(DateTime From, DateTime To, StatsGranularity Granularity, int Offset)
{
    // Offset 0 = période en cours, -1 = la précédente, +1 = la suivante.
    public static StatsPeriod For(StatsGranularity granularity, int offset = 0, DateTime? nowOpt = null)
    {
        var now = (nowOpt ?? DateTime.Now).Date;
        switch (granularity)
        {
            case StatsGranularity.Day:
            {
                var from = now.AddDays(offset);
                return new StatsPeriod(from, from.AddDays(1), granularity, offset);
            }
            case StatsGranularity.Week:
            {
                // Semaine qui commence le lundi, comme la bande des jours
                // déjà affichée au-dessus du streak.
                var delta = ((int)now.DayOfWeek + 6) % 7;
                var monday = now.AddDays(-delta).AddDays(offset * 7);
                return new StatsPeriod(monday, monday.AddDays(7), granularity, offset);
            }
            default:
            {
                var first = new DateTime(now.Year, now.Month, 1).AddMonths(offset);
                return new StatsPeriod(first, first.AddMonths(1), granularity, offset);
            }
        }
    }

    // Calculée depuis les bornes de CETTE période (From appartient déjà à
    // la période) plutôt qu'en repartant de maintenant : sinon le décalage
    // s'appliquerait deux fois.
    public StatsPeriod Previous() => For(Granularity, -1, From) with { Offset = Offset - 1 };

    public bool IsCurrent => Offset == 0;

    // Nombre de jours couverts, pour dimensionner les graphiques jour par jour.
    public int DayCount => (int)Math.Round((To - From).TotalDays);
}

public sealed record PeriodSummary(
    int Minutes,
    int Sessions,
    int LongestSessionMinutes,
    int AverageSessionMinutes,
    int ActiveDays,
    int PreviousMinutes,
    int PreviousSessions);

public sealed record SessionRow(DateTime EndedAt, string QuestName, string Kind, bool HardMode, int Minutes);

public static class StatsRange
{
    private static DateTime ToLocal(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().DateTime;

    private static List<HistoryEntry> EntriesIn(StatsPeriod period, List<HistoryEntry>? preloaded = null)
    {
        var entries = preloaded ?? History.Load();
        var result = new List<HistoryEntry>();
        foreach (var e in entries)
        {
            var at = ToLocal(e.EndedAt);
            if (at >= period.From && at < period.To) result.Add(e);
        }
        return result;
    }

    public static PeriodSummary Summarize(StatsPeriod period)
    {
        // Un seul Load() pour la période ET la précédente : history.json est
        // relu à chaque navigation de flèche, autant ne pas le lire deux fois.
        var all = History.Load();
        var now = EntriesIn(period, all);
        var before = EntriesIn(period.Previous(), all);

        var minutes = now.Sum(e => e.FocusedMinutes);
        var activeDays = now.Select(e => ToLocal(e.EndedAt).Date).Distinct().Count();

        return new PeriodSummary(
            Minutes: (int)Math.Round(minutes),
            Sessions: now.Count,
            LongestSessionMinutes: now.Count > 0 ? (int)Math.Round(now.Max(e => e.FocusedMinutes)) : 0,
            AverageSessionMinutes: now.Count > 0 ? (int)Math.Round(minutes / now.Count) : 0,
            ActiveDays: activeDays,
            PreviousMinutes: (int)Math.Round(before.Sum(e => e.FocusedMinutes)),
            PreviousSessions: before.Count);
    }

    public static List<QuestBreakdownRow> QuestsIn(StatsPeriod period)
    {
        var byQuest = new Dictionary<string, double>();
        foreach (var e in EntriesIn(period))
        {
            var name = string.IsNullOrWhiteSpace(e.QuestName) ? History.DefaultQuest : e.QuestName.Trim();
            byQuest[name] = byQuest.GetValueOrDefault(name) + e.FocusedMinutes;
        }
        return byQuest
            .Select(kv => new QuestBreakdownRow(kv.Key, (int)Math.Round(kv.Value)))
            .OrderByDescending(r => r.Minutes)
            .ToList();
    }

    // Une ligne par jour de la période, jours vides compris, pour que le
    // graphique garde la même largeur de barres d'une semaine à l'autre.
    public static List<DailyBreakdownRow> DaysIn(StatsPeriod period)
    {
        var byDay = new Dictionary<DateTime, double>();
        foreach (var e in EntriesIn(period))
        {
            var day = ToLocal(e.EndedAt).Date;
            byDay[day] = byDay.GetValueOrDefault(day) + e.FocusedMinutes;
        }

        var rows = new List<DailyBreakdownRow>();
        for (var d = period.From; d < period.To; d = d.AddDays(1))
        {
            rows.Add(new DailyBreakdownRow(
                $"{d.Year}-{d.Month:D2}-{d.Day:D2}",
                (int)Math.Round(byDay.GetValueOrDefault(d))));
        }
        return rows;
    }

    // Nombre de sessions par jour, pour le second graphique de "jour par jour".
    public static List<DailyBreakdownRow> SessionsPerDayIn(StatsPeriod period)
    {
        var byDay = new Dictionary<DateTime, int>();
        foreach (var e in EntriesIn(period))
        {
            var day = ToLocal(e.EndedAt).Date;
            byDay[day] = byDay.GetValueOrDefault(day) + 1;
        }

        var rows = new List<DailyBreakdownRow>();
        for (var d = period.From; d < period.To; d = d.AddDays(1))
        {
            rows.Add(new DailyBreakdownRow(
                $"{d.Year}-{d.Month:D2}-{d.Day:D2}",
                byDay.GetValueOrDefault(d)));
        }
        return rows;
    }

    private static string HourBucket(int hour)
    {
        if (hour >= 5 && hour < 12) return "morning";
        if (hour >= 12 && hour < 18) return "afternoon";
        if (hour >= 18 && hour < 22) return "evening";
        return "night";
    }

    // Minutes par heure de la journée (24 lignes, heures creuses comprises).
    // En vue "Jour", la répartition jour par jour se réduirait à un seul
    // point : c'est l'heure qui porte l'information à cette échelle.
    public static List<DailyBreakdownRow> HoursIn(StatsPeriod period)
    {
        var byHour = new double[24];
        foreach (var e in EntriesIn(period)) byHour[ToLocal(e.EndedAt).Hour] += e.FocusedMinutes;
        return Enumerable.Range(0, 24)
            .Select(h => new DailyBreakdownRow($"{h:D2}:00", (int)Math.Round(byHour[h])))
            .ToList();
    }

    public static List<TimeOfDayRow> TimeOfDayIn(StatsPeriod period)
    {
        var buckets = new Dictionary<string, double>
        {
            ["morning"] = 0, ["afternoon"] = 0, ["evening"] = 0, ["night"] = 0,
        };
        foreach (var e in EntriesIn(period))
            buckets[HourBucket(ToLocal(e.EndedAt).Hour)] += e.FocusedMinutes;

        return new[] { "morning", "afternoon", "evening", "night" }
            .Select(key => new TimeOfDayRow(key, (int)Math.Round(buckets[key])))
            .ToList();
    }

    // Sessions de la période, la plus récente en premier.
    public static List<SessionRow> SessionsIn(StatsPeriod period, int limit = 60) =>
        EntriesIn(period)
            .OrderByDescending(e => e.EndedAt)
            .Take(limit)
            .Select(e => new SessionRow(
                ToLocal(e.EndedAt),
                string.IsNullOrWhiteSpace(e.QuestName) ? History.DefaultQuest : e.QuestName.Trim(),
                // Normalisé comme dans KindsIn : un Kind vide afficherait la
                // clé de traduction brute ("stats.kind.") dans la liste.
                string.IsNullOrWhiteSpace(e.Kind) ? "custom" : e.Kind,
                e.HardMode,
                (int)Math.Round(e.FocusedMinutes)))
            .ToList();

    // Répartition par type de session (libre, pomodoro, plage horaire) :
    // c'est l'équivalent honnête d'un camembert "focus / meetings / breaks",
    // avec les seules catégories qu'Umbra mesure réellement.
    public static List<QuestBreakdownRow> KindsIn(StatsPeriod period)
    {
        var byKind = new Dictionary<string, double>();
        foreach (var e in EntriesIn(period))
        {
            var kind = string.IsNullOrWhiteSpace(e.Kind) ? "custom" : e.Kind;
            byKind[kind] = byKind.GetValueOrDefault(kind) + e.FocusedMinutes;
        }
        return byKind
            .Select(kv => new QuestBreakdownRow(kv.Key, (int)Math.Round(kv.Value)))
            .OrderByDescending(r => r.Minutes)
            .ToList();
    }
}
