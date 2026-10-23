using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.ClaudeCode;

/// Today's tokens, output, tool calls and sessions, and a 16-week streak grid. Reads again every minute while open.
public sealed class ClaudeCodeModule : ContentControl
{
    private readonly ClaudeCodeStats _stats = ClaudeCodeStats.Shared;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };

    public ClaudeCodeModule()
    {
        _timer.Tick += (_, _) => Reload();
        Render();
        Loaded += (_, _) =>
        {
            _stats.Changed += Render;
            Reload();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _stats.Changed -= Render;
            _timer.Stop();
        };
    }

    private Color Accent => AppSettings.Shared.Theme.AccentFor(ModuleId.Claude);

    private void Reload()
    {
        if (ClaudeCodeStats.RootExists) _stats.Refresh();
        else Render();
    }

    private void Render()
    {
        if (!ClaudeCodeStats.RootExists)
        {
            Content = Ui.EmptyState("\uE943", "No Claude Code data yet",
                "Usage appears here once %USERPROFILE%\\.claude\\projects has transcripts.");
            return;
        }
        if (!_stats.HasLoaded)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var spinner = Spinner(12);
            spinner.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(spinner);
            var label = Ui.Text("Reading transcripts…", 12, ThemeResources.SecondaryText);
            label.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(label);
            Content = stack;
            return;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(TodayCard());
        var streak = StreakCard();
        Grid.SetColumn(streak, 2);
        grid.Children.Add(streak);
        Content = grid;
    }

    private UIElement TodayCard()
    {
        var body = new DockPanel();

        var footer = new DockPanel();
        var folder = Ui.Glyph("\uE8B7", 11, ThemeResources.SecondaryText);
        folder.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(folder, Dock.Left);
        footer.Children.Add(folder);
        if (_stats.LastActivity is { } last)
        {
            var ago = Format.Relative(last);
            var when = Ui.Text(ago == "now" ? "active now" : $"{ago} ago", 11, ThemeResources.SecondaryText);
            when.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(when, Dock.Right);
            footer.Children.Add(when);
        }
        var project = Ui.Text(_stats.Project ?? "No recent project", 12, ThemeResources.SecondaryText, FontWeights.Medium);
        project.TextWrapping = TextWrapping.NoWrap;
        project.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(project);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        var top = new StackPanel();
        var tokens = Ui.Text(CompactNumber.Format(_stats.TodayTokens), 44, ThemeResources.Accent, FontWeights.Bold).Tabular();
        tokens.TextWrapping = TextWrapping.NoWrap;
        if (AppSettings.Shared.Theme.Glow) tokens.Effect = ExpandedView.Glow(Accent, 5);
        top.Children.Add(tokens);
        top.Children.Add(Ui.Text("TOKENS", 10, ThemeResources.SecondaryText, FontWeights.SemiBold));
        var stats = new UniformGrid { Rows = 1, Margin = new Thickness(-4, 10, -4, 0) };
        stats.Children.Add(Stat(CompactNumber.Format(_stats.TodayOutput), "Output"));
        stats.Children.Add(Stat(_stats.TodayTools.ToString(CultureInfo.CurrentCulture), "Tool calls"));
        stats.Children.Add(Stat(_stats.TodaySessions.ToString(CultureInfo.CurrentCulture), "Sessions"));
        top.Children.Add(stats);
        body.Children.Add(top);

        return new Card("Today", "\uE945", body, _stats.IsLoading ? Spinner(10) : null);
    }

    private static UIElement Stat(string value, string label)
    {
        var stack = new StackPanel();
        var number = Ui.Text(value, 17, weight: FontWeights.SemiBold).Tabular();
        number.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(number);
        var caption = Ui.Text(label.ToUpperInvariant(), 9, ThemeResources.SecondaryText, FontWeights.SemiBold);
        caption.TextWrapping = TextWrapping.NoWrap;
        caption.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(caption);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(4, 0, 4, 0),
            Child = stack,
        };
        chip.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return chip;
    }

    private UIElement StreakCard()
    {
        var streak = _stats.Streak;
        var accessory = new StackPanel { Orientation = Orientation.Horizontal };
        var flame = Flame(streak > 0);
        flame.Margin = new Thickness(0, 0, 4, 0);
        accessory.Children.Add(flame);
        accessory.Children.Add(Ui.Text($"{streak} day streak", 11, ThemeResources.SecondaryText, FontWeights.SemiBold));
        return new Card("Last 16 weeks", "\uE8A9", new StreakGrid(_stats.Daily, Accent), accessory);
    }

    private static readonly Geometry FlameShape =
        Geometry.Parse("M 6,0 C 7,4 12,6 12,11 A 6,6 0 0 1 0,11 C 0,8 2,6.5 3,4.5 C 3.5,6.5 4.5,7.5 5.5,7.5 C 5,5 5,2.5 6,0 Z");

    /// Segoe has no flame glyph, so it is drawn.
    private static Path Flame(bool lit)
    {
        var path = new Path { Data = FlameShape, Stretch = Stretch.Uniform, Height = 11, Width = 11 * 12 / 17.0, VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(Shape.FillProperty, lit ? ThemeResources.Accent : ThemeResources.SecondaryText);
        return path;
    }

    /// The refresh arrow, turning, as a spinner.
    private static TextBlock Spinner(double size)
    {
        var glyph = Ui.Glyph("\uE72C", size, ThemeResources.SecondaryText);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform();
        glyph.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return glyph;
    }
}

/// GitHub-style grid of tokens per day: 16 week columns, a row per weekday, darker for busier days.
internal sealed class StreakGrid : DockPanel
{
    private const int Weeks = 16;
    private const double Spacing = 4;

    private readonly Canvas _cells = new() { ClipToBounds = true };
    private readonly List<Border> _squares = new();

    public StreakGrid(IReadOnlyDictionary<DateTime, long> daily, Color accent)
    {
        var theme = AppSettings.Shared.Theme;
        var empty = theme.Text.WithOpacity(0.08).Brush();
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;
        var offset = ((int)today.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var firstColumn = today.AddDays(-offset).AddDays(-7 * (Weeks - 1));
        var inRange = daily.Where(p => p.Key >= firstColumn).ToList();
        var peak = Math.Max(1, inRange.Select(p => p.Value).DefaultIfEmpty(1).Max());
        var total = inRange.Sum(p => p.Value);
        var activeDays = inRange.Count(p => p.Value > 0);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        legend.Children.Add(Small("Less"));
        foreach (var level in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            legend.Children.Add(new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(3, 0, 0, 0),
                Background = level == 0 ? empty : accent.WithOpacity(0.2 + 0.8 * level).Brush(),
            });
        var more = Small("More");
        more.Margin = new Thickness(3, 0, 0, 0);
        legend.Children.Add(more);
        DockPanel.SetDock(legend, Dock.Right);
        footer.Children.Add(legend);
        var summary = Small($"{CompactNumber.Format(total)} tokens · {activeDays} active days");
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(summary);
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        for (var column = 0; column < Weeks; column++)
            for (var row = 0; row < 7; row++)
            {
                var day = firstColumn.AddDays(column * 7 + row);
                var square = new Border { CornerRadius = new CornerRadius(3) };
                var value = daily.GetValueOrDefault(day);
                if (day > today)
                {
                    square.Background = Brushes.Transparent;
                }
                else
                {
                    if (value == 0)
                    {
                        square.Background = empty;
                        if (day == today) square.BorderBrush = accent.WithOpacity(0.6).Brush();
                    }
                    else
                    {
                        // Square-root scale so one huge day doesn't wash out the rest.
                        var level = Math.Sqrt((double)value / peak);
                        square.Background = accent.WithOpacity(0.2 + 0.8 * level).Brush();
                        if (day == today) square.BorderBrush = theme.Text.WithOpacity(0.8).Brush();
                    }
                    if (day == today) square.BorderThickness = new Thickness(1);
                    square.ToolTip = $"{day.ToString("ddd, MMM d", culture)}: {CompactNumber.Format(value)} tokens";
                }
                square.Tag = (column, row);
                _squares.Add(square);
                _cells.Children.Add(square);
            }
        _cells.SizeChanged += (_, e) => Layout(e.NewSize);
        Children.Add(_cells);
    }

    /// Squares as big as both directions allow, the whole grid centred.
    private void Layout(Size size)
    {
        var side = Math.Max(6, Math.Min((size.Width - Spacing * (Weeks - 1)) / Weeks, (size.Height - Spacing * 6) / 7));
        var left = Math.Max(0, (size.Width - (side * Weeks + Spacing * (Weeks - 1))) / 2);
        var top = Math.Max(0, (size.Height - (side * 7 + Spacing * 6)) / 2);
        foreach (var square in _squares)
        {
            var (column, row) = ((int, int))square.Tag;
            square.Width = side;
            square.Height = side;
            Canvas.SetLeft(square, left + column * (side + Spacing));
            Canvas.SetTop(square, top + row * (side + Spacing));
        }
    }

    private static TextBlock Small(string text)
    {
        var block = Ui.Text(text, 10, ThemeResources.SecondaryText);
        block.TextWrapping = TextWrapping.NoWrap;
        return block;
    }
}
