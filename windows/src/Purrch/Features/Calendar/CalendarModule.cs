using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Calendar;

/// Seven-day agenda on the left, open reminders on the right, from <see cref="CalendarService"/>.
public sealed class CalendarModule : Grid
{
    private readonly CalendarService _service = CalendarService.Shared;
    private readonly Grid _layout = new();
    private readonly ContentControl _agenda = new();
    private readonly ContentControl _agendaAccessory = new();
    private readonly ContentControl _reminders = new();
    private readonly TextBlock _reminderCount = Ui.Text("", 11, ThemeResources.SecondaryText);
    private CalendarsEditor? _editor;

    public CalendarModule()
    {
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        _layout.Children.Add(new Card("Next 7 days", "\uE787", _agenda, _agendaAccessory));

        var remindersBody = new DockPanel();
        var add = AddField();
        add.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(add, Dock.Bottom);
        remindersBody.Children.Add(add);
        remindersBody.Children.Add(_reminders);
        var reminders = new Card("Reminders", "\uE762", remindersBody, _reminderCount);
        SetColumn(reminders, 2);
        _layout.Children.Add(reminders);
        Children.Add(_layout);

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
        RenderAgenda();
        RenderReminders();
        _editor?.Refresh();
    }

    // MARK: Agenda

    private void RenderAgenda()
    {
        if (!_service.HasSources)
        {
            _agendaAccessory.Content = AccentButton("Add calendar", OpenEditor);
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(Ui.EmptyState("\uE787", "No calendars connected",
                "Sign in with Microsoft or Google, or add an iCal link or an .ics file."));
            var pill = Ui.Pill("Add calendar", "\uE710", OpenEditor, prominent: true);
            pill.HorizontalAlignment = HorizontalAlignment.Center;
            pill.Margin = new Thickness(0, 12, 0, 0);
            empty.Children.Add(pill);
            _agenda.Content = empty;
            return;
        }

        var accessory = new StackPanel { Orientation = Orientation.Horizontal };
        accessory.Children.Add(Ui.Text(_service.IsLoading && _service.Events.Count == 0 ? "Loading…" : $"{_service.Events.Count} events",
            11, ThemeResources.SecondaryText));
        var manage = Ui.Bare(Ui.Glyph("\uE713", 11, ThemeResources.SecondaryText), OpenEditor, "Calendars");
        manage.Margin = new Thickness(8, 0, 0, 0);
        accessory.Children.Add(manage);
        _agendaAccessory.Content = accessory;

        if (_service.Events.Count == 0)
        {
            // Every calendar and signed-in account failing means there is nothing to trust, not a clear week.
            var errors = _service.Sources.Select(_service.Error)
                .Concat(_service.Accounts.Where(a => _service.Email(a.Kind) != null).Select(a => _service.AccountError(a.Kind)))
                .ToList();
            var failing = errors.FirstOrDefault(e => e != null);
            _agenda.Content = _service.IsLoading ? Ui.EmptyState("\uE787", "Loading calendars…", "")
                : failing != null && errors.All(e => e != null)
                    ? Ui.EmptyState("\uE7BA", "Couldn't load your calendars", failing)
                    : Ui.EmptyState("\uE73E", "Nothing scheduled", "Your week is clear");
            return;
        }

        var list = new StackPanel();
        var today = DateTime.Today;
        foreach (var group in _service.Events.GroupBy(e => e.Start.Date < today ? today : e.Start.Date).OrderBy(g => g.Key))
        {
            var day = Ui.Text(DayTitle(group.Key).ToUpper(CultureInfo.CurrentCulture), 10,
                group.Key == today ? ThemeResources.Accent : ThemeResources.SecondaryText, FontWeights.Bold);
            day.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 5);
            list.Children.Add(day);
            foreach (var e in group) list.Children.Add(EventRow(e));
        }
        _agenda.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
    }

    private UIElement EventRow(AgendaEvent e)
    {
        var row = new DockPanel();
        var bar = new Border { Width = 4, Height = 26, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
        bar.Background = (_service.ColourOf(e.SourceId) ?? Colors.Gray).Brush();
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);

        if (e.Location != null)
        {
            var pin = Ui.Glyph("\uE707", 10, ThemeResources.SecondaryText);
            pin.ToolTip = e.Location;
            pin.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pin, Dock.Right);
            row.Children.Add(pin);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(e.Title, 13, weight: FontWeights.Medium);
        var time = Ui.Text(TimeText(e), 11, ThemeResources.SecondaryText).Tabular();
        foreach (var line in new[] { title, time })
        {
            line.TextWrapping = TextWrapping.NoWrap;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(line);
        }
        time.Margin = new Thickness(0, 1, 0, 0);
        row.Children.Add(text);

        var surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 5),
            Child = row,
        };
        surface.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return surface;
    }

    private static string DayTitle(DateTime day)
    {
        if (day == DateTime.Today) return "Today";
        if (day == DateTime.Today.AddDays(1)) return "Tomorrow";
        return day.ToString("dddd, d MMM", CultureInfo.CurrentCulture);
    }

    private static string TimeText(AgendaEvent e) =>
        e.IsAllDay ? "All day" : $"{e.Start.ToString("t", CultureInfo.CurrentCulture)} – {e.End.ToString("t", CultureInfo.CurrentCulture)}";

    // MARK: Reminders

    private void RenderReminders()
    {
        var items = _service.Reminders;
        _reminderCount.Text = items.Count.ToString(CultureInfo.CurrentCulture);
        if (items.Count == 0)
        {
            _reminders.Content = Ui.EmptyState("\uE73E", "All done", "");
            return;
        }
        var list = new StackPanel();
        foreach (var item in items)
            list.Children.Add(new ReminderRow(item, _service.ColourOf(item.SourceId), () => _service.Complete(item)));
        _reminders.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
    }

    /// "Add a reminder" field under the list; Enter adds and keeps the cursor there.
    private Border AddField()
    {
        var input = Ui.Input("", 12);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _service.AddReminder(input.Text);
            input.Clear();
            e.Handled = true;
        };
        var row = new DockPanel();
        var plus = Ui.Glyph("\uE710", 10, ThemeResources.SecondaryText);
        plus.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(plus, Dock.Left);
        row.Children.Add(plus);
        row.Children.Add(Ui.WithPlaceholder(input, "Add a reminder"));
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 30,
            Child = row,
            Cursor = Cursors.IBeam,
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        border.MouseLeftButtonDown += (_, _) => input.Focus();
        return border;
    }

    private static Button AccentButton(string title, Action action)
    {
        var button = Ui.Bare(Ui.Text(title, 11, ThemeResources.Accent, FontWeights.SemiBold), action);
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    // MARK: Calendars editor

    private void OpenEditor()
    {
        if (_editor != null) return;
        var editor = new CalendarsEditor(_service, CloseEditor);
        _editor = editor;
        Children.Add(editor);
        _layout.IsHitTestVisible = false;
        _layout.Animate(OpacityProperty, 0.25, Motion.ExpoOut(0.15));
        editor.Opacity = 0;
        var scale = new ScaleTransform(0.97, 0.97);
        editor.RenderTransformOrigin = new Point(0.5, 0.5);
        editor.RenderTransform = scale;
        editor.Animate(OpacityProperty, 1, Motion.ExpoOut(0.15));
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.15));
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.15));
    }

    private void CloseEditor()
    {
        if (_editor is not { } editor) return;
        _editor = null;
        editor.IsHitTestVisible = false;
        _layout.IsHitTestVisible = true;
        _layout.Animate(OpacityProperty, 1, Motion.ExpoOut(0.15));
        editor.Animate(OpacityProperty, 0, Motion.ExpoOut(0.15), completed: () => Children.Remove(editor));
        if (editor.RenderTransform is ScaleTransform scale)
        {
            scale.Animate(ScaleTransform.ScaleXProperty, 0.97, Motion.ExpoOut(0.15));
            scale.Animate(ScaleTransform.ScaleYProperty, 0.97, Motion.ExpoOut(0.15));
        }
    }
}

/// One reminder: a ring in the calendar's colour that fills on click, then the row goes.
internal sealed class ReminderRow : DockPanel
{
    private readonly TextBlock _check;
    private readonly TextBlock _title;
    private readonly Color? _colour;
    private bool _checked;

    public ReminderRow(ReminderItem item, Color? colour, Action complete)
    {
        Margin = new Thickness(0, 0, 0, 6);
        _colour = colour;
        _check = Ui.Glyph("\uEA3A", 15);
        Paint(hover: false);
        var button = Ui.Bare(_check, () =>
        {
            if (_checked) return;
            _checked = true;
            Paint(hover: true);
            Strike();
            var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
            delay.Tick += (_, _) =>
            {
                delay.Stop();
                complete();
            };
            delay.Start();
        }, "Mark as done");
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Margin = new Thickness(0, 1, 8, 0);
        button.MouseEnter += (_, _) => Paint(hover: true);
        button.MouseLeave += (_, _) => Paint(hover: false);
        SetDock(button, Dock.Left);
        Children.Add(button);

        var text = new StackPanel();
        _title = Ui.Text(item.Title, 13);
        _title.MaxHeight = 36;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(_title);
        if (item.Due is { } due)
        {
            // To Do and Google Tasks keep only the day, which is overdue once it has passed.
            var label = item.DueIsDate ? DueDay(due)
                : $"{due.ToString("ddd", CultureInfo.CurrentCulture)} {due.ToString("t", CultureInfo.CurrentCulture)}";
            var when = Ui.Text(label, 10, ThemeResources.SecondaryText).Tabular();
            if (item.DueIsDate ? due.Date < DateTime.Today : due < DateTime.Now) when.Foreground = Color.FromRgb(0xFF, 0x45, 0x3A).Brush();
            when.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(when);
        }
        Children.Add(text);
    }

    private static string DueDay(DateTime due)
    {
        if (due.Date == DateTime.Today) return "Today";
        if (due.Date == DateTime.Today.AddDays(1)) return "Tomorrow";
        return due.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }

    private void Paint(bool hover)
    {
        _check.Text = _checked ? "\uEC61" : "\uEA3A";
        // Reminders added in Purrch have no calendar colour and use the accent throughout.
        if (_checked || hover || _colour is not { } colour) _check.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.Accent);
        else _check.Foreground = colour.Brush();
    }

    private void Strike()
    {
        _title.TextDecorations = TextDecorations.Strikethrough;
        _title.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.SecondaryText);
    }
}

/// The card for adding and removing calendars and signing in to accounts, shown over the dimmed module.
internal sealed class CalendarsEditor : Border
{
    private readonly CalendarService _service;
    private readonly StackPanel _list = new();
    private readonly TextBox _input;
    private readonly TextBlock _status = Ui.Text("", 11, ThemeResources.SecondaryText);

    public CalendarsEditor(CalendarService service, Action done)
    {
        _service = service;
        Width = 470;
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, ThemeResources.Background);
        SetResourceReference(BorderBrushProperty, ThemeResources.Border);
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.5, BlurRadius = 24, ShadowDepth = 4, Direction = 270 };

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("Calendars", 14, weight: FontWeights.Bold));
        stack.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxHeight = 116,
            Margin = new Thickness(0, 8, 0, 0),
            Content = _list,
        });

        var field = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 34,
            BorderThickness = new Thickness(0.8),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.IBeam,
        };
        field.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        field.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);
        _input = Ui.Input("", 13);
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Add(_input.Text);
            e.Handled = true;
        };
        _input.TextChanged += (_, _) => _status.Text = "";
        var row = new DockPanel();
        var link = Ui.Glyph("\uE71B", 12, ThemeResources.SecondaryText);
        link.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(link, Dock.Left);
        row.Children.Add(link);
        row.Children.Add(Ui.WithPlaceholder(_input, "Paste an iCal link (https:// or webcal://)"));
        field.Child = row;
        field.MouseLeftButtonDown += (_, _) => _input.Focus();
        stack.Children.Add(field);

        _status.Margin = new Thickness(2, 6, 0, 0);
        stack.Children.Add(_status);
        var help = Ui.Text("Google Calendar: Settings → your calendar → Secret address in iCal format. " +
                           "Outlook: Settings → Calendar → Shared calendars → Publish a calendar → ICS.",
                           10.5, ThemeResources.SecondaryText);
        help.Margin = new Thickness(2, 4, 0, 0);
        help.Opacity = 0.8;
        stack.Children.Add(help);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var file = Ui.Pill("Open .ics file…", "\uE8E5", PickFile);
        DockPanel.SetDock(file, Dock.Left);
        buttons.Children.Add(file);
        var add = Ui.Pill("Add", "\uE710", () => Add(_input.Text), prominent: true);
        DockPanel.SetDock(add, Dock.Right);
        buttons.Children.Add(add);
        var close = Ui.Pill("Done", "\uE73E", done);
        close.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        stack.Children.Add(buttons);
        Child = stack;

        Refresh();
        Loaded += (_, _) => _input.Focus();
    }

    /// Lists the accounts with their calendars and a switch for each, then the iCal calendars with their
    /// colour, where they come from and any problem.
    public void Refresh()
    {
        _list.Children.Clear();
        foreach (var account in _service.Accounts)
        {
            _list.Children.Add(new AccountRow(_service, account, compact: true) { Margin = new Thickness(0, 0, 0, 6) });
            foreach (var calendar in _service.CalendarsOf(account.Kind))
                _list.Children.Add(AccountCalendarRow(account.Kind, calendar));
        }
        foreach (var source in _service.Sources)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0) };
            dot.Background = (_service.ColourOf(source.Id) ?? Colors.Gray).Brush();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            var target = source;
            var remove = Ui.Bare(Ui.Glyph("\uE74D", 11, ThemeResources.SecondaryText), () => _service.Remove(target), "Remove");
            remove.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var text = new StackPanel();
            var title = Ui.Text(source.Title, 12, weight: FontWeights.SemiBold);
            var error = _service.Error(source);
            var detail = Ui.Text(error ?? (source.IsLink ? HostOf(source.Location) : source.Location), 10,
                ThemeResources.SecondaryText);
            if (error != null) detail.Foreground = Color.FromRgb(0xFF, 0x45, 0x3A).Brush();
            foreach (var line in new[] { title, detail })
            {
                line.TextWrapping = TextWrapping.NoWrap;
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Children.Add(line);
            }
            row.Children.Add(text);
            _list.Children.Add(row);
        }
    }

    /// A calendar or task list in a signed-in account, with a switch to leave it out of Purrch.
    private UIElement AccountCalendarRow(AccountKind kind, AccountCalendar calendar)
    {
        var row = new DockPanel { Margin = new Thickness(20, 0, 0, 6) };
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0) };
        dot.Background = (_service.ColourOf(calendar.Key) ?? Colors.Gray).Brush();
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var target = calendar;
        var toggle = Ui.Switch(!_service.IsHidden(kind, calendar), on => _service.SetHidden(kind, target, !on));
        toggle.Margin = new Thickness(8, 0, 0, 0);
        toggle.ToolTip = "Show in Purrch";
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);

        if (calendar.IsTaskList)
        {
            var tag = Ui.Text("Tasks", 10, ThemeResources.SecondaryText);
            tag.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
        }

        var title = Ui.Text(calendar.Name, 12);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(title);
        return row;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void Add(string text)
    {
        if (_service.Add(text) is { } problem)
        {
            _status.Text = problem;
            return;
        }
        _input.Clear();
        _status.Text = "Added. Loading…";
    }

    /// The file dialog takes focus from the panel; pin it meanwhile so clicks in the dialog don't fold it
    /// away, then keep it open until the pointer comes back.
    private void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a calendar file",
            Filter = "iCalendar files (*.ics)|*.ics|All files (*.*)|*.*",
        };
        var state = NotchState.Shared;
        var wasPinned = state.IsPinned;
        state.IsPinned = true;
        bool? picked;
        try { picked = dialog.ShowDialog(Window.GetWindow(this)); }
        finally
        {
            state.IsPinned = wasPinned;
            state.HoldUntilHovered = true;
        }
        if (picked == true) Add(dialog.FileName);
    }
}

/// A Microsoft or Google account: who is signed in, what went wrong, and Sign in / Sign out.
/// Used in the Calendars card (compact) and in Settings → Integrations, and keeps itself up to date.
internal sealed class AccountRow : ContentControl
{
    private readonly CalendarService _service;
    private readonly CalendarAccount _account;
    private readonly bool _compact;

    public AccountRow(CalendarService service, CalendarAccount account, bool compact)
    {
        _service = service;
        _account = account;
        _compact = compact;
        Render();
        Loaded += (_, _) =>
        {
            _service.Changed += Render;
            Render();
        };
        Unloaded += (_, _) => _service.Changed -= Render;
    }

    private void Render()
    {
        var kind = _account.Kind;
        var email = _service.Email(kind);
        var signingIn = _service.IsSigningIn(kind);
        var error = _service.AccountError(kind) ?? _service.AccountWarning(kind);

        var row = new DockPanel();
        var icon = Ui.Glyph("\uE77B", 12, ThemeResources.SecondaryText);
        icon.Width = 12;
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (signingIn) buttons.Children.Add(ActionButton("Cancel", "\uE711", () => _service.CancelSignIn(kind)));
        else if (email == null && _compact && _account.SetupProblem != null)
            buttons.Children.Add(ActionButton("Set up", "\uE713", () => SettingsView.Open(SettingsView.Section.Integrations), prominent: true));
        else if (email == null || _service.NeedsSignIn(kind))
            buttons.Children.Add(ActionButton("Sign in", "\uE77B", () => _service.SignIn(kind), prominent: true));
        if (email != null && !signingIn) buttons.Children.Add(ActionButton("Sign out", "\uE711", () => _service.SignOut(kind)));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var title = Ui.Text(email == null ? _account.Title : $"{_account.Title} · {email}", 12, weight: FontWeights.SemiBold);
        var detail = Ui.Text(signingIn ? "Finish signing in in your browser…"
            : error ?? (email != null ? CalendarsSummary(kind) : _account.SetupProblem ?? "Not signed in"), 10, ThemeResources.SecondaryText);
        if (error != null && !signingIn) detail.Foreground = Color.FromRgb(0xFF, 0x45, 0x3A).Brush();
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        // Errors say what to do, so they may take a second line.
        if (_compact && error == null)
        {
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        text.Children.Add(title);
        text.Children.Add(detail);
        row.Children.Add(text);
        Content = row;
    }

    private string CalendarsSummary(AccountKind kind)
    {
        var all = _service.CalendarsOf(kind);
        var calendars = all.Count(c => !c.IsTaskList);
        var lists = all.Count - calendars;
        if (all.Count == 0) return _service.IsLoading ? "Loading…" : "Signed in";
        return $"{calendars} {(calendars == 1 ? "calendar" : "calendars")}, {lists} task {(lists == 1 ? "list" : "lists")}";
    }

    private Button ActionButton(string title, string glyph, Action action, bool prominent = false)
    {
        Button button;
        if (_compact) button = Ui.Bare(Ui.Text(title, 11, prominent ? ThemeResources.Accent : ThemeResources.SecondaryText, FontWeights.SemiBold), action);
        else button = Ui.Pill(title, glyph, action, prominent);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(8, 0, 0, 0);
        return button;
    }
}
