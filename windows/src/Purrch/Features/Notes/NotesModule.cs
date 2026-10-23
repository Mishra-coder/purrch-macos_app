using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Features.Notes;

/// A list of notes on the left, the selected one on the right. Each note is a checklist or free text.
public sealed class NotesModule : Grid
{
    private readonly NotesStore _store = NotesStore.Shared;
    private readonly StackPanel _list = new();
    private readonly ContentControl _listHost = new();
    private readonly ScrollViewer _listScroll;
    private readonly ContentControl _editorHost = new();
    private Guid? _editing;
    private bool _editingChecklist;
    private string _query = "";

    public NotesModule()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var search = Ui.SearchField("Search notes", q => { _query = q; RebuildList(); }, out var searchBox);
        // A new note is empty, so a search would hide it straight away.
        var add = Ui.IconButton("\uE710", 32, () =>
        {
            searchBox.Text = "";
            _store.Add();
        }, prominent: true, tooltip: "New note");
        add.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(search);
        DockPanel.SetDock(top, Dock.Top);
        sidebar.Children.Add(top);
        _listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list };
        sidebar.Children.Add(_listHost);
        Children.Add(sidebar);

        SetColumn(_editorHost, 2);
        Children.Add(_editorHost);

        RebuildList();
        ShowEditor(force: true);
        Loaded += (_, _) => _store.Changed += OnChanged;
        Unloaded += (_, _) =>
        {
            _store.Changed -= OnChanged;
            _store.Flush();
        };
    }

    private void OnChanged()
    {
        RebuildList();
        ShowEditor(force: false);
    }

    private void RebuildList()
    {
        var q = _query.Trim();
        bool Has(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var notes = _store.Sorted.Where(n => q.Length == 0 || Has(n.Title) || Has(n.Body) || n.Items.Any(i => Has(i.Text))).ToList();
        if (notes.Count == 0)
        {
            _listHost.Content = Ui.EmptyState("\uE70B", q.Length == 0 ? "No notes" : "No matches", "");
            return;
        }
        _list.Children.Clear();
        foreach (var note in notes) _list.Children.Add(Row(note));
        _listHost.Content = _listScroll;
    }

    private UIElement Row(NoteItem note)
    {
        var selected = note.Id == _store.Selection;
        var head = new DockPanel();
        var when = Ui.Text(Format.Relative(note.Updated), 10, ThemeResources.SecondaryText);
        DockPanel.SetDock(when, Dock.Right);
        head.Children.Add(when);
        var kind = Ui.Glyph(note.IsChecklist ? "\uE762" : "\uE8E4", 10, selected ? ThemeResources.Accent : ThemeResources.SecondaryText);
        kind.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(kind, Dock.Left);
        head.Children.Add(kind);
        var title = Ui.Text(note.DisplayTitle, 13, weight: FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(title);

        var preview = Ui.Text(note.Preview, 11, ThemeResources.SecondaryText);
        preview.TextWrapping = TextWrapping.NoWrap;
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        preview.Margin = new Thickness(0, 3, 0, 0);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(preview);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 5),
            BorderThickness = new Thickness(0.7),
            Child = body,
            Cursor = Cursors.Hand,
        };
        card.SetResourceReference(Border.BackgroundProperty, selected ? ThemeResources.AccentFaint : ThemeResources.Surface);
        card.SetResourceReference(Border.BorderBrushProperty, selected ? ThemeResources.Accent : ThemeResources.Hairline);
        card.MouseLeftButtonUp += (_, _) =>
        {
            _store.Flush();
            _store.Selection = note.Id;
            RebuildList();
            ShowEditor(force: true);
        };

        var menu = new ContextMenu();
        menu.Items.Add(Ui.MenuEntry(note.IsChecklist ? "Convert to Text" : "Convert to Checklist", () =>
        {
            note.IsChecklist = !note.IsChecklist;
            _store.Touch(note);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuEntry("Delete", () => _store.Delete(note.Id), destructive: true));
        card.ContextMenu = menu;
        return card;
    }

    /// Rebuilds the editor only when a different note (or mode) is shown, so typing keeps its focus.
    private void ShowEditor(bool force)
    {
        var note = _store.Selected;
        if (!force && note?.Id == _editing && note?.IsChecklist == _editingChecklist) return;
        _editing = note?.Id;
        _editingChecklist = note?.IsChecklist ?? false;
        _editorHost.Content = note == null
            ? new Card("Note", "\uE8FD", Ui.EmptyState("\uE70F", "Pick or create a note", "Press + to start a new list"))
            : new NoteEditor(note, _store);
    }
}

/// Title, mode switch and delete on top; a checklist or a text editor below.
internal sealed class NoteEditor : Border
{
    private readonly NoteItem _note;
    private readonly NotesStore _store;
    private readonly StackPanel _items = new();
    private readonly TextBlock _count = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium);
    private readonly Button _clearDone;

    public NoteEditor(NoteItem note, NotesStore store)
    {
        _note = note;
        _store = store;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(12);
        BorderThickness = new Thickness(0.8);
        SetResourceReference(BackgroundProperty, ThemeResources.Surface);
        SetResourceReference(BorderBrushProperty, ThemeResources.Border);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var delete = Ui.IconButton("\uE74D", 26, () => store.Delete(note.Id), tooltip: "Delete note");
        delete.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(delete, Dock.Right);
        header.Children.Add(delete);
        var modes = ModeSwitch();
        DockPanel.SetDock(modes, Dock.Right);
        header.Children.Add(modes);
        var title = Ui.Input(note.Title, 15, weight: FontWeights.Bold);
        title.TextChanged += (_, _) => { note.Title = title.Text; store.Touch(note); };
        header.Children.Add(Ui.WithPlaceholder(title, "Title"));

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        _clearDone = Ui.Bare(Ui.Text("Clear done", 11, ThemeResources.SecondaryText, FontWeights.Medium), () =>
        {
            note.Items.RemoveAll(i => i.Done);
            store.Touch(note);
            RebuildItems();
        });

        if (note.IsChecklist) BuildChecklist(body);
        else BuildText(body);
        Child = body;
    }

    private UIElement ModeSwitch()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ModeButton("\uE762", _note.IsChecklist, "Checklist", true));
        row.Children.Add(ModeButton("\uE8E4", !_note.IsChecklist, "Full text", false));
        var capsule = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(2), Child = row, VerticalAlignment = VerticalAlignment.Center };
        capsule.SetResourceReference(BackgroundProperty, ThemeResources.Hairline);
        return capsule;
    }

    private Button ModeButton(string glyph, bool active, string tooltip, bool checklist)
    {
        var pill = new Border
        {
            Width = 30,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Child = Ui.Glyph(glyph, 11, active ? ThemeResources.Background : ThemeResources.SecondaryText),
        };
        if (active) pill.SetResourceReference(BackgroundProperty, ThemeResources.Accent);
        else pill.Background = Brushes.Transparent;
        return Ui.Bare(pill, () =>
        {
            if (_note.IsChecklist == checklist) return;
            _note.IsChecklist = checklist;
            _store.Touch(_note);
        }, tooltip);
    }

    private void BuildText(DockPanel body)
    {
        var editor = Ui.Input(_note.Body, 13, multiline: true);
        editor.TextChanged += (_, _) => { _note.Body = editor.Text; _store.Touch(_note); };
        var frame = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = Ui.WithPlaceholder(editor, "Write anything") };
        frame.SetResourceReference(BackgroundProperty, ThemeResources.Hairline);
        body.Children.Add(frame);
    }

    private void BuildChecklist(DockPanel body)
    {
        var addRow = new DockPanel();
        var plus = Ui.Glyph("\uE710", 12, ThemeResources.Accent);
        plus.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(plus, Dock.Left);
        addRow.Children.Add(plus);
        _clearDone.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_clearDone, Dock.Right);
        addRow.Children.Add(_clearDone);
        _count.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_count, Dock.Right);
        addRow.Children.Add(_count);
        var input = Ui.Input("", 13);
        // Return adds the item and keeps the cursor here for the next one.
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = input.Text.Trim();
            if (text.Length == 0) return;
            _note.Items.Add(new NoteCheckItem { Text = text });
            input.Text = "";
            _store.Touch(_note);
            RebuildItems(scrollToEnd: true);
            e.Handled = true;
        };
        addRow.Children.Add(Ui.WithPlaceholder(input, "Add an item"));

        var addFrame = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Child = addRow, Margin = new Thickness(0, 6, 0, 0) };
        addFrame.SetResourceReference(BackgroundProperty, ThemeResources.Hairline);
        DockPanel.SetDock(addFrame, Dock.Bottom);
        body.Children.Add(addFrame);

        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _items });
        RebuildItems();
    }

    private void RebuildItems(bool scrollToEnd = false)
    {
        _items.Children.Clear();
        if (_note.Items.Count == 0)
        {
            var empty = Ui.Text("Nothing here yet. Type below and press Enter.", 12, ThemeResources.SecondaryText);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 20, 0, 0);
            _items.Children.Add(empty);
        }
        foreach (var item in _note.Items) _items.Children.Add(ItemRow(item));
        UpdateCount();
        if (scrollToEnd && _items.Parent is ScrollViewer scroll) scroll.ScrollToEnd();
    }

    private void UpdateCount()
    {
        var done = _note.Items.Count(i => i.Done);
        _count.Text = _note.Items.Count == 0 ? "" : $"{done}/{_note.Items.Count}";
        _clearDone.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement ItemRow(NoteCheckItem item)
    {
        var row = new DockPanel();
        var mark = Ui.Glyph("", 15);
        var input = Ui.Input(item.Text, 13);
        // Done items stay editable, struck through in the secondary colour.
        void ShowState()
        {
            mark.Text = item.Done ? "\uEC61" : "\uEA3A";
            mark.SetResourceReference(TextBlock.ForegroundProperty, item.Done ? ThemeResources.Accent : ThemeResources.SecondaryText);
            input.SetResourceReference(Control.ForegroundProperty, item.Done ? ThemeResources.SecondaryText : ThemeResources.Text);
            input.TextDecorations = item.Done ? TextDecorations.Strikethrough : null;
        }
        ShowState();

        var tick = new ScaleTransform(1, 1);
        mark.RenderTransformOrigin = new Point(0.5, 0.5);
        mark.RenderTransform = tick;
        Button? check = null;
        check = Ui.Bare(mark, () =>
        {
            item.Done = !item.Done;
            _store.Touch(_note);
            ShowState();
            check!.ToolTip = item.Done ? "Mark as not done" : "Mark as done";
            // A quick 0.15 s ease, like the Mac's withAnimation(.easeOut(duration: 0.15)).
            tick.ScaleX = tick.ScaleY = 0.7;
            tick.Animate(ScaleTransform.ScaleXProperty, 1, Motion.EaseOut(0.15));
            tick.Animate(ScaleTransform.ScaleYProperty, 1, Motion.EaseOut(0.15));
            input.Opacity = 0.4;
            input.Animate(OpacityProperty, 1, Motion.EaseOut(0.15));
            UpdateCount();
        }, item.Done ? "Mark as not done" : "Mark as done");
        check.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);

        var remove = Ui.Bare(Ui.Glyph("\uE711", 10, ThemeResources.SecondaryText), () =>
        {
            _note.Items.Remove(item);
            _store.Touch(_note);
            RebuildItems();
        }, "Delete item");
        remove.Visibility = Visibility.Hidden;
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);

        input.TextChanged += (_, _) => { item.Text = input.Text; _store.Touch(_note); };
        row.Children.Add(input);

        var frame = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 2), Child = row, Background = Brushes.Transparent };
        frame.MouseEnter += (_, _) => { remove.Visibility = Visibility.Visible; frame.SetResourceReference(BackgroundProperty, ThemeResources.Hairline); };
        frame.MouseLeave += (_, _) => { remove.Visibility = Visibility.Hidden; frame.Background = Brushes.Transparent; };
        return frame;
    }
}
