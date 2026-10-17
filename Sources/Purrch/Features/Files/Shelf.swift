import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ShelfEntry: Codable, Identifiable, Equatable {
    var id = UUID()
    var path: String
    var bookmark: Data?
    var added = Date()

    var url: URL { URL(fileURLWithPath: path) }
}

/// Files parked in the notch. They are referenced by path + bookmark, never copied.
@MainActor
final class ShelfStore: ObservableObject {
    static let shared = ShelfStore()

    @Published private(set) var entries: [ShelfEntry]
    private let storeName = "shelf"

    private init() {
        entries = Store.load("shelf", default: [ShelfEntry]())
        prune()
    }

    func add(_ urls: [URL]) {
        var changed = false
        for url in urls where url.isFileURL {
            let path = url.standardizedFileURL.path
            guard !entries.contains(where: { $0.path == path }) else { continue }
            let bookmark = try? url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)
            entries.append(ShelfEntry(path: path, bookmark: bookmark))
            changed = true
        }
        if changed { save() }
    }

    func remove(_ entry: ShelfEntry) {
        entries.removeAll { $0.id == entry.id }
        save()
    }

    func clear() {
        entries.removeAll()
        save()
    }

    /// Follows moved files via their bookmark and drops ones that no longer exist.
    func prune() {
        let fm = FileManager.default
        var changed = false
        var kept: [ShelfEntry] = []
        for var entry in entries {
            if !fm.fileExists(atPath: entry.path), let data = entry.bookmark {
                var stale = false
                if let resolved = try? URL(resolvingBookmarkData: data, options: [.withoutUI],
                                           relativeTo: nil, bookmarkDataIsStale: &stale),
                   fm.fileExists(atPath: resolved.path) {
                    entry.path = resolved.path
                    if stale { entry.bookmark = try? resolved.bookmarkData() }
                    changed = true
                }
            }
            if fm.fileExists(atPath: entry.path) { kept.append(entry) } else { changed = true }
        }
        if changed {
            entries = kept
            save()
        }
    }

    private func save() { Store.save(storeName, entries) }
}

struct ShelfModule: View {
    @ObservedObject private var store = ShelfStore.shared
    @Environment(\.theme) private var theme
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(store.entries.isEmpty ? "Drop files to park them here"
                     : "\(store.entries.count) item\(store.entries.count == 1 ? "" : "s")")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.secondaryText)
                Spacer()
                PillButton(title: "Add", icon: "plus") {
                    FileMeta.choose { ShelfStore.shared.add($0) }
                }
                if !store.entries.isEmpty {
                    if store.entries.count > 1 {
                        IconButton(systemName: "square.and.arrow.up", size: 28, help: "AirDrop all") {
                            FileMeta.airDrop(store.entries.map(\.url))
                        }
                    }
                    PillButton(title: "Clear", icon: "trash") {
                        withAnimation(.easeOut(duration: 0.2)) { store.clear() }
                    }
                }
            }

            ZStack {
                if store.entries.isEmpty {
                    dropZone
                } else {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(alignment: .top, spacing: 12) {
                            ForEach(store.entries) { entry in
                                ShelfTile(entry: entry)
                                    .transition(.scale(scale: 0.9).combined(with: .opacity))
                            }
                        }
                        .padding(.vertical, 4)
                        .padding(.horizontal, 2)
                    }
                    if targeted {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(theme.accent, style: StrokeStyle(lineWidth: 2, dash: [7, 5]))
                            .background(RoundedRectangle(cornerRadius: 16).fill(theme.accent.opacity(0.08)))
                            .allowsHitTesting(false)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Text("Files stay where they are. Drag them back out whenever you need them.")
                .font(.system(size: 11))
                .foregroundStyle(theme.secondaryText.opacity(0.8))
                .frame(maxWidth: .infinity)
        }
        .contentShape(Rectangle())
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            DropLoader.fileURLs(from: providers) { urls in
                withAnimation(.spring(response: 0.3)) { ShelfStore.shared.add(urls) }
            }
            return true
        }
        .onAppear { store.prune() }
        .animation(.spring(response: 0.3), value: store.entries)
    }

    private var dropZone: some View {
        VStack(spacing: 10) {
            Image(systemName: targeted ? "tray.and.arrow.down.fill" : "tray.and.arrow.down")
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(theme.accent)
                .themeGlow(theme)
            Text(targeted ? "Release to add" : "Drop files here")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(theme.text)
            Text("Drag anything onto the notch to keep it handy")
                .font(.system(size: 12))
                .foregroundStyle(theme.secondaryText)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(targeted ? theme.accent.opacity(0.08) : theme.surface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(targeted ? theme.accent : theme.border,
                              style: StrokeStyle(lineWidth: targeted ? 2 : 1, dash: [7, 5]))
        )
    }
}

private struct ShelfTile: View {
    let entry: ShelfEntry
    @Environment(\.theme) private var theme
    @State private var hovering = false
    @State private var size: Int64?

    private let width: CGFloat = 150

    var body: some View {
        let url = entry.url
        let label = FileMeta.label(url)
        VStack(alignment: .leading, spacing: 5) {
            ZStack(alignment: .bottomLeading) {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(theme.text.opacity(hovering ? 0.14 : 0.08))
                FileThumbnailView(url: url, size: CGSize(width: 220, height: 160))
                    .padding(12)
                    .frame(width: width, height: 110)
                Tag(text: label, color: FileMeta.color(for: label))
                    .background(RoundedRectangle(cornerRadius: 4).fill(theme.background.opacity(0.7)))
                    .padding(8)
            }
            .frame(width: width, height: 110)
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(hovering ? theme.accent.opacity(0.7) : theme.border.opacity(0.6), lineWidth: 0.8))
            .overlay(alignment: .topTrailing) {
                if hovering {
                    HStack(spacing: 4) {
                        miniButton("dot.radiowaves.left.and.right", help: "AirDrop") { FileMeta.airDrop([url]) }
                        miniButton("xmark", help: "Remove from shelf") {
                            withAnimation { ShelfStore.shared.remove(entry) }
                        }
                    }
                    .padding(6)
                }
            }

            Text(url.lastPathComponent)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(theme.text)
                .lineLimit(1)
                .truncationMode(.middle)
            HStack(spacing: 4) {
                Image(systemName: "doc").font(.system(size: 10))
                Text(size.map(Format.bytes) ?? "…").font(.system(size: 11))
            }
            .foregroundStyle(theme.secondaryText)
        }
        .frame(width: width)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture(count: 2) { NSWorkspace.shared.open(url) }
        .onDrag { NSItemProvider(contentsOf: url) ?? NSItemProvider(object: url as NSURL) }
        .contextMenu {
            Button("Open") { NSWorkspace.shared.open(url) }
            Button("Reveal in Finder") { FileMeta.reveal([url]) }
            Button("Copy") { FileMeta.copyFiles([url]) }
            Button("AirDrop") { FileMeta.airDrop([url]) }
            Divider()
            Button("Remove from Shelf") { ShelfStore.shared.remove(entry) }
        }
        .task(id: entry.path) {
            let u = url
            size = await Task.detached { FileMeta.size(u) }.value
        }
        .help(url.path)
    }

    private func miniButton(_ symbol: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(theme.text)
                .frame(width: 20, height: 20)
                .background(Circle().fill(theme.background.opacity(0.85)))
                .overlay(Circle().strokeBorder(theme.border, lineWidth: 0.6))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}
