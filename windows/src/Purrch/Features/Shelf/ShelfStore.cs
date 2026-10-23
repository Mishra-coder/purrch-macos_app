using System.IO;
using Purrch.Core;

namespace Purrch.Features.Shelf;

public sealed class ShelfEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
}

/// Files parked in the pill. They are referenced by path, never copied. Saved to shelf.json.
public sealed class ShelfStore
{
    public static ShelfStore Shared { get; } = new();

    private const string StoreName = "shelf";
    private readonly List<ShelfEntry> _entries;

    /// Raised after any change to the list.
    public event Action? Changed;

    private ShelfStore()
    {
        _entries = Store.Load<List<ShelfEntry>>(StoreName);
        Prune();
    }

    public IReadOnlyList<ShelfEntry> Entries => _entries;

    public void Add(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var raw in paths)
        {
            if (Normalise(raw) is not { } path) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (_entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _entries.Add(new ShelfEntry { Path = path });
            changed = true;
        }
        if (changed) Save();
    }

    public void Remove(ShelfEntry entry)
    {
        if (_entries.RemoveAll(e => e.Id == entry.Id) > 0) Save();
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Save();
    }

    /// Drops files that were deleted or moved away since they were parked.
    public void Prune()
    {
        if (_entries.RemoveAll(e => !File.Exists(e.Path) && !Directory.Exists(e.Path)) > 0) Save();
    }

    private static string? Normalise(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            // "C:\" keeps its slash; "C:\Folder\" loses it, so the same folder isn't parked twice.
            var root = System.IO.Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void Save()
    {
        Store.Save(StoreName, _entries);
        Changed?.Invoke();
    }
}
