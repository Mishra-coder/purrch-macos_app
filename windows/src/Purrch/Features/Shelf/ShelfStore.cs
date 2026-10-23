using System.IO;
using Purrch.Core;

namespace Purrch.Features.Shelf;

public sealed class ShelfEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    /// The drive's serial and the file's NTFS ID, so a renamed or moved file is followed like a Mac bookmark.
    /// Null for entries saved before IDs were kept, and on drives without stable IDs.
    public ulong? Volume { get; set; }
    public string? FileId { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
}

/// Files parked in the pill. They are referenced by path and file ID, never copied. Saved to shelf.json.
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
            if (!Exists(path)) continue;
            if (_entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var identity = FileIdentity.Of(path);
            _entries.Add(new ShelfEntry { Path = path, Volume = identity?.Volume, FileId = identity?.Id });
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

    /// Follows files that were renamed or moved on the same drive, and drops ones that were deleted
    /// (or moved to another drive) since they were parked.
    public void Prune()
    {
        var changed = false;
        foreach (var entry in _entries.ToList())
        {
            if (Exists(entry.Path))
            {
                // Entries from before IDs were kept pick one up the first time they are seen.
                if (entry.FileId == null && FileIdentity.Of(entry.Path) is { } identity)
                {
                    (entry.Volume, entry.FileId) = identity;
                    changed = true;
                }
                continue;
            }
            var moved = entry is { Volume: { } volume, FileId: { } id } ? FileIdentity.Find(entry.Path, volume, id) : null;
            if (moved != null && !_entries.Any(e => e != entry && string.Equals(e.Path, moved, StringComparison.OrdinalIgnoreCase)))
                entry.Path = moved;
            else
                _entries.Remove(entry);
            changed = true;
        }
        if (changed) Save();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

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
