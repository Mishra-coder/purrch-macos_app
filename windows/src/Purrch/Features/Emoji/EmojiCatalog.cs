using System.Globalization;
using System.IO;
using System.Windows.Media;
using Purrch.Core;

namespace Purrch.Features.Emoji;

public enum EmojiCategory { Smileys, People, Nature, Food, Activities, Travel, Objects, Symbols, Flags }

public static class EmojiCategoryInfo
{
    public static IReadOnlyList<EmojiCategory> All { get; } = Enum.GetValues<EmojiCategory>();

    public static string Title(this EmojiCategory c) => c switch
    {
        EmojiCategory.Nature => "Animals & Nature",
        _ => c.ToString(),
    };

    /// Drawn in one colour with Segoe UI Emoji, so it reads as an icon like the Mac's SF Symbols.
    public static string Icon(this EmojiCategory c) => c switch
    {
        EmojiCategory.Smileys => "\U0001F642",
        EmojiCategory.People => "\U0001F44B",
        EmojiCategory.Nature => "\U0001F33F",
        EmojiCategory.Food => "\U0001F374",
        EmojiCategory.Activities => "⚽",
        EmojiCategory.Travel => "\U0001F697",
        EmojiCategory.Objects => "\U0001F4A1",
        EmojiCategory.Symbols => "❤",
        _ => "\U0001F6A9",
    };

    public static EmojiCategory FromTitle(string title) =>
        All.FirstOrDefault(c => c.Title() == title, EmojiCategory.Symbols);
}

public sealed record EmojiEntry(string Char, string Name, EmojiCategory Category)
{
    private static readonly TextInfo English = CultureInfo.GetCultureInfo("en-US").TextInfo;

    /// "Grinning Face", for the footer.
    public string DisplayName => English.ToTitleCase(Name);
}

/// The emoji list, read from Assets/Emoji.tsv. That file is made from the Mac app's catalog by
/// windows/tools/make-emoji-catalog.sh, because .NET has no Unicode emoji data of its own.
public static class EmojiCatalog
{
    private const int FirstRegionalIndicator = 0x1F1E6;

    private static readonly Lazy<IReadOnlyList<EmojiEntry>> Entries = new(Load);
    private static readonly Lazy<IReadOnlyDictionary<string, EmojiEntry>> ByCharLookup = new(() =>
        All.GroupBy(e => e.Char).ToDictionary(g => g.Key, g => g.First()));

    public static IReadOnlyList<EmojiEntry> All => Entries.Value;
    public static IReadOnlyDictionary<string, EmojiEntry> ByChar => ByCharLookup.Value;

    private static IReadOnlyList<EmojiEntry> Load()
    {
        var list = new List<EmojiEntry>();
        using var stream = typeof(EmojiCatalog).Assembly.GetManifestResourceStream("Purrch.Emoji.tsv");
        if (stream == null)
        {
            Log.Write("Emoji.tsv is missing from the build");
            return list;
        }
        var glyphs = EmojiGlyphs();
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || parts[0].Length == 0) continue;
            // Windows 10's Segoe UI Emoji predates the newest emoji; leave out what it would draw as an empty box.
            if (glyphs != null && !glyphs.ContainsKey(char.ConvertToUtf32(parts[0], 0))) continue;
            var category = EmojiCategoryInfo.FromTitle(parts[2]);
            var name = category == EmojiCategory.Flags ? FlagName(parts[0], parts[1]) : parts[1];
            list.Add(new EmojiEntry(parts[0], name, category));
        }
        return list;
    }

    /// Segoe UI Emoji's character map, or null when the font can't be read (then nothing is left out).
    private static IDictionary<int, ushort>? EmojiGlyphs() =>
        new Typeface("Segoe UI Emoji").TryGetGlyphTypeface(out var font) ? font.CharacterToGlyphMap : null;

    /// "flag deutschland de" on a German Windows, as the Mac app names flags in the user's language.
    /// Keeps the catalog's English name for the pride flags and for codes Windows doesn't know.
    private static string FlagName(string flag, string name)
    {
        var letters = flag.EnumerateRunes().ToList();
        if (letters.Count != 2 || letters.Any(r => r.Value is < FirstRegionalIndicator or > FirstRegionalIndicator + 25)) return name;
        var code = string.Concat(letters.Select(r => (char)('A' + r.Value - FirstRegionalIndicator)));
        try
        {
            return $"flag {new RegionInfo(code).DisplayName.ToLowerInvariant()} {code.ToLowerInvariant()}";
        }
        catch (ArgumentException)
        {
            return name;
        }
    }
}

/// The last 30 emoji copied, newest first.
public sealed class EmojiRecents
{
    private const int Limit = 30;
    public static EmojiRecents Shared { get; } = new();

    private readonly List<string> _items = Store.Load("emoji-recent", new List<string>());

    public IReadOnlyList<string> Items => _items;
    public event Action? Changed;

    public void Use(string emoji)
    {
        _items.Remove(emoji);
        _items.Insert(0, emoji);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        Store.Save("emoji-recent", _items);
        Changed?.Invoke();
    }
}
