using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Umbra.Core;
using Umbra.Core.Tracking;
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;

namespace Umbra.App.Pages;

// Navigation par période (jour / semaine / mois avec les flèches) et les
// quatre onglets de l'écran de statistiques. Séparé de StatsPage.xaml.cs,
// qui garde le streak, la heatmap, la musique et les tentatives bloquées.
public partial class StatsPage
{
    private enum StatsTab { Overview, TimeSpent, DayByDay, Records }

    private StatsGranularity _granularity = StatsGranularity.Week;
    private int _periodOffset;
    private StatsTab _tab = StatsTab.Overview;

    // Palette des parts (temps par tâche, par type) - l'ambre du streak en
    // tête pour rester dans la famille de couleurs de la page.
    private static readonly Color[] SliceColors =
    [
        Color.FromRgb(0xF7, 0xA2, 0x1B),
        Color.FromRgb(0x2D, 0xA5, 0xE6),
        Color.FromRgb(0x7A, 0xC9, 0x8E),
        Color.FromRgb(0xB9, 0x8B, 0xE0),
        Color.FromRgb(0xE0, 0x79, 0x6A),
        Color.FromRgb(0x6F, 0xB0, 0xDE),
        Color.FromRgb(0xD2, 0xC0, 0x6A),
    ];

    private StatsPeriod CurrentPeriod => StatsPeriod.For(_granularity, _periodOffset);

    private void InitializePeriods()
    {
        PageTitleText.Text = Loc.T("stats.title");
        QuestCaption.Text = Loc.T("stats.quests");
        KindCaption.Text = Loc.T("stats.kinds");
        TimeOfDayCaption.Text = Loc.T("stats.timeofday");
        TimelineCaption.Text = Loc.Language == "fr" ? "CHRONOLOGIE D'ACTIVITÉ (24H)" : "ACTIVITY TIMELINE (24H)";
        TimelineTitleText.Text = Loc.Language == "fr" ? "Activité des applications" : "Application Activity";
        TimelineHost.SizeChanged -= TimelineHost_SizeChanged;
        TimelineHost.SizeChanged += TimelineHost_SizeChanged;
        HeatmapCaption.Text = Loc.T("stats.activity");
        BestEffortsCaption.Text = Loc.T("stats.best");
        SessionListCaption.Text = Loc.T("stats.sessions");
        FocusPerDayEmptyText.Text = Loc.T("stats.empty.period");
        SessionListEmptyText.Text = Loc.T("stats.empty.period");

        BuildGranularityButtons();
        BuildTabs();
        RenderPeriod();
    }

    // ---------- barres d'onglets ----------

    private Button Pill(string text, bool selected, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(14, 5, 14, 6),
            Margin = new Thickness(0, 0, 4, 0),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            Background = selected ? Res("ControlFillColorSecondaryBrush") : System.Windows.Media.Brushes.Transparent,
            Foreground = Res("TextFillColorPrimaryBrush"),
            Opacity = selected ? 1.0 : 0.68,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void BuildGranularityButtons()
    {
        GranularityPanel.Children.Clear();
        foreach (var (g, key) in new[]
                 {
                     (StatsGranularity.Day, "stats.gran.day"),
                     (StatsGranularity.Week, "stats.gran.week"),
                     (StatsGranularity.Month, "stats.gran.month"),
                 })
        {
            var granularity = g;
            GranularityPanel.Children.Add(Pill(Loc.T(key), _granularity == granularity, () =>
            {
                if (_granularity == granularity) return;
                _granularity = granularity;
                // Changer d'échelle remet sur la période en cours : garder un
                // offset de -3 en passant de jour à mois enverrait l'utilisateur
                // trois mois en arrière sans qu'il l'ait demandé.
                _periodOffset = 0;
                BuildGranularityButtons();
                RenderPeriod();
            }));
        }
    }

    private void BuildTabs()
    {
        TabsPanel.Children.Clear();
        foreach (var (t, key) in new[]
                 {
                     (StatsTab.Overview, "stats.tab.overview"),
                     (StatsTab.TimeSpent, "stats.tab.timespent"),
                     (StatsTab.DayByDay, "stats.tab.daybyday"),
                     (StatsTab.Records, "stats.tab.records"),
                 })
        {
            var tab = t;
            TabsPanel.Children.Add(Pill(Loc.T(key), _tab == tab, () =>
            {
                if (_tab == tab) return;
                _tab = tab;
                BuildTabs();
                RenderPeriod();
            }));
        }
    }

    private void PrevPeriod_Click(object sender, RoutedEventArgs e)
    {
        _periodOffset--;
        RenderPeriod();
    }

    private void NextPeriod_Click(object sender, RoutedEventArgs e)
    {
        // Pas de navigation dans le futur : il n'y a rien à y voir.
        if (_periodOffset >= 0) return;
        _periodOffset++;
        RenderPeriod();
    }

    // ---------- rendu ----------

    private void RenderPeriod()
    {
        var period = CurrentPeriod;
        PeriodLabelText.Text = PeriodLabel(period);
        PeriodStatsCaption.Text = PeriodRangeText(period);
        FocusPerDayCaption.Text = Loc.T(_granularity == StatsGranularity.Day ? "stats.chart.hours" : "stats.chart.focus");
        NextPeriodButton.IsEnabled = _periodOffset < 0;
        NextPeriodButton.Opacity = _periodOffset < 0 ? 1 : 0.3;

        TabOverview.Visibility = _tab == StatsTab.Overview ? Visibility.Visible : Visibility.Collapsed;
        TabTimeSpent.Visibility = _tab == StatsTab.TimeSpent ? Visibility.Visible : Visibility.Collapsed;
        TabDayByDay.Visibility = _tab == StatsTab.DayByDay ? Visibility.Visible : Visibility.Collapsed;
        TabRecords.Visibility = _tab == StatsTab.Records ? Visibility.Visible : Visibility.Collapsed;

        switch (_tab)
        {
            case StatsTab.Overview: RenderOverview(period); break;
            case StatsTab.TimeSpent: RenderTimeSpent(period); break;
            case StatsTab.DayByDay: RenderDayByDay(period); break;
            default: RenderRecords(period); break;
        }
    }

    private string PeriodLabel(StatsPeriod p)
    {
        var culture = Loc.Language == "fr"
            ? new System.Globalization.CultureInfo("fr-FR")
            : new System.Globalization.CultureInfo("en-US");

        if (p.Granularity == StatsGranularity.Day)
            return p.IsCurrent ? Loc.T("stats.period.today") : p.From.ToString("ddd d MMM", culture);
        if (p.Granularity == StatsGranularity.Week)
            return p.IsCurrent
                ? Loc.T("stats.period.thisweek")
                : $"{p.From:dd/MM} - {p.To.AddDays(-1):dd/MM}";
        return p.IsCurrent ? Loc.T("stats.period.thismonth") : p.From.ToString("MMMM yyyy", culture);
    }

    // "Sur la période" ne dit rien : on écrit la plage réellement couverte.
    private string PeriodRangeText(StatsPeriod p)
    {
        var culture = Loc.Language == "fr"
            ? new System.Globalization.CultureInfo("fr-FR")
            : new System.Globalization.CultureInfo("en-US");
        var last = p.To.AddDays(-1);
        if (p.Granularity == StatsGranularity.Day)
            return Capitalize(p.From.ToString("dddd d MMMM yyyy", culture));
        if (p.Granularity == StatsGranularity.Week)
            return string.Format(Loc.T("stats.range.week"),
                p.From.ToString("d MMMM", culture), last.ToString("d MMMM yyyy", culture));
        return Capitalize(p.From.ToString("MMMM yyyy", culture));
    }

    // "Aucune session sur cette période" laisse croire à un bug quand on
    // vient de passer en vue Jour : on nomme la période réellement vide.
    private string EmptyText() => string.Format(Loc.T("stats.empty.named"), PeriodRangeText(CurrentPeriod).ToLowerInvariant());

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string Minutes(int m) =>
        m >= 60 ? $"{m / 60}h{(m % 60 == 0 ? "" : (m % 60).ToString("D2"))}" : $"{m} min";

    private FrameworkElement StatTile(string label, string value, Brush? accent = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = accent ?? Res("TextFillColorPrimaryBrush"),
        });
        return panel;
    }

    // Tendance par rapport à la période précédente. Pas de pourcentage quand
    // la période précédente est vide : "+100 %" à partir de zéro ne veut rien
    // dire, on affiche la valeur brute à la place.
    private FrameworkElement TrendTile(string label, int value, int previous, Func<int, string> fmt)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = fmt(value), FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextFillColorPrimaryBrush"),
        });

        string trend;
        Brush colour;
        if (previous <= 0)
        {
            trend = value > 0 ? Loc.T("stats.trend.new") : "-";
            colour = Res("TextFillColorSecondaryBrush");
        }
        else
        {
            var pct = (int)Math.Round((value - previous) * 100.0 / previous);
            trend = (pct >= 0 ? "+" : "") + pct + " %";
            colour = pct >= 0
                ? new SolidColorBrush(Color.FromRgb(0x7A, 0xC9, 0x8E))
                : new SolidColorBrush(Color.FromRgb(0xE0, 0x79, 0x6A));
        }
        panel.Children.Add(new TextBlock
        {
            Text = trend, FontSize = 11, Foreground = colour, Margin = new Thickness(0, 3, 0, 0),
        });
        return panel;
    }

    private void RenderOverview(StatsPeriod period)
    {
        var s = StatsRange.Summarize(period);

        PeriodStatsGrid.Children.Clear();
        PeriodStatsGrid.Children.Add(TrendTile(Loc.T("stats.focus"), s.Minutes, s.PreviousMinutes, Minutes));
        PeriodStatsGrid.Children.Add(TrendTile(Loc.T("stats.sessions"), s.Sessions, s.PreviousSessions, v => v.ToString()));
        PeriodStatsGrid.Children.Add(StatTile(Loc.T("stats.longest"), Minutes(s.LongestSessionMinutes)));
        PeriodStatsGrid.Children.Add(StatTile(Loc.T("stats.activedays"), $"{s.ActiveDays} / {period.DayCount}"));

        // Le calendrier d'activité et les musiques ont rejoint cet onglet :
        // c'est la vue ouverte par défaut, elle doit se lire d'un coup d'oeil.
        // Les courbes restent dans "Jour par jour".
        RenderActivityHeatmap();
    }

    private void RenderTimeSpent(StatsPeriod period)
    {
        var quests = StatsRange.QuestsIn(period);
        DrawDonut(QuestDonutHost, quests);
        DrawLegend(QuestLegendPanel, quests);

        var kinds = StatsRange.KindsIn(period)
            .Select(k => new QuestBreakdownRow(Loc.T("stats.kind." + k.QuestName), k.Minutes))
            .ToList();
        DrawBarRows(KindPanel, kinds);

        var tod = StatsRange.TimeOfDayIn(period)
            .Select(r => new QuestBreakdownRow(Loc.T("stats.tod." + r.Key), r.Minutes))
            .ToList();
        DrawBarRows(TimeOfDayPanel, tod);

        RenderBlockedAttempts();
    }

    private void RenderDayByDay(StatsPeriod period)
    {
        var focus = FocusPoints(period);
        DrawLineChart(FocusPerDayChart, focus, Minutes);
        var empty = focus.All(r => r.Value == 0);
        FocusPerDayEmptyText.Text = EmptyText();
        FocusPerDayEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        FocusPerDayChart.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _ = RenderTimelineAsync(period.From.Date);
    }

    private void RenderRecords(StatsPeriod period)
    {
        var all = History.GetStats();
        BestEffortsGrid.Children.Clear();
        BestEffortsGrid.Children.Add(StatTile(Loc.T("stats.longest.all"), Minutes(all.LongestSessionMinutes)));
        BestEffortsGrid.Children.Add(StatTile(Loc.T("stats.bestday"), Minutes(all.BestDayMinutes)));
        BestEffortsGrid.Children.Add(StatTile(Loc.T("stats.beststreak"),
            string.Format(Loc.T("stats.days"), all.BestStreakDays),
            new SolidColorBrush(StreakColor)));

        var sessions = StatsRange.SessionsIn(period);
        SessionList.Items.Clear();
        foreach (var row in sessions) SessionList.Items.Add(SessionRowView(row));
        SessionListEmptyText.Text = EmptyText();
        SessionListEmptyText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement SessionRowView(SessionRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pill = new Border
        {
            Background = new SolidColorBrush(StreakColor),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Minutes(row.Minutes), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x06)),
            },
        };
        Grid.SetColumn(pill, 0);
        grid.Children.Add(pill);

        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = row.QuestName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock
        {
            Text = " · " + Loc.T("stats.kind." + row.Kind), FontSize = 12, Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (row.HardMode)
            label.Children.Add(new TextBlock
            {
                Text = " · " + Loc.T("stats.hardmode"), FontSize = 12, Opacity = 0.6,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x79, 0x6A)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var time = new TextBlock
        {
            Text = row.EndedAt.ToString("dd/MM HH:mm"), FontSize = 12, Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        return grid;
    }

    // ---------- primitives graphiques ----------

    // Courbe avec aire remplie et un point par jour. Dessinée sur un Canvas
    // en coordonnées pixels, donc re-dessinée à chaque redimensionnement :
    // les données et le formateur sont mémorisés sur le Tag de l'hôte.
    private void DrawLineChart(Grid host, List<ChartPoint> points, Func<int, string> fmt)
    {
        host.Tag = new ChartData(points, fmt);
        host.SizeChanged -= LineChart_SizeChanged;
        host.SizeChanged += LineChart_SizeChanged;
        RedrawLineChart(host);
    }

    // Axis = libellé sous l'axe, Tip = texte complet de l'infobulle.
    private sealed record ChartPoint(string Axis, string Tip, int Value);
    private sealed record ChartData(List<ChartPoint> Rows, Func<int, string> Format);

    private void LineChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid host) RedrawLineChart(host);
    }

    private void RedrawLineChart(Grid host)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        if (host.Tag is not ChartData data || data.Rows.Count == 0) return;

        var w = host.ActualWidth;
        var h = host.ActualHeight;
        // Au tout premier passage la taille n'est pas encore calculée :
        // SizeChanged rappellera cette méthode dès que WPF l'aura mesurée.
        if (w < 40 || h < 40) return;

        const double leftPad = 46, bottomPad = 22, topPad = 10, rightPad = 8;
        var plotW = w - leftPad - rightPad;
        var plotH = h - topPad - bottomPad;
        var rows = data.Rows;
        var max = Math.Max(1, rows.Max(r => r.Value));
        // Échelle arrondie vers le haut, pour une graduation lisible.
        var step = max <= 60 ? 15 : max <= 180 ? 60 : max <= 600 ? 120 : 240;
        var top = (int)(Math.Ceiling(max / (double)step) * step);

        var canvas = new Canvas();
        host.Children.Add(canvas);

        double X(int i) => leftPad + (rows.Count == 1 ? plotW / 2 : plotW * i / (rows.Count - 1.0));
        double Y(int v) => topPad + plotH * (1 - v / (double)top);

        // Graduations horizontales + libellés de l'axe
        var gridBrush = Res("ControlFillColorSecondaryBrush");
        for (var level = 0; level <= top; level += step)
        {
            var y = Y(level);
            canvas.Children.Add(new Line
            {
                X1 = leftPad, X2 = leftPad + plotW, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, Opacity = 0.5,
            });
            var label = new TextBlock
            {
                Text = data.Format(level), FontSize = 10, Opacity = 0.45,
                TextAlignment = TextAlignment.Right, Width = leftPad - 8,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            canvas.Children.Add(label);
        }

        var accent = new SolidColorBrush(StreakColor);

        // Aire sous la courbe
        var area = new PathFigure { StartPoint = new Point(X(0), Y(0)), IsClosed = true };
        for (var i = 0; i < rows.Count; i++) area.Segments.Add(new LineSegment(new Point(X(i), Y(rows[i].Value)), true));
        area.Segments.Add(new LineSegment(new Point(X(rows.Count - 1), Y(0)), true));
        canvas.Children.Add(new Path
        {
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x66, StreakColor.R, StreakColor.G, StreakColor.B),
                Color.FromArgb(0x00, StreakColor.R, StreakColor.G, StreakColor.B),
                90),
            Data = new PathGeometry([area]),
        });

        // Courbe
        var line = new Polyline { Stroke = accent, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
        foreach (var (row, i) in rows.Select((r, i) => (r, i))) line.Points.Add(new Point(X(i), Y(row.Value)));
        canvas.Children.Add(line);

        // Points : plus discrets quand le mois est dense
        var dot = rows.Count > 20 ? 3.0 : 4.5;
        for (var i = 0; i < rows.Count; i++)
        {
            var marker = new Ellipse
            {
                Width = dot * 2, Height = dot * 2,
                Fill = rows[i].Value > 0 ? accent : Res("ControlFillColorDefaultBrush"),
                Stroke = accent, StrokeThickness = 1.6,
                ToolTip = $"{rows[i].Tip} · {data.Format(rows[i].Value)}",
            };
            Canvas.SetLeft(marker, X(i) - dot);
            Canvas.SetTop(marker, Y(rows[i].Value) - dot);
            canvas.Children.Add(marker);
        }

        // Libellés de jours, un sur N quand c'est trop dense
        var labelStep = rows.Count > 16 ? (rows.Count > 24 ? 5 : 3) : 1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i % labelStep != 0 && i != rows.Count - 1) continue;
            var caption = new TextBlock
            {
                Text = rows[i].Axis, FontSize = 10, Opacity = 0.5,
                TextAlignment = TextAlignment.Center, Width = 48,
            };
            Canvas.SetLeft(caption, X(i) - 24);
            Canvas.SetTop(caption, topPad + plotH + 6);
            canvas.Children.Add(caption);
        }
    }

    // Construit les points du graphique selon l'échelle : par heure en vue
    // Jour (sinon la courbe se réduirait à un point unique), sinon par jour
    // avec le nom du jour, parce qu'un "5" nu sous l'axe ne dit rien.
    private List<ChartPoint> FocusPoints(StatsPeriod period)
    {
        var culture = Loc.Language == "fr"
            ? new System.Globalization.CultureInfo("fr-FR")
            : new System.Globalization.CultureInfo("en-US");

        if (period.Granularity == StatsGranularity.Day)
            return StatsRange.HoursIn(period)
                .Select(r => new ChartPoint(r.Date[..2] + "h", r.Date, r.Minutes))
                .ToList();

        return StatsRange.DaysIn(period).Select(r =>
        {
            var d = DateTime.Parse(r.Date);
            var axis = period.Granularity == StatsGranularity.Week
                ? Capitalize(d.ToString("ddd", culture)) + " " + d.Day
                : d.Day.ToString();
            return new ChartPoint(axis, Capitalize(d.ToString("dddd d MMMM", culture)), r.Minutes);
        }).ToList();
    }

    private List<ChartPoint> SessionPoints(StatsPeriod period)
    {
        var culture = Loc.Language == "fr"
            ? new System.Globalization.CultureInfo("fr-FR")
            : new System.Globalization.CultureInfo("en-US");
        return StatsRange.SessionsPerDayIn(period).Select(r =>
        {
            var d = DateTime.Parse(r.Date);
            var axis = period.Granularity == StatsGranularity.Week
                ? Capitalize(d.ToString("ddd", culture)) + " " + d.Day
                : d.Day.ToString();
            return new ChartPoint(axis, Capitalize(d.ToString("dddd d MMMM", culture)), r.Minutes);
        }).ToList();
    }

    // Lignes "libellé - barre - valeur", utilisées pour les types de session
    // et les moments de la journée.
    private void DrawBarRows(Panel host, List<QuestBreakdownRow> rows)
    {
        host.Children.Clear();
        var max = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Minutes)) : 1;
        if (rows.Count == 0 || rows.All(r => r.Minutes == 0))
        {
            host.Children.Add(new TextBlock
            {
                Text = EmptyText(), FontSize = 12, Opacity = 0.55,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 10),
            });
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            var name = new TextBlock { Text = row.QuestName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var track = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, row.Minutes / (double)max), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - row.Minutes / (double)max), GridUnitType.Star) });
            var fill = new Border
            {
                Background = new SolidColorBrush(SliceColors[i % SliceColors.Length]),
                CornerRadius = new CornerRadius(5),
            };
            Grid.SetColumn(fill, 0);
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            var value = new TextBlock
            {
                Text = Minutes(row.Minutes), FontSize = 12, Opacity = 0.75,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);

            host.Children.Add(grid);
        }
    }

    private static Point OnCircle(double cx, double cy, double r, double deg)
    {
        var rad = (deg - 90) * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    private void DrawDonut(Grid host, List<QuestBreakdownRow> rows)
    {
        host.Children.Clear();
        var total = rows.Sum(r => r.Minutes);
        const double size = 168, thickness = 26;
        var cx = size / 2;
        var r = (size - thickness) / 2;

        var canvas = new Canvas { Width = size, Height = size };
        host.Children.Add(canvas);

        if (total <= 0)
        {
            canvas.Children.Add(new Ellipse
            {
                Width = size - thickness, Height = size - thickness,
                Margin = new Thickness(thickness / 2),
                Stroke = Res("ControlFillColorSecondaryBrush"), StrokeThickness = thickness,
            });
            return;
        }

        // Une part unique à 100 % ne peut pas se dessiner comme un arc (début
        // et fin confondus) : c'est un cercle plein.
        if (rows.Count == 1)
        {
            canvas.Children.Add(new Ellipse
            {
                Width = size - thickness, Height = size - thickness,
                Margin = new Thickness(thickness / 2),
                Stroke = new SolidColorBrush(SliceColors[0]), StrokeThickness = thickness,
            });
        }
        else
        {
            double start = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var sweep = rows[i].Minutes / (double)total * 360.0;
                if (sweep <= 0.01) { start += sweep; continue; }
                var figure = new PathFigure { StartPoint = OnCircle(cx, cx, r, start), IsClosed = false };
                figure.Segments.Add(new ArcSegment(
                    OnCircle(cx, cx, r, start + sweep), new Size(r, r), 0,
                    sweep > 180, SweepDirection.Clockwise, true));
                canvas.Children.Add(new Path
                {
                    Stroke = new SolidColorBrush(SliceColors[i % SliceColors.Length]),
                    StrokeThickness = thickness,
                    Data = new PathGeometry([figure]),
                });
                start += sweep;
            }
        }

        var centre = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        centre.Children.Add(new TextBlock
        {
            Text = Minutes(total), FontSize = 19, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        centre.Children.Add(new TextBlock
        {
            Text = Loc.T("stats.donut.total"), FontSize = 10, Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        host.Children.Add(centre);
    }

    private void DrawLegend(Panel host, List<QuestBreakdownRow> rows)
    {
        host.Children.Clear();
        var total = rows.Sum(r => r.Minutes);
        if (total <= 0)
        {
            host.Children.Add(new TextBlock { Text = EmptyText(), FontSize = 12, Opacity = 0.55 });
            return;
        }

        for (var i = 0; i < rows.Count && i < 8; i++)
        {
            var row = rows[i];
            var line = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(SliceColors[i % SliceColors.Length]),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 0);
            line.Children.Add(dot);

            var name = new TextBlock
            {
                Text = row.QuestName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            var value = new TextBlock
            {
                Text = $"{Minutes(row.Minutes)}   {(int)Math.Round(row.Minutes * 100.0 / total)} %",
                FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            line.Children.Add(value);

            host.Children.Add(line);
        }
    }

    private bool _databaseInitialized;

    private async Task RenderTimelineAsync(DateTime selectedDate)
    {
        try
        {
            if (!_databaseInitialized)
            {
                await ActivityDatabase.InitializeAsync().ConfigureAwait(true);
                _databaseInitialized = true;
            }

            var tz = TimeZoneInfo.Local;
            ActivityTimelineCalculator.GetDayBoundaries(selectedDate, tz, out var dayStartUtc, out var dayEndUtc);

            var intervals = await ActivityDatabase.GetIntervalsAsync(
                dayStartUtc,
                dayEndUtc).ConfigureAwait(true);

            var result = ActivityTimelineCalculator.CalculateDayTimeline(selectedDate, intervals, tz);
            TimelineHost.Tag = result;
            RedrawTimeline();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[StatsPage] Error rendering activity timeline: {ex.Message}");
        }
    }

    private void TimelineHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawTimeline();
    }

    private void RedrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        TimelineRuler.Children.Clear();
        TimelineRuler.ColumnDefinitions.Clear();

        if (TimelineHost.Tag is not TimelineDayResult result)
        {
            return;
        }

        var w = TimelineHost.ActualWidth;
        var h = TimelineHost.ActualHeight;

        // Draw Hour Ruler from timezone-aware RulerTicks
        if (result.RulerTicks is { Count: > 0 } ticks && w >= 40)
        {
            var rulerCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Height = 16
            };
            TimelineRuler.Children.Add(rulerCanvas);

            // Major display ticks: start, end, every 3 hours, and repeated hours
            var displayTicks = ticks.Where(t =>
                t.Fraction <= 0.001 ||
                t.Fraction >= 0.999 ||
                t.TimeLocal.Hour % 3 == 0 ||
                ticks.Count(other => other.TimeLocal.Hour == t.TimeLocal.Hour) > 1
            ).ToList();

            foreach (var tick in displayTicks)
            {
                var hourText = new TextBlock
                {
                    Text = tick.Label,
                    FontSize = 10,
                    Opacity = 0.5,
                };

                hourText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var textW = hourText.DesiredSize.Width > 0 ? hourText.DesiredSize.Width : 28.0;

                double left;
                if (tick.Fraction <= 0.001)
                {
                    left = 0;
                }
                else if (tick.Fraction >= 0.999)
                {
                    left = Math.Max(0, w - textW);
                }
                else
                {
                    left = Math.Max(0, Math.Min(w - textW, tick.Fraction * w - textW / 2.0));
                }

                Canvas.SetLeft(hourText, left);
                Canvas.SetTop(hourText, 0);
                rulerCanvas.Children.Add(hourText);
            }
        }

        if (result.Blocks.Count == 0)
        {
            TimelineEmptyText.Visibility = Visibility.Visible;
            TimelineEmptyText.Text = Loc.Language == "fr"
                ? $"Aucune activité enregistrée pour le {result.Date:dd/MM/yyyy}."
                : $"No activity recorded for {result.Date:yyyy-MM-dd}.";
            TimelineTotalText.Text = "";
            return;
        }

        TimelineEmptyText.Visibility = Visibility.Collapsed;
        TimelineTotalText.Text = Loc.Language == "fr"
            ? $"Total : {result.FormattedTotalActive}"
            : $"Total: {result.FormattedTotalActive}";

        if (w < 40 || h < 20) return;

        foreach (var block in result.Blocks)
        {
            var left = block.StartFraction * w;
            var blockWidth = (block.EndFraction - block.StartFraction) * w;
            if (blockWidth < 2.0) blockWidth = 2.0;
            if (left + blockWidth > w) blockWidth = Math.Max(1.0, w - left);

            var color = (Color)ColorConverter.ConvertFromString(block.ColorHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var rect = new Border
            {
                Width = blockWidth,
                Height = Math.Max(10, h - 8),
                Background = brush,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = CreateBlockTooltip(block),
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, 0);
            TimelineCanvas.Children.Add(rect);
        }
    }

    private static ToolTip CreateBlockTooltip(TimelineBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };

        panel.Children.Add(new TextBlock
        {
            Text = block.ProcessName,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Category: {block.Category}",
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 2)
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Active Duration: {block.FormattedDuration}",
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 2)
        });

        bool isAmbiguous = block.StartTimeLocal.Offset != block.EndTimeLocal.Offset;
        string rangeText = isAmbiguous
            ? $"{block.StartTimeLocal:HH:mm:ss (zzz)} - {block.EndTimeLocal:HH:mm:ss (zzz)}"
            : $"{block.StartTimeLocal:HH:mm:ss} - {block.EndTimeLocal:HH:mm:ss}";

        panel.Children.Add(new TextBlock
        {
            Text = rangeText,
            FontSize = 11,
            Opacity = 0.6
        });

        return new ToolTip { Content = panel };
    }
}
