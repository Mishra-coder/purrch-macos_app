import AppKit
import Combine
import SwiftUI

struct CaptureFile: Identifiable, Equatable, Hashable {
    var id: String { url.path }
    let url: URL
    let date: Date
    let size: Int64
    var isVideo: Bool { ["mov", "mp4"].contains(url.pathExtension.lowercased()) }
    var name: String { url.deletingPathExtension().lastPathComponent }
}

enum CaptureFilter: String, CaseIterable, Identifiable {
    case all = "All", screenshots = "Screenshots", recordings = "Recordings"
    var id: String { rawValue }

    func matches(_ file: CaptureFile) -> Bool {
        switch self {
        case .all: true
        case .screenshots: !file.isVideo
        case .recordings: file.isVideo
        }
    }
}

@MainActor
final class CapturesModel: ObservableObject {
    @Published private(set) var files: [CaptureFile] = []
    @Published private(set) var loaded = false
    /// Files just moved to the Trash, with where they went, so "Undo" can put them back.
    @Published private(set) var lastTrashed: [(original: URL, trashed: URL)] = []
    private var undoTimer: Timer?

    nonisolated static let extensions: Set<String> = ["png", "jpg", "jpeg", "heic", "mov", "mp4"]

    /// Where macOS saves screenshots (`defaults read com.apple.screencapture location`).
    static var folder: URL {
        if let raw = UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "location"),
           !raw.isEmpty {
            let path = (raw as NSString).expandingTildeInPath
            var dir: ObjCBool = false
            if FileManager.default.fileExists(atPath: path, isDirectory: &dir), dir.boolValue {
                return URL(fileURLWithPath: path, isDirectory: true)
            }
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop", isDirectory: true)
    }

    func refresh() {
        let folder = Self.folder
        Task.detached(priority: .utility) {
            let found = Self.scan(folder)
            await MainActor.run {
                if found != self.files { withAnimation(Motion.pop) { self.files = found } }
                self.loaded = true
            }
        }
    }

    nonisolated private static func scan(_ folder: URL) -> [CaptureFile] {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .fileSizeKey, .isRegularFileKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(
            at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        let cutoff = Date().addingTimeInterval(-24 * 3600)
        return urls.compactMap { url -> CaptureFile? in
            guard extensions.contains(url.pathExtension.lowercased()),
                  let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true,
                  let date = values.contentModificationDate, date > cutoff else { return nil }
            return CaptureFile(url: url, date: date, size: Int64(values.fileSize ?? 0))
        }
        .sorted { $0.date > $1.date }
    }

    func takeScreenshot() {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let target = Self.folder.appendingPathComponent("Screenshot \(formatter.string(from: Date())).png")
        NotchState.shared.isPinned = false
        NotchState.shared.close()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            process.arguments = ["-i", target.path]
            process.terminationHandler = { _ in
                DispatchQueue.main.async { MainActor.assumeIsolated { self.refresh() } }
            }
            try? process.run()
        }
    }

    /// Moves files to the Trash (never deletes outright) and offers Undo for a few seconds.
    func trash(_ targets: [CaptureFile]) {
        guard !targets.isEmpty else { return }
        let urls = targets.map(\.url)
        withAnimation(Motion.pop) { files.removeAll { targets.contains($0) } }
        NSWorkspace.shared.recycle(urls) { moved, _ in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    withAnimation(Motion.expoOut(0.35)) {
                        self.lastTrashed = moved.map { (original: $0.key, trashed: $0.value) }
                    }
                    self.undoTimer?.invalidate()
                    self.undoTimer = Timer.scheduledTimer(withTimeInterval: 6, repeats: false) { _ in
                        MainActor.assumeIsolated { withAnimation(Motion.expoOut(0.35)) { self.lastTrashed = [] } }
                    }
                    self.refresh()
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
        refresh()
    }

    func copy(_ targets: [CaptureFile]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        var objects: [NSPasteboardWriting] = targets.map { $0.url as NSURL }
        if targets.count == 1, let file = targets.first, !file.isVideo, let image = NSImage(contentsOf: file.url) {
            objects.append(image)
        }
        pb.writeObjects(objects)
    }
}

// MARK: - Module

struct CapturesModule: View {
    @StateObject private var model = CapturesModel()
    @Environment(\.theme) private var theme
    @State private var filter: CaptureFilter = .all
    @State private var selecting = false
    @State private var selection = Set<CaptureFile>()
    @State private var previewing: CaptureFile?
    @State private var toast: String?
    private let timer = Timer.publish(every: 3, on: .main, in: .common).autoconnect()

    private var visible: [CaptureFile] { model.files.filter(filter.matches) }

    var body: some View {
        ZStack {
            VStack(alignment: .leading, spacing: 10) {
                toolbar
                if visible.isEmpty {
                    emptyState
                } else {
                    grid
                }
            }

            if let file = previewing {
                CapturePreview(file: file, files: visible, model: model, current: $previewing) { message in
                    show(message)
                }
                .transition(.opacity.combined(with: .scale(scale: 0.96)))
                .zIndex(2)
            }

            VStack {
                Spacer()
                bottomBar
            }
            .zIndex(3)
        }
        .animation(Motion.expoOut(0.35), value: selecting)
        .animation(Motion.expoOut(0.4), value: previewing)
        .onAppear { model.refresh() }
        .onReceive(timer) { _ in model.refresh() }
    }

    // MARK: Toolbar

    private var toolbar: some View {
        HStack(spacing: 8) {
            HStack(spacing: 2) {
                ForEach(CaptureFilter.allCases) { f in
                    let count = model.files.filter(f.matches).count
                    Button {
                        withAnimation(Motion.pop) { filter = f }
                    } label: {
                        HStack(spacing: 5) {
                            Text(f.rawValue)
                            Text("\(count)")
                                .font(.system(size: 10, weight: .bold))
                                .padding(.horizontal, 5).padding(.vertical, 1)
                                .background(Capsule().fill(filter == f ? theme.background.opacity(0.25) : theme.text.opacity(0.1)))
                        }
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(filter == f ? theme.background : theme.text.opacity(0.8))
                        .padding(.horizontal, 10).padding(.vertical, 5)
                        .background {
                            if filter == f { Capsule().fill(theme.accent) }
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(3)
            .background(Capsule().fill(theme.text.opacity(0.06)))

            Text("Last 24 hours")
                .font(.system(size: 11))
                .foregroundStyle(theme.secondaryText)
            Spacer()
            if !visible.isEmpty {
                PillButton(title: selecting ? "Done" : "Select", icon: selecting ? "checkmark" : "checkmark.circle") {
                    selecting.toggle()
                    if !selecting { selection.removeAll() }
                }
            }
            IconButton(systemName: "folder", size: 28, help: "Open folder") {
                NSWorkspace.shared.open(CapturesModel.folder)
            }
            PillButton(title: "Screenshot", icon: "camera.viewfinder", prominent: true) {
                model.takeScreenshot()
            }
        }
    }

    // MARK: Grid

    private var grid: some View {
        ScrollView(.vertical, showsIndicators: false) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160, maximum: 220), spacing: 12)], spacing: 12) {
                ForEach(visible) { file in
                    CaptureTile(file: file,
                                selecting: selecting,
                                selected: selection.contains(file),
                                onOpen: {
                                    if selecting { toggle(file) } else { previewing = file }
                                },
                                onCopy: { model.copy([file]); show("Copied") },
                                onReveal: { FileMeta.reveal([file.url]) },
                                onTrash: { model.trash([file]) })
                        .transition(.scale(scale: 0.6).combined(with: .opacity))
                }
            }
            .padding(.vertical, 4)
            .padding(.bottom, selecting || !model.lastTrashed.isEmpty || toast != nil ? 48 : 0)
        }
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            ZStack {
                Circle().fill(theme.accent.opacity(0.12)).frame(width: 72, height: 72)
                Image(systemName: filter == .recordings ? "record.circle" : "camera.viewfinder")
                    .font(.system(size: 28, weight: .light))
                    .foregroundStyle(theme.accent)
            }
            Text(model.loaded ? "Nothing captured today" : "Looking for captures…")
                .font(.system(size: 14, weight: .semibold))
            Text("Press ⇧⌘4 or use the Screenshot button — new captures land here.")
                .font(.system(size: 12))
                .foregroundStyle(theme.secondaryText)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: Bottom bar (selection, undo, toast)

    @ViewBuilder
    private var bottomBar: some View {
        if selecting {
            HStack(spacing: 10) {
                Text(selection.isEmpty ? "Tap captures to select" : "\(selection.count) selected")
                    .font(.system(size: 12, weight: .semibold))
                Button(selection.count == visible.count ? "Deselect all" : "Select all") {
                    withAnimation(Motion.pop) {
                        selection = selection.count == visible.count ? [] : Set(visible)
                    }
                }
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundStyle(theme.accent)
                Spacer()
                PillButton(title: "Copy", icon: "doc.on.doc") {
                    model.copy(Array(selection)); show("Copied \(selection.count)")
                }
                .disabled(selection.isEmpty)
                Button {
                    model.trash(Array(selection))
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
        } else if !model.lastTrashed.isEmpty {
            HStack(spacing: 10) {
                Image(systemName: "trash").foregroundStyle(theme.secondaryText)
                Text(model.lastTrashed.count == 1 ? "Moved to Trash" : "Moved \(model.lastTrashed.count) to Trash")
                    .font(.system(size: 12, weight: .semibold))
                Button("Undo") { model.undoTrash() }
                    .buttonStyle(.plain)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(theme.accent)
            }
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(Capsule().fill(theme.surface).shadow(color: .black.opacity(0.4), radius: 10, y: 4))
            .overlay(Capsule().strokeBorder(theme.border, lineWidth: 0.8))
            .transition(.move(edge: .bottom).combined(with: .opacity))
        } else if let toast {
            Label(toast, systemImage: "checkmark.circle.fill")
                .font(.system(size: 12, weight: .semibold))
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(Capsule().fill(theme.surface).shadow(color: .black.opacity(0.4), radius: 10, y: 4))
                .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }

    private func toggle(_ file: CaptureFile) {
        withAnimation(Motion.pop) {
            if selection.contains(file) { selection.remove(file) } else { selection.insert(file) }
        }
    }

    private func show(_ message: String) {
        withAnimation(Motion.expoOut(0.3)) { toast = message }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) {
            withAnimation(Motion.expoOut(0.3)) { if toast == message { toast = nil } }
        }
    }
}

// MARK: - Tile

private struct CaptureTile: View {
    @Environment(\.theme) private var theme
    let file: CaptureFile
    let selecting: Bool
    let selected: Bool
    let onOpen: () -> Void
    let onCopy: () -> Void
    let onReveal: () -> Void
    let onTrash: () -> Void
    @State private var hovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ZStack {
                theme.text.opacity(0.06)
                FileThumbnailView(url: file.url, size: CGSize(width: 440, height: 280), contentMode: .fill)
                if file.isVideo {
                    Image(systemName: "play.fill")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 34, height: 34)
                        .background(Circle().fill(.black.opacity(0.55)))
                }
                // Dim + actions on hover.
                LinearGradient(colors: [.black.opacity(hovering ? 0.55 : 0), .clear], startPoint: .top, endPoint: .center)
            }
            .aspectRatio(16 / 10, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(selected ? theme.accent : (hovering ? theme.text.opacity(0.35) : theme.border.opacity(0.5)),
                                  lineWidth: selected ? 2.5 : 0.8)
            )
            .overlay(alignment: .topTrailing) {
                if hovering && !selecting {
                    HStack(spacing: 5) {
                        tileButton("doc.on.doc", help: "Copy", action: onCopy)
                        tileButton("folder", help: "Show in Finder", action: onReveal)
                        tileButton("trash.fill", help: "Move to Trash", tint: .red, action: onTrash)
                    }
                    .padding(7)
                    .transition(.opacity.combined(with: .scale(scale: 0.8, anchor: .topTrailing)))
                }
            }
            .overlay(alignment: .topLeading) {
                if selecting {
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(selected ? theme.accent : .white)
                        .background(Circle().fill(.black.opacity(0.35)).padding(2))
                        .padding(7)
                        .transition(.scale.combined(with: .opacity))
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if file.isVideo {
                    Tag(text: file.url.pathExtension, color: .white)
                        .padding(6)
                }
            }
            .scaleEffect(selected ? 0.95 : (hovering ? 1.03 : 1))
            .shadow(color: .black.opacity(hovering ? 0.45 : 0.2), radius: hovering ? 10 : 4, y: hovering ? 6 : 2)

            VStack(alignment: .leading, spacing: 1) {
                Text(file.name)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.text)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("\(Format.relative(file.date)) ago · \(Format.bytes(file.size))")
                    .font(.system(size: 10))
                    .foregroundStyle(theme.secondaryText)
            }
            .padding(.horizontal, 2)
        }
        .contentShape(Rectangle())
        .onHover { h in withAnimation(Motion.expoOut(0.25)) { hovering = h } }
        .onTapGesture(perform: onOpen)
        .onDrag { NSItemProvider(contentsOf: file.url) ?? NSItemProvider(object: file.url as NSURL) }
        .help(file.url.lastPathComponent)
    }

    private func tileButton(_ icon: String, help: String, tint: Color = .white, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(tint)
                .frame(width: 26, height: 26)
                .background(Circle().fill(.ultraThinMaterial))
                .background(Circle().fill(.black.opacity(0.35)))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

// MARK: - Preview

/// Large in-notch viewer with previous / next, copy, open and delete.
private struct CapturePreview: View {
    @Environment(\.theme) private var theme
    let file: CaptureFile
    let files: [CaptureFile]
    let model: CapturesModel
    @Binding var current: CaptureFile?
    let notify: (String) -> Void
    @State private var image: NSImage?

    private var index: Int { files.firstIndex(of: file) ?? 0 }

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(theme.background.opacity(0.96))
                .onTapGesture { current = nil }

            HStack(spacing: 12) {
                navButton("chevron.left", enabled: index > 0) { current = files[index - 1] }

                ZStack {
                    Color.black.opacity(0.3)
                    if let image {
                        Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                            .transition(.opacity)
                    } else {
                        FileThumbnailView(url: file.url, size: CGSize(width: 900, height: 560), contentMode: .fit)
                    }
                    if file.isVideo {
                        Button { NSWorkspace.shared.open(file.url) } label: {
                            Image(systemName: "play.fill")
                                .font(.system(size: 20, weight: .bold))
                                .foregroundStyle(.black)
                                .frame(width: 52, height: 52)
                                .background(Circle().fill(.white))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(theme.border, lineWidth: 0.8))
                .id(file.id)
                .transition(.opacity)

                VStack(alignment: .leading, spacing: 10) {
                    HStack {
                        Spacer()
                        IconButton(systemName: "xmark", size: 26, help: "Close") { current = nil }
                    }
                    Text(file.name)
                        .font(.system(size: 13, weight: .bold))
                        .lineLimit(3)
                    Text("\(file.date.formatted(date: .omitted, time: .shortened)) · \(Format.bytes(file.size))")
                        .font(.system(size: 11))
                        .foregroundStyle(theme.secondaryText)
                    Text("\(index + 1) of \(files.count)")
                        .font(.system(size: 11))
                        .foregroundStyle(theme.secondaryText)
                    Spacer()
                    PillButton(title: "Copy", icon: "doc.on.doc") { model.copy([file]); notify("Copied") }
                    PillButton(title: "Open", icon: "arrow.up.forward.app") { NSWorkspace.shared.open(file.url) }
                    PillButton(title: "Finder", icon: "folder") { FileMeta.reveal([file.url]) }
                    Button {
                        let next = index + 1 < files.count ? files[index + 1] : (index > 0 ? files[index - 1] : nil)
                        model.trash([file])
                        current = next
                    } label: {
                        Label("Delete", systemImage: "trash.fill")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12).padding(.vertical, 6)
                            .background(Capsule().fill(Color.red.opacity(0.9)))
                    }
                    .buttonStyle(.plain)
                }
                .frame(width: 150)

                navButton("chevron.right", enabled: index + 1 < files.count) { current = files[index + 1] }
            }
            .padding(12)
        }
        .task(id: file.id) {
            image = nil
            guard !file.isVideo else { return }
            let url = file.url
            image = await Task.detached(priority: .userInitiated) { NSImage(contentsOf: url) }.value
        }
    }

    private func navButton(_ icon: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        IconButton(systemName: icon, size: 30, help: icon == "chevron.left" ? "Previous" : "Next") {
            withAnimation(Motion.expoOut(0.3)) { action() }
        }
        .opacity(enabled ? 1 : 0.25)
        .disabled(!enabled)
    }
}
