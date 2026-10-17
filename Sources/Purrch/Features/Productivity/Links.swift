import AppKit
import SwiftUI

enum SearchEngine: String, CaseIterable, Identifiable {
    case google, duckduckgo, bing, perplexity
    var id: String { rawValue }

    var title: String {
        switch self {
        case .google: "Google"
        case .duckduckgo: "DuckDuckGo"
        case .bing: "Bing"
        case .perplexity: "Perplexity"
        }
    }

    func url(for query: String) -> URL? {
        var components: URLComponents
        switch self {
        case .google: components = URLComponents(string: "https://www.google.com/search")!
        case .duckduckgo: components = URLComponents(string: "https://duckduckgo.com/")!
        case .bing: components = URLComponents(string: "https://www.bing.com/search")!
        case .perplexity: components = URLComponents(string: "https://www.perplexity.ai/search")!
        }
        components.queryItems = [URLQueryItem(name: "q", value: query)]
        return components.url
    }
}

struct SavedSite: Codable, Identifiable, Equatable {
    var id = UUID()
    var title: String
    var url: String

    var host: String {
        LinkParsing.url(from: url)?.host ?? url
    }

    static let defaults: [SavedSite] = [
        SavedSite(title: "GitHub", url: "https://github.com"),
        SavedSite(title: "YouTube", url: "https://www.youtube.com"),
        SavedSite(title: "Gmail", url: "https://mail.google.com"),
        SavedSite(title: "Claude", url: "https://claude.ai"),
        SavedSite(title: "Google Drive", url: "https://drive.google.com"),
        SavedSite(title: "Hacker News", url: "https://news.ycombinator.com"),
    ]
}

enum LinkParsing {
    /// Returns a URL if the text looks like an address rather than a search query.
    static func url(from raw: String) -> URL? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !text.contains(" ") else { return nil }
        let lower = text.lowercased()
        if lower.hasPrefix("http://") || lower.hasPrefix("https://") || lower.hasPrefix("file://") {
            return URL(string: text)
        }
        let hostPart = lower.split(separator: "/", maxSplits: 1).first.map(String.init) ?? lower
        let isLocal = hostPart.hasPrefix("localhost") || hostPart.range(of: #"^\d{1,3}(\.\d{1,3}){3}(:\d+)?$"#,
                                                                       options: .regularExpression) != nil
        let looksLikeDomain = hostPart.range(of: #"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(:\d+)?$"#,
                                             options: .regularExpression) != nil
        guard isLocal || looksLikeDomain else { return nil }
        return URL(string: (isLocal ? "http://" : "https://") + text)
    }
}

@MainActor
final class LinksStore: ObservableObject {
    static let shared = LinksStore()
    @Published var sites: [SavedSite] { didSet { Store.save("links", sites) } }
    private init() { sites = Store.load("links", default: SavedSite.defaults) }
}

struct LinksModule: View {
    @Environment(\.theme) private var theme
    @StateObject private var store = LinksStore.shared
    @AppStorage("links.engine") private var engine: SearchEngine = .google
    @State private var query = ""
    @State private var editing: SavedSite?
    @State private var isNew = false

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 8) {
                SearchField(text: $query, placeholder: "Search \(engine.title) or type a URL",
                            icon: LinkParsing.url(from: query) == nil ? "magnifyingglass" : "globe",
                            onSubmit: submit)
                Menu {
                    Picker("Engine", selection: $engine) {
                        ForEach(SearchEngine.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.inline)
                } label: {
                    HStack(spacing: 4) {
                        Text(engine.title)
                        Image(systemName: "chevron.down").font(.system(size: 9, weight: .bold))
                    }
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .background(Capsule().fill(theme.text.opacity(0.1)))
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                IconButton(systemName: "plus", size: 32, prominent: true, help: "Add site") {
                    isNew = true
                    editing = SavedSite(title: "", url: "")
                }
            }
            ZStack {
                grid
                if let site = editing {
                    SiteEditor(site: site, isNew: isNew) { saved in
                        if let saved { upsert(saved) }
                        editing = nil
                    }
                    .transition(.opacity.combined(with: .scale(scale: 0.97)))
                }
            }
            .animation(.easeOut(duration: 0.15), value: editing)
        }
    }

    private var grid: some View {
        Group {
            if store.sites.isEmpty {
                EmptyState(icon: "link", title: "No saved sites", subtitle: "Press + to add one")
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 104, maximum: 140), spacing: 8)], spacing: 8) {
                        ForEach(store.sites) { site in
                            SiteTile(site: site,
                                     onOpen: { open(site) },
                                     onEdit: { isNew = false; editing = site },
                                     onRemove: { store.sites.removeAll { $0.id == site.id } })
                        }
                    }
                }
                .scrollIndicators(.never)
            }
        }
        .opacity(editing == nil ? 1 : 0.25)
        .allowsHitTesting(editing == nil)
    }

    private func submit() {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        let target = LinkParsing.url(from: text) ?? engine.url(for: text)
        if let target { NSWorkspace.shared.open(target) }
        query = ""
    }

    private func open(_ site: SavedSite) {
        if let url = LinkParsing.url(from: site.url) ?? URL(string: site.url) { NSWorkspace.shared.open(url) }
    }

    private func upsert(_ site: SavedSite) {
        if let index = store.sites.firstIndex(where: { $0.id == site.id }) {
            store.sites[index] = site
        } else {
            store.sites.append(site)
        }
    }
}

private struct SiteTile: View {
    @Environment(\.theme) private var theme
    let site: SavedSite
    let onOpen: () -> Void
    let onEdit: () -> Void
    let onRemove: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: onOpen) {
            VStack(spacing: 7) {
                AsyncImage(url: URL(string: "https://www.google.com/s2/favicons?domain=\(site.host)&sz=64")) { phase in
                    if let image = phase.image {
                        image.resizable().interpolation(.high).scaledToFit()
                    } else {
                        Text(String(site.title.prefix(1)).uppercased())
                            .font(.system(size: 16, weight: .bold, design: .rounded))
                            .foregroundStyle(theme.accent(for: .links))
                    }
                }
                .frame(width: 26, height: 26)
                .frame(width: 42, height: 42)
                .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(theme.text.opacity(0.08)))
                Text(site.title.isEmpty ? site.host : site.title)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                Text(site.host)
                    .font(.system(size: 10))
                    .foregroundStyle(theme.secondaryText)
                    .lineLimit(1)
            }
            .padding(.vertical, 10)
            .padding(.horizontal, 6)
            .frame(maxWidth: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(hovering ? theme.text.opacity(0.08) : theme.surface)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(hovering ? theme.accent(for: .links).opacity(0.7) : theme.border.opacity(0.6),
                                  lineWidth: 0.8)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .overlay(alignment: .topTrailing) {
            if hovering {
                HStack(spacing: 3) {
                    smallButton("pencil", help: "Edit", action: onEdit)
                    smallButton("xmark", help: "Remove", action: onRemove)
                }
                .padding(5)
            }
        }
        .onHover { hovering = $0 }
        .contextMenu {
            Button("Open", action: onOpen)
            Button("Copy Link") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(site.url, forType: .string)
            }
            Button("Edit…", action: onEdit)
            Divider()
            Button("Remove", role: .destructive, action: onRemove)
        }
        .help(site.url)
    }

    private func smallButton(_ icon: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 8, weight: .bold))
                .foregroundStyle(theme.text)
                .frame(width: 18, height: 18)
                .background(Circle().fill(theme.background.opacity(0.85)))
                .overlay(Circle().strokeBorder(theme.border, lineWidth: 0.6))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

private struct SiteEditor: View {
    @Environment(\.theme) private var theme
    @State var site: SavedSite
    let isNew: Bool
    let done: (SavedSite?) -> Void

    private var valid: Bool { LinkParsing.url(from: site.url) != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(isNew ? "Add site" : "Edit site")
                .font(.system(size: 14, weight: .bold))
                .foregroundStyle(theme.text)
            SearchField(text: $site.title, placeholder: "Title", icon: "textformat")
            SearchField(text: $site.url, placeholder: "example.com", icon: "link", onSubmit: save)
            HStack {
                Spacer()
                PillButton(title: "Cancel") { done(nil) }
                PillButton(title: isNew ? "Add" : "Save", icon: "checkmark", prominent: true, action: save)
                    .disabled(!valid)
                    .opacity(valid ? 1 : 0.5)
            }
        }
        .padding(14)
        .frame(width: 340)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(theme.background))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
        .shadow(color: .black.opacity(0.5), radius: 12, y: 4)
    }

    private func save() {
        guard let url = LinkParsing.url(from: site.url) else { return }
        var result = site
        result.url = url.absoluteString
        if result.title.trimmingCharacters(in: .whitespaces).isEmpty {
            result.title = (url.host ?? "Site").replacingOccurrences(of: "www.", with: "")
        }
        done(result)
    }
}
