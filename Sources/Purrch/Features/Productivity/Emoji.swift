import AppKit
import SwiftUI

enum EmojiCategory: String, CaseIterable, Identifiable {
    case smileys = "Smileys"
    case people = "People"
    case nature = "Animals & Nature"
    case food = "Food"
    case activities = "Activities"
    case travel = "Travel"
    case objects = "Objects"
    case symbols = "Symbols"
    case flags = "Flags"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .smileys: "face.smiling"
        case .people: "hand.wave"
        case .nature: "leaf"
        case .food: "fork.knife"
        case .activities: "soccerball"
        case .travel: "car"
        case .objects: "lightbulb"
        case .symbols: "heart"
        case .flags: "flag"
        }
    }
}

struct EmojiEntry: Identifiable, Hashable {
    var id: String { char }
    let char: String
    let name: String
    let category: EmojiCategory
}

/// Emoji list generated from the Unicode tables in the OS.
enum EmojiCatalog {
    static let all: [EmojiEntry] = build()
    static let byChar: [String: EmojiEntry] = Dictionary(all.map { ($0.char, $0) }, uniquingKeysWith: { a, _ in a })

    /// The classic face block comes first so the grid opens on familiar emoji.
    private static let ranges: [ClosedRange<UInt32>] = [
        0x1F600...0x1F64F, 0x1F910...0x1F92F, 0x1F970...0x1F97F,
        0x231A...0x23FF, 0x25AA...0x25FE, 0x2600...0x27BF, 0x2934...0x2935, 0x2B05...0x2B55,
        0x3030...0x3030, 0x303D...0x303D, 0x3297...0x3299,
        0x1F004...0x1F0CF, 0x1F170...0x1F251, 0x1F300...0x1FAFF,
    ]

    private static func build() -> [EmojiEntry] {
        var result: [EmojiEntry] = []
        for range in ranges {
            for value in range {
                guard let scalar = Unicode.Scalar(value) else { continue }
                let p = scalar.properties
                guard p.isEmoji, !p.isEmojiModifier, !(0x1F1E6...0x1F1FF).contains(value),
                      let name = p.name?.lowercased() else { continue }
                // Hair components render as swatches; skip them.
                if (0x1F9B0...0x1F9B3).contains(value) { continue }
                let char = p.isEmojiPresentation ? String(scalar) : String(scalar) + "\u{FE0F}"
                result.append(EmojiEntry(char: char, name: name, category: category(value, name)))
            }
        }
        var seen = Set<String>()
        result = result.filter { seen.insert($0.char).inserted }
        result.append(contentsOf: flags())
        return result
    }

    private static let countryCodes = [
        "US", "GB", "IN", "CA", "AU", "NZ", "IE", "DE", "FR", "IT", "ES", "PT", "NL", "BE", "CH", "AT",
        "SE", "NO", "DK", "FI", "IS", "PL", "CZ", "HU", "RO", "GR", "UA", "TR", "RU", "IL", "SA", "AE",
        "QA", "EG", "ZA", "NG", "KE", "MA", "JP", "CN", "KR", "TW", "HK", "SG", "MY", "ID", "TH", "VN",
        "PH", "PK", "BD", "LK", "NP", "BR", "MX", "AR", "CL", "CO", "PE", "EU", "UN",
    ]

    private static func flags() -> [EmojiEntry] {
        var list: [EmojiEntry] = countryCodes.map { code in
            let flag = code.unicodeScalars.compactMap { Unicode.Scalar(0x1F1E6 + $0.value - 65) }
                .map(String.init).joined()
            let country = Locale.current.localizedString(forRegionCode: code) ?? code
            return EmojiEntry(char: flag, name: "flag \(country.lowercased()) \(code.lowercased())", category: .flags)
        }
        list.insert(EmojiEntry(char: "🏳️‍🌈", name: "rainbow flag pride", category: .flags), at: 0)
        list.insert(EmojiEntry(char: "🏳️‍⚧️", name: "transgender flag", category: .flags), at: 1)
        list.insert(EmojiEntry(char: "🏴‍☠️", name: "pirate flag", category: .flags), at: 2)
        return list
    }

    private static let peopleWords: Set<String> = [
        "hand", "hands", "person", "people", "man", "woman", "men", "women", "boy", "girl", "baby", "child",
        "adult", "finger", "thumbs", "fist", "eye", "eyes", "ear", "nose", "mouth", "tongue", "lips", "leg",
        "foot", "family", "kiss", "couple", "prince", "princess", "santa", "mage", "fairy", "vampire",
        "merperson", "elf", "genie", "zombie", "guardsman", "detective", "worker", "dancer", "bride",
        "pregnant", "palms", "clapping", "waving", "writing", "nail", "selfie", "flexed", "brain", "tooth",
        "bone", "footprints", "silhouette", "busts", "bust", "ninja", "troll", "superhero", "supervillain",
        "older", "princess", "crown", "biting", "lungs", "anatomical",
    ]

    private static func category(_ v: UInt32, _ name: String) -> EmojiCategory {
        let words = Set(name.split(separator: " ").map(String.init))
        func within(_ ranges: ClosedRange<UInt32>...) -> Bool { ranges.contains { $0.contains(v) } }

        // Smileys & emotion
        let natureFace = within(0x1F400...0x1F43F, 0x1F300...0x1F32C, 0x1F980...0x1F9AE)
        if (words.contains("face") && !name.contains("clock") && !natureFace)
            || words.contains("smiling") || words.contains("grinning")
            || within(0x1F600...0x1F644, 0x1F910...0x1F917, 0x1F920...0x1F925, 0x1F927...0x1F92F,
                      0x1F970...0x1F97A, 0x1FAE0...0x1FAE8, 0x1F47B...0x1F47F, 0x1F480...0x1F480,
                      0x1F4A9...0x1F4A9, 0x1F648...0x1F64A, 0x1F48B...0x1F48C, 0x1F4AF...0x1F4AF,
                      0x1F4A2...0x1F4A6, 0x1F4AB...0x1F4AD, 0x1F573...0x1F573, 0x1F5E8...0x1F5EF)
            || (words.contains("heart") && !within(0x2665...0x2665, 0x1FAC0...0x1FAC0)) {
            return .smileys
        }
        // People & body
        if within(0x1F442...0x1F487, 0x1F645...0x1F647, 0x1F64B...0x1F64F, 0x1F918...0x1F91F, 0x1F926...0x1F926,
                  0x1F930...0x1F939, 0x1F93C...0x1F93E, 0x1F9B5...0x1F9BB, 0x1F9CD...0x1F9DF,
                  0x1FAC3...0x1FAC5, 0x1FAF0...0x1FAF8, 0x1F6B4...0x1F6B6, 0x1F6C0...0x1F6C0, 0x1F6CC...0x1F6CC,
                  0x1F3C3...0x1F3C4, 0x1F3C7...0x1F3C7, 0x1F3CA...0x1F3CC, 0x1F574...0x1F575, 0x1F57A...0x1F57A,
                  0x1F590...0x1F590, 0x1F595...0x1F596, 0x1F4AA...0x1F4AA, 0x270A...0x270D, 0x261D...0x261D,
                  0x26F9...0x26F9, 0x1F9E0...0x1F9E0, 0x1FAC0...0x1FAC2, 0x1F463...0x1F463, 0x1F5E3...0x1F5E3)
            || (!words.isDisjoint(with: peopleWords) && within(0x1F400...0x1FAFF) && !within(0x1F400...0x1F43F)
                && !within(0x1F680...0x1F6FF) && !within(0x1F950...0x1F9AE)) {
            return .people
        }
        // Animals & nature
        if within(0x1F400...0x1F43F, 0x1F980...0x1F9AE, 0x1F330...0x1F335, 0x1F337...0x1F344, 0x1F490...0x1F490,
                  0x1FAB0...0x1FABF, 0x1F300...0x1F320, 0x1F321...0x1F32C, 0x2600...0x2604, 0x26C4...0x26C5,
                  0x26A1...0x26A1, 0x2744...0x2744, 0x2614...0x2614, 0x1F4AE...0x1F4AE, 0x1F54A...0x1F54A,
                  0x1F577...0x1F578, 0x1FAD7...0x1FAD7, 0x1F3F5...0x1F3F5, 0x1FA77...0x1FA77, 0x1F9A0...0x1F9A0) {
            return .nature
        }
        // Food & drink
        if within(0x1F32D...0x1F32F, 0x1F336...0x1F336, 0x1F345...0x1F37F, 0x1F950...0x1F96F, 0x1F9C0...0x1F9CB,
                  0x1FAD0...0x1FADB, 0x2615...0x2615, 0x1F37D...0x1F37D) {
            return .food
        }
        // Activities
        if within(0x1F380...0x1F393, 0x1F3A0...0x1F3C2, 0x1F3C5...0x1F3C6, 0x1F3C8...0x1F3C9, 0x1F3CF...0x1F3D3,
                  0x1F3F8...0x1F3F9, 0x26BD...0x26BE, 0x26F3...0x26F3, 0x26F8...0x26F8, 0x1F93A...0x1F93B,
                  0x1F93F...0x1F94F, 0x1F9E9...0x1F9E9, 0x1F9F5...0x1F9F6, 0x1FA80...0x1FA8F, 0x1F004...0x1F0CF,
                  0x2660...0x2667, 0x265F...0x265F, 0x1F396...0x1F39F, 0x1F9F8...0x1F9F8, 0x1FAA9...0x1FAA9,
                  0x1F52B...0x1F52B) {
            return .activities
        }
        // Travel & places
        if within(0x1F680...0x1F6FF, 0x1F3D4...0x1F3F0, 0x1F5FA...0x1F5FF, 0x26EA...0x26EA, 0x26F0...0x26FA,
                  0x2708...0x2708, 0x1F550...0x1F567, 0x2693...0x2693, 0x231A...0x231B, 0x23F0...0x23F3,
                  0x1F6E0...0x1F6E5, 0x1F3CD...0x1F3CE, 0x1F9F3...0x1F9F3, 0x1F488...0x1F488, 0x1FAA7...0x1FAA7) {
            return .travel
        }
        // Objects
        if within(0x1F4B0...0x1F4FF, 0x1F507...0x1F514, 0x1F516...0x1F517, 0x1F526...0x1F52A, 0x1F52C...0x1F52E,
                  0x1F56F...0x1F5E2, 0x1F5E4...0x1F5E7, 0x1F5F0...0x1F5F9, 0x1F453...0x1F462, 0x1F48D...0x1F48E,
                  0x1F9E2...0x1F9E8, 0x1F9EA...0x1F9F2, 0x1F9F4...0x1F9F4, 0x1F9F7...0x1F9F7, 0x1F9F9...0x1F9FF,
                  0x1FA70...0x1FA76, 0x1FA78...0x1FA7F, 0x1FA90...0x1FAA8, 0x1FAAA...0x1FAAF, 0x2328...0x2328,
                  0x260E...0x260E, 0x2702...0x2702, 0x2709...0x2709, 0x270F...0x270F, 0x2712...0x2712,
                  0x1F3A5...0x1F3A5, 0x2692...0x2692, 0x2694...0x2697, 0x2699...0x2699, 0x26CF...0x26CF,
                  0x26D1...0x26D1, 0x26B0...0x26B1, 0x1F45A...0x1F45A, 0x1F4A1...0x1F4A1, 0x1F4A3...0x1F4A3,
                  0x1F3EE...0x1F3EE, 0x1F399...0x1F39B, 0x1F39E...0x1F39F) {
            return .objects
        }
        return .symbols
    }
}

@MainActor
final class EmojiRecents: ObservableObject {
    static let shared = EmojiRecents()
    @Published private(set) var items: [String] = Store.load("emoji-recent", default: [String]())

    func use(_ emoji: String) {
        items.removeAll { $0 == emoji }
        items.insert(emoji, at: 0)
        if items.count > 30 { items.removeLast(items.count - 30) }
        Store.save("emoji-recent", items)
    }
}

struct EmojiModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var recents = EmojiRecents.shared
    @State private var query = ""
    @State private var category: EmojiCategory = .smileys
    @State private var hovered: EmojiEntry?
    @State private var toast: String?

    private var visible: [EmojiEntry] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        if q.isEmpty { return EmojiCatalog.all.filter { $0.category == category } }
        let terms = q.split(separator: " ").map(String.init)
        return EmojiCatalog.all.filter { entry in terms.allSatisfy { entry.name.contains($0) } || entry.char == q }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                SearchField(text: $query, placeholder: "Search emoji")
                    .frame(width: 250)
                HStack(spacing: 2) {
                    ForEach(EmojiCategory.allCases) { cat in
                        let selected = category == cat && query.isEmpty
                        Button {
                            category = cat
                            query = ""
                        } label: {
                            Image(systemName: cat.icon)
                                .font(.system(size: 12, weight: .semibold))
                                .foregroundStyle(selected ? theme.accent : theme.secondaryText)
                                .frame(width: 30, height: 28)
                                .background(RoundedRectangle(cornerRadius: 8)
                                    .fill(selected ? theme.accent.opacity(0.16) : .clear))
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help(cat.rawValue)
                    }
                }
                Spacer(minLength: 0)
            }

            if !recents.items.isEmpty && query.isEmpty {
                HStack(spacing: 4) {
                    Text("RECENT")
                        .font(.system(size: 10, weight: .semibold))
                        .tracking(0.8)
                        .foregroundStyle(theme.secondaryText)
                        .frame(width: 52, alignment: .leading)
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 2) {
                            ForEach(recents.items, id: \.self) { char in
                                cell(EmojiCatalog.byChar[char] ?? EmojiEntry(char: char, name: "", category: .symbols),
                                     size: 28)
                            }
                        }
                    }
                }
                Divider().overlay(theme.border.opacity(0.5))
            }

            let items = visible
            if items.isEmpty {
                EmptyState(icon: "face.dashed", title: "No emoji found", subtitle: "Try another word.")
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 36, maximum: 40), spacing: 2)], spacing: 2) {
                        ForEach(items) { cell($0, size: 36) }
                    }
                }
            }

            HStack {
                Text(hovered.map { "\($0.char)  \($0.name.capitalized)" }
                     ?? (query.isEmpty ? category.rawValue : "\(items.count) results"))
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(theme.secondaryText)
                    .lineLimit(1)
                Spacer()
                Text("Click to copy")
                    .font(.system(size: 11))
                    .foregroundStyle(theme.secondaryText.opacity(0.7))
            }
        }
        .overlay(alignment: .bottom) {
            if let toast {
                Text("Copied \(toast) 👍")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(theme.background)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 7)
                    .background(Capsule().fill(theme.accent))
                    .themeGlow(theme)
                    .padding(.bottom, 22)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
    }

    private func cell(_ entry: EmojiEntry, size: CGFloat) -> some View {
        EmojiCell(entry: entry, size: size, hovered: hovered?.char == entry.char) { inside in
            if inside { hovered = entry } else if hovered?.char == entry.char { hovered = nil }
        } onTap: {
            copy(entry)
        }
    }

    private func copy(_ entry: EmojiEntry) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(entry.char, forType: .string)
        recents.use(entry.char)
        withAnimation(.spring(response: 0.3)) { toast = entry.char }
        let char = entry.char
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
            withAnimation(.easeOut(duration: 0.2)) { if toast == char { toast = nil } }
        }
    }
}

private struct EmojiCell: View {
    @Environment(\.theme) private var theme
    let entry: EmojiEntry
    let size: CGFloat
    let hovered: Bool
    let onHover: (Bool) -> Void
    let onTap: () -> Void

    var body: some View {
        Text(entry.char)
            .font(.system(size: size * 0.66))
            .frame(width: size, height: size)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(hovered ? theme.accent.opacity(0.2) : .clear))
            .scaleEffect(hovered ? 1.12 : 1)
            .animation(.easeOut(duration: 0.12), value: hovered)
            .contentShape(Rectangle())
            .onHover(perform: onHover)
            .onTapGesture(perform: onTap)
            .onDrag { NSItemProvider(object: entry.char as NSString) }
    }
}
