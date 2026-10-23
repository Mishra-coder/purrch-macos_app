using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Revenue;

/// Today's Stripe total, the last seven days as bars, and today's sales, from <see cref="RevenueService"/>.
public sealed class RevenueModule : ContentControl
{
    private readonly RevenueService _service = RevenueService.Shared;

    public RevenueModule()
    {
        Render();
        Loaded += (_, _) =>
        {
            _service.Changed += Render;
            _service.Start();
            Render();
        };
        Unloaded += (_, _) =>
        {
            _service.Changed -= Render;
            _service.Stop();
        };
    }

    private void Render()
    {
        if (!RevenueService.HasKey)
        {
            Content = MissingKey();
            return;
        }
        var s = _service.KeyRejected ? null : _service.Snapshot;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        left.RowDefinitions.Add(new RowDefinition());
        left.Children.Add(TotalCard(s));
        var week = WeekCard(s);
        Grid.SetRow(week, 2);
        left.Children.Add(week);
        grid.Children.Add(left);

        var sales = SalesCard(s);
        Grid.SetColumn(sales, 2);
        grid.Children.Add(sales);
        Content = grid;
    }

    private static UIElement MissingKey()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.EmptyState("\uE8C7", "Connect Stripe",
            "Add a restricted, read-only Stripe key (Charges: read) in Settings."));
        var open = Ui.Pill("Open Settings", "\uE713", () => SettingsView.Open(SettingsView.Section.Integrations), prominent: true);
        open.HorizontalAlignment = HorizontalAlignment.Center;
        open.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(open);
        return stack;
    }

    private static string CurrencyCode(RevenueSnapshot? s) => (s?.Currency ?? RevenueService.Currency).ToUpperInvariant();

    private static string Money(RevenueSnapshot? s, double value) => RevenueService.Money(value, s?.Currency ?? RevenueService.Currency);

    /// "Today", or the weekday when the copy on screen is from an earlier day (offline overnight).
    private static string DayName(RevenueSnapshot? s) =>
        s == null || s.FetchedAt.Date == DateTime.Today ? "Today" : s.FetchedAt.ToString("dddd", CultureInfo.CurrentCulture);

    // MARK: Today

    private UIElement TotalCard(RevenueSnapshot? s)
    {
        UIElement? accessory = null;
        if (_service.IsLoading) accessory = Spinner(true, 11);
        else if (s != null) accessory = Updated(s.FetchedAt, _service.Message);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var total = Ui.Text(Money(s, s?.TodayTotal() ?? 0), 36, ThemeResources.Accent, FontWeights.Bold).Tabular();
        total.TextWrapping = TextWrapping.NoWrap;
        if (AppSettings.Shared.Theme.Glow && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent)
            total.Effect = ExpandedView.Glow(accent.Color, 5);
        stack.Children.Add(new Viewbox { Child = total, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        var count = s?.Sales.Count ?? 0;
        var detail = Ui.Text($"{count} sale{(count == 1 ? "" : "s")} · {CurrencyCode(s)}", 12, ThemeResources.SecondaryText);
        detail.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(detail);
        return new Card(DayName(s), "\uE8C7", stack, accessory);
    }

    /// When the figures were fetched. A warning sign marks a copy the last refresh couldn't replace.
    private static UIElement Updated(DateTime fetched, string? problem)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        if (problem != null)
        {
            var warning = Ui.Glyph("\uE7BA", 10, ThemeResources.SecondaryText);
            warning.Margin = new Thickness(0, 0, 5, 0);
            row.Children.Add(warning);
            row.ToolTip = problem + "\nShowing the last copy.";
        }
        var format = fetched.Date == DateTime.Today ? "t" : "ddd t";
        row.Children.Add(Ui.Text(fetched.ToString(format, CultureInfo.CurrentCulture), 11, ThemeResources.SecondaryText).Tabular());
        return row;
    }

    // MARK: Seven days

    private static UIElement WeekCard(RevenueSnapshot? s)
    {
        var week = s?.Week ?? new List<RevenueSnapshot.Day>();
        var peak = Math.Max(week.Count == 0 ? 0 : week.Max(d => d.Total), 0.01);
        var accessory = Ui.Text(Money(s, week.Sum(d => d.Total)), 11, ThemeResources.SecondaryText).Tabular();

        var columns = new UniformGrid { Rows = 1 };
        foreach (var day in week)
        {
            var current = day.Date == week[^1].Date;
            var column = new DockPanel { Margin = new Thickness(3, 0, 3, 0), Background = Brushes.Transparent };
            var letter = Ui.Text(day.Date.ToString("ddd", CultureInfo.CurrentCulture)[..1], 9,
                current ? ThemeResources.Text : ThemeResources.SecondaryText, FontWeights.SemiBold);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            letter.Margin = new Thickness(0, 4, 0, 0);
            DockPanel.SetDock(letter, Dock.Bottom);
            column.Children.Add(letter);
            column.Children.Add(Bar(day.Total / peak, current));
            column.ToolTip = $"{day.Date.ToString("dddd", CultureInfo.CurrentCulture)}: {Money(s, day.Total)}";
            columns.Children.Add(column);
        }
        return new Card("7 days", "\uE787", columns, accessory);
    }

    /// One day's bar, its share of the week's best day; star rows do the sizing. Always at least 3 pixels.
    private static Grid Bar(double fraction, bool current)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - f, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(f, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
        var bar = new Border { CornerRadius = new CornerRadius(4), Opacity = current ? 1 : 0.45 };
        bar.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        Grid.SetRow(bar, 1);
        Grid.SetRowSpan(bar, 2);
        grid.Children.Add(bar);
        return grid;
    }

    // MARK: Sales

    private UIElement SalesCard(RevenueSnapshot? s)
    {
        var refresh = Ui.Bare(Spinner(_service.IsLoading, 11), () => _service.Refresh(force: true), "Refresh");
        return new Card("Sales", "\uE8FD", SalesBody(s), refresh);
    }

    private UIElement SalesBody(RevenueSnapshot? s)
    {
        if (s == null && _service.Message is { } message && !_service.IsLoading)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Ui.EmptyState("\uE7BA", "Couldn't load Stripe", message));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(Ui.Pill("Try again", "\uE72C", () => _service.Refresh(force: true), prominent: !_service.KeyRejected));
            var settings = Ui.Pill("Open Settings", "\uE713", () => SettingsView.Open(SettingsView.Section.Integrations), prominent: _service.KeyRejected);
            settings.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(settings);
            stack.Children.Add(buttons);
            return stack;
        }
        if (s == null)
        {
            var spinner = Spinner(true, 16);
            spinner.HorizontalAlignment = HorizontalAlignment.Center;
            spinner.VerticalAlignment = VerticalAlignment.Center;
            return spinner;
        }
        if (s.Sales.Count == 0)
            return Ui.EmptyState("\uE7BF", $"No sales yet {(DayName(s) == "Today" ? "today" : "that day")}",
                "New payments show up within a minute.");

        var list = new StackPanel();
        foreach (var sale in s.Sales) list.Children.Add(SaleRow(s, sale));
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
    }

    private static UIElement SaleRow(RevenueSnapshot s, RevenueSnapshot.Sale sale)
    {
        var row = new DockPanel();
        // A filled coin with the currency symbol, the counterpart of the Mac's dollarsign.circle.fill.
        var symbol = RevenueService.Money(0, s.Currency).Replace("0", "").Trim();
        var coinText = Ui.Text(symbol.Length is > 0 and <= 2 ? symbol : "\u00A4", 10, ThemeResources.Background, FontWeights.Bold);
        coinText.HorizontalAlignment = HorizontalAlignment.Center;
        var coin = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Child = coinText, Margin = new Thickness(0, 0, 10, 0) };
        coin.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        DockPanel.SetDock(coin, Dock.Left);
        row.Children.Add(coin);

        var amount = Ui.Text(Money(s, sale.Amount), 14, weight: FontWeights.SemiBold).Tabular();
        amount.TextWrapping = TextWrapping.NoWrap;
        amount.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(amount, Dock.Right);
        row.Children.Add(amount);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text(sale.Label, 12, weight: FontWeights.Medium);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.ToolTip = sale.Label;
        text.Children.Add(label);
        var time = Ui.Text(sale.Created.ToString("t", CultureInfo.CurrentCulture), 10, ThemeResources.SecondaryText).Tabular();
        time.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(time);
        row.Children.Add(text);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 5),
            Child = row,
        };
        card.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return card;
    }

    /// The refresh arrow; it turns while a fetch is running.
    private static TextBlock Spinner(bool spinning, double size)
    {
        var glyph = Ui.Glyph("\uE72C", size, ThemeResources.SecondaryText);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform();
        glyph.RenderTransform = rotate;
        if (spinning)
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return glyph;
    }
}
