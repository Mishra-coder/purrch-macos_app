using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// First-run welcome: what Purrch is, how to open and close it, and a theme to start with.
public sealed class OnboardingView : Grid
{
    public OnboardingView()
    {
        Margin = new Thickness(0, 6, 0, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel();
        var start = Ui.Pill("Get started", "\uE72A", () => AppSettings.Shared.HasCompletedOnboarding = true, prominent: true);
        DockPanel.SetDock(start, Dock.Bottom);
        left.Children.Add(start);

        var intro = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Mascot.png")),
            Width = 56,
            Height = 48,
            Stretch = Stretch.Uniform,
        });
        var heading = Ui.Text("Welcome to Purrch", 22, weight: FontWeights.Bold);
        heading.Margin = new Thickness(10, 0, 0, 0);
        title.Children.Add(heading);
        intro.Children.Add(title);

        var blurb = Ui.Text("The top of your screen is now a small control centre. Hover over the pill to open it, move away to close.",
            13, ThemeResources.SecondaryText);
        blurb.Margin = new Thickness(0, 10, 0, 10);
        intro.Children.Add(blurb);
        intro.Children.Add(Bullet("\uE72E", "Everything stays on your PC"));
        intro.Children.Add(Bullet("\uE718", "Esc closes, the pin keeps it open"));
        intro.Children.Add(Bullet("\uE713", "Gear, then Modules, hides tools you don't need"));
        left.Children.Add(intro);
        Children.Add(left);

        var right = new DockPanel();
        var pick = Ui.Text("Pick a look", 13, weight: FontWeights.SemiBold);
        pick.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(pick, Dock.Top);
        right.Children.Add(pick);
        right.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden,
            Content = new ThemePicker(columns: 2),
        });
        SetColumn(right, 2);
        Children.Add(right);
    }

    private static UIElement Bullet(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(Ui.Glyph(glyph, 12, ThemeResources.Accent));
        var label = Ui.Text(text, 12);
        label.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }
}
