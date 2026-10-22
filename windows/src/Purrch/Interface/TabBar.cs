using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// The module dock. Icons magnify under the pointer like the macOS Dock and show their name;
/// drag one sideways to reorder, the others slide out of the way.
public sealed class TabBar : Border
{
    private const double Slot = 31;
    private const double SlotHeight = 34;
    private const double DragThreshold = 5;

    private readonly NotchState _state = NotchState.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly Canvas _canvas = new() { Height = SlotHeight };
    private readonly Border _selection = new() { Width = 28, Height = 26, CornerRadius = new CornerRadius(9) };
    private readonly Border _label = new() { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 9, 4), IsHitTestVisible = false };
    private readonly TextBlock _labelText = Ui.Text("", 11, weight: FontWeights.SemiBold);
    private readonly DispatcherTimer _hoverSelect = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<TabButton> _buttons = new();

    private List<ModuleId> _modules = new();
    /// Live order while a drag is in progress; null otherwise.
    private List<ModuleId>? _draft;
    private TabButton? _pressed;
    private Point _pressPoint;
    private int _dragStartIndex;
    private bool _dragging;
    private int? _hovered;

    public TabBar()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(6, 3, 6, 3);
        BorderThickness = new Thickness(0.8);
        SetResourceReference(BackgroundProperty, ThemeResources.Surface);
        SetResourceReference(BorderBrushProperty, ThemeResources.Hairline);
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 12, ShadowDepth = 3, Direction = 270 };

        _label.Child = _labelText;
        _label.SetResourceReference(BackgroundProperty, ThemeResources.Surface);
        _label.BorderThickness = new Thickness(0.6);
        _label.SetResourceReference(BorderBrushProperty, ThemeResources.Hairline);
        _label.Visibility = Visibility.Collapsed;
        Panel.SetZIndex(_label, 10);
        Panel.SetZIndex(_selection, -1);

        Child = _canvas;

        _hoverSelect.Tick += (_, _) => SelectHovered();
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeave += (_, _) => { if (!_dragging) SetHovered(null); };
        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.LostMouseCapture += (_, _) => EndDrag(commit: _dragging);
        // A transparent fill so the gaps between icons still receive the mouse.
        _canvas.Background = Brushes.Transparent;

        _state.PropertyChanged += OnStateChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Rebuild();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotchState.Selected) or nameof(NotchState.ShowSettings)) UpdateSelection(animated: true);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ModuleOrder) or nameof(AppSettings.EnabledModules) or nameof(AppSettings.ThemeId))
        {
            if (!_dragging) Rebuild();
        }
    }

    private void Rebuild()
    {
        _modules = _settings.EnabledModules.ToList();
        _canvas.Children.Clear();
        _buttons.Clear();
        _canvas.Width = _modules.Count * Slot;
        _canvas.Children.Add(_selection);
        foreach (var module in _modules)
        {
            var button = new TabButton(module, _settings.Theme);
            _buttons.Add(button);
            _canvas.Children.Add(button);
        }
        _canvas.Children.Add(_label);
        Layout(animated: false);
        UpdateSelection(animated: false);
    }

    /// Places every icon in its slot of the current (possibly draft) order.
    private void Layout(bool animated)
    {
        var order = _draft ?? _modules;
        foreach (var button in _buttons)
        {
            if (_dragging && button == _pressed) continue;
            var x = order.IndexOf(button.Module) * Slot;
            if (animated) button.Animate(Canvas.LeftProperty, x, Motion.Pop);
            else button.Set(Canvas.LeftProperty, x);
        }
    }

    private void UpdateSelection(bool animated)
    {
        var theme = _settings.Theme;
        var selected = _state.ShowSettings ? (ModuleId?)null : _state.Selected;
        foreach (var button in _buttons) button.SetSelected(button.Module == selected, theme);

        var index = selected is { } m ? (_draft ?? _modules).IndexOf(m) : -1;
        if (index < 0)
        {
            _selection.Animate(OpacityProperty, 0, Motion.ExpoOut(0.3));
            return;
        }
        var tint = theme.AccentFor(selected!.Value);
        _selection.Background = (theme.Glow ? tint : tint.WithOpacity(0.2)).Brush();
        _selection.Effect = new DropShadowEffect { Color = tint, BlurRadius = 16, ShadowDepth = 0, Opacity = theme.Glow ? 0.7 : 0.35 };
        Canvas.SetTop(_selection, (SlotHeight - _selection.Height) / 2);
        var x = index * Slot + (Slot - _selection.Width) / 2;
        // The highlight glides to the new tab, like SwiftUI's matched geometry.
        if (animated && _selection.Opacity > 0) _selection.Animate(Canvas.LeftProperty, x, Motion.Pop);
        else _selection.Set(Canvas.LeftProperty, x);
        _selection.Animate(OpacityProperty, 1, Motion.ExpoOut(0.3));
    }

    // MARK: - Hover

    private int IndexAt(Point p) => Math.Clamp((int)Math.Floor(p.X / Slot), 0, Math.Max(0, _modules.Count - 1));

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_pressed != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && Math.Abs(p.X - _pressPoint.X) > DragThreshold) BeginDrag();
            if (_dragging) { Drag(p.X - _pressPoint.X); return; }
        }
        SetHovered(IndexAt(p));
    }

    private void SetHovered(int? index)
    {
        if (index == _hovered) return;
        _hovered = index;
        for (var i = 0; i < _buttons.Count; i++)
        {
            var at = _modules.IndexOf(_buttons[i].Module);
            _buttons[i].SetMagnification(Magnification(at));
        }

        _hoverSelect.Stop();
        if (index is not { } hovered)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        ShowLabel(hovered);
        // A short dwell, so sweeping across the dock doesn't flick through modules.
        _hoverSelect.Start();
    }

    /// Dock-style magnification: the hovered icon grows most, its neighbours a little.
    private double Magnification(int index)
    {
        if (_hovered is not { } hovered || _dragging) return 1;
        return Math.Abs(index - hovered) switch { 0 => 1.3, 1 => 1.12, 2 => 1.04, _ => 1 };
    }

    private void ShowLabel(int index)
    {
        _labelText.Text = _modules[index].Title();
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _label.DesiredSize.Width;
        Canvas.SetLeft(_label, index * Slot + Slot / 2 - width / 2);
        Canvas.SetTop(_label, -34);
        _label.Opacity = 0;
        _label.Animate(OpacityProperty, 1, Motion.ExpoOut(0.25));
    }

    private void SelectHovered()
    {
        _hoverSelect.Stop();
        if (_dragging || _hovered is not { } index || index >= _modules.Count) return;
        var module = _modules[index];
        if (_state.Selected != module || _state.ShowSettings) _state.Select(module);
    }

    // MARK: - Click and drag

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        var index = IndexAt(p);
        if (index >= _modules.Count) return;
        _pressed = _buttons.First(b => b.Module == _modules[index]);
        _pressPoint = p;
        _dragStartIndex = index;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        var pressed = _pressed;
        _canvas.ReleaseMouseCapture();
        // A plain click (no drag) selects.
        if (!wasDragging && pressed != null) _state.Select(pressed.Module);
        _pressed = null;
        e.Handled = true;
    }

    private void BeginDrag()
    {
        if (_pressed == null) return;
        _dragging = true;
        _draft = _modules.ToList();
        _hoverSelect.Stop();
        _label.Visibility = Visibility.Collapsed;
        _hovered = null;
        foreach (var b in _buttons) b.SetMagnification(1);
        _pressed.SetLifted(true);
        Panel.SetZIndex(_pressed, 5);
    }

    private void Drag(double dx)
    {
        if (_pressed == null || _draft == null) return;
        _pressed.Set(Canvas.LeftProperty, Math.Clamp(_dragStartIndex * Slot + dx, -Slot / 2, (_modules.Count - 0.5) * Slot));
        var current = _draft.IndexOf(_pressed.Module);
        var target = Math.Clamp(_dragStartIndex + (int)Math.Round(dx / Slot), 0, _draft.Count - 1);
        if (target == current) return;
        _draft.RemoveAt(current);
        _draft.Insert(target, _pressed.Module);
        Layout(animated: true);
        UpdateSelection(animated: true);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging) { _pressed = null; return; }
        var order = _draft;
        var pressed = _pressed;
        _dragging = false;
        _pressed = null;
        if (pressed != null)
        {
            pressed.SetLifted(false);
            Panel.SetZIndex(pressed, 0);
        }
        if (commit && order != null)
        {
            _draft = null;
            _settings.ReorderEnabled(order); // rebuilds through OnSettingsChanged
        }
        else
        {
            _draft = null;
            Layout(animated: true);
            UpdateSelection(animated: true);
        }
    }

    /// One icon in the dock.
    private sealed class TabButton : Grid
    {
        private readonly TextBlock _glyph;
        private readonly Ellipse _dot = new() { Width = 3.5, Height = 3.5, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0 };
        private readonly Border _lift = new() { Width = 28, Height = 26, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Opacity = 0 };
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _shift = new();
        private bool _selected;
        private double _magnification = 1;

        public ModuleId Module { get; }

        public TabButton(ModuleId module, Theme theme)
        {
            Module = module;
            Width = Slot;
            Height = SlotHeight;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            RenderTransformOrigin = new Point(0.5, 1);
            RenderTransform = new TransformGroup { Children = { _scale, _shift } };

            _lift.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
            _lift.BorderBrush = theme.AccentFor(module).WithOpacity(0.7).Brush();
            _lift.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.5, BlurRadius = 16, ShadowDepth = 4, Direction = 270 };
            Children.Add(_lift);

            _glyph = Ui.Glyph(module.Glyph(), 13.5);
            Children.Add(_glyph);

            // Running indicator under the selected icon, like the Dock.
            _dot.Fill = theme.AccentFor(module).Brush();
            _dot.Margin = new Thickness(0, 0, 0, 0.5);
            Children.Add(_dot);
            SetSelected(false, theme);
        }

        public void SetSelected(bool selected, Theme theme)
        {
            var wasSelected = _selected;
            _selected = selected;
            var tint = theme.AccentFor(Module);
            if (selected)
            {
                _glyph.Foreground = (theme.Glow ? theme.Background : tint).Brush();
                _glyph.Opacity = 1;
            }
            else
            {
                _glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.Text);
                _glyph.Opacity = _magnification > 1.1 ? 1 : 0.68;
            }
            _dot.Animate(OpacityProperty, selected ? 1 : 0, Motion.ExpoOut(0.3));
            if (selected && !wasSelected) Bounce();
        }

        public void SetMagnification(double m)
        {
            _magnification = m;
            _scale.Animate(ScaleTransform.ScaleXProperty, m, Motion.Peek);
            _scale.Animate(ScaleTransform.ScaleYProperty, m, Motion.Peek);
            _shift.Animate(TranslateTransform.YProperty, -(m - 1) * 10, Motion.Peek);
            if (!_selected) _glyph.Opacity = m > 1.1 ? 1 : 0.68;
        }

        public void SetLifted(bool lifted)
        {
            var scale = lifted ? 1.25 : 1;
            _scale.Animate(ScaleTransform.ScaleXProperty, scale, Motion.Pop);
            _scale.Animate(ScaleTransform.ScaleYProperty, scale, Motion.Pop);
            _shift.Animate(TranslateTransform.YProperty, lifted ? -6 : 0, Motion.Pop);
            _lift.Animate(OpacityProperty, lifted ? 1 : 0, Motion.Pop);
        }

        /// Small hop when an icon becomes selected.
        private void Bounce()
        {
            var hop = new TranslateTransform();
            _glyph.RenderTransform = hop;
            hop.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, -4, TimeSpan.FromSeconds(0.14))
            {
                AutoReverse = true,
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        }
    }
}
