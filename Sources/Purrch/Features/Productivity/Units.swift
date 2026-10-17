import AppKit
import SwiftUI

struct UnitDef: Identifiable, Hashable {
    let name: String
    let symbol: String
    let unit: Dimension
    let aliases: [String]
    var id: String { name }

    init(_ name: String, _ symbol: String, _ unit: Dimension, _ aliases: [String] = []) {
        self.name = name
        self.symbol = symbol
        self.unit = unit
        self.aliases = aliases
    }

    static func == (a: UnitDef, b: UnitDef) -> Bool { a.name == b.name }
    func hash(into hasher: inout Hasher) { hasher.combine(name) }
}

enum UnitCategory: String, CaseIterable, Identifiable {
    case length, mass, temperature, volume, speed, area, data, time, energy, pressure
    var id: String { rawValue }

    var title: String {
        switch self {
        case .data: "Data"
        default: rawValue.capitalized
        }
    }

    var icon: String {
        switch self {
        case .length: "ruler"
        case .mass: "scalemass"
        case .temperature: "thermometer.medium"
        case .volume: "drop"
        case .speed: "speedometer"
        case .area: "square.dashed"
        case .data: "externaldrive"
        case .time: "clock"
        case .energy: "bolt"
        case .pressure: "gauge.with.dots.needle.33percent"
        }
    }

    var units: [UnitDef] { UnitCatalog.units[self] ?? [] }
    var defaultPair: (String, String) {
        switch self {
        case .length: ("Kilometers", "Miles")
        case .mass: ("Kilograms", "Pounds")
        case .temperature: ("Celsius", "Fahrenheit")
        case .volume: ("Liters", "US gallons")
        case .speed: ("Kilometers/hour", "Miles/hour")
        case .area: ("Square meters", "Square feet")
        case .data: ("Gigabytes", "Megabytes")
        case .time: ("Hours", "Minutes")
        case .energy: ("Kilocalories", "Kilojoules")
        case .pressure: ("Bars", "PSI")
        }
    }
}

enum UnitCatalog {
    static let units: [UnitCategory: [UnitDef]] = [
        .length: [
            UnitDef("Kilometers", "km", UnitLength.kilometers, ["kilometer", "kilometre", "kilometres", "kms"]),
            UnitDef("Meters", "m", UnitLength.meters, ["meter", "metre", "metres"]),
            UnitDef("Centimeters", "cm", UnitLength.centimeters, ["centimeter", "centimetre"]),
            UnitDef("Millimeters", "mm", UnitLength.millimeters, ["millimeter", "millimetre"]),
            UnitDef("Miles", "mi", UnitLength.miles, ["mile", "miles"]),
            UnitDef("Yards", "yd", UnitLength.yards, ["yard", "yds"]),
            UnitDef("Feet", "ft", UnitLength.feet, ["foot", "feet", "'"]),
            UnitDef("Inches", "in", UnitLength.inches, ["inch", "inches", "\""]),
            UnitDef("Nautical miles", "nmi", UnitLength.nauticalMiles, ["nautical mile", "nm"]),
        ],
        .mass: [
            UnitDef("Kilograms", "kg", UnitMass.kilograms, ["kilogram", "kilo", "kilos", "kgs"]),
            UnitDef("Grams", "g", UnitMass.grams, ["gram", "gr"]),
            UnitDef("Milligrams", "mg", UnitMass.milligrams, ["milligram"]),
            UnitDef("Tonnes", "t", UnitMass.metricTons, ["tonne", "ton", "tons", "metric ton"]),
            UnitDef("Pounds", "lb", UnitMass.pounds, ["pound", "lbs"]),
            UnitDef("Ounces", "oz", UnitMass.ounces, ["ounce"]),
            UnitDef("Stones", "st", UnitMass.stones, ["stone"]),
        ],
        .temperature: [
            UnitDef("Celsius", "°C", UnitTemperature.celsius, ["c", "celsius", "degc", "centigrade"]),
            UnitDef("Fahrenheit", "°F", UnitTemperature.fahrenheit, ["f", "fahrenheit", "degf"]),
            UnitDef("Kelvin", "K", UnitTemperature.kelvin, ["k", "kelvin"]),
        ],
        .volume: [
            UnitDef("Liters", "L", UnitVolume.liters, ["l", "liter", "litre", "litres"]),
            UnitDef("Milliliters", "mL", UnitVolume.milliliters, ["ml", "milliliter", "millilitre"]),
            UnitDef("Cubic meters", "m³", UnitVolume.cubicMeters, ["m3", "cubic meter"]),
            UnitDef("US gallons", "gal", UnitVolume.gallons, ["gallon", "gallons", "us gal"]),
            UnitDef("Imperial gallons", "imp gal", UnitVolume.imperialGallons, ["imperial gallon", "uk gal"]),
            UnitDef("Quarts", "qt", UnitVolume.quarts, ["quart"]),
            UnitDef("Pints", "pt", UnitVolume.pints, ["pint"]),
            UnitDef("Cups", "cup", UnitVolume.cups, ["cups"]),
            UnitDef("Fluid ounces", "fl oz", UnitVolume.fluidOunces, ["floz", "fluid ounce"]),
            UnitDef("Tablespoons", "tbsp", UnitVolume.tablespoons, ["tablespoon"]),
            UnitDef("Teaspoons", "tsp", UnitVolume.teaspoons, ["teaspoon"]),
        ],
        .speed: [
            UnitDef("Kilometers/hour", "km/h", UnitSpeed.kilometersPerHour, ["kmh", "kph", "kmph"]),
            UnitDef("Meters/second", "m/s", UnitSpeed.metersPerSecond, ["mps"]),
            UnitDef("Miles/hour", "mph", UnitSpeed.milesPerHour, ["mi/h"]),
            UnitDef("Knots", "kn", UnitSpeed.knots, ["knot", "knots", "kt"]),
        ],
        .area: [
            UnitDef("Square meters", "m²", UnitArea.squareMeters, ["m2", "sqm", "sq m"]),
            UnitDef("Square kilometers", "km²", UnitArea.squareKilometers, ["km2", "sq km"]),
            UnitDef("Square centimeters", "cm²", UnitArea.squareCentimeters, ["cm2", "sq cm"]),
            UnitDef("Square feet", "ft²", UnitArea.squareFeet, ["ft2", "sqft", "sq ft"]),
            UnitDef("Square inches", "in²", UnitArea.squareInches, ["in2", "sq in"]),
            UnitDef("Square yards", "yd²", UnitArea.squareYards, ["yd2", "sq yd"]),
            UnitDef("Square miles", "mi²", UnitArea.squareMiles, ["mi2", "sq mi"]),
            UnitDef("Hectares", "ha", UnitArea.hectares, ["hectare"]),
            UnitDef("Acres", "ac", UnitArea.acres, ["acre"]),
        ],
        .data: [
            UnitDef("Bytes", "B", UnitInformationStorage.bytes, ["byte", "bytes"]),
            UnitDef("Kilobytes", "KB", UnitInformationStorage.kilobytes, ["kilobyte"]),
            UnitDef("Megabytes", "MB", UnitInformationStorage.megabytes, ["megabyte"]),
            UnitDef("Gigabytes", "GB", UnitInformationStorage.gigabytes, ["gigabyte", "gigs"]),
            UnitDef("Terabytes", "TB", UnitInformationStorage.terabytes, ["terabyte"]),
            UnitDef("Kibibytes", "KiB", UnitInformationStorage.kibibytes, ["kibibyte"]),
            UnitDef("Mebibytes", "MiB", UnitInformationStorage.mebibytes, ["mebibyte"]),
            UnitDef("Gibibytes", "GiB", UnitInformationStorage.gibibytes, ["gibibyte"]),
            UnitDef("Tebibytes", "TiB", UnitInformationStorage.tebibytes, ["tebibyte"]),
            UnitDef("Bits", "bit", UnitInformationStorage.bits, ["bits", "b"]),
            UnitDef("Megabits", "Mb", UnitInformationStorage.megabits, ["megabit", "mbit"]),
            UnitDef("Gigabits", "Gb", UnitInformationStorage.gigabits, ["gigabit", "gbit"]),
        ],
        .time: [
            UnitDef("Milliseconds", "ms", UnitDuration.milliseconds, ["millisecond", "msec"]),
            UnitDef("Seconds", "s", UnitDuration.seconds, ["sec", "secs", "second"]),
            UnitDef("Minutes", "min", UnitDuration.minutes, ["mins", "minute"]),
            UnitDef("Hours", "h", UnitDuration.hours, ["hr", "hrs", "hour"]),
            UnitDef("Days", "d", UnitDuration(symbol: "d", converter: UnitConverterLinear(coefficient: 86_400)),
                    ["day", "days"]),
            UnitDef("Weeks", "wk", UnitDuration(symbol: "wk", converter: UnitConverterLinear(coefficient: 604_800)),
                    ["week", "weeks", "w"]),
            UnitDef("Years", "yr", UnitDuration(symbol: "yr", converter: UnitConverterLinear(coefficient: 31_557_600)),
                    ["year", "years", "y"]),
        ],
        .energy: [
            UnitDef("Joules", "J", UnitEnergy.joules, ["joule"]),
            UnitDef("Kilojoules", "kJ", UnitEnergy.kilojoules, ["kilojoule"]),
            UnitDef("Calories", "cal", UnitEnergy.calories, ["calorie"]),
            UnitDef("Kilocalories", "kcal", UnitEnergy.kilocalories, ["kilocalorie", "kcals", "Cal"]),
            UnitDef("Watt-hours", "Wh", UnitEnergy(symbol: "Wh", converter: UnitConverterLinear(coefficient: 3_600)),
                    ["watt hour", "watt-hour"]),
            UnitDef("Kilowatt-hours", "kWh", UnitEnergy.kilowattHours, ["kilowatt hour", "kilowatt-hour"]),
            UnitDef("BTU", "BTU", UnitEnergy(symbol: "BTU", converter: UnitConverterLinear(coefficient: 1_055.056)),
                    ["btus"]),
        ],
        .pressure: [
            UnitDef("Pascals", "Pa", UnitPressure.newtonsPerMetersSquared, ["pascal"]),
            UnitDef("Kilopascals", "kPa", UnitPressure.kilopascals, ["kilopascal"]),
            UnitDef("Hectopascals", "hPa", UnitPressure.hectopascals, ["hectopascal"]),
            UnitDef("Bars", "bar", UnitPressure.bars, ["bars"]),
            UnitDef("Millibars", "mbar", UnitPressure.millibars, ["millibar"]),
            UnitDef("Atmospheres", "atm",
                    UnitPressure(symbol: "atm", converter: UnitConverterLinear(coefficient: 101_325)),
                    ["atmosphere"]),
            UnitDef("PSI", "psi", UnitPressure.poundsForcePerSquareInch, []),
            UnitDef("mmHg", "mmHg", UnitPressure.millimetersOfMercury, ["torr"]),
            UnitDef("inHg", "inHg", UnitPressure.inchesOfMercury, []),
        ],
    ]

    private static let exact: [String: (UnitCategory, UnitDef)] = {
        var map: [String: (UnitCategory, UnitDef)] = [:]
        for category in UnitCategory.allCases {
            for def in category.units {
                for key in [def.symbol, def.name] where map[key] == nil { map[key] = (category, def) }
            }
        }
        return map
    }()

    private static let folded: [String: (UnitCategory, UnitDef)] = {
        var map: [String: (UnitCategory, UnitDef)] = [:]
        for category in UnitCategory.allCases {
            for def in category.units {
                for key in [def.symbol, def.name] + def.aliases {
                    let k = key.lowercased()
                    if map[k] == nil { map[k] = (category, def) }
                }
            }
        }
        return map
    }()

    static func lookup(_ raw: String) -> (UnitCategory, UnitDef)? {
        let text = raw.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return nil }
        if let hit = exact[text] { return hit }
        let lower = text.lowercased()
        if let hit = folded[lower] { return hit }
        if lower.hasPrefix("°"), let hit = folded[String(lower.dropFirst())] { return hit }
        if lower.hasSuffix("s"), let hit = folded[String(lower.dropLast())] { return hit }
        return nil
    }

    struct Query {
        var value: Double
        var from: (UnitCategory, UnitDef)?
        var to: (UnitCategory, UnitDef)?
    }

    /// Parses "5", "5 km", "5 km to mi", "100 f in c".
    static func parse(_ input: String) -> Query? {
        let text = input.trimmingCharacters(in: .whitespaces)
        let pattern = #"^([-+]?[\d.,]*\d[\d.,]*(?:e[-+]?\d+)?)\s*(.*)$"#
        guard let regex = try? NSRegularExpression(pattern: pattern, options: .caseInsensitive),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let numberRange = Range(match.range(at: 1), in: text) else { return nil }
        let number = text[numberRange].replacingOccurrences(of: ",", with: "")
        guard let value = Double(number) else { return nil }
        let rest = Range(match.range(at: 2), in: text).map { String(text[$0]) } ?? ""
        var query = Query(value: value)
        guard !rest.isEmpty else { return query }
        let separators = #"\s+(?:to|in|into|as)\s+|\s*(?:->|=>|=|→)\s*"#
        if let sep = rest.range(of: separators, options: [.regularExpression, .caseInsensitive]) {
            query.from = lookup(String(rest[..<sep.lowerBound]))
            query.to = lookup(String(rest[sep.upperBound...]))
            if let f = query.from, let t = query.to, f.0 != t.0 { query.to = nil }
        } else {
            query.from = lookup(rest)
        }
        return query
    }

    static func format(_ value: Double) -> String {
        if value == 0 { return "0" }
        let magnitude = abs(value)
        if magnitude >= 1e15 || magnitude < 1e-6 {
            return String(format: "%.4g", value)
        }
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = true
        formatter.maximumSignificantDigits = 9
        formatter.usesSignificantDigits = magnitude < 1
        formatter.maximumFractionDigits = magnitude >= 1000 ? 2 : 6
        if magnitude < 1 { formatter.maximumSignificantDigits = 6 }
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }
}

struct UnitsModule: View {
    @Environment(\.theme) private var theme
    @AppStorage("units.category") private var category: UnitCategory = .length
    @AppStorage("units.from") private var fromName = "Kilometers"
    @AppStorage("units.to") private var toName = "Miles"
    @State private var input = "1"

    private var fromUnit: UnitDef {
        category.units.first { $0.name == fromName } ?? category.units[0]
    }
    private var toUnit: UnitDef {
        category.units.first { $0.name == toName } ?? category.units[min(1, category.units.count - 1)]
    }
    private var value: Double? { UnitCatalog.parse(input)?.value }

    private func convert(_ value: Double, to unit: UnitDef) -> Double {
        Measurement<Dimension>(value: value, unit: fromUnit.unit).converted(to: unit.unit).value
    }

    var body: some View {
        VStack(spacing: 8) {
            categoryBar
            HStack(spacing: 10) {
                converter
                    .frame(width: 330)
                allUnits
            }
        }
        .onChange(of: input) { _, newValue in applyParsed(newValue) }
    }

    private var categoryBar: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 5) {
                ForEach(UnitCategory.allCases) { item in
                    let active = item == category
                    Button { select(item) } label: {
                        HStack(spacing: 4) {
                            Image(systemName: item.icon).font(.system(size: 10, weight: .semibold))
                            Text(item.title)
                        }
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(active ? theme.background : theme.text.opacity(0.85))
                        .padding(.horizontal, 9)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(active ? theme.accent(for: .units) : theme.text.opacity(0.08)))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .scrollIndicators(.never)
    }

    private var converter: some View {
        VStack(alignment: .leading, spacing: 8) {
            SearchField(text: $input, placeholder: "5 km to mi", icon: "number")
            HStack(spacing: 6) {
                unitPicker(selection: $fromName)
                IconButton(systemName: "arrow.left.arrow.right", size: 26, help: "Swap") {
                    let old = fromName
                    fromName = toUnit.name
                    toName = old
                }
                unitPicker(selection: $toName)
            }
            Spacer(minLength: 0)
            VStack(alignment: .leading, spacing: 2) {
                if let value {
                    Text("\(UnitCatalog.format(value)) \(fromUnit.symbol) =")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(theme.secondaryText)
                        .lineLimit(1)
                    let result = convert(value, to: toUnit)
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(UnitCatalog.format(result))
                            .font(.system(size: 38, weight: .bold, design: .rounded))
                            .foregroundStyle(theme.accent(for: .units))
                            .themeGlow(theme, radius: 4)
                            .monospacedDigit()
                            .lineLimit(1)
                            .minimumScaleFactor(0.4)
                            .textSelection(.enabled)
                        Text(toUnit.symbol)
                            .font(.system(size: 16, weight: .semibold, design: .rounded))
                            .foregroundStyle(theme.secondaryText)
                    }
                    .onTapGesture { copy(UnitCatalog.format(result)) }
                    .help("Click to copy")
                } else {
                    Text("Type a number, or something like “100 f in c”.")
                        .font(.system(size: 12))
                        .foregroundStyle(theme.secondaryText)
                }
            }
        }
        .padding(12)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(theme.surface))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(theme.border, lineWidth: theme.glow ? 1.2 : 0.8))
    }

    private func unitPicker(selection: Binding<String>) -> some View {
        Picker("", selection: selection) {
            ForEach(category.units) { def in
                Text("\(def.name) (\(def.symbol))").tag(def.name)
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .frame(maxWidth: .infinity)
    }

    private var allUnits: some View {
        Card("All \(category.title.lowercased()) units", icon: category.icon) {
            if let value {
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(category.units) { def in
                            let converted = convert(value, to: def)
                            HStack {
                                Text(def.name)
                                    .font(.system(size: 12))
                                    .foregroundStyle(theme.secondaryText)
                                Spacer()
                                Text(UnitCatalog.format(converted))
                                    .font(.system(size: 13, weight: .semibold, design: .rounded))
                                    .monospacedDigit()
                                    .foregroundStyle(def == toUnit ? theme.accent(for: .units) : theme.text)
                                Text(def.symbol)
                                    .font(.system(size: 11))
                                    .foregroundStyle(theme.secondaryText)
                                    .frame(width: 44, alignment: .leading)
                            }
                            .padding(.horizontal, 6)
                            .padding(.vertical, 3)
                            .background(RoundedRectangle(cornerRadius: 6)
                                .fill(def == fromUnit ? theme.text.opacity(0.07) : .clear))
                            .contentShape(Rectangle())
                            .onTapGesture { copy(UnitCatalog.format(converted)) }
                            .help("Click to copy")
                        }
                    }
                }
                .scrollIndicators(.never)
            } else {
                EmptyState(icon: category.icon, title: "Enter a value")
            }
        }
    }

    private func select(_ newCategory: UnitCategory) {
        guard newCategory != category else { return }
        category = newCategory
        (fromName, toName) = newCategory.defaultPair
    }

    private func applyParsed(_ text: String) {
        guard let query = UnitCatalog.parse(text), let from = query.from ?? query.to else { return }
        if from.0 != category { category = from.0 }
        if let f = query.from { fromName = f.1.name }
        if let t = query.to {
            toName = t.1.name
        } else if toName == fromName || !category.units.contains(where: { $0.name == toName }) {
            toName = category.units.first { $0.name != fromName }?.name ?? toName
        }
    }

    private func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}
