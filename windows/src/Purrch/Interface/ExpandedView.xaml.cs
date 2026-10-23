using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// The open panel: header, the selected module, and the tab bar.
public partial class ExpandedView : UserControl
{
    private readonly NotchState _state = NotchState.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly TabBar _tabBar = new();
    private string? _contentKey;
    private UIElement? _content;

    public ExpandedView()
    {
        InitializeComponent();
        TabBarArea.Children.Add(_tabBar);
        _state.PropertyChanged += OnStateChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        RefreshHeader();
        RefreshPin();
        RefreshSettingsButton();
        RefreshContent(animated: false);
        RefreshOnboarding();
    }

    /// Staggered entrance each time the panel opens: header, then content, then the tab bar.
    public void PlayEntrance()
    {
        Header.Opacity = 0;
        HeaderShift.Y = -8;
        Header.Animate(OpacityProperty, 1, Motion.ExpoOut(0.6), delay: 0.07);
        HeaderShift.Animate(TranslateTransform.YProperty, 0, Motion.ExpoOut(0.6), delay: 0.07);

        ContentArea.Opacity = 0;
        ContentScale.ScaleX = ContentScale.ScaleY = 0.965;
        ContentArea.Animate(OpacityProperty, 1, Motion.ExpoOut(0.6), delay: 0.07);
        ContentScale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.6), delay: 0.07);
        ContentScale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.6), delay: 0.07);

        TabBarArea.Opacity = 0;
        TabBarShift.Y = 18;
        TabBarArea.Animate(OpacityProperty, 1, Motion.ExpoOut(0.6), delay: 0.1);
        TabBarShift.Animate(TranslateTransform.YProperty, 0, Motion.Open, delay: 0.1);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NotchState.Selected):
            case nameof(NotchState.ShowSettings):
                RefreshHeader();
                RefreshSettingsButton();
                RefreshContent(animated: true);
                break;
            case nameof(NotchState.IsPinned):
                RefreshPin();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ThemeId):
                RefreshHeader();
                RefreshPin();
                // Module content picks colours from the theme when it is built. Settings and the
                // welcome screen hold the theme picker, which recolours itself and must stay put.
                if (_contentKey is not ("settings" or "onboarding")) RefreshContent(animated: false, force: true);
                break;
            case nameof(AppSettings.HasCompletedOnboarding):
                RefreshContent(animated: true);
                RefreshOnboarding();
                break;
        }
    }

    private void RefreshHeader()
    {
        var theme = _settings.Theme;
        var module = _state.Selected;
        BadgeGlyph.Text = _state.ShowSettings ? "\uE713" : module.Glyph();
        BadgeGlyph.SetResourceReference(TextBlock.ForegroundProperty, theme.Glow ? ThemeResources.Background : ThemeResources.Accent);
        Badge.Effect = theme.Glow ? Glow(theme.AccentFor(module), 5) : null;
        TitleText.Text = _state.ShowSettings ? "Settings" : module.Title();
        SubtitleText.Text = _state.ShowSettings ? "Themes, modules, AI keys and startup"
            : module == ModuleId.Home ? DateTime.Now.ToString("dddd, MMMM d")
            : module.Subtitle();
    }

    /// Pinned is the one state that stops the panel closing on hover-out, so make it unmistakable.
    private void RefreshPin()
    {
        var pinned = _state.IsPinned;
        PinGlyph.Text = pinned ? "\uE840" : "\uE718";
        PinLabel.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        PinPill.Padding = new Thickness(pinned ? 10 : 0, 0, pinned ? 10 : 0, 0);
        if (pinned) PinPill.SetResourceReference(Border.BackgroundProperty, ThemeResources.ThemeAccent);
        else PinPill.Background = Brushes.Transparent;
        var foreground = pinned ? ThemeResources.Background : ThemeResources.Text;
        PinGlyph.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        PinLabel.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        PinButton.ToolTip = pinned ? "Pinned: stays open. Click to let it close when you move away." : "Keep open";
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => _state.IsPinned = !_state.IsPinned;

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _state.SwitchDirection = 1;
        _state.ShowSettings = !_state.ShowSettings;
    }

    private void RefreshSettingsButton()
    {
        SettingsGlyph.Text = _state.ShowSettings ? "\uE711" : "\uE713";
        SettingsSpin.Animate(RotateTransform.AngleProperty, _state.ShowSettings ? 90 : 0, Motion.ExpoOut(0.45));
    }

    /// The tab bar waits until the welcome screen has been dismissed.
    private void RefreshOnboarding() =>
        TabBarArea.Visibility = _settings.HasCompletedOnboarding ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshContent(bool animated, bool force = false)
    {
        var key = !_settings.HasCompletedOnboarding ? "onboarding"
            : _state.ShowSettings ? "settings"
            : _state.Selected.Key();
        if (key == _contentKey && !force) return;
        _contentKey = key;

        var next = BuildContent();
        var previous = _content;
        _content = next;
        ContentArea.Children.Add(next);

        if (previous == null) return;
        if (!animated)
        {
            ContentArea.Children.Remove(previous);
            return;
        }

        // Offset + fade, sliding in from the side the tab change moved towards.
        var shift = new TranslateTransform(34 * _state.SwitchDirection, 0);
        next.RenderTransform = shift;
        next.Opacity = 0;
        shift.Animate(TranslateTransform.XProperty, 0, Motion.ExpoOut(0.45));
        next.Animate(OpacityProperty, 1, Motion.ExpoOut(0.45));
        previous.IsHitTestVisible = false;
        previous.Animate(OpacityProperty, 0, Motion.ExpoOut(0.2), completed: () => ContentArea.Children.Remove(previous));
    }

    private UIElement BuildContent()
    {
        if (!_settings.HasCompletedOnboarding) return new OnboardingView();
        if (_state.ShowSettings) return new SettingsView();
        return _state.Selected switch
        {
            ModuleId.Timers => new Features.Timers.TimersModule(),
            ModuleId.Clipboard => new Features.Clipboard.ClipboardModule(),
            ModuleId.Notes => new Features.Notes.NotesModule(),
            ModuleId.Scratchpad => new Features.Notes.ScratchpadModule(),
            ModuleId.Emoji => new Features.Emoji.EmojiModule(),
            ModuleId.Units => new Features.Units.UnitsModule(),
            ModuleId.Links => new Features.Links.LinksModule(),
            ModuleId.NowPlaying => new Features.Media.NowPlayingModule(),
            ModuleId.Message => new Features.Media.MessageModule(),
            ModuleId.Ambience => new Features.Media.AmbienceModule(),
            ModuleId.System => new Features.Insights.SystemModule(),
            ModuleId.Assistant => new Features.Assistant.AssistantModule(),
            ModuleId.Translate => new Features.Translate.TranslateModule(),
            _ => new ModulePlaceholder(_state.Selected, _settings.Theme.Glow),
        };
    }

    internal static System.Windows.Media.Effects.DropShadowEffect Glow(Color color, double radius) => new()
    {
        Color = color,
        BlurRadius = radius * 2,
        ShadowDepth = 0,
        Opacity = 0.6,
    };
}
