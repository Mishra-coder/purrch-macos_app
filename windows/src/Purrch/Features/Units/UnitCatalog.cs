using System.Globalization;
using System.Text.RegularExpressions;

namespace Purrch.Features.Units;

public enum UnitCategory { Length, Mass, Temperature, Volume, Speed, Area, Data, Time, Energy, Pressure }

/// One unit. A value in it is <c>value * Factor + Offset</c> in the category's base unit
/// (metres, kilograms, kelvin, litres, m/s, m², bytes, seconds, joules, pascals).
public sealed record UnitDef(string Name, string Symbol, double Factor, string[] Aliases, double Offset = 0);

public static class UnitCategoryInfo
{
    public static IReadOnlyList<UnitCategory> All { get; } = Enum.GetValues<UnitCategory>();

    public static string Title(this UnitCategory c) => c.ToString();

    /// Segoe glyph for the category. Volume has none, so the module draws a drop for it.
    public static string Glyph(this UnitCategory c) => c switch
    {
        UnitCategory.Length => "\uED5E",
        UnitCategory.Mass => "\uE7B8",
        UnitCategory.Temperature => "\uE706",
        UnitCategory.Speed => "\uEC4A",
        UnitCategory.Area => "\uE8B3",
        UnitCategory.Data => "\uEDA2",
        UnitCategory.Time => "\uE823",
        UnitCategory.Energy => "\uE945",
        UnitCategory.Pressure => "\uE9D9",
        _ => "",
    };

    public static IReadOnlyList<UnitDef> Units(this UnitCategory c) => UnitCatalog.Units[c];

    public static (string From, string To) DefaultPair(this UnitCategory c) => c switch
    {
        UnitCategory.Mass => ("Kilograms", "Pounds"),
        UnitCategory.Temperature => ("Celsius", "Fahrenheit"),
        UnitCategory.Volume => ("Liters", "US gallons"),
        UnitCategory.Speed => ("Kilometers/hour", "Miles/hour"),
        UnitCategory.Area => ("Square meters", "Square feet"),
        UnitCategory.Data => ("Gigabytes", "Megabytes"),
        UnitCategory.Time => ("Hours", "Minutes"),
        UnitCategory.Energy => ("Kilocalories", "Kilojoules"),
        UnitCategory.Pressure => ("Bars", "PSI"),
        _ => ("Kilometers", "Miles"),
    };
}

/// The units on offer, plus parsing of typed queries like "100 f in c" and number formatting.
/// Factors match the Mac app's Foundation units (cups, tablespoons and teaspoons are metric there too).
public static partial class UnitCatalog
{
    private static UnitDef U(string name, string symbol, double factor, params string[] aliases) =>
        new(name, symbol, factor, aliases);

    public static readonly IReadOnlyDictionary<UnitCategory, UnitDef[]> Units = new Dictionary<UnitCategory, UnitDef[]>
    {
        [UnitCategory.Length] = new[]
        {
            U("Kilometers", "km", 1000, "kilometer", "kilometre", "kilometres", "kms"),
            U("Meters", "m", 1, "meter", "metre", "metres"),
            U("Centimeters", "cm", 0.01, "centimeter", "centimetre"),
            U("Millimeters", "mm", 0.001, "millimeter", "millimetre"),
            U("Miles", "mi", 1609.344, "mile", "miles"),
            U("Yards", "yd", 0.9144, "yard", "yds"),
            U("Feet", "ft", 0.3048, "foot", "feet", "'"),
            U("Inches", "in", 0.0254, "inch", "inches", "\""),
            U("Nautical miles", "nmi", 1852, "nautical mile", "nm"),
        },
        [UnitCategory.Mass] = new[]
        {
            U("Kilograms", "kg", 1, "kilogram", "kilo", "kilos", "kgs"),
            U("Grams", "g", 0.001, "gram", "gr"),
            U("Milligrams", "mg", 1e-6, "milligram"),
            U("Tonnes", "t", 1000, "tonne", "ton", "tons", "metric ton"),
            U("Pounds", "lb", 0.45359237, "pound", "lbs"),
            U("Ounces", "oz", 0.028349523125, "ounce"),
            U("Stones", "st", 6.35029318, "stone"),
        },
        [UnitCategory.Temperature] = new[]
        {
            new UnitDef("Celsius", "°C", 1, new[] { "c", "celsius", "degc", "centigrade" }, 273.15),
            new UnitDef("Fahrenheit", "°F", 5.0 / 9, new[] { "f", "fahrenheit", "degf" }, 459.67 * 5 / 9),
            U("Kelvin", "K", 1, "k", "kelvin"),
        },
        [UnitCategory.Volume] = new[]
        {
            U("Liters", "L", 1, "l", "liter", "litre", "litres"),
            U("Milliliters", "mL", 0.001, "ml", "milliliter", "millilitre"),
            U("Cubic meters", "m³", 1000, "m3", "cubic meter"),
            U("US gallons", "gal", 3.785411784, "gallon", "gallons", "us gal"),
            U("Imperial gallons", "imp gal", 4.54609, "imperial gallon", "uk gal"),
            U("Quarts", "qt", 0.946352946, "quart"),
            U("Pints", "pt", 0.473176473, "pint"),
            U("Cups", "cup", 0.24, "cups"),
            U("Fluid ounces", "fl oz", 0.0295735295625, "floz", "fluid ounce"),
            U("Tablespoons", "tbsp", 0.015, "tablespoon"),
            U("Teaspoons", "tsp", 0.005, "teaspoon"),
        },
        [UnitCategory.Speed] = new[]
        {
            U("Kilometers/hour", "km/h", 1 / 3.6, "kmh", "kph", "kmph"),
            U("Meters/second", "m/s", 1, "mps"),
            U("Miles/hour", "mph", 0.44704, "mi/h"),
            U("Knots", "kn", 1852 / 3600.0, "knot", "knots", "kt"),
        },
        [UnitCategory.Area] = new[]
        {
            U("Square meters", "m²", 1, "m2", "sqm", "sq m"),
            U("Square kilometers", "km²", 1e6, "km2", "sq km"),
            U("Square centimeters", "cm²", 1e-4, "cm2", "sq cm"),
            U("Square feet", "ft²", 0.09290304, "ft2", "sqft", "sq ft"),
            U("Square inches", "in²", 0.00064516, "in2", "sq in"),
            U("Square yards", "yd²", 0.83612736, "yd2", "sq yd"),
            U("Square miles", "mi²", 2_589_988.110336, "mi2", "sq mi"),
            U("Hectares", "ha", 10_000, "hectare"),
            U("Acres", "ac", 4046.8564224, "acre"),
        },
        [UnitCategory.Data] = new[]
        {
            U("Bytes", "B", 1, "byte", "bytes"),
            U("Kilobytes", "KB", 1e3, "kilobyte"),
            U("Megabytes", "MB", 1e6, "megabyte"),
            U("Gigabytes", "GB", 1e9, "gigabyte", "gigs"),
            U("Terabytes", "TB", 1e12, "terabyte"),
            U("Kibibytes", "KiB", 1024, "kibibyte"),
            U("Mebibytes", "MiB", 1024.0 * 1024, "mebibyte"),
            U("Gibibytes", "GiB", 1024.0 * 1024 * 1024, "gibibyte"),
            U("Tebibytes", "TiB", 1024.0 * 1024 * 1024 * 1024, "tebibyte"),
            U("Bits", "bit", 0.125, "bits", "b"),
            U("Megabits", "Mb", 125_000, "megabit", "mbit"),
            U("Gigabits", "Gb", 125_000_000, "gigabit", "gbit"),
        },
        [UnitCategory.Time] = new[]
        {
            U("Milliseconds", "ms", 0.001, "millisecond", "msec"),
            U("Seconds", "s", 1, "sec", "secs", "second"),
            U("Minutes", "min", 60, "mins", "minute"),
            U("Hours", "h", 3600, "hr", "hrs", "hour"),
            U("Days", "d", 86_400, "day", "days"),
            U("Weeks", "wk", 604_800, "week", "weeks", "w"),
            U("Years", "yr", 31_557_600, "year", "years", "y"),
        },
        [UnitCategory.Energy] = new[]
        {
            U("Joules", "J", 1, "joule"),
            U("Kilojoules", "kJ", 1000, "kilojoule"),
            U("Calories", "cal", 4.184, "calorie"),
            U("Kilocalories", "kcal", 4184, "kilocalorie", "kcals", "Cal"),
            U("Watt-hours", "Wh", 3600, "watt hour", "watt-hour"),
            U("Kilowatt-hours", "kWh", 3.6e6, "kilowatt hour", "kilowatt-hour"),
            U("BTU", "BTU", 1055.056, "btus"),
        },
        [UnitCategory.Pressure] = new[]
        {
            U("Pascals", "Pa", 1, "pascal"),
            U("Kilopascals", "kPa", 1000, "kilopascal"),
            U("Hectopascals", "hPa", 100, "hectopascal"),
            U("Bars", "bar", 100_000, "bars"),
            U("Millibars", "mbar", 100, "millibar"),
            U("Atmospheres", "atm", 101_325, "atmosphere"),
            U("PSI", "psi", 6894.757293168),
            U("mmHg", "mmHg", 133.322387415, "torr"),
            U("inHg", "inHg", 3386.388640341),
        },
    };

    // Exact spelling wins first ("Cal" is kcal, "cal" is calories), then a case-folded match with aliases.
    private static readonly Dictionary<string, (UnitCategory, UnitDef)> Exact = BuildMap(folded: false);
    private static readonly Dictionary<string, (UnitCategory, UnitDef)> Folded = BuildMap(folded: true);

    private static Dictionary<string, (UnitCategory, UnitDef)> BuildMap(bool folded)
    {
        var map = new Dictionary<string, (UnitCategory, UnitDef)>();
        foreach (var category in UnitCategoryInfo.All)
            foreach (var def in Units[category])
            {
                var keys = folded ? new[] { def.Symbol, def.Name }.Concat(def.Aliases).Select(k => k.ToLowerInvariant())
                                  : new[] { def.Symbol, def.Name };
                foreach (var key in keys) map.TryAdd(key, (category, def));
            }
        return map;
    }

    public static (UnitCategory Category, UnitDef Unit)? Lookup(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;
        if (Exact.TryGetValue(text, out var hit)) return hit;
        var lower = text.ToLowerInvariant();
        if (Folded.TryGetValue(lower, out hit)) return hit;
        if (lower.StartsWith('°') && Folded.TryGetValue(lower[1..], out hit)) return hit;
        if (lower.EndsWith('s') && Folded.TryGetValue(lower[..^1], out hit)) return hit;
        return null;
    }

    public sealed record Query(double Value, (UnitCategory Category, UnitDef Unit)? From, (UnitCategory Category, UnitDef Unit)? To);

    [GeneratedRegex(@"^([-+]?[0-9.,]*[0-9][0-9.,]*(?:e[-+]?[0-9]+)?)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+(?:to|in|into|as)\s+|\s*(?:->|=>|=|→)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    /// Parses "5", "5 km", "5 km to mi", "100 f in c".
    public static Query? Parse(string input)
    {
        var match = Number().Match(input.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value)) return null;
        var rest = match.Groups[2].Value;
        if (rest.Length == 0) return new Query(value, null, null);
        var sep = Separator().Match(rest);
        if (!sep.Success) return new Query(value, Lookup(rest), null);
        var from = Lookup(rest[..sep.Index]);
        var to = Lookup(rest[(sep.Index + sep.Length)..]);
        if (from is { } f && to is { } t && f.Category != t.Category) to = null;
        return new Query(value, from, to);
    }

    public static double Convert(double value, UnitDef from, UnitDef to) =>
        (value * from.Factor + from.Offset - to.Offset) / to.Factor;

    /// Grouped, with up to 6 decimals (2 from a thousand up, 6 significant digits below 1),
    /// and short scientific notation for very large or tiny values.
    public static string Format(double value)
    {
        if (value == 0) return "0";
        if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(CultureInfo.CurrentCulture);
        var magnitude = Math.Abs(value);
        if (magnitude >= 1e15 || magnitude < 1e-6)
            return value.ToString("G4", CultureInfo.InvariantCulture).Replace("E", "e");
        if (magnitude < 1)
        {
            var decimals = Math.Clamp(5 - (int)Math.Floor(Math.Log10(magnitude)), 0, 15);
            return Math.Round(value, decimals).ToString("#,0.###############", CultureInfo.CurrentCulture);
        }
        return value.ToString(magnitude >= 1000 ? "#,0.##" : "#,0.######", CultureInfo.CurrentCulture);
    }
}
