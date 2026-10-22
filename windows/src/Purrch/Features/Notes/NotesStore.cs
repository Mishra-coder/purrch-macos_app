using Purrch.Core;

namespace Purrch.Features.Notes;

public sealed class NoteCheckItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class NoteItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<NoteCheckItem> Items { get; set; } = new();
    public bool IsChecklist { get; set; } = true;
    public DateTime Updated { get; set; } = DateTime.Now;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title.Trim();

    public string Preview
    {
        get
        {
            if (IsChecklist)
            {
                if (Items.Count == 0) return "Empty list";
                var done = Items.Count(i => i.Done);
                var next = Items.FirstOrDefault(i => !i.Done)?.Text ?? "All done";
                return $"{done}/{Items.Count} · {next}";
            }
            var line = Body.Split('\n', 2)[0].TrimEnd('\r');
            return line.Length == 0 ? "No text" : line;
        }
    }
}

/// Notes, saved to %APPDATA%\Purrch\notes.json shortly after each edit.
public sealed class NotesStore
{
    public static NotesStore Shared { get; } = new();

    private readonly List<NoteItem> _notes;
    private readonly DebouncedSaver _saver;

    /// A note was added, removed, renamed or edited; lists should refresh.
    public event Action? Changed;

    public Guid? Selection { get; set; }

    private NotesStore()
    {
        _notes = Store.Load<List<NoteItem>>("notes");
        if (_notes.Count == 0)
        {
            _notes.Add(new NoteItem
            {
                Title = "Today",
                Items =
                {
                    new NoteCheckItem { Text = "Try the checklist" },
                    new NoteCheckItem { Text = "Switch to text mode with the toggle" },
                },
            });
        }
        _saver = new DebouncedSaver(TimeSpan.FromSeconds(0.7), () => Store.Save("notes", _notes));
        Selection = Sorted.FirstOrDefault()?.Id;
    }

    /// Most recently edited first.
    public IReadOnlyList<NoteItem> Sorted => _notes.OrderByDescending(n => n.Updated).ToList();

    public NoteItem? Selected => _notes.FirstOrDefault(n => n.Id == Selection);

    /// Call after changing a note in place.
    public void Touch(NoteItem note)
    {
        note.Updated = DateTime.Now;
        _saver.Schedule();
        Changed?.Invoke();
    }

    public NoteItem Add()
    {
        var note = new NoteItem();
        _notes.Insert(0, note);
        Selection = note.Id;
        _saver.Schedule();
        Changed?.Invoke();
        return note;
    }

    public void Delete(Guid id)
    {
        _notes.RemoveAll(n => n.Id == id);
        if (Selection == id) Selection = Sorted.FirstOrDefault()?.Id;
        _saver.Schedule();
        Changed?.Invoke();
    }

    public void Flush() => _saver.Flush();
}
