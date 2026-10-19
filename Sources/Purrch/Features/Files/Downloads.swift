import AppKit
import SwiftUI

struct DownloadItem: Identifiable, Equatable {
    var id: String { url.path }
    let url: URL
    let size: Int64
    let date: Date
    var name: String { url.lastPathComponent }
}

struct ActiveDownload: Identifiable, Equatable {
    var id: String { path }
    let path: String
    /// Name of the finished file.
    let name: String
    var bytes: Int64
    var total: Int64?
    /// 0...1. Real when `total` is known, estimated otherwise.
    var progress: Double
    var speed: Double
    var lastChange: Date
}

@MainActor
final class DownloadsService: ObservableObject {
    static let shared = DownloadsService()

    @Published var activeProgress: Double?
    @Published var activeName: String?
    @Published var recent: [DownloadItem] = []
    @Published private(set) var active: [ActiveDownload] = []
    /// Finished during this session, newest first.
    @Published private(set) var completed: [DownloadItem] = []
    @Published private(set) var accessDenied = false
    /// Files just moved to the Trash, with where they went, so "Undo" can put them back.
    @Published private(set) var lastTrashed: [(original: URL, trashed: URL)] = []
    private var undoTimer: Timer?

    let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads", isDirectory: true)

    private static let partialExtensions: Set<String> = ["crdownload", "download", "part", "partial"]
    private var source: DispatchSourceFileSystemObject?
    private var timer: Timer?
    private var tracked: [String: ActiveDownload] = [:]
    private var lastFullScan = Date.distantPast
    private var completionWork: DispatchWorkItem?

    private init() {}

    func start() {
        guard timer == nil else { return }
        startWatching()
        scan()
        let timer = Timer(timeInterval: 1, repeats: true) { _ in
            MainActor.assumeIsolated { DownloadsService.shared.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func startWatching() {
        let fd = open(folder.path, O_EVTONLY)
        guard fd >= 0 else { accessDenied = true; return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd,
                                                               eventMask: [.write, .rename, .delete, .extend],
                                                               queue: .main)
        source.setEventHandler {
            MainActor.assumeIsolated { DownloadsService.shared.scan() }
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        self.source = source
    }

    private func tick() {
        // Fast polling while something downloads; slow background refresh otherwise.
        if !tracked.isEmpty || Date().timeIntervalSince(lastFullScan) > 15 || source == nil { scan() }
    }

    func rescan() {
        if source == nil { startWatching() }
        scan()
    }

    // MARK: Scan

    func scan() {
        lastFullScan = Date()
        let keys: [URLResourceKey] = [.fileSizeKey, .addedToDirectoryDateKey, .creationDateKey,
                                      .contentModificationDateKey, .isDirectoryKey]
        let urls: [URL]
        do {
            urls = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys,
                                                               options: [.skipsHiddenFiles])
            accessDenied = false
        } catch {
            accessDenied = true
            recent = []
            return
        }

        let now = Date()
        var seen: Set<String> = []
        var finished: [DownloadItem] = []

        for url in urls where Self.partialExtensions.contains(url.pathExtension.lowercased()) {
            let path = url.path
            seen.insert(path)
            let (bytes, total) = Self.measure(url)
            var item = tracked[path] ?? ActiveDownload(
                path: path, name: url.deletingPathExtension().lastPathComponent,
                bytes: bytes, total: total, progress: 0.02, speed: 0, lastChange: now)
            let dt = now.timeIntervalSince(item.lastChange)
            if bytes != item.bytes, dt > 0 {
                let instant = Double(max(0, bytes - item.bytes)) / dt
                item.speed = item.speed == 0 ? instant : item.speed * 0.6 + instant * 0.4
                item.lastChange = now
            } else if dt > 3 {
                item.speed = 0
            }
            item.bytes = bytes
            item.total = total
            if let total, total > 0 {
                item.progress = min(1, Double(bytes) / Double(total))
            } else if tracked[path] != nil {
                // Unknown total: creep towards 95% as data keeps arriving.
                let step = bytes > 0 ? 0.04 : 0.01
                item.progress = min(0.95, item.progress + (0.95 - item.progress) * step)
            }
            tracked[path] = item
        }

        // Anything no longer partial has either finished or been cancelled.
        for (path, item) in tracked where !seen.contains(path) {
            tracked[path] = nil
            let final = URL(fileURLWithPath: path).deletingPathExtension()
            if FileManager.default.fileExists(atPath: final.path) {
                finished.append(DownloadItem(url: final, size: FileMeta.size(final), date: now))
            } else if let match = urls.first(where: {
                !Self.partialExtensions.contains($0.pathExtension.lowercased())
                    && $0.lastPathComponent.hasPrefix(item.name.components(separatedBy: ".").first ?? item.name)
                    && ((try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate)
                        .map { now.timeIntervalSince($0) < 10 } ?? false)
            }) {
                // Chrome's "Unconfirmed 123.crdownload" ends up with a different name.
                finished.append(DownloadItem(url: match, size: FileMeta.size(match), date: now))
            }
        }

        let newActive = tracked.values.sorted { $0.path < $1.path }
        if newActive != active { active = newActive }

        recent = urls
            .filter { !Self.partialExtensions.contains($0.pathExtension.lowercased()) }
            .map { url -> DownloadItem in
                let values = try? url.resourceValues(forKeys: Set(keys))
                let date = values?.addedToDirectoryDate ?? values?.creationDate
                    ?? values?.contentModificationDate ?? .distantPast
                let size = values?.isDirectory == true ? 0 : Int64(values?.fileSize ?? 0)
                return DownloadItem(url: url, size: size, date: date)
            }
            .sorted { $0.date > $1.date }
            .prefix(20)
            .map { $0 }

        if !finished.isEmpty { complete(finished) }
        publishLiveActivity()
    }

    // MARK: Trash

    /// Moves files to the Trash (never deletes outright) and offers Undo for a few seconds.
    func trash(_ urls: [URL]) {
        guard !urls.isEmpty else { return }
        withAnimation(Motion.pop) {
            recent.removeAll { urls.contains($0.url) }
            completed.removeAll { urls.contains($0.url) }
        }
        NSWorkspace.shared.recycle(urls) { moved, _ in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    let service = DownloadsService.shared
                    withAnimation(Motion.expoOut(0.35)) {
                        service.lastTrashed = moved.map { (original: $0.key, trashed: $0.value) }
                    }
                    service.undoTimer?.invalidate()
                    service.undoTimer = Timer.scheduledTimer(withTimeInterval: 6, repeats: false) { _ in
                        MainActor.assumeIsolated {
                            withAnimation(Motion.expoOut(0.35)) { DownloadsService.shared.lastTrashed = [] }
                        }
                    }
                    service.scan()
                }
            }
        }
    }

    func undoTrash() {
        for item in lastTrashed {
            try? FileManager.default.moveItem(at: item.trashed, to: item.original)
        }
        undoTimer?.invalidate()
        withAnimation(Motion.expoOut(0.35)) { lastTrashed = [] }
        scan()
    }

    private func complete(_ items: [DownloadItem]) {
        completed = Array((items + completed.filter { c in !items.contains { $0.url == c.url } }).prefix(5))
        guard tracked.isEmpty, let last = items.last else { return }
        // Show a full ring briefly in the closed notch.
        activeName = last.name
        activeProgress = 1
        completionWork?.cancel()
        let work = DispatchWorkItem {
            MainActor.assumeIsolated {
                let service = DownloadsService.shared
                if service.tracked.isEmpty {
                    service.activeProgress = nil
                    service.activeName = nil
                }
            }
        }
        completionWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: work)
    }

    private func publishLiveActivity() {
        guard !tracked.isEmpty else {
            if activeProgress != nil && activeProgress! < 1 { activeProgress = nil; activeName = nil }
            return
        }
        completionWork?.cancel()
        let items = Array(tracked.values)
        let progress = items.map(\.progress).reduce(0, +) / Double(items.count)
        activeProgress = progress
        activeName = items.count == 1 ? items[0].name : "\(items.count) downloads"
    }

    /// Bytes so far and, when known, the expected total.
    nonisolated private static func measure(_ url: URL) -> (Int64, Int64?) {
        if url.pathExtension.lowercased() == "download" {
            let plist = url.appendingPathComponent("Info.plist")
            if let data = try? Data(contentsOf: plist),
               let dict = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] {
                let soFar = (dict["DownloadEntryProgressBytesSoFar"] as? NSNumber)?.int64Value
                let total = (dict["DownloadEntryProgressTotalToLoad"] as? NSNumber)?.int64Value
                if let soFar {
                    return (soFar, (total ?? 0) > 0 ? total : nil)
                }
            }
            return (FileMeta.directorySize(url, limit: 50), nil)
        }
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        return (Int64(size), nil)
    }
}

// MARK: - View

struct DownloadsModule: View {
    @ObservedObject private var service = DownloadsService.shared
    @Environment(\.theme) private var theme
    @State private var selecting = false
    @State private var selection = Set<URL>()

    private var justDone: [DownloadItem] {
        service.completed.filter { c in !service.recent.prefix(3).contains { $0.url == c.url } }
    }

    /// Every finished download on screen, once each.
    private var selectable: [URL] {
        var seen = Set<URL>()
        return (justDone + service.recent).map(\.url).filter { seen.insert($0).inserted }
    }

    var body: some View {
        Group {
            if service.accessDenied {
                VStack(spacing: 10) {
                    EmptyState(icon: "lock.shield", title: "Can't read Downloads",
                               subtitle: "Allow Purrch to access the Downloads folder in System Settings › Privacy & Security › Files and Folders.")
                    HStack(spacing: 8) {
                        PillButton(title: "Open Settings", icon: "gearshape", prominent: true) {
                            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders") {
                                NSWorkspace.shared.open(url)
                            }
                        }
                        PillButton(title: "Try again", icon: "arrow.clockwise") { service.rescan() }
                    }
                    .padding(.bottom, 12)
                }
            } else {
                ZStack(alignment: .bottom) {
                    ScrollView(.vertical, showsIndicators: false) {
                        VStack(alignment: .leading, spacing: 6) {
                            if !service.active.isEmpty {
                                section("Downloading")
                                ForEach(service.active) { ActiveRow(item: $0) }
                            }
                            if !justDone.isEmpty {
                                section("Just finished")
                                ForEach(justDone) { row($0, highlight: true) }
                            }
                            section("Recent")
                            if service.recent.isEmpty {
                                EmptyState(icon: "arrow.down.circle", title: "No downloads yet",
                                           subtitle: "Files you download will appear here with live progress.")
                                    .frame(height: 160)
                            } else {
                                TimelineView(.periodic(from: .now, by: 30)) { _ in
                                    VStack(spacing: 6) {
                                        ForEach(service.recent) { item in
                                            row(item, highlight: service.completed.contains { $0.url == item.url })
                                        }
                                    }
                                }
                            }
                        }
                        .padding(.bottom, selecting || !service.lastTrashed.isEmpty ? 48 : 0)
                    }
                    bottomBar
                }
                .animation(Motion.expoOut(0.35), value: selecting)
            }
        }
        .onAppear { service.rescan() }
        .onChange(of: service.recent) { _, _ in
            // Forget files that left the folder; leave select mode once nothing is left.
            selection.formIntersection(selectable)
            if selectable.isEmpty { selecting = false }
        }
    }

    private func row(_ item: DownloadItem, highlight: Bool) -> some View {
        DownloadRow(item: item, highlight: highlight, selecting: selecting,
                    selected: selection.contains(item.url)) {
            withAnimation(Motion.pop) {
                if selection.contains(item.url) { selection.remove(item.url) } else { selection.insert(item.url) }
            }
        }
    }

    // MARK: Bottom bar (selection, undo)

    @ViewBuilder
    private var bottomBar: some View {
        if selecting {
            let all = selectable
            let allSelected = !all.isEmpty && selection.count == all.count
            HStack(spacing: 10) {
                Text(selection.isEmpty ? "Tap downloads to select" : "\(selection.count) selected")
                    .font(.system(size: 12, weight: .semibold))
                Button(allSelected ? "Deselect all" : "Select all") {
                    withAnimation(Motion.pop) { selection = allSelected ? [] : Set(all) }
                }
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundStyle(theme.accent)
                Spacer()
                Button {
                    service.trash(Array(selection))
                    selection.removeAll()
                    selecting = false
                } label: {
                    Label("Delete", systemImage: "trash.fill")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 12).padding(.vertical, 6)
                        .background(Capsule().fill(Color.red.opacity(selection.isEmpty ? 0.35 : 0.9)))
                }
                .buttonStyle(.plain)
                .disabled(selection.isEmpty)
            }
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(Capsule().fill(theme.surface).shadow(color: .black.opacity(0.4), radius: 10, y: 4))
            .overlay(Capsule().strokeBorder(theme.border, lineWidth: 0.8))
            .transition(.move(edge: .bottom).combined(with: .opacity))
        } else if !service.lastTrashed.isEmpty {
            HStack(spacing: 10) {
                Image(systemName: "trash").foregroundStyle(theme.secondaryText)
                Text(service.lastTrashed.count == 1 ? "Moved to Trash" : "Moved \(service.lastTrashed.count) to Trash")
                    .font(.system(size: 12, weight: .semibold))
                Button("Undo") { service.undoTrash() }
                    .buttonStyle(.plain)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(theme.accent)
            }
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(Capsule().fill(theme.surface).shadow(color: .black.opacity(0.4), radius: 10, y: 4))
            .overlay(Capsule().strokeBorder(theme.border, lineWidth: 0.8))
            .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }

    private func section(_ title: String) -> some View {
        HStack {
            Text(title.uppercased())
                .font(.system(size: 10, weight: .semibold))
                .tracking(1)
                .foregroundStyle(theme.secondaryText)
            Spacer()
            if title == "Recent" {
                if !selectable.isEmpty {
                    Button(selecting ? "Done" : "Select") {
                        selecting.toggle()
                        if !selecting { selection.removeAll() }
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.accent)
                    .padding(.trailing, 6)
                }
                Button("Open Downloads") { NSWorkspace.shared.open(service.folder) }
                    .buttonStyle(.plain)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(theme.accent)
            }
        }
        .padding(.top, 4)
    }
}

private struct ActiveRow: View {
    let item: ActiveDownload
    @Environment(\.theme) private var theme

    var body: some View {
        ListRow {
            HStack(spacing: 10) {
                Image(systemName: "arrow.down.circle.fill")
                    .font(.system(size: 22))
                    .foregroundStyle(theme.accent)
                    .themeGlow(theme, radius: 4)
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(item.name)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(theme.text)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer()
                        Text(item.total != nil ? "\(Int(item.progress * 100))%" : "…")
                            .font(.system(size: 12, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .foregroundStyle(theme.accent)
                    }
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule().fill(theme.text.opacity(0.12))
                            Capsule().fill(theme.accent)
                                .frame(width: max(4, geo.size.width * item.progress))
                                .themeGlow(theme, radius: 3)
                        }
                    }
                    .frame(height: 5)
                    .animation(.easeOut(duration: 0.4), value: item.progress)
                    Text(detail)
                        .font(.system(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(theme.secondaryText)
                }
            }
        }
    }

    private var detail: String {
        var parts: [String] = []
        if let total = item.total {
            parts.append("\(Format.bytes(item.bytes)) of \(Format.bytes(total))")
        } else {
            parts.append(Format.bytes(item.bytes))
        }
        if item.speed > 0 {
            parts.append("\(Format.bytes(Int64(item.speed)))/s")
            if let total = item.total, total > item.bytes {
                parts.append("\(Format.duration(Double(total - item.bytes) / item.speed)) left")
            }
        } else {
            parts.append("waiting…")
        }
        return parts.joined(separator: " · ")
    }
}

private struct DownloadRow: View {
    let item: DownloadItem
    var highlight = false
    var selecting = false
    var selected = false
    var onToggle: () -> Void = {}
    @Environment(\.theme) private var theme
    @State private var copied = false

    var body: some View {
        let isImage = FileMeta.isImage(item.url)
        ListRow {
            HStack(spacing: 10) {
                if selecting {
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 17, weight: .semibold))
                        .foregroundStyle(selected ? theme.accent : theme.secondaryText)
                        .transition(.scale.combined(with: .opacity))
                }
                if isImage {
                    FileThumbnailView(url: item.url, size: CGSize(width: 60, height: 60), contentMode: .fill)
                        .frame(width: 30, height: 30)
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                } else {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: item.url.path))
                        .resizable()
                        .frame(width: 30, height: 30)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(item.name)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(theme.text)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    HStack(spacing: 5) {
                        let label = FileMeta.label(item.url)
                        Tag(text: label, color: FileMeta.color(for: label))
                        if highlight {
                            Tag(text: "Done", color: .green)
                        }
                        Text(([item.size > 0 ? Format.bytes(item.size) : nil, Format.relative(item.date)]
                              .compactMap { $0 }).joined(separator: " · "))
                            .font(.system(size: 11))
                            .foregroundStyle(theme.secondaryText)
                    }
                }
                Spacer(minLength: 6)
                if !selecting {
                    if isImage {
                        IconButton(systemName: copied ? "checkmark" : "doc.on.doc", size: 26, help: "Copy image") {
                            FileMeta.copyFiles([item.url])
                            withAnimation(.easeOut(duration: 0.15)) { copied = true }
                            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                                withAnimation(.easeOut(duration: 0.15)) { copied = false }
                            }
                        }
                    }
                    IconButton(systemName: "magnifyingglass", size: 26, help: "Reveal in Finder") {
                        FileMeta.reveal([item.url])
                    }
                    IconButton(systemName: "arrow.up.forward.app", size: 26, help: "Open") {
                        NSWorkspace.shared.open(item.url)
                    }
                    IconButton(systemName: "trash", size: 26, help: "Move to Trash") {
                        DownloadsService.shared.trash([item.url])
                    }
                }
            }
        }
        .overlay {
            if selecting {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(selected ? theme.accent : .clear, lineWidth: 1.5)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { if selecting { onToggle() } }
        .onDrag { NSItemProvider(contentsOf: item.url) ?? NSItemProvider(object: item.url as NSURL) }
        .contextMenu {
            Button("Open") { NSWorkspace.shared.open(item.url) }
            Button("Reveal in Finder") { FileMeta.reveal([item.url]) }
            Button("Copy") { FileMeta.copyFiles([item.url]) }
            Divider()
            Button("Move to Trash") { DownloadsService.shared.trash([item.url]) }
        }
    }
}
