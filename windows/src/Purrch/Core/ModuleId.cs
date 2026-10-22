using System.Windows.Media;

namespace Purrch.Core;

/// Every tool in the panel, in default tab-bar order (the same order as the Mac app).
public enum ModuleId
{
    Home, Timers, Clipboard, Notes, Scratchpad, Emoji, Units, Links,
    Shelf, Captures, Converter, Downloads, Assistant, Message, Ambience,
    Claude, Calendar, Weather, NowPlaying, System, ScreenTime, Revenue, Analytics, Translate,
}

public static class ModuleInfo
{
    public static IReadOnlyList<ModuleId> All { get; } = Enum.GetValues<ModuleId>();

    public static string Title(this ModuleId m) => m switch
    {
        ModuleId.Home => "Home",
        ModuleId.Timers => "Timers",
        ModuleId.Clipboard => "Clipboard",
        ModuleId.Notes => "Notes",
        ModuleId.Scratchpad => "Scratchpad",
        ModuleId.Emoji => "Emoji",
        ModuleId.Units => "Units",
        ModuleId.Links => "Links",
        ModuleId.Shelf => "Shelf",
        ModuleId.Captures => "Captures",
        ModuleId.Converter => "File Converter",
        ModuleId.Downloads => "Downloads",
        ModuleId.Assistant => "Assistant",
        ModuleId.Message => "Message",
        ModuleId.Ambience => "Ambience",
        ModuleId.Claude => "Claude Code",
        ModuleId.Calendar => "Calendar",
        ModuleId.Weather => "Weather",
        ModuleId.NowPlaying => "Now Playing",
        ModuleId.System => "System",
        ModuleId.ScreenTime => "Screen Time",
        ModuleId.Revenue => "Revenue",
        ModuleId.Analytics => "Analytics",
        ModuleId.Translate => "Translate",
        _ => m.ToString(),
    };

    public static string Subtitle(this ModuleId m) => m switch
    {
        ModuleId.Home => "Glanceable widgets and a quick launcher",
        ModuleId.Timers => "Pomodoro, countdown, stopwatch, water",
        ModuleId.Clipboard => "Searchable history of everything you copy",
        ModuleId.Notes => "Quick lists with a full editor",
        ModuleId.Scratchpad => "A text box that never forgets",
        ModuleId.Emoji => "Search and copy with one click",
        ModuleId.Units => "Instant conversions",
        ModuleId.Links => "Web search and saved sites",
        ModuleId.Shelf => "Park files in the pill, drag them out later",
        ModuleId.Captures => "Your screenshots and recordings, newest first",
        ModuleId.Converter => "Convert and shrink images locally",
        ModuleId.Downloads => "Live download progress",
        ModuleId.Assistant => "Ask Claude, OpenAI or Gemini with your own key",
        ModuleId.Message => "Scroll text across the pill",
        ModuleId.Ambience => "Layer rain, wind and noise",
        ModuleId.Claude => "Today's Claude Code usage",
        ModuleId.Calendar => "Seven-day agenda and reminders",
        ModuleId.Weather => "Now, next 12 hours, this week",
        ModuleId.NowPlaying => "Artwork, scrubber and controls",
        ModuleId.System => "CPU, memory, disk, network, battery",
        ModuleId.ScreenTime => "App usage over seven days",
        ModuleId.Revenue => "Today's sales from Stripe",
        ModuleId.Analytics => "Live visitors from Plausible",
        ModuleId.Translate => "Translate, fix grammar, change tone",
        _ => "",
    };

    /// Glyph in Segoe Fluent Icons (Windows 11), which keeps the Segoe MDL2 Assets code points (Windows 10).
    public static string Glyph(this ModuleId m) => m switch
    {
        ModuleId.Home => "\uE80F",
        ModuleId.Timers => "\uE916",
        ModuleId.Clipboard => "\uE77F",
        ModuleId.Notes => "\uE8FD",
        ModuleId.Scratchpad => "\uE70F",
        ModuleId.Emoji => "\uE76E",
        ModuleId.Units => "\uE8AB",
        ModuleId.Links => "\uE71B",
        ModuleId.Shelf => "\uE7B8",
        ModuleId.Captures => "\uE91B",
        ModuleId.Converter => "\uE8B9",
        ModuleId.Downloads => "\uE896",
        ModuleId.Assistant => "\uE945",
        ModuleId.Message => "\uE8BD",
        ModuleId.Ambience => "\uE767",
        ModuleId.Claude => "\uE943",
        ModuleId.Calendar => "\uE787",
        ModuleId.Weather => "\uE753",
        ModuleId.NowPlaying => "\uE8D6",
        ModuleId.System => "\uE9D9",
        ModuleId.ScreenTime => "\uE823",
        ModuleId.Revenue => "\uE8C7",
        ModuleId.Analytics => "\uE9D2",
        ModuleId.Translate => "\uE8C1",
        _ => "\uE8A9",
    };

    // macOS system colours (dark appearance), so the Playful theme matches the Mac app.
    private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Pink = Color.FromRgb(0xFF, 0x37, 0x5F);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    private static readonly Color Mint = Color.FromRgb(0x63, 0xE6, 0xE2);
    private static readonly Color Teal = Color.FromRgb(0x40, 0xC8, 0xE0);
    private static readonly Color Cyan = Color.FromRgb(0x64, 0xD2, 0xFF);
    private static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);
    private static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private static readonly Color[] Palette =
    {
        Blue, Orange, Pink, Yellow, Mint, Yellow, Teal, Cyan,
        Indigo, Purple, Green, Blue, Purple, Pink, Teal,
        Orange, Red, Cyan, Pink, Green, Indigo, Green, Blue, Mint,
    };

    public static Color PlayfulColor(this ModuleId m) => Palette[(int)m % Palette.Length];

    /// Lower-camel-case key used in settings.json ("nowPlaying", "screenTime").
    public static string Key(this ModuleId m)
    {
        var name = m.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static ModuleId? FromKey(string? key) =>
        Enum.TryParse<ModuleId>(key, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : null;
}
